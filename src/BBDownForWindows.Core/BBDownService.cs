using System.Text;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class BBDownService(ApplicationPaths paths, IProcessRunner processRunner, IToolLocator toolLocator, ISettingsStore settingsStore) : IBBDownService
{
    public async Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var request = new DownloadRequest { Url = url, Pages = pages };
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildInfoArguments(request), context, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"BBDown 信息解析失败，退出码 {result.ExitCode}");
        return BBDownParser.ParseInfo(result.Output);
    }

    public async Task DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("视频 URL 不能为空");
        if (request.Season && request.Quality.Equals("4K", StringComparison.OrdinalIgnoreCase))
            await RunResolutionFirstSeasonAsync(request, context, cancellationToken);
        else if (ShouldSelectAudio(request))
            await RunAudioSelectionAsync(request, context, cancellationToken);
        else
            await RunDirectDownloadAsync(request, context, cancellationToken);
    }

    public async Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        try { if (File.Exists(paths.QrCodeFile)) File.Delete(paths.QrCodeFile); } catch (IOException) { }
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await RunAsync(tools.BBDown, [tv ? "logintv" : "login"], context, cancellationToken);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"登录流程失败，退出码 {result.ExitCode}");
    }

    public async Task<string> GetTitleAsync(string url, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await processRunner.RunAsync(new ProcessRunRequest(tools.BBDown, [url, "-info"], paths.RuntimeDirectory), null, cancellationToken);
        return BBDownParser.ParseInfo(result.Output).Title;
    }

    private async Task RunDirectDownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var tools = await ResolveToolsAsync(cancellationToken);
        var result = await RunAsync(tools.BBDown, BBDownCommandBuilder.BuildDownloadArguments(request, tools), context, cancellationToken);
        if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"下载失败，退出码 {result.ExitCode}");
        context.AppendLog("\n下载完成\n");
    }

    private async Task RunAudioSelectionAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
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
    }

    private async Task RunResolutionFirstSeasonAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
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
    }

    private async Task<ToolPaths> ResolveToolsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var tools = toolLocator.Locate(settings);
        if (string.IsNullOrWhiteSpace(tools.BBDown)) throw new FileNotFoundException("找不到 BBDown.exe");
        return tools;
    }

    private Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TaskExecutionContext context, CancellationToken cancellationToken, string? input = null)
    {
        context.AppendLog($"\n$ {Path.GetFileName(executable)} {string.Join(' ', arguments.Select(Quote))}\n");
        return processRunner.RunAsync(new ProcessRunRequest(executable, arguments, paths.RuntimeDirectory, input), line =>
        {
            if (!Regex.IsMatch(line.Trim(), "^https?://.*bilivideo\\.com", RegexOptions.IgnoreCase)) context.AppendLog(line);
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
        Aria2MaxConnection = source.Aria2MaxConnection, Aria2Split = source.Aria2Split,
        Aria2MaxConcurrentDownloads = source.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = source.Aria2MinSplitSize,
        SaveTaskLogs = source.SaveTaskLogs, ApiMode = source.ApiMode, Language = source.Language, MultiFilePattern = source.MultiFilePattern
    };

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
