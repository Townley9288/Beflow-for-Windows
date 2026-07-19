using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class DualAudioService(ApplicationPaths paths, IBBDownService bbdown, IProcessRunner processRunner, ISettingsStore settingsStore, IToolLocator toolLocator) : IDualAudioService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<DualAudioCatalog> ParseAsync(
        DualAudioParseRequest request,
        IProgress<DownloadParseProgress>? sourceAProgress,
        IProgress<DownloadParseProgress>? sourceBProgress,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceAUrl)) throw new ArgumentException("来源 A URL 不能为空");
        if (request.SourceMode == DualAudioSourceMode.Separate && string.IsNullOrWhiteSpace(request.SourceBUrl))
            throw new ArgumentException("来源 B URL 不能为空");

        if (request.SourceMode == DualAudioSourceMode.Interleaved)
            return await ParseInterleavedAsync(request, sourceAProgress, sourceBProgress, context, cancellationToken);

        var sourceAContext = context.WithPrefix("来源 A");
        var sourceBContext = context.WithPrefix("来源 B");
        if (request.OnlySource == DualAudioSource.A)
        {
            var sourceAOnly = await ParseSourceAsync(request.SourceAUrl, request.Mode, string.Empty, request.ApiMode, sourceAProgress, sourceAContext, cancellationToken);
            return new DualAudioCatalog
            {
                SourceMode = request.SourceMode,
                ParseMode = request.Mode,
                SourceAUrl = request.SourceAUrl.Trim(),
                SourceBUrl = request.SourceBUrl.Trim(),
                SourceA = sourceAOnly.Catalog,
                SourceAError = sourceAOnly.Error
            };
        }
        if (request.OnlySource == DualAudioSource.B)
        {
            var sourceBOnly = await ParseSourceAsync(request.SourceBUrl, request.Mode, string.Empty, request.ApiMode, sourceBProgress, sourceBContext, cancellationToken);
            return new DualAudioCatalog
            {
                SourceMode = request.SourceMode,
                ParseMode = request.Mode,
                SourceAUrl = request.SourceAUrl.Trim(),
                SourceBUrl = request.SourceBUrl.Trim(),
                SourceB = sourceBOnly.Catalog,
                SourceBError = sourceBOnly.Error
            };
        }
        var taskA = ParseSourceAsync(request.SourceAUrl, request.Mode, string.Empty, request.ApiMode, sourceAProgress, sourceAContext, cancellationToken);
        var taskB = ParseSourceAsync(request.SourceBUrl, request.Mode, string.Empty, request.ApiMode, sourceBProgress, sourceBContext, cancellationToken);
        await Task.WhenAll(taskA, taskB);
        var sourceA = await taskA;
        var sourceB = await taskB;
        return new DualAudioCatalog
        {
            SourceMode = request.SourceMode,
            ParseMode = request.Mode,
            SourceAUrl = request.SourceAUrl.Trim(),
            SourceBUrl = request.SourceBUrl.Trim(),
            SourceA = sourceA.Catalog,
            SourceB = sourceB.Catalog,
            SourceAError = sourceA.Error,
            SourceBError = sourceB.Error,
            Pairs = BuildPairs(sourceA.Catalog, sourceB.Catalog)
        };
    }

    public async Task<DualAudioBatchResult> DownloadAndMuxAsync(
        DualAudioBatchRequest request,
        IProgress<DualAudioProgressSnapshot>? progress,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBatch(request);
        var selectedPairs = request.Pairs.Where(item => item.IsSelected && item.SourceAPageNumber > 0 && item.SourceBPageNumber > 0).ToList();
        if (selectedPairs.Count == 0) throw new InvalidOperationException("没有选择已配对的分集");

        var settings = await settingsStore.LoadAsync(cancellationToken);
        var tools = toolLocator.Locate(new AppSettings { MkvmergePath = request.MkvmergePath, Aria2cPath = request.Options.Aria2cPath });
        if (string.IsNullOrWhiteSpace(tools.Mkvmerge)) throw new FileNotFoundException("找不到 mkvmerge.exe，请在设置中选择 MKVToolNix 路径");
        var root = string.IsNullOrWhiteSpace(request.WorkDirectory) ? settings.WorkDirectory : request.WorkDirectory;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先设置输出目录");
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var title = string.IsNullOrWhiteSpace(request.SourceATitle) ? await bbdown.GetTitleAsync(request.SourceAUrl, cancellationToken) : request.SourceATitle;
        var taskDirectory = CreateUniqueTaskDirectory(root, title);
        var sourceADirectory = Directory.CreateDirectory(Path.Combine(taskDirectory, "来源A")).FullName;
        var sourceBDirectory = Directory.CreateDirectory(Path.Combine(taskDirectory, "来源B")).FullName;
        var outputDirectory = Directory.CreateDirectory(Path.Combine(taskDirectory, "多音轨MKV")).FullName;
        var manifestPath = Path.Combine(taskDirectory, "dual-audio-task.json");
        var result = new DualAudioBatchResult
        {
            Title = title,
            TaskDirectory = taskDirectory,
            OutputDirectory = outputDirectory,
            ManifestPath = manifestPath,
            Pairs = selectedPairs.Select(CreatePendingResult).ToList()
        };
        context.AppendLog($"任务目录: {taskDirectory}\n");
        await WriteManifestAsync(manifestPath, request, result, CancellationToken.None);

        var completed = 0;
        for (var index = 0; index < selectedPairs.Count; index++)
        {
            var pair = selectedPairs[index];
            var pairResult = result.Pairs[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                pairResult.State = DualAudioPairState.Validating;
                Report(progress, DualAudioProgressPhase.Validating, completed, selectedPairs.Count, pair.PairNumber, null, string.Empty, string.Empty, "正在确认来源 A/B 规格");
                var sourceAContext = context.WithPrefix($"第 {pair.PairNumber} 对 / A");
                var sourceBContext = context.WithPrefix($"第 {pair.PairNumber} 对 / B");
                var parseA = bbdown.ParseEpisodeAsync(request.SourceAUrl, pair.SourceAPageNumber, request.ApiMode, sourceAContext, cancellationToken);
                var parseB = bbdown.ParseEpisodeAsync(request.SourceBUrl, pair.SourceBPageNumber, request.ApiMode, sourceBContext, cancellationToken);
                await Task.WhenAll(parseA, parseB);
                var episodeA = await parseA;
                var episodeB = await parseB;

                var resolveOptions = CloneDownloadRequest(request.Options);
                resolveOptions.DownloadMode = DownloadMode.VideoAndAudio;
                var decisionA = StreamSelectionPolicy.Resolve(episodeA, pair.SourceA, resolveOptions);
                var decisionB = StreamSelectionPolicy.Resolve(episodeB, pair.SourceB, resolveOptions);
                var selectionA = ToSelection(pair.SourceA, episodeA, decisionA);
                var selectionB = ToSelection(pair.SourceB, episodeB, decisionB);
                if (selectionA.Video is null || selectionA.Audio is null || selectionB.Video is null || selectionB.Audio is null)
                    throw new InvalidOperationException("来源 A/B 必须各自选择视频和音频规格");

                var recommendation = DualAudioRecommendationPolicy.Recommend(selectionA.Video, selectionB.Video, request.Options.Encoding);
                var mainSource = pair.MainVideoMode switch
                {
                    DualAudioMainVideoMode.SourceA => DualAudioSource.A,
                    DualAudioMainVideoMode.SourceB => DualAudioSource.B,
                    _ => recommendation.Source
                };
                pair.MainVideoSource = mainSource;
                pair.RecommendationReason = pair.MainVideoMode == DualAudioMainVideoMode.Recommended
                    ? recommendation.Reason
                    : $"已固定使用来源 {(mainSource == DualAudioSource.A ? "A" : "B")} 主视频";
                pairResult.MainVideoSource = mainSource;
                pairResult.RecommendationReason = pair.RecommendationReason;
                pairResult.SourceAVideo = selectionA.Video;
                pairResult.SourceAAudio = selectionA.Audio;
                pairResult.SourceBVideo = selectionB.Video;
                pairResult.SourceBAudio = selectionB.Audio;
                pairResult.SourceBDelayMs = pair.SourceBDelayOverrideMs ?? request.SourceBDelayMs;
                ValidateDelay(pairResult.SourceBDelayMs);
                context.AppendLog($"第 {pair.PairNumber} 对：{pair.RecommendationReason}\n");

                var sourceAStem = UniqueStem(sourceADirectory, $"[P{pair.PairNumber:00}]{pair.SourceAPageTitle}");
                var sourceBStem = UniqueStem(sourceBDirectory, $"[P{pair.PairNumber:00}]{pair.SourceBPageTitle}");
                ExactDownloadResult exactA;
                ExactDownloadResult exactB;
                if (mainSource == DualAudioSource.A)
                {
                    exactA = await DownloadSourceAsync(request, episodeA, selectionA, DownloadMode.VideoAndAudio, sourceADirectory, sourceAStem,
                        DualAudioSource.A, 0, completed, selectedPairs.Count, pair.PairNumber, progress, sourceAContext, cancellationToken);
                    exactB = await DownloadSourceAsync(request, episodeB, selectionB, DownloadMode.AudioOnly, sourceBDirectory, sourceBStem,
                        DualAudioSource.B, 1, completed, selectedPairs.Count, pair.PairNumber, progress, sourceBContext, cancellationToken);
                }
                else
                {
                    exactB = await DownloadSourceAsync(request, episodeB, selectionB, DownloadMode.VideoAndAudio, sourceBDirectory, sourceBStem,
                        DualAudioSource.B, 0, completed, selectedPairs.Count, pair.PairNumber, progress, sourceBContext, cancellationToken);
                    exactA = await DownloadSourceAsync(request, episodeA, selectionA, DownloadMode.AudioOnly, sourceADirectory, sourceAStem,
                        DualAudioSource.A, 1, completed, selectedPairs.Count, pair.PairNumber, progress, sourceAContext, cancellationToken);
                }
                pairResult.SourceAFiles = exactA.OutputFiles;
                pairResult.SourceBFiles = exactB.OutputFiles;

                var fullFile = FindDownloadedFile(mainSource == DualAudioSource.A ? exactA.OutputFiles : exactB.OutputFiles, requireVideo: true);
                var otherAudioFile = FindDownloadedFile(mainSource == DualAudioSource.A ? exactB.OutputFiles : exactA.OutputFiles, requireVideo: false);
                var output = UniqueOutputPath(outputDirectory, $"[P{pair.PairNumber:00}]{pair.SourceAPageTitle}", new HashSet<string>(result.OutputFiles, StringComparer.OrdinalIgnoreCase));
                pairResult.State = DualAudioPairState.Muxing;
                Report(progress, DualAudioProgressPhase.Muxing, completed, selectedPairs.Count, pair.PairNumber, null, string.Empty, string.Empty, "正在封装 MKV");
                var mux = await processRunner.RunAsync(new ProcessRunRequest(
                    tools.Mkvmerge,
                    BuildMkvmergeArguments(fullFile, otherAudioFile, output, request, mainSource, pairResult.SourceBDelayMs),
                    paths.RuntimeDirectory), context.AppendLog, cancellationToken);
                if (mux.Cancelled || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (mux.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("mkvmerge 封装失败");
                pairResult.OutputFile = output;
                pairResult.State = DualAudioPairState.Completed;
                result.OutputFiles.Add(output);
                completed++;
                if (!request.KeepSourceFiles)
                {
                    DeleteCreatedFiles(exactA.OutputFiles, sourceADirectory);
                    DeleteCreatedFiles(exactB.OutputFiles, sourceBDirectory);
                }
                Report(progress, DualAudioProgressPhase.Completed, completed, selectedPairs.Count, pair.PairNumber, 100, string.Empty, string.Empty, "本集封装完成");
            }
            catch (OperationCanceledException)
            {
                pairResult.State = DualAudioPairState.Cancelled;
                pairResult.Error = "任务已取消";
                result.Cancelled = true;
                foreach (var remaining in result.Pairs.Skip(index + 1))
                {
                    remaining.State = DualAudioPairState.Cancelled;
                    remaining.Error = "任务取消，尚未开始";
                }
                break;
            }
            catch (Exception exception)
            {
                pairResult.State = DualAudioPairState.Failed;
                pairResult.Error = exception.Message;
                completed++;
                context.AppendLog($"第 {pair.PairNumber} 对失败：{exception.Message}\n");
                Report(progress, DualAudioProgressPhase.Failed, completed, selectedPairs.Count, pair.PairNumber, null, string.Empty, string.Empty, "本集失败，继续下一对");
            }
            finally
            {
                await WriteManifestAsync(manifestPath, request, result, CancellationToken.None);
            }
        }

        Report(progress, result.Cancelled ? DualAudioProgressPhase.Cancelled : DualAudioProgressPhase.Completed,
            completed, selectedPairs.Count, 0, result.Cancelled ? null : 100, string.Empty, string.Empty,
            result.Cancelled ? "任务已取消" : $"封装完成：成功 {result.Succeeded}，失败 {result.Failed}");
        return result;
    }

    public async Task<DualAudioRemuxPreparation> InspectExistingAsync(string taskDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskDirectory) || !Directory.Exists(taskDirectory))
            return new DualAudioRemuxPreparation { Error = "请先选择有效的已有任务目录" };

        try
        {
            var manifestPath = Path.Combine(taskDirectory, "dual-audio-task.json");
            if (File.Exists(manifestPath))
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<DualAudioTaskManifest>(stream, ManifestJsonOptions, cancellationToken)
                               ?? throw new InvalidDataException("多音轨任务清单无效");
                if (!manifest.Request.KeepSourceFiles)
                    return new DualAudioRemuxPreparation { IsManifest = true, Error = "原任务未保留来源文件，无法直接重新封装" };

                var completed = manifest.Result.Pairs.Where(item => item.State == DualAudioPairState.Completed).ToList();
                if (completed.Count == 0)
                    return new DualAudioRemuxPreparation { IsManifest = true, Error = "任务清单中没有已完成且可重新封装的分集" };
                foreach (var pair in completed)
                {
                    var mainFiles = pair.MainVideoSource == DualAudioSource.A ? pair.SourceAFiles : pair.SourceBFiles;
                    var otherFiles = pair.MainVideoSource == DualAudioSource.A ? pair.SourceBFiles : pair.SourceAFiles;
                    _ = FindDownloadedFile(mainFiles.Where(File.Exists), requireVideo: true);
                    _ = FindDownloadedFile(otherFiles.Where(File.Exists), requireVideo: false);
                }

                var delays = completed.Select(item => item.SourceBDelayMs).Distinct().ToList();
                return new DualAudioRemuxPreparation
                {
                    CanRemux = true,
                    IsManifest = true,
                    SourceALabel = manifest.Request.SourceALabel,
                    SourceBLabel = manifest.Request.SourceBLabel,
                    SourceALanguage = manifest.Request.SourceALanguage,
                    SourceBLanguage = manifest.Request.SourceBLanguage,
                    DefaultAudioSource = manifest.Request.DefaultAudioSource,
                    SourceBDelayMs = manifest.Request.SourceBDelayMs,
                    HasPerPairDelays = delays.Count > 1 || delays.SingleOrDefault() != manifest.Request.SourceBDelayMs
                };
            }

            var primary = Path.Combine(taskDirectory, "主版本");
            var secondary = Path.Combine(taskDirectory, "副音轨");
            if (!Directory.Exists(primary) || !Directory.Exists(secondary))
                return new DualAudioRemuxPreparation { Error = "已有任务目录必须包含新任务清单，或旧版“主版本/副音轨”子目录" };
            try { _ = FindPairs(primary, secondary, DualAudioSourceMode.Separate); }
            catch (InvalidOperationException) { _ = FindPairs(primary, secondary, DualAudioSourceMode.Interleaved); }
            return new DualAudioRemuxPreparation { CanRemux = true };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DualAudioRemuxPreparation { IsManifest = File.Exists(Path.Combine(taskDirectory, "dual-audio-task.json")), Error = exception.Message };
        }
    }

    public async Task RemuxExistingAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        Validate(request, requireUrls: false);
        var preparation = await InspectExistingAsync(request.ExistingTaskDirectory, cancellationToken);
        if (!preparation.CanRemux) throw new InvalidOperationException(preparation.Error);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        settings.MkvmergePath = request.MkvmergePath;
        var mkvmerge = toolLocator.Locate(settings).Mkvmerge;
        if (string.IsNullOrWhiteSpace(mkvmerge)) throw new FileNotFoundException("找不到 mkvmerge.exe");
        var manifestPath = Path.Combine(request.ExistingTaskDirectory, "dual-audio-task.json");
        if (File.Exists(manifestPath))
        {
            await RemuxManifestAsync(manifestPath, request, mkvmerge, context, cancellationToken);
            return;
        }

        var primary = Path.Combine(request.ExistingTaskDirectory, "主版本");
        var secondary = Path.Combine(request.ExistingTaskDirectory, "副音轨");
        if (!Directory.Exists(primary) || !Directory.Exists(secondary))
            throw new DirectoryNotFoundException("已有任务目录必须包含新任务清单，或旧版“主版本/副音轨”子目录");
        var output = CreateUniqueRemuxDirectory(request.ExistingTaskDirectory, request.SecondaryAudioDelayMs);
        context.AppendLog("仅重新封装旧任务：不会启动 BBDown，也不会重新下载\n");
        await MuxLegacyDirectoriesAsync(primary, secondary, output, request, mkvmerge, context, cancellationToken);
    }

    public static List<DualAudioEpisodePair> BuildPairs(DownloadCatalog? sourceA, DownloadCatalog? sourceB)
    {
        var episodesA = sourceA?.Episodes.OrderBy(item => item.Page.Number).ToList() ?? [];
        var episodesB = sourceB?.Episodes.OrderBy(item => item.Page.Number).ToList() ?? [];
        var count = Math.Max(episodesA.Count, episodesB.Count);
        var pairs = new List<DualAudioEpisodePair>(count);
        for (var index = 0; index < count; index++)
        {
            var sourceAEpisode = index < episodesA.Count ? episodesA[index] : null;
            var sourceBEpisode = index < episodesB.Count ? episodesB[index] : null;
            pairs.Add(new DualAudioEpisodePair
            {
                PairNumber = index + 1,
                SourceA = sourceAEpisode,
                SourceB = sourceBEpisode,
                IsSelected = sourceAEpisode is not null && sourceBEpisode is not null
            });
        }
        return pairs;
    }

    public static List<DualAudioEpisodePair> BuildRestoredPairs(
        DownloadCatalog? sourceA,
        DownloadCatalog? sourceB,
        IEnumerable<DualAudioPairSelection> restoredSelections)
    {
        var episodesA = sourceA?.Episodes.OrderBy(item => item.Page.Number).ToList() ?? [];
        var episodesB = sourceB?.Episodes.OrderBy(item => item.Page.Number).ToList() ?? [];
        var availableB = episodesB.ToDictionary(item => item.Page.Number);
        var restoredByA = restoredSelections
            .Where(item => item.SourceAPageNumber > 0 && item.SourceBPageNumber > 0)
            .GroupBy(item => item.SourceAPageNumber)
            .ToDictionary(group => group.Key, group => group.First().SourceBPageNumber);
        var usedB = new HashSet<int>();
        var pairs = new List<DualAudioEpisodePair>();

        foreach (var episodeA in episodesA)
        {
            DownloadEpisodeInfo? episodeB = null;
            if (restoredByA.TryGetValue(episodeA.Page.Number, out var restoredPage)
                && availableB.TryGetValue(restoredPage, out var restoredEpisode)
                && usedB.Add(restoredPage))
                episodeB = restoredEpisode;
            pairs.Add(new DualAudioEpisodePair { SourceA = episodeA, SourceB = episodeB });
        }

        var remainingB = episodesB.Where(item => !usedB.Contains(item.Page.Number)).GetEnumerator();
        foreach (var pair in pairs.Where(item => item.SourceB is null))
        {
            if (!remainingB.MoveNext()) break;
            pair.SourceB = remainingB.Current;
            usedB.Add(remainingB.Current.Page.Number);
        }
        while (remainingB.MoveNext())
            pairs.Add(new DualAudioEpisodePair { SourceB = remainingB.Current });

        for (var index = 0; index < pairs.Count; index++)
        {
            pairs[index].PairNumber = index + 1;
            pairs[index].IsSelected = pairs[index].IsPaired;
        }
        return pairs;
    }

    public static IReadOnlyList<(string Primary, string Secondary)> FindPairs(string primaryDirectory, string secondaryDirectory, DualAudioSourceMode mode)
    {
        var primary = Directory.EnumerateFiles(primaryDirectory).Where(file => new[] { ".mp4", ".mkv" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Order().ToList();
        var secondary = Directory.EnumerateFiles(secondaryDirectory).Where(file => new[] { ".m4a", ".mp4", ".mkv", ".aac", ".flac", ".eac3", ".ac3", ".dts" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Order().ToList();
        if (mode == DualAudioSourceMode.Interleaved)
        {
            if (primary.Count != secondary.Count) throw new InvalidOperationException($"奇偶分P配对不完整：主版本 {primary.Count} 个，副音轨 {secondary.Count} 个");
            return primary.Zip(secondary).Select(pair => (pair.First, pair.Second)).ToList();
        }
        var primaryByPrefix = BuildEpisodeMap(primary, "主版本");
        var secondaryByPrefix = BuildEpisodeMap(secondary, "副音轨");
        var missingSecondary = primaryByPrefix.Keys.Except(secondaryByPrefix.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var missingPrimary = secondaryByPrefix.Keys.Except(primaryByPrefix.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (missingSecondary.Count > 0 || missingPrimary.Count > 0)
        {
            var details = new List<string>();
            if (missingSecondary.Count > 0) details.Add($"缺少副音轨 P{string.Join(",P", missingSecondary)}");
            if (missingPrimary.Count > 0) details.Add($"缺少主版本 P{string.Join(",P", missingPrimary)}");
            throw new InvalidOperationException($"独立链接配对不完整：{string.Join("；", details)}");
        }
        return primaryByPrefix.OrderBy(pair => int.Parse(pair.Key)).Select(pair => (pair.Value, secondaryByPrefix[pair.Key])).ToList();
    }

    public static IReadOnlyList<string> BuildMkvmergeArguments(string primary, string secondary, string output, DualAudioRequest request)
    {
        ValidateDelay(request.SecondaryAudioDelayMs);
        var primaryDefault = request.SecondaryIsDefault ? "no" : "yes";
        var secondaryDefault = request.SecondaryIsDefault ? "yes" : "no";
        var order = request.SecondaryIsDefault ? "0:0,1:0,0:1" : "0:0,0:1,1:0";
        var arguments = new List<string>
        {
            "-o", output, "--track-order", order,
            "--language", $"1:{request.PrimaryLanguage}", "--track-name", $"1:{request.PrimaryLabel}", "--default-track-flag", $"1:{primaryDefault}", primary,
            "--no-video"
        };
        if (request.SecondaryAudioDelayMs != 0) arguments.AddRange(["--sync", $"0:{request.SecondaryAudioDelayMs}"]);
        arguments.AddRange(["--language", $"0:{request.SecondaryLanguage}", "--track-name", $"0:{request.SecondaryLabel}", "--default-track-flag", $"0:{secondaryDefault}", secondary]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildMkvmergeArguments(string fullFile, string otherAudioFile, string output, DualAudioBatchRequest request, DualAudioSource mainSource, int sourceBDelayMs)
    {
        ValidateDelay(sourceBDelayMs);
        var defaultA = request.DefaultAudioSource == DualAudioSource.A ? "yes" : "no";
        var defaultB = request.DefaultAudioSource == DualAudioSource.B ? "yes" : "no";
        if (mainSource == DualAudioSource.A)
        {
            var arguments = new List<string>
            {
                "-o", output, "--track-order", "0:0,0:1,1:0",
                "--language", $"1:{request.SourceALanguage}", "--track-name", $"1:{request.SourceALabel}", "--default-track-flag", $"1:{defaultA}", fullFile,
                "--no-video"
            };
            if (sourceBDelayMs != 0) arguments.AddRange(["--sync", $"0:{sourceBDelayMs}"]);
            arguments.AddRange(["--language", $"0:{request.SourceBLanguage}", "--track-name", $"0:{request.SourceBLabel}", "--default-track-flag", $"0:{defaultB}", otherAudioFile]);
            return arguments;
        }
        else
        {
            var arguments = new List<string>
            {
                "-o", output, "--track-order", "0:0,1:0,0:1",
                "--language", $"1:{request.SourceBLanguage}", "--track-name", $"1:{request.SourceBLabel}", "--default-track-flag", $"1:{defaultB}", fullFile,
                "--no-video"
            };
            if (sourceBDelayMs != 0) arguments.AddRange(["--sync", $"0:{-sourceBDelayMs}"]);
            arguments.AddRange(["--language", $"0:{request.SourceALanguage}", "--track-name", $"0:{request.SourceALabel}", "--default-track-flag", $"0:{defaultA}", otherAudioFile]);
            return arguments;
        }
    }

    internal static (DownloadRequest Primary, DownloadRequest Secondary) BuildDownloadRequests(DualAudioRequest request, string primaryPages, string secondaryPages, string primaryDirectory, string secondaryDirectory)
    {
        var primary = BuildLegacyDownloadRequest(request, request.PrimaryUrl, primaryPages, primaryDirectory, DownloadMode.VideoAndAudio, request.PrimaryLanguage, request.PrimaryAudioCodec);
        var secondaryUrl = request.SourceMode == DualAudioSourceMode.Interleaved ? request.PrimaryUrl : request.SecondaryUrl;
        var secondary = BuildLegacyDownloadRequest(request, secondaryUrl, secondaryPages, secondaryDirectory, DownloadMode.AudioOnly, request.SecondaryLanguage, request.SecondaryAudioCodec);
        return (primary, secondary);
    }

    private async Task<DualAudioCatalog> ParseInterleavedAsync(DualAudioParseRequest request, IProgress<DownloadParseProgress>? progressA, IProgress<DownloadParseProgress>? progressB, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var prefixed = context.WithPrefix("奇偶分P");
        DownloadCatalog catalog;
        string error = string.Empty;
        try
        {
            if (request.Mode == DownloadParseMode.Current)
            {
                var current = await bbdown.ParseDownloadAsync(new DownloadParseRequest(request.SourceAUrl, DownloadParseMode.Current, request.ApiMode), null, prefixed, cancellationToken);
                var page = current.Episodes.First().Page.Number;
                var odd = page % 2 == 0 ? Math.Max(1, page - 1) : page;
                catalog = await bbdown.ParseDownloadAsync(new DownloadParseRequest(request.SourceAUrl, DownloadParseMode.All, request.ApiMode, $"{odd},{odd + 1}"),
                    new InlineProgress<DownloadParseProgress>(update => RouteInterleavedProgress(update, progressA, progressB)), prefixed, cancellationToken);
            }
            else
            {
                catalog = await bbdown.ParseDownloadAsync(new DownloadParseRequest(request.SourceAUrl, DownloadParseMode.All, request.ApiMode),
                    new InlineProgress<DownloadParseProgress>(update => RouteInterleavedProgress(update, progressA, progressB)), prefixed, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
            return new DualAudioCatalog
            {
                SourceMode = request.SourceMode,
                ParseMode = request.Mode,
                SourceAUrl = request.SourceAUrl.Trim(),
                SourceBUrl = request.SourceAUrl.Trim(),
                SourceAError = error,
                SourceBError = error
            };
        }

        var sourceA = FilterCatalog(catalog, item => item.Page.Number % 2 == 1);
        var sourceB = FilterCatalog(catalog, item => item.Page.Number % 2 == 0);
        return new DualAudioCatalog
        {
            SourceMode = request.SourceMode,
            ParseMode = request.Mode,
            SourceAUrl = request.SourceAUrl.Trim(),
            SourceBUrl = request.SourceAUrl.Trim(),
            SourceA = sourceA,
            SourceB = sourceB,
            SourceAError = error,
            SourceBError = error,
            Pairs = BuildPairs(sourceA, sourceB)
        };
    }

    private async Task<(DownloadCatalog? Catalog, string Error)> ParseSourceAsync(string url, DownloadParseMode mode, string pages, string apiMode, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return (await bbdown.ParseDownloadAsync(new DownloadParseRequest(url.Trim(), mode, apiMode, pages), progress, context, cancellationToken), string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.AppendLog($"解析失败：{exception.Message}\n");
            return (null, exception.Message);
        }
        catch (OperationCanceledException)
        {
            context.AppendLog("解析已取消，保留另一来源已完成的结果。\n");
            return (null, "解析已取消");
        }
    }

    private async Task<ExactDownloadResult> DownloadSourceAsync(DualAudioBatchRequest request, DownloadEpisodeInfo episode, EpisodeStreamSelection selection,
        DownloadMode mode, string directory, string stem, DualAudioSource source, int stageIndex, int completed, int total, int pairNumber,
        IProgress<DualAudioProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var options = CloneDownloadRequest(request.Options);
        options.Url = source == DualAudioSource.A ? request.SourceAUrl : request.SourceBUrl;
        options.WorkDirectory = directory;
        options.DownloadMode = mode;
        options.Language = source == DualAudioSource.A ? request.SourceALanguage : request.SourceBLanguage;
        options.Subtitle = false;
        options.Cover = false;
        options.Danmaku = false;
        var exactSelection = new EpisodeStreamSelection
        {
            PageNumber = selection.PageNumber,
            PageTitle = selection.PageTitle,
            Video = mode == DownloadMode.AudioOnly ? null : selection.Video,
            Audio = selection.Audio,
            FallbackReason = selection.FallbackReason
        };
        var phase = source == DualAudioSource.A ? DualAudioProgressPhase.DownloadingSourceA : DualAudioProgressPhase.DownloadingSourceB;
        return await bbdown.DownloadExactAsync(new ExactDownloadRequest
        {
            Options = options,
            Episode = episode,
            Selection = exactSelection,
            OutputDirectory = directory,
            RelativeOutputPath = stem
        }, new InlineProgress<ExactDownloadProgress>(update =>
        {
            var stageProgress = (update.Percent ?? 0) / 100d;
            var pairProgress = (stageIndex + stageProgress) / 3d;
            var overall = total == 0 ? 0 : Math.Clamp((completed + pairProgress) * 100d / total, 0, 100);
            progress?.Report(new DualAudioProgressSnapshot(phase, completed, total, pairNumber, overall, update.Percent, update.Speed, update.Eta,
                $"来源 {(source == DualAudioSource.A ? "A" : "B")}：{update.Message}"));
        }), context, cancellationToken);
    }

    private async Task RemuxManifestAsync(string manifestPath, DualAudioRequest legacyOverrides, string mkvmerge, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<DualAudioTaskManifest>(stream, ManifestJsonOptions, cancellationToken)
                       ?? throw new InvalidDataException("多音轨任务清单无效");
        var batch = manifest.Request;
        if (legacyOverrides.OverrideManifestAudioMetadata)
        {
            if (!string.IsNullOrWhiteSpace(legacyOverrides.PrimaryLabel)) batch.SourceALabel = legacyOverrides.PrimaryLabel;
            if (!string.IsNullOrWhiteSpace(legacyOverrides.SecondaryLabel)) batch.SourceBLabel = legacyOverrides.SecondaryLabel;
            if (!string.IsNullOrWhiteSpace(legacyOverrides.PrimaryLanguage)) batch.SourceALanguage = legacyOverrides.PrimaryLanguage;
            if (!string.IsNullOrWhiteSpace(legacyOverrides.SecondaryLanguage)) batch.SourceBLanguage = legacyOverrides.SecondaryLanguage;
            batch.DefaultAudioSource = legacyOverrides.SecondaryIsDefault ? DualAudioSource.B : DualAudioSource.A;
        }
        var completedPairs = manifest.Result.Pairs.Where(item => item.State == DualAudioPairState.Completed).ToList();
        if (completedPairs.Count == 0) throw new InvalidOperationException("任务清单中没有已完成且可重新封装的分集");
        var output = CreateUniqueRemuxDirectory(Path.GetDirectoryName(manifestPath)!,
            legacyOverrides.OverrideManifestDelay ? legacyOverrides.SecondaryAudioDelayMs : null);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in completedPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mainFiles = pair.MainVideoSource == DualAudioSource.A ? pair.SourceAFiles : pair.SourceBFiles;
            var otherFiles = pair.MainVideoSource == DualAudioSource.A ? pair.SourceBFiles : pair.SourceAFiles;
            var full = FindDownloadedFile(mainFiles.Where(File.Exists), requireVideo: true);
            var audio = FindDownloadedFile(otherFiles.Where(File.Exists), requireVideo: false);
            var target = UniqueOutputPath(output, $"[P{pair.PairNumber:00}]{pair.SourceAPageTitle}", reserved);
            var delay = legacyOverrides.OverrideManifestDelay ? legacyOverrides.SecondaryAudioDelayMs : pair.SourceBDelayMs;
            var result = await processRunner.RunAsync(new ProcessRunRequest(mkvmerge,
                BuildMkvmergeArguments(full, audio, target, batch, pair.MainVideoSource, delay), paths.RuntimeDirectory), context.AppendLog, cancellationToken);
            if (result.Cancelled || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(target)) throw new InvalidOperationException($"重新封装失败：第 {pair.PairNumber} 对");
        }
    }

    private async Task MuxLegacyDirectoriesAsync(string primaryDirectory, string secondaryDirectory, string outputDirectory, DualAudioRequest request, string mkvmerge, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var pairs = FindPairs(primaryDirectory, secondaryDirectory, request.SourceMode);
        if (pairs.Count == 0) throw new InvalidOperationException("没有找到可匹配的主视频/副音轨文件");
        Directory.CreateDirectory(outputDirectory);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var output = UniqueOutputPath(outputDirectory, Path.GetFileNameWithoutExtension(pair.Primary), reserved);
            var result = await processRunner.RunAsync(new ProcessRunRequest(mkvmerge, BuildMkvmergeArguments(pair.Primary, pair.Secondary, output, request), paths.RuntimeDirectory), context.AppendLog, cancellationToken);
            if (result.Cancelled || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException($"封装失败: {Path.GetFileName(pair.Primary)}");
        }
    }

    private static DualAudioPairResult CreatePendingResult(DualAudioPairSelection pair) => new()
    {
        PairNumber = pair.PairNumber,
        SourceAPageNumber = pair.SourceAPageNumber,
        SourceAPageTitle = pair.SourceAPageTitle,
        SourceBPageNumber = pair.SourceBPageNumber,
        SourceBPageTitle = pair.SourceBPageTitle,
        MainVideoSource = pair.MainVideoSource,
        SourceBDelayMs = pair.SourceBDelayOverrideMs ?? 0
    };

    private static EpisodeStreamSelection ToSelection(EpisodeStreamSelection desired, DownloadEpisodeInfo current, StreamSelectionDecision decision) => new()
    {
        PageNumber = current.Page.Number,
        PageTitle = current.Page.Title,
        Video = decision.Video is null ? null : new VideoStreamSelection(decision.Video.Quality, decision.Video.Resolution, decision.Video.Codec, decision.Video.BitrateKbps, desired.Video?.IsManual == true),
        Audio = decision.Audio is null ? null : new AudioStreamSelection(decision.Audio.Codec, decision.Audio.BitrateKbps, desired.Audio?.IsManual == true),
        FallbackReason = decision.FallbackReason
    };

    private static DownloadCatalog FilterCatalog(DownloadCatalog catalog, Func<DownloadEpisodeInfo, bool> predicate) => new()
    {
        SourceUrl = catalog.SourceUrl,
        Title = catalog.Title,
        Metadata = catalog.Metadata,
        ParsedAt = catalog.ParsedAt,
        AllPages = catalog.AllPages.Where(page => predicate(new DownloadEpisodeInfo { Page = page })).ToList(),
        Episodes = catalog.Episodes.Where(predicate).OrderBy(item => item.Page.Number).ToList()
    };

    private static void RouteInterleavedProgress(DownloadParseProgress update, IProgress<DownloadParseProgress>? progressA, IProgress<DownloadParseProgress>? progressB)
    {
        if (update.Episode?.Page.Number % 2 == 0) progressB?.Report(update);
        else progressA?.Report(update);
    }

    private static void Report(IProgress<DualAudioProgressSnapshot>? progress, DualAudioProgressPhase phase, int completed, int total, int currentPair,
        double? currentPercent, string speed, string eta, string message)
    {
        var pairProgress = phase == DualAudioProgressPhase.Muxing ? 2d / 3d : 0d;
        var overall = total == 0 ? 0 : Math.Clamp((completed + pairProgress) * 100d / total, 0, 100);
        progress?.Report(new DualAudioProgressSnapshot(phase, completed, total, currentPair, overall, currentPercent, speed, eta, message));
    }

    private static async Task WriteManifestAsync(string path, DualAudioBatchRequest request, DualAudioBatchResult result, CancellationToken cancellationToken) =>
        await AtomicJson.WriteAsync(path, new DualAudioTaskManifest { UpdatedAt = DateTimeOffset.Now, Request = request, Result = result }, ManifestJsonOptions, cancellationToken);

    private static string FindDownloadedFile(IEnumerable<string> files, bool requireVideo)
    {
        var existing = files.Where(File.Exists).ToList();
        var match = requireVideo ? existing.FirstOrDefault(DownloadFileKinds.IsVideoFile) : existing.FirstOrDefault();
        return match ?? throw new FileNotFoundException(requireVideo ? "没有找到下载完成的视频文件" : "没有找到下载完成的音频文件");
    }

    private static void DeleteCreatedFiles(IEnumerable<string> files, string allowedDirectory)
    {
        var root = Path.GetFullPath(allowedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreateUniqueTaskDirectory(string root, string title)
    {
        var safe = Sanitize(title);
        var baseName = $"{(string.IsNullOrWhiteSpace(safe) ? "多音轨封装" : safe)}_{DateTime.Now:yyyyMMdd_HHmmss}";
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(root, index == 1 ? baseName : $"{baseName}_{index}");
            if (Directory.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string UniqueStem(string directory, string stem)
    {
        var safe = SanitizeStem(stem);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? safe : $"{safe} ({index})";
            if (!Directory.EnumerateFiles(directory, candidate + ".*", SearchOption.TopDirectoryOnly).Any()) return candidate;
        }
    }

    private static string UniqueOutputPath(string directory, string stem, ISet<string> reserved)
    {
        var safe = SanitizeStem(stem);
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(directory, index == 1 ? $"{safe}.mkv" : $"{safe} ({index}).mkv");
            if (!File.Exists(path) && reserved.Add(path)) return path;
        }
    }

    private static string CreateUniqueRemuxDirectory(string root, int? delay)
    {
        var delayLabel = delay.HasValue
            ? $"延迟{(delay > 0 ? $"+{delay}" : delay.ToString())}ms"
            : "保留原参数";
        var name = $"多音轨MKV_{delayLabel}_{DateTime.Now:yyyyMMdd_HHmmss}";
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(root, index == 1 ? name : $"{name}_{index}");
            if (Directory.Exists(candidate)) continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private static DownloadRequest CloneDownloadRequest(DownloadRequest request) => new()
    {
        Url = request.Url, Pages = request.Pages, Season = request.Season, Quality = request.Quality, Encoding = request.Encoding,
        DownloadMode = request.DownloadMode, AudioCodec = request.AudioCodec, AudioBitratePriority = request.AudioBitratePriority,
        Danmaku = request.Danmaku, Subtitle = request.Subtitle, Cover = request.Cover, WorkDirectory = request.WorkDirectory,
        MultiThread = request.MultiThread, UposHost = request.UposHost, UseAria2c = request.UseAria2c, Aria2AutoTune = request.Aria2AutoTune,
        Aria2cPath = request.Aria2cPath, Aria2MaxConnection = request.Aria2MaxConnection, Aria2Split = request.Aria2Split,
        Aria2MaxConcurrentDownloads = request.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = request.Aria2MinSplitSize,
        SaveTaskLogs = request.SaveTaskLogs, ApiMode = request.ApiMode, Language = request.Language, MultiFilePattern = request.MultiFilePattern,
        OrganizeInTitleDirectory = request.OrganizeInTitleDirectory, TitleHint = request.TitleHint
    };

    private static DownloadRequest BuildLegacyDownloadRequest(DualAudioRequest request, string url, string pages, string directory, DownloadMode mode, string language, string audioCodec) => new()
    {
        Url = url, Pages = pages, Quality = request.Quality, Encoding = request.Encoding, DownloadMode = mode,
        AudioCodec = audioCodec, AudioBitratePriority = request.AudioBitratePriority, WorkDirectory = directory, Language = language,
        MultiFilePattern = "[P<pageNumberWithZero>]<pageTitle>", MultiThread = request.MultiThread, UposHost = request.UposHost,
        UseAria2c = request.UseAria2c, Aria2AutoTune = request.Aria2AutoTune, Aria2cPath = request.Aria2cPath, Aria2MaxConnection = request.Aria2MaxConnection,
        Aria2Split = request.Aria2Split, Aria2MaxConcurrentDownloads = request.Aria2MaxConcurrentDownloads,
        Aria2MinSplitSize = request.Aria2MinSplitSize, Subtitle = false, Cover = false, Danmaku = false
    };

    private static Dictionary<string, string> BuildEpisodeMap(IEnumerable<string> files, string label)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var prefix = Regex.Match(Path.GetFileName(file), "^\\[P(\\d+)\\]").Groups[1].Value;
            if (string.IsNullOrWhiteSpace(prefix)) throw new InvalidOperationException($"{label}文件缺少 [Pxx] 前缀：{Path.GetFileName(file)}");
            var normalized = int.Parse(prefix).ToString();
            if (!map.TryAdd(normalized, file)) throw new InvalidOperationException($"{label}存在重复分P：P{normalized}");
        }
        return map;
    }

    private static void ValidateBatch(DualAudioBatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceAUrl)) throw new ArgumentException("来源 A URL 不能为空");
        if (string.IsNullOrWhiteSpace(request.SourceBUrl)) throw new ArgumentException("来源 B URL 不能为空");
        ValidateDelay(request.SourceBDelayMs);
        foreach (var pair in request.Pairs.Where(item => item.SourceBDelayOverrideMs.HasValue)) ValidateDelay(pair.SourceBDelayOverrideMs!.Value);
    }

    private static void Validate(DualAudioRequest request, bool requireUrls)
    {
        ValidateDelay(request.SecondaryAudioDelayMs);
        if (!requireUrls) return;
        if (string.IsNullOrWhiteSpace(request.PrimaryUrl)) throw new ArgumentException("主版本 URL 不能为空");
        if (request.SourceMode == DualAudioSourceMode.Separate && string.IsNullOrWhiteSpace(request.SecondaryUrl)) throw new ArgumentException("副音轨 URL 不能为空");
    }

    private static void ValidateDelay(int delay)
    {
        if (delay is < -10000 or > 10000) throw new ArgumentOutOfRangeException(nameof(delay), "来源 B 延迟必须在 -10000 到 10000 毫秒之间");
    }

    private static string Sanitize(string value)
    {
        var safe = Regex.Replace(value ?? string.Empty, "[\\\\/:*?\"<>|\\x00-\\x1f]+", "_").Trim(' ', '.', '_');
        return safe.Length > 100 ? safe[..100].TrimEnd(' ', '.', '_') : safe;
    }

    private static string SanitizeStem(string value)
    {
        var safe = Regex.Replace(value ?? string.Empty, "[\\\\/:*?\"<>|\\x00-\\x1f]+", "_").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "多音轨视频";
        return safe.Length > 140 ? safe[..140].TrimEnd(' ', '.') : safe;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
