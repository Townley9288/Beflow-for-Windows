namespace BBDownForWindows.Core;

public sealed class DownloadQueueExecutor(QueuedMediaDownloader downloader, IProcessRunner runner,
    IToolLocator tools, IDownloadNamingService naming, IHistoryStore history) : IDownloadQueueExecutor
{
    public async Task ExecuteAsync(DownloadQueueItem item, Func<QueueCheckpoint, Task> saveCheckpoint,
        IProgress<QueueProgress> progress, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var state = item.Checkpoint;
        if (state.WorkDirectory.Length == 0)
        {
            state.WorkDirectory = Path.Combine(Path.GetFullPath(item.Download?.Options.WorkDirectory ?? item.DualAudio!.WorkDirectory), ".beflow-queue", item.Id.ToString("N"));
            if (Directory.Exists(state.WorkDirectory)) throw new IOException("任务工作目录已被占用");
            if (item.Kind == DownloadQueueKind.DualAudio)
                state.OutputDirectory = Path.Combine(Path.GetFullPath(item.DualAudio!.WorkDirectory), $"多音轨_{item.AddedAt:yyyyMMdd_HHmmss}_{item.Id.ToString("N")[..8]}");
            if (item.Download is { } batch)
                state.Units = batch.Episodes.Select(e => new QueueUnitCheckpoint { Number = e.PageNumber, Title = e.PageTitle }).ToList();
            else state.Units = item.DualAudio!.Pairs.Where(p => p.IsSelected).Select(p => new QueueUnitCheckpoint { Number = p.PairNumber, Title = p.SourceAPageTitle }).ToList();
            await Save();
        }
        Directory.CreateDirectory(state.WorkDirectory);
        foreach (var unit in state.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unit.Completed)
            {
                foreach (var file in unit.Files) file.Verify();
                await CleanupSources(unit);
                continue;
            }
            if (unit.Error.Length > 0) continue;
            try
            {
                ReportStage("确认规格");
                if (unit.Publications.Count == 0)
                {
                    if (item.Download is { } normal) await DownloadNormal(normal, unit);
                    else await DownloadDual(item.DualAudio!, unit);
                }
                await Publish(unit);
                ReportStage("完成", 100);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not QueuePersistenceException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                unit.Error = exception.Message;
                context.AppendLog($"P{unit.Number} 失败：{exception.Message}\n");
                await Save(); ReportStage("失败");
            }
            void ReportStage(string phase, double? percent = null) => progress.Report(new(item.Id, unit.Number, phase, percent, "", ""));
        }

        async Task DownloadNormal(DownloadBatchRequest batch, QueueUnitCheckpoint unit)
        {
            var desired = batch.Episodes.Single(e => e.PageNumber == unit.Number);
            var episode = item.Catalog!.Episodes.Single(e => e.Page.Number == desired.PageNumber);
            if (unit.FinalPath.Length == 0)
            {
                var choice = StreamSelectionPolicy.Resolve(episode, Strict(desired), batch.Options);
                var output = naming.BuildPlan(new DownloadNamingContext
                {
                    RootDirectory = batch.Options.WorkDirectory, SourceUrl = batch.Options.Url, VideoTitle = batch.Title,
                    Page = episode.Page, Profile = batch.NamingProfile, ProfileKind = batch.NamingProfileKind,
                    TotalPages = batch.TotalPages, DownloadMode = batch.Options.DownloadMode, ApiMode = batch.Options.ApiMode,
                    DownloadedAt = batch.DownloadedAt, Metadata = batch.Metadata, Video = choice.Video, Audio = choice.Audio,
                    PreferredRelativePath = desired.RelativeOutputPath, FailOnConflict = true
                }, state.Units.Where(u => u.FinalPath.Length > 0).Select(u => Path.ChangeExtension(u.FinalPath, null)).ToHashSet(StringComparer.OrdinalIgnoreCase));
                unit.FinalPath = Path.Combine(output.LeafDirectory, output.FileStem + (batch.Options.DownloadMode == DownloadMode.AudioOnly ? ".m4a" : ".mp4"));
                state.OutputDirectory = output.MainDirectory;
                unit.SourceA.Directory = Path.Combine(state.WorkDirectory, $"P{unit.Number:D5}", "A");
                await Save();
            }
            EnsureUnoccupied(unit.FinalPath);
            await downloader.DownloadAsync(batch.Options, desired, episode.Page.Cid, unit.SourceA, Save,
                new DirectProgress<ExactDownloadProgress>(p => Report(unit, "下载", p)), context.WithPrefix($"P{unit.Number}"), cancellationToken);
            unit.Publications = unit.SourceA.Files.Select(file => new QueuePublication(file,
                Path.Combine(Path.GetDirectoryName(unit.FinalPath)!, Path.GetFileNameWithoutExtension(unit.FinalPath) + Path.GetFileName(file.Path)["output".Length..]))).ToList();
            await Save();
        }
        async Task DownloadDual(DualAudioBatchRequest batch, QueueUnitCheckpoint unit)
        {
            var pair = batch.Pairs.Single(p => p.PairNumber == unit.Number);
            var episodeA = item.DualCatalog!.SourceA!.Episodes.Single(e => e.Page.Number == pair.SourceAPageNumber);
            var episodeB = item.DualCatalog.SourceB!.Episodes.Single(e => e.Page.Number == pair.SourceBPageNumber);
            if (episodeA.IsMuxedStream || episodeB.IsMuxedStream) throw new InvalidOperationException("旧式合流不支持多音轨拆分");
            if (unit.FinalPath.Length == 0)
            {
                var safeTitle = string.Concat(unit.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
                if (safeTitle.Length > 120) safeTitle = safeTitle[..120];
                unit.FinalPath = Path.Combine(state.OutputDirectory, "多音轨MKV", $"[P{unit.Number:00}]{safeTitle}.mkv");
                unit.MuxPath = Path.Combine(state.WorkDirectory, $"P{unit.Number:D5}", "mux.mkv");
                unit.SourceA.Directory = Path.Combine(state.OutputDirectory, "来源A", $"P{unit.Number:D5}");
                unit.SourceB.Directory = Path.Combine(state.OutputDirectory, "来源B", $"P{unit.Number:D5}");
                if (File.Exists(unit.MuxPath)) throw new IOException("任务封装临时目标已被占用");
                await Save();
            }
            EnsureUnoccupied(unit.FinalPath);
            await Source(DualAudioSource.A, pair.SourceA, episodeA.Page.Cid, unit.SourceA);
            await Source(DualAudioSource.B, pair.SourceB, episodeB.Page.Cid, unit.SourceB);
            if (!unit.MuxCompleted)
            {
                // The saved path is private to this task. Only the prior interrupted mux can exist here.
                Directory.CreateDirectory(Path.GetDirectoryName(unit.MuxPath)!);
                if (File.Exists(unit.MuxPath)) File.Delete(unit.MuxPath);
                await Save();
                var fullSource = pair.MainVideoSource == DualAudioSource.A ? unit.SourceA : unit.SourceB;
                var otherSource = pair.MainVideoSource == DualAudioSource.A ? unit.SourceB : unit.SourceA;
                var full = fullSource.Files.Single(f => DownloadFileKinds.IsVideoFile(f.Path)).Path;
                var audio = otherSource.Files.Single(f => Path.GetExtension(f.Path).Equals(".m4a", StringComparison.OrdinalIgnoreCase)).Path;
                var mkvmerge = tools.Locate(new AppSettings { MkvmergePath = batch.MkvmergePath }).Mkvmerge;
                progress.Report(new(item.Id, unit.Number, "封装", null, "", ""));
                var result = await runner.RunAsync(new ProcessRunRequest(mkvmerge,
                    DualAudioService.BuildMkvmergeArguments(full, audio, unit.MuxPath, batch, pair.MainVideoSource, pair.SourceBDelayOverrideMs ?? batch.SourceBDelayMs),
                    state.WorkDirectory), context.AppendLog, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.ExitCode is not (0 or 1)) throw new IOException($"mkvmerge 封装失败：{result.ExitCode}");
                unit.Files = [QueueFileStamp.Read(unit.MuxPath)]; unit.MuxCompleted = true;
                await Save();
            }
            unit.Files.Single().Verify();
            unit.Publications = [new(unit.Files.Single(), unit.FinalPath)];
            await Save();

            async Task Source(DualAudioSource source, EpisodeStreamSelection selection, string cid, QueueSourceCheckpoint checkpoint)
            {
                var options = QueueSnapshot.Copy(batch.Options);
                options.Url = source == DualAudioSource.A ? batch.SourceAUrl : batch.SourceBUrl;
                options.Language = source == DualAudioSource.A ? batch.SourceALanguage : batch.SourceBLanguage;
                options.ApiMode = batch.ApiMode;
                options.DownloadMode = source == pair.MainVideoSource ? DownloadMode.VideoAndAudio : DownloadMode.AudioOnly;
                await downloader.DownloadAsync(options, selection, cid, checkpoint, Save,
                    new DirectProgress<ExactDownloadProgress>(p => Report(unit, $"来源 {source}", p)), context.WithPrefix($"第{unit.Number}对/{source}"), cancellationToken);
            }
        }
        async Task Publish(QueueUnitCheckpoint unit)
        {
            foreach (var publication in unit.Publications)
            {
                var expected = publication.Source with { Path = publication.Destination };
                if (File.Exists(publication.Source.Path))
                {
                    publication.Source.Verify(); EnsureUnoccupied(publication.Destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(publication.Destination)!);
                    File.Move(publication.Source.Path, publication.Destination, false);
                }
                expected.Verify();
            }
            unit.Files = unit.Publications.Select(p => p.Source with { Path = p.Destination }).ToList();
            unit.Completed = true; await Save();
            await CleanupSources(unit);
        }
        async Task CleanupSources(QueueUnitCheckpoint unit)
        {
            if (item.DualAudio is { KeepSourceFiles: false })
            {
                foreach (var source in new[] { unit.SourceA, unit.SourceB })
                {
                    if (source.SourcesCleaned) continue;
                    foreach (var file in source.Files) { if (File.Exists(file.Path)) { file.Verify(); File.Delete(file.Path); } }
                    source.SourcesCleaned = true; await Save();
                }
            }
        }
        void Report(QueueUnitCheckpoint unit, string prefix, ExactDownloadProgress p) =>
            progress.Report(new(item.Id, unit.Number, $"{prefix} · {p.Message}", p.Percent, p.Speed, p.Eta));
        async Task Save()
        {
            try
            {
                await saveCheckpoint(state);
                var record = DownloadQueueHistory.Create(item);
                await history.AddAsync(record);
                if (item.DualAudio is not null && state.OutputDirectory.Length > 0)
                {
                    var manifest = new DualAudioTaskManifest
                    {
                        Request = item.DualAudio,
                        Result = new DualAudioBatchResult
                        {
                            Title = item.Title, TaskDirectory = state.OutputDirectory,
                            OutputDirectory = Path.Combine(state.OutputDirectory, "多音轨MKV"),
                            ManifestPath = Path.Combine(state.OutputDirectory, "dual-audio-task.json"),
                            Pairs = record.DualAudioBatch!.Pairs, OutputFiles = record.OutputFiles
                        }
                    };
                    await AtomicJson.WriteAsync(manifest.Result.ManifestPath, manifest, SettingsStore.CreateOptions(), CancellationToken.None);
                }
            }
            catch (Exception exception) { throw new QueuePersistenceException("保存任务阶段失败：" + exception.Message, exception); }
        }
    }
    internal static EpisodeStreamSelection Strict(EpisodeStreamSelection desired)
    {
        var copy = QueueSnapshot.Copy(desired);
        if (copy.Video is not null) copy.Video = copy.Video with { IsManual = true };
        if (copy.Audio is not null) copy.Audio = copy.Audio with { IsManual = true };
        return copy;
    }
    private static void EnsureUnoccupied(string path)
    { if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"目标已被占用：{path}"); }
    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

