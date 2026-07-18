using System.Text;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class BBDownService(ApplicationPaths paths, IProcessRunner processRunner, IToolLocator toolLocator, ISettingsStore settingsStore,
    IBilibiliMetadataService? metadataService = null, IDownloadNamingService? downloadNamingService = null) : IBBDownService
{
    private readonly BBDownRuntimeManager _runtimeManager = new(paths);
    private readonly IDownloadNamingService _downloadNaming = downloadNamingService ?? new DownloadNamingService();

    public async Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var request = new DownloadRequest { Url = url, Pages = pages };
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(request), context, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"BBDown 信息解析失败，退出码 {result.ExitCode}");
        return BBDownParser.ParseInfo(result.Output);
    }

    public async Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("视频 URL 不能为空");
        var metadataTask = metadataService?.GetAsync(request.Url, cancellationToken) ?? Task.FromResult<BilibiliVideoMetadata?>(null);
        var tools = await ResolveToolsAsync(cancellationToken);
        var infoRequest = new DownloadRequest
        {
            Url = request.Url.Trim(),
            Season = request.Mode == DownloadParseMode.All && string.IsNullOrWhiteSpace(request.Pages),
            Pages = request.Pages,
            ApiMode = request.ApiMode
        };
        var expectedTotal = string.IsNullOrWhiteSpace(request.Pages) ? 0 : BBDownParser.ExpandPageExpression(request.Pages).Distinct().Count();
        var parser = new StreamingDownloadParser(request.Mode, progress, expectedTotal);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(infoRequest), context, cancellationToken, observer: parser.Consume);
        parser.Complete();
        if ((result.Cancelled || cancellationToken.IsCancellationRequested) && parser.Episodes.Count == 0) throw new OperationCanceledException(cancellationToken);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"BBDown 信息解析失败，退出码 {result.ExitCode}");
        if (parser.Episodes.Count == 0) throw new InvalidOperationException("没有解析到可下载的分集");
        var metadata = await metadataTask;
        if (metadataService is not null && metadata is null)
            context.AppendLog("B 站公开元数据暂不可用，命名规则中的账号、编号或发布时间字段可能留空。\n");
        var title = !string.IsNullOrWhiteSpace(parser.Title) ? parser.Title : metadata?.Title ?? string.Empty;
        return new DownloadCatalog
        {
            SourceUrl = request.Url.Trim(),
            Title = title,
            Metadata = metadata,
            ParsedAt = DateTimeOffset.Now,
            AllPages = parser.Pages.ToList(),
            Episodes = parser.Episodes.OrderBy(item => item.Page.Number).ToList()
        };
    }

    public async Task<DownloadBatchResult> DownloadBatchAsync(DownloadBatchRequest request, IProgress<DownloadProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (request.Episodes.Count == 0) throw new InvalidOperationException("没有选择要下载的分集");
        var tools = await ResolveToolsAsync(cancellationToken);
        var title = string.IsNullOrWhiteSpace(request.Title) ? request.Options.TitleHint : request.Title;
        if (string.IsNullOrWhiteSpace(title)) title = await GetTitleAsync(request.Options.Url, cancellationToken);
        if (string.IsNullOrWhiteSpace(title)) title = "Beflow 下载";
        var baseDirectory = string.IsNullOrWhiteSpace(request.Options.WorkDirectory)
            ? (await settingsStore.LoadAsync(cancellationToken)).WorkDirectory
            : request.Options.WorkDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory)) throw new InvalidOperationException("请先设置下载目录");
        baseDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(baseDirectory);
        var profile = (request.NamingProfile ?? DownloadNamingProfile.Default()).Clone();
        var profileKind = request.NamingProfileKind;
        var first = request.Episodes[0];
        var outputDirectory = _downloadNaming.ResolveMainDirectory(new DownloadNamingContext
        {
            RootDirectory = baseDirectory,
            SourceUrl = request.Options.Url,
            VideoTitle = title,
            Page = new PageInfo(first.PageNumber, string.Empty, first.PageTitle, string.Empty),
            Profile = profile,
            ProfileKind = profileKind,
            TotalPages = Math.Max(request.TotalPages, request.Episodes.Count),
            DownloadMode = request.Options.DownloadMode,
            ApiMode = request.Options.ApiMode,
            DownloadedAt = request.DownloadedAt,
            Metadata = request.Metadata
        });
        Directory.CreateDirectory(outputDirectory);
        context.AppendLog($"输出主文件夹: {outputDirectory}\n");

        var episodeResults = request.Episodes.Select(item => new DownloadEpisodeResult
        {
            PageNumber = item.PageNumber,
            PageTitle = item.PageTitle,
            State = DownloadEpisodeResultState.Pending,
            Video = item.Video,
            Audio = item.Audio,
            FallbackReason = item.FallbackReason,
            RelativeOutputPath = item.RelativeOutputPath
        }).ToList();
        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var cancelled = false;

        for (var index = 0; index < request.Episodes.Count; index++)
        {
            var desired = request.Episodes[index];
            var episodeResult = episodeResults[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                episodeResult.State = DownloadEpisodeResultState.Validating;
                ReportProgress(progress, DownloadProgressPhase.Validating, completed, request.Episodes.Count, desired, 0, null, string.Empty, string.Empty, "正在确认所选规格");
                var current = await ParseExactPageAsync(request.Options.Url, desired.PageNumber, request.Options.ApiMode, context, cancellationToken);
                var decision = StreamSelectionPolicy.Resolve(current, desired, request.Options);
                episodeResult.Video = decision.Video is null ? null : new VideoStreamSelection(decision.Video.Quality, decision.Video.Resolution, decision.Video.Codec, decision.Video.BitrateKbps, desired.Video?.IsManual == true);
                episodeResult.Audio = decision.Audio is null ? null : new AudioStreamSelection(decision.Audio.Codec, decision.Audio.BitrateKbps, desired.Audio?.IsManual == true);
                episodeResult.FallbackReason = decision.FallbackReason;

                var outputPlan = _downloadNaming.BuildPlan(new DownloadNamingContext
                {
                    RootDirectory = baseDirectory,
                    SourceUrl = request.Options.Url,
                    VideoTitle = title,
                    Page = current.Page,
                    Profile = profile,
                    ProfileKind = profileKind,
                    TotalPages = Math.Max(request.TotalPages, request.Episodes.Count),
                    DownloadMode = request.Options.DownloadMode,
                    ApiMode = request.Options.ApiMode,
                    DownloadedAt = request.DownloadedAt,
                    Metadata = request.Metadata,
                    Video = decision.Video,
                    Audio = decision.Audio,
                    PreferredRelativePath = desired.RelativeOutputPath,
                    AllowPartialReuse = !string.IsNullOrWhiteSpace(desired.RelativeOutputPath)
                }, reservedPaths);
                Directory.CreateDirectory(outputPlan.LeafDirectory);
                episodeResult.RelativeOutputPath = outputPlan.RelativePath;
                episodeResult.OutputDirectory = outputPlan.LeafDirectory;
                foreach (var warning in outputPlan.Warnings) context.AppendLog($"P{desired.PageNumber} 命名提示：{warning}\n");
                context.AppendLog($"P{desired.PageNumber} 输出: {outputPlan.RelativePath}\n");

                var part = Clone(request.Options);
                part.Pages = desired.PageNumber.ToString();
                part.Season = false;
                part.WorkDirectory = baseDirectory;
                part.OrganizeInTitleDirectory = false;
                part.TitleHint = title;
                part.MultiFilePattern = outputPlan.RelativePath;
                if (part.UseAria2c)
                {
                    var largestStream = Math.Max(decision.Video?.EstimatedSizeBytes ?? 0, decision.Audio?.EstimatedSizeBytes ?? 0);
                    var tuning = Aria2TuningPolicy.Apply(part, largestStream);
                    if (tuning.Applied) context.AppendLog(tuning.Description + "\n");
                }
                var arguments = BBDownCommandBuilder.BuildExactDownloadArguments(part, tools);
                var input = BuildExactInput(decision.Video, decision.Audio, part.DownloadMode);
                var before = SnapshotDirectory(outputPlan.LeafDirectory);
                var aria = new Aria2ProgressParser();
                var internalProgress = new BBDownInternalProgressParser(
                    decision.Video?.EstimatedSizeBytes ?? 0,
                    decision.Audio?.EstimatedSizeBytes ?? 0,
                    part.DownloadMode);
                var observerGate = new object();
                episodeResult.State = DownloadEpisodeResultState.Downloading;
                var downloadMessage = part.UseAria2c ? "正在使用 aria2c 下载" : "正在使用 BBDown 内置下载器下载";
                ReportProgress(progress, DownloadProgressPhase.Downloading, completed, request.Episodes.Count, desired, 0, 0, string.Empty, string.Empty, downloadMessage);
                var process = await RunAsync(tools.BBDown, arguments, context, cancellationToken, input, line =>
                {
                    lock (observerGate)
                    {
                        var hasTransfer = part.UseAria2c
                            ? aria.TryConsume(line, out var transfer)
                            : internalProgress.TryConsume(line, out transfer);
                        if (hasTransfer)
                        {
                            episodeResult.State = DownloadEpisodeResultState.Downloading;
                            ReportProgress(progress, DownloadProgressPhase.Downloading, completed, request.Episodes.Count, desired,
                                transfer.Percent, transfer.Percent, transfer.Speed, transfer.Eta, downloadMessage);
                        }
                        else if (line.Contains("合并音视频", StringComparison.Ordinal) || line.Contains("混流", StringComparison.Ordinal))
                        {
                            episodeResult.State = DownloadEpisodeResultState.Muxing;
                            ReportProgress(progress, DownloadProgressPhase.Muxing, completed, request.Episodes.Count, desired, 100, null, string.Empty, string.Empty, "正在合并音视频");
                        }
                    }
                }, usePseudoConsole: !part.UseAria2c,
                    shouldLog: line => part.UseAria2c || !BBDownInternalProgressParser.IsProgressOutput(line));
                if (process.Cancelled || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (process.ExitCode != 0) throw new InvalidOperationException($"BBDown 下载失败，退出码 {process.ExitCode}");
                episodeResult.OutputFiles = FindChangedFiles(outputPlan.LeafDirectory, before).ToList();
                foreach (var file in episodeResult.OutputFiles) allFiles.Add(file);
                episodeResult.State = DownloadEpisodeResultState.Completed;
                completed++;
                ReportProgress(progress, DownloadProgressPhase.Completed, completed, request.Episodes.Count, desired, 100, 100, string.Empty, string.Empty, $"P{desired.PageNumber} 下载完成");
            }
            catch (OperationCanceledException)
            {
                episodeResult.State = DownloadEpisodeResultState.Cancelled;
                episodeResult.Error = "任务已取消";
                cancelled = true;
                for (var remaining = index + 1; remaining < episodeResults.Count; remaining++)
                {
                    episodeResults[remaining].State = DownloadEpisodeResultState.Cancelled;
                    episodeResults[remaining].Error = "任务取消，尚未开始";
                }
                break;
            }
            catch (Exception exception)
            {
                episodeResult.State = DownloadEpisodeResultState.Failed;
                episodeResult.Error = exception.Message;
                completed++;
                context.AppendLog($"\nP{desired.PageNumber} 下载失败：{exception.Message}\n");
                ReportProgress(progress, DownloadProgressPhase.Failed, completed, request.Episodes.Count, desired, 100, null, string.Empty, string.Empty, $"P{desired.PageNumber} 下载失败，继续下一集");
            }
        }

        var finalPhase = cancelled ? DownloadProgressPhase.Cancelled : DownloadProgressPhase.Completed;
        progress?.Report(new DownloadProgressSnapshot(finalPhase, completed, request.Episodes.Count, 0, string.Empty,
            request.Episodes.Count == 0 ? 0 : completed * 100d / request.Episodes.Count, null, string.Empty, string.Empty,
            cancelled ? "下载任务已取消" : "批量下载完成"));
        var videoDirectories = allFiles.Where(DownloadFileKinds.IsVideoFile)
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DownloadBatchResult
        {
            Title = title,
            OutputDirectory = outputDirectory,
            Episodes = episodeResults,
            OutputFiles = allFiles.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            RenameDirectory = videoDirectories.Count == 1 ? videoDirectories[0]! : string.Empty
        };
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("视频 URL 不能为空");
        var effectiveRequest = Clone(request);
        var title = request.TitleHint.Trim();
        var outputDirectory = request.WorkDirectory;
        if (request.OrganizeInTitleDirectory)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                context.AppendLog("正在解析标题并准备独立下载文件夹…\n");
                var tools = await ResolveToolsAsync(cancellationToken);
                var info = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(request), context, cancellationToken);
                if (info.ExitCode != 0) throw new InvalidOperationException($"下载前标题解析失败，退出码 {info.ExitCode}");
                title = BBDownParser.ParseInfo(info.Output).Title;
            }
            if (string.IsNullOrWhiteSpace(title)) title = "Beflow 下载";
            var baseDirectory = request.WorkDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = (await settingsStore.LoadAsync(cancellationToken)).WorkDirectory;
            outputDirectory = ResolveTitleDirectory(baseDirectory, title);
            Directory.CreateDirectory(outputDirectory);
            effectiveRequest.WorkDirectory = outputDirectory;
            if (string.IsNullOrWhiteSpace(effectiveRequest.MultiFilePattern))
                effectiveRequest.MultiFilePattern = "[P<pageNumberWithZero>]<pageTitle>";
            context.AppendLog($"输出文件夹: {outputDirectory}\n");
        }

        var before = SnapshotDirectory(outputDirectory);
        string downloadedTitle;
        if (effectiveRequest.Season && effectiveRequest.Quality.Equals("4K", StringComparison.OrdinalIgnoreCase))
            downloadedTitle = await RunResolutionFirstSeasonAsync(effectiveRequest, context, cancellationToken);
        else if (ShouldSelectAudio(effectiveRequest))
            downloadedTitle = await RunAudioSelectionAsync(effectiveRequest, context, cancellationToken);
        else
            downloadedTitle = await RunDirectDownloadAsync(effectiveRequest, context, cancellationToken);

        if (!string.IsNullOrWhiteSpace(downloadedTitle)) title = downloadedTitle;
        var outputFiles = FindChangedFiles(outputDirectory, before);
        return new DownloadResult(
            title,
            outputDirectory,
            outputFiles,
            outputFiles.Any(IsVideoFile));
    }

    public async Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        try { if (File.Exists(paths.QrCodeFile)) File.Delete(paths.QrCodeFile); } catch (IOException) { }
        var tools = await ResolveToolsAsync(cancellationToken);
        var arguments = new[] { tv ? "logintv" : "login" };
        context.AppendLog($"\n$ {Path.GetFileName(tools.BBDown)} {arguments[0]}\n");
        var result = await processRunner.RunAsync(new ProcessRunRequest(tools.BBDown, arguments, paths.RuntimeDirectory), line => context.AppendLog(SanitizeLoginOutput(line)), cancellationToken);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"登录流程失败，退出码 {result.ExitCode}");
    }

    internal static string SanitizeLoginOutput(string line) => Regex.Replace(
        line,
        "(?i)(SESSDATA|AccessToken|access_token)\\s*=\\s*[^\\s;&]+",
        "$1=[已隐藏]");

    public async Task<string> GetTitleAsync(string url, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await processRunner.RunAsync(new ProcessRunRequest(tools.BBDown, [url, "-info"], paths.RuntimeDirectory), null, cancellationToken);
        return BBDownParser.ParseInfo(result.Output).Title;
    }

    private async Task<string> RunDirectDownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildDownloadArguments(request, tools), context, cancellationToken);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"下载失败，退出码 {result.ExitCode}");
        context.AppendLog("\n下载完成\n");
        return BBDownParser.ParseInfo(result.Output).Title;
    }

    private async Task<string> RunAudioSelectionAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        context.AppendLog($"正在分析音频流，目标格式: {request.AudioCodec}...\n");
        var info = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(request), context, cancellationToken);
        if (info.ExitCode != 0) throw new InvalidOperationException($"音频格式分析失败，退出码 {info.ExitCode}");
        var pages = BBDownParser.ParseSelectedPages(info.Output);
        if (pages.Count == 0) throw new InvalidOperationException("无法确认实际选择的分P");
        var pageAudio = BBDownParser.ParsePageAudioStreams(info.Output);
        var selection = BBDownParser.SelectPreferredAudioIndices(pageAudio, pages, request.AudioCodec);
        AppendAudioPlan(context, pageAudio, pages, selection.Indices, request.AudioCodec, selection.FallbackPages);
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(request, tools);
        arguments.Add("--interactive");
        var input = BBDownParser.BuildInteractiveInput(pages, selection.Indices, request.DownloadMode == DownloadMode.AudioOnly);
        var result = await RunAsync(tools.BBDown, arguments, context, cancellationToken, input);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"下载失败，退出码 {result.ExitCode}");
        context.AppendLog("\n下载完成\n");
        return BBDownParser.ParseInfo(info.Output).Title;
    }

    private async Task<string> RunResolutionFirstSeasonAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        context.AppendLog("正在分析整季每一集的实际分辨率和编码...\n");
        var infoRequest = Clone(request); infoRequest.Season = true;
        var info = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(infoRequest), context, cancellationToken);
        if (info.ExitCode != 0) throw new InvalidOperationException($"整季规格分析失败，退出码 {info.ExitCode}");
        var pageStreams = BBDownParser.ParsePageVideoStreams(info.Output);
        var expected = BBDownParser.ParseExpectedSeasonPages(info.Output);
        if (expected.Count == 0) throw new InvalidOperationException("无法确认整季分P数量");
        var missing = expected.Where(page => !pageStreams.TryGetValue(page, out var streams) || streams.Count == 0).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"以下分P缺少视频规格: {string.Join(',', missing.Select(page => $"P{page}"))}");
        var groups = BBDownParser.BuildResolutionGroups(pageStreams, request.Encoding);
        if (groups.Sum(group => group.Pages.Count) != expected.Count) throw new InvalidOperationException("部分分P没有不超过 3840x2160 的可用视频流");

        Dictionary<int, int>? audioIndices = null;
        if (ShouldSelectAudio(request))
        {
            var pageAudio = BBDownParser.ParsePageAudioStreams(info.Output);
            var selection = BBDownParser.SelectPreferredAudioIndices(pageAudio, expected, request.AudioCodec);
            audioIndices = selection.Indices;
            AppendAudioPlan(context, pageAudio, expected, selection.Indices, request.AudioCodec, selection.FallbackPages);
        }

        context.AppendLog("\n=== 整季分辨率优先选流结果 ===\n");
        foreach (var group in groups) context.AppendLog($"{group.Resolution} / {group.Quality} / {group.Codec}: P{string.Join(',', group.Pages)}\n");
        foreach (var group in groups)
        {
            var part = Clone(request);
            part.Season = false;
            part.Pages = string.Join(',', group.Pages);
            part.Quality = group.Quality;
            part.Encoding = group.Codec;
            var arguments = BBDownCommandBuilder.BuildDownloadArguments(part, tools);
            string? input = null;
            if (audioIndices is not null)
            {
                arguments.Add("--interactive");
                input = BBDownParser.BuildInteractiveInput(group.Pages, audioIndices, request.DownloadMode == DownloadMode.AudioOnly);
            }
            context.AppendLog($"\n=== 下载 {group.Resolution} / {group.Codec}: P{part.Pages} ===\n");
            var result = await RunAsync(tools.BBDown, arguments, context, cancellationToken, input);
            if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"分组下载失败: P{part.Pages}");
        }
        context.AppendLog("\n整季分辨率优先下载完成\n");
        return BBDownParser.ParseInfo(info.Output).Title;
    }

    private async Task<ToolPaths> ResolveToolsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var tools = toolLocator.Locate(settings);
        if (string.IsNullOrWhiteSpace(tools.BBDown)) throw new FileNotFoundException("找不到 BBDown.exe");
        tools.BBDown = _runtimeManager.PrepareExecutable(tools.BBDown);
        return tools;
    }

    private async Task<DownloadEpisodeInfo> ParseExactPageAsync(string url, int page, string apiMode, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        var request = new DownloadRequest { Url = url, Pages = page.ToString(), ApiMode = apiMode };
        var parser = new StreamingDownloadParser(DownloadParseMode.Current, null);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(request), context, cancellationToken, observer: parser.Consume);
        parser.Complete();
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"P{page} 规格确认失败，退出码 {result.ExitCode}");
        return parser.Episodes.FirstOrDefault(item => item.Page.Number == page)
               ?? throw new InvalidOperationException($"P{page} 没有解析到可用规格");
    }

    private static string BuildExactInput(VideoStreamInfo? video, AudioStreamInfo? audio, DownloadMode mode) => mode switch
    {
        DownloadMode.VideoOnly when video is not null => $"{video.Index}\n",
        DownloadMode.AudioOnly when audio is not null => $"{audio.Index}\n",
        DownloadMode.VideoAndAudio when video is not null && audio is not null => $"{video.Index}\n{audio.Index}\n",
        _ => throw new InvalidOperationException("所选下载类型缺少对应的视频或音频流")
    };

    private static void ReportProgress(IProgress<DownloadProgressSnapshot>? progress, DownloadProgressPhase phase, int completed, int total,
        EpisodeStreamSelection episode, double currentContribution, double? currentPercent, string speed, string eta, string message)
    {
        var overall = total <= 0 ? 0 : Math.Clamp((completed + currentContribution / 100d) * 100d / total, 0, 100);
        progress?.Report(new DownloadProgressSnapshot(phase, completed, total, episode.PageNumber, episode.PageTitle, overall, currentPercent, speed, eta, message));
    }

    private Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TaskExecutionContext context,
        CancellationToken cancellationToken, string? input = null, Action<string>? observer = null,
        bool usePseudoConsole = false, Func<string, bool>? shouldLog = null)
    {
        context.AppendLog($"\n$ {Path.GetFileName(executable)} {string.Join(' ', arguments.Select(Quote))}\n");
        return processRunner.RunAsync(new ProcessRunRequest(executable, arguments, paths.RuntimeDirectory, input, usePseudoConsole), line =>
        {
            observer?.Invoke(line);
            if ((shouldLog?.Invoke(line) ?? true) &&
                !Regex.IsMatch(line.Trim(), "^https?://.*bilivideo\\.com", RegexOptions.IgnoreCase)) context.AppendLog(line);
        }, cancellationToken);
    }

    private static bool ShouldSelectAudio(DownloadRequest request) =>
        !request.AudioCodec.Equals("auto", StringComparison.OrdinalIgnoreCase) && request.DownloadMode != DownloadMode.VideoOnly;

    private static void AppendAudioPlan(TaskExecutionContext context, Dictionary<int, List<AudioStreamInfo>> streams, IEnumerable<int> pages, IReadOnlyDictionary<int, int> indices, string codec, IEnumerable<int> fallback)
    {
        var fallbackSet = fallback.ToHashSet();
        context.AppendLog($"\n=== 音频选择结果（目标格式: {codec}）===\n");
        foreach (var page in pages)
        {
            var selected = streams[page].First(item => item.Index == indices[page]);
            context.AppendLog($"P{page}: {selected.Codec} {selected.Bitrate} / 音频序号 {selected.Index}{(fallbackSet.Contains(page) ? $"（没有 {codec}，已自动回退）" : string.Empty)}\n");
        }
    }

    private static DownloadRequest Clone(DownloadRequest source) => new()
    {
        Url = source.Url, Pages = source.Pages, Season = source.Season, Quality = source.Quality, Encoding = source.Encoding,
        DownloadMode = source.DownloadMode, AudioCodec = source.AudioCodec, AudioBitratePriority = source.AudioBitratePriority,
        Danmaku = source.Danmaku, Subtitle = source.Subtitle, Cover = source.Cover, WorkDirectory = source.WorkDirectory,
        MultiThread = source.MultiThread, UposHost = source.UposHost, UseAria2c = source.UseAria2c, Aria2cPath = source.Aria2cPath,
        Aria2AutoTune = source.Aria2AutoTune,
        Aria2MaxConnection = source.Aria2MaxConnection, Aria2Split = source.Aria2Split,
        Aria2MaxConcurrentDownloads = source.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = source.Aria2MinSplitSize,
        SaveTaskLogs = source.SaveTaskLogs, ApiMode = source.ApiMode, Language = source.Language, MultiFilePattern = source.MultiFilePattern,
        OrganizeInTitleDirectory = source.OrganizeInTitleDirectory, TitleHint = source.TitleHint
    };

    private static Dictionary<string, FileStamp> SnapshotDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
                .ToDictionary(file => file.FullName, file => new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks), StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { return new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase); }
        catch (UnauthorizedAccessException) { return new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase); }
    }

    private static IReadOnlyList<string> FindChangedFiles(string directory, IReadOnlyDictionary<string, FileStamp> before)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [];
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !before.TryGetValue(file.FullName, out var previous) || previous.Length != file.Length || previous.LastWriteTicks != file.LastWriteTimeUtc.Ticks)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string ResolveTitleDirectory(string baseDirectory, string title)
    {
        var folderName = RenameService.SanitizeTitleDirectoryName(title);
        var available = 235 - Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar).Length - 1;
        if (available < 8) throw new PathTooLongException("下载目录路径过长，无法创建安全的片名文件夹");
        available = Math.Min(120, available);
        if (folderName.Length > available) folderName = folderName[..available].TrimEnd('.', ' ');
        var candidate = Path.Combine(baseDirectory, folderName);
        if (!File.Exists(candidate)) return candidate;
        for (var index = 2; index < 1000; index++)
        {
            var suffix = $" ({index})";
            var stem = folderName.Length + suffix.Length > available ? folderName[..Math.Max(1, available - suffix.Length)].TrimEnd('.', ' ') : folderName;
            candidate = Path.Combine(baseDirectory, stem + suffix);
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("无法为下载任务创建可用的片名文件夹");
    }

    private static bool IsVideoFile(string path) => Path.GetExtension(path) is var extension && (
        extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webm", StringComparison.OrdinalIgnoreCase));

    private sealed record FileStamp(long Length, long LastWriteTicks);

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