public sealed class QueuePersistenceException(string message, Exception inner) : IOException(message, inner);

public static class DownloadQueueHistory
{
    public static HistoryRecord Create(DownloadQueueItem item)
    {
        var record = new HistoryRecord
        {
            Id = item.Id, QueueTaskId = item.Id, ParentQueueTaskId = item.ParentId,
            Title = item.Title, Url = item.Url, SecondaryUrl = item.DualAudio?.SourceBUrl ?? "",
            Timestamp = item.AddedAt, LogPath = item.LogPaths.LastOrDefault() ?? "", OutputDirectory = item.OutputDirectory,
            TaskType = item.Kind == DownloadQueueKind.Download ? TaskKind.DownloadBatch : TaskKind.DualAudioMux,
            OutputFiles = item.Checkpoint.Units.Where(u => u.Completed).SelectMany(u => u.Files.Select(f => f.Path)).ToList()
        };
        if (item.Download is { } download)
        {
            record.DownloadBatch = new DownloadBatchHistory
            {
                Options = download.Options, ParsedAt = download.ParsedAt, DownloadedAt = download.DownloadedAt,
                TotalPages = download.TotalPages, NamingProfile = download.NamingProfile, NamingProfileKind = download.NamingProfileKind,
                Episodes = download.Episodes.Select(e =>
                {
                    var unit = item.Checkpoint.Units.SingleOrDefault(u => u.Number == e.PageNumber);
                    return new DownloadEpisodeResult
                    {
                        PageNumber = e.PageNumber, PageTitle = e.PageTitle, Video = e.Video, Audio = e.Audio, IsMuxedStream = e.IsMuxedStream,
                        State = unit?.Completed == true ? DownloadEpisodeResultState.Completed : unit?.Error.Length > 0 ? DownloadEpisodeResultState.Failed : DownloadEpisodeResultState.Pending,
                        Error = unit?.Error ?? "", OutputDirectory = unit is null ? "" : Path.GetDirectoryName(unit.FinalPath) ?? "",
                        OutputFiles = unit?.Completed == true ? unit.Files.Select(f => f.Path).ToList() : []
                    };
                }).ToList()
            };
        }
        else
        {
            var dual = item.DualAudio!;
            record.DualAudioBatch = new DualAudioBatchHistory
            {
                Request = dual, ManifestPath = item.Checkpoint.OutputDirectory.Length > 0 ? Path.Combine(item.Checkpoint.OutputDirectory, "dual-audio-task.json") : "",
                Pairs = dual.Pairs.Where(p => p.IsSelected).Select(p =>
                {
                    var unit = item.Checkpoint.Units.SingleOrDefault(u => u.Number == p.PairNumber);
                    return new DualAudioPairResult
                    {
                        PairNumber = p.PairNumber, SourceAPageNumber = p.SourceAPageNumber, SourceAPageTitle = p.SourceAPageTitle,
                        SourceBPageNumber = p.SourceBPageNumber, SourceBPageTitle = p.SourceBPageTitle, MainVideoSource = p.MainVideoSource,
                        SourceAVideo = p.SourceA.Video, SourceAAudio = p.SourceA.Audio, SourceBVideo = p.SourceB.Video, SourceBAudio = p.SourceB.Audio,
                        SourceBDelayMs = p.SourceBDelayOverrideMs ?? dual.SourceBDelayMs,
                        State = unit?.Completed == true ? DualAudioPairState.Completed : unit?.Error.Length > 0 ? DualAudioPairState.Failed : DualAudioPairState.Pending,
                        SourceAFiles = unit?.SourceA.Files.Select(f => f.Path).ToList() ?? [], SourceBFiles = unit?.SourceB.Files.Select(f => f.Path).ToList() ?? [],
                        OutputFile = unit?.Completed == true ? unit.FinalPath : "", Error = unit?.Error ?? ""
                    };
                }).ToList()
            };
        }
        return record;
    }
}
