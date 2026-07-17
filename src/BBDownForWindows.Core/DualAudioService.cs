using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class DualAudioService(ApplicationPaths paths, IBBDownService bbdown, IProcessRunner processRunner, ISettingsStore settingsStore, IToolLocator toolLocator) : IDualAudioService
{
    public async Task<string> DownloadAndMuxAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        Validate(request, requireUrls: true);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var tools = toolLocator.Locate(new AppSettings { MkvmergePath = request.MkvmergePath, Aria2cPath = request.Aria2cPath });
        if (string.IsNullOrWhiteSpace(tools.Mkvmerge)) throw new FileNotFoundException("找不到 mkvmerge.exe，请在设置中选择 MKVToolNix 路径");
        var title = await bbdown.GetTitleAsync(request.PrimaryUrl, cancellationToken);
        var taskDirectory = Path.Combine(string.IsNullOrWhiteSpace(request.WorkDirectory) ? settings.WorkDirectory : request.WorkDirectory, BuildTaskName(title));
        var primaryDirectory = Path.Combine(taskDirectory, "主版本");
        var secondaryDirectory = Path.Combine(taskDirectory, "副音轨");
        var outputDirectory = Path.Combine(taskDirectory, "多音轨MKV");
        Directory.CreateDirectory(primaryDirectory);
        Directory.CreateDirectory(secondaryDirectory);
        context.AppendLog($"任务目录: {taskDirectory}\n");

        var (primaryPages, secondaryPages) = await ResolvePagesAsync(request, context, cancellationToken);
        var (primary, secondary) = BuildDownloadRequests(request, primaryPages, secondaryPages, primaryDirectory, secondaryDirectory);
        context.AppendLog($"音频格式: 主音轨 {DisplayAudioCodec(request.PrimaryAudioCodec)} / 副音轨 {DisplayAudioCodec(request.SecondaryAudioCodec)}\n");
        context.AppendLog("\n=== 下载主版本完整视频 ===\n");
        await bbdown.DownloadAsync(primary, context, cancellationToken);
        context.AppendLog("\n=== 下载副版本音轨 ===\n");
        await bbdown.DownloadAsync(secondary, context, cancellationToken);
        await MuxDirectoriesAsync(primaryDirectory, secondaryDirectory, outputDirectory, request, tools.Mkvmerge, context, cancellationToken);
        return title;
    }

    public async Task RemuxExistingAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        Validate(request, requireUrls: false);
        if (string.IsNullOrWhiteSpace(request.ExistingTaskDirectory) || !Directory.Exists(request.ExistingTaskDirectory))
            throw new DirectoryNotFoundException("请先选择有效的已有任务目录");
        var primary = Path.Combine(request.ExistingTaskDirectory, "主版本");
        var secondary = Path.Combine(request.ExistingTaskDirectory, "副音轨");
        if (!Directory.Exists(primary) || !Directory.Exists(secondary)) throw new DirectoryNotFoundException("已有任务目录必须包含“主版本”和“副音轨”子目录");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        settings.MkvmergePath = request.MkvmergePath;
        var mkvmerge = toolLocator.Locate(settings).Mkvmerge;
        if (string.IsNullOrWhiteSpace(mkvmerge)) throw new FileNotFoundException("找不到 mkvmerge.exe");
        var output = CreateUniqueRemuxDirectory(request.ExistingTaskDirectory, request.SecondaryAudioDelayMs);
        context.AppendLog("仅重新封装：不会启动 BBDown，也不会重新下载\n");
        await MuxDirectoriesAsync(primary, secondary, output, request, mkvmerge, context, cancellationToken);
    }

    public static IReadOnlyList<(string Primary, string Secondary)> FindPairs(string primaryDirectory, string secondaryDirectory, DualAudioSourceMode mode)
    {
        var primary = Directory.EnumerateFiles(primaryDirectory).Where(file => new[] { ".mp4", ".mkv" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Order().ToList();
        var secondary = Directory.EnumerateFiles(secondaryDirectory).Where(file => new[] { ".m4a", ".mp4", ".mkv", ".aac", ".flac", ".eac3", ".ac3", ".dts" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Order().ToList();
        if (mode == DualAudioSourceMode.Interleaved)
        {
            if (primary.Count != secondary.Count)
                throw new InvalidOperationException($"奇偶分P配对不完整：主版本 {primary.Count} 个，副音轨 {secondary.Count} 个");
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

    private async Task<(string PrimaryPages, string SecondaryPages)> ResolvePagesAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var selectedPages = string.IsNullOrWhiteSpace(request.Pages) ? "ALL" : request.Pages;
        if (request.SourceMode == DualAudioSourceMode.Separate) return (selectedPages, selectedPages);
        var info = await bbdown.GetVideoInfoAsync(request.PrimaryUrl, selectedPages, context, cancellationToken);
        if (info.Pages.Count == 0) throw new InvalidOperationException("未能识别分P列表，无法拆分奇偶分P");
        return (string.Join(',', info.Pages.Where(page => page.Number % 2 == 1).Select(page => page.Number)), string.Join(',', info.Pages.Where(page => page.Number % 2 == 0).Select(page => page.Number)));
    }

    internal static (DownloadRequest Primary, DownloadRequest Secondary) BuildDownloadRequests(
        DualAudioRequest request,
        string primaryPages,
        string secondaryPages,
        string primaryDirectory,
        string secondaryDirectory)
    {
        var primary = BuildDownloadRequest(request, request.PrimaryUrl, primaryPages, primaryDirectory, DownloadMode.VideoAndAudio, request.PrimaryLanguage, request.PrimaryAudioCodec);
        var secondaryUrl = request.SourceMode == DualAudioSourceMode.Interleaved ? request.PrimaryUrl : request.SecondaryUrl;
        var secondary = BuildDownloadRequest(request, secondaryUrl, secondaryPages, secondaryDirectory, DownloadMode.AudioOnly, request.SecondaryLanguage, request.SecondaryAudioCodec);
        return (primary, secondary);
    }

    private static DownloadRequest BuildDownloadRequest(DualAudioRequest request, string url, string pages, string directory, DownloadMode mode, string language, string audioCodec) => new()
    {
        Url = url, Pages = pages, Quality = request.Quality, Encoding = request.Encoding, DownloadMode = mode,
        AudioCodec = audioCodec, AudioBitratePriority = request.AudioBitratePriority, WorkDirectory = directory, Language = language,
        MultiFilePattern = "[P<pageNumberWithZero>]<pageTitle>", MultiThread = request.MultiThread, UposHost = request.UposHost,
        UseAria2c = request.UseAria2c, Aria2AutoTune = request.Aria2AutoTune, Aria2cPath = request.Aria2cPath, Aria2MaxConnection = request.Aria2MaxConnection,
        Aria2Split = request.Aria2Split, Aria2MaxConcurrentDownloads = request.Aria2MaxConcurrentDownloads,
        Aria2MinSplitSize = request.Aria2MinSplitSize, Subtitle = false, Cover = false, Danmaku = false
    };

    private async Task MuxDirectoriesAsync(string primaryDirectory, string secondaryDirectory, string outputDirectory, DualAudioRequest request, string mkvmerge, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var pairs = FindPairs(primaryDirectory, secondaryDirectory, request.SourceMode);
        if (pairs.Count == 0) throw new InvalidOperationException("没有找到可匹配的主视频/副音轨文件");
        Directory.CreateDirectory(outputDirectory);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        context.AppendLog($"\n=== 开始封装 {pairs.Count} 个 MKV ===\n");
        foreach (var pair in pairs)
        {
            var output = UniqueOutputPath(outputDirectory, Path.GetFileNameWithoutExtension(pair.Primary), reserved);
            var result = await processRunner.RunAsync(new ProcessRunRequest(mkvmerge, BuildMkvmergeArguments(pair.Primary, pair.Secondary, output, request), paths.RuntimeDirectory), context.AppendLog, cancellationToken);
            if (result.ExitCode != 0 && !result.Cancelled) throw new InvalidOperationException($"封装失败: {Path.GetFileName(pair.Primary)}");
        }
        context.AppendLog($"\n全部完成，输出目录: {outputDirectory}\n");
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
        if (delay is < -10000 or > 10000) throw new ArgumentOutOfRangeException(nameof(delay), "副音轨延迟必须在 -10000 到 10000 毫秒之间");
    }

    private static string BuildTaskName(string title)
    {
        var safe = Sanitize(title);
        return $"{(string.IsNullOrWhiteSpace(safe) ? "多音轨封装" : safe)}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static string CreateUniqueRemuxDirectory(string root, int delay)
    {
        var signed = delay > 0 ? $"+{delay}" : delay.ToString();
        var name = $"多音轨MKV_延迟{signed}ms_{DateTime.Now:yyyyMMdd_HHmmss}";
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(root, index == 1 ? name : $"{name}_{index}");
            if (Directory.Exists(candidate)) continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private static string UniqueOutputPath(string directory, string stem, ISet<string> reserved)
    {
        for (var index = 1; ; index++)
        {
            var path = Path.Combine(directory, index == 1 ? $"{stem}.mkv" : $"{stem}_{index}.mkv");
            if (!File.Exists(path) && reserved.Add(path)) return path;
        }
    }

    private static string? EpisodePrefix(string path) => Regex.Match(Path.GetFileName(path), "^\\[P(\\d+)\\]").Groups[1].Value is { Length: > 0 } value ? value : null;
    private static Dictionary<string, string> BuildEpisodeMap(IEnumerable<string> files, string label)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var prefix = EpisodePrefix(file) ?? throw new InvalidOperationException($"{label}文件缺少 [Pxx] 前缀：{Path.GetFileName(file)}");
            var normalized = int.Parse(prefix).ToString();
            if (!map.TryAdd(normalized, file)) throw new InvalidOperationException($"{label}存在重复分P：P{normalized}");
        }
        return map;
    }
    private static string DisplayAudioCodec(string codec) => codec.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "自动" : codec;
    private static string Sanitize(string value)
    {
        var safe = Regex.Replace(value ?? string.Empty, "[\\\\/:*?\"<>|\\x00-\\x1f]+", "_").Trim(' ', '.', '_');
        return safe.Length > 100 ? safe[..100].TrimEnd(' ', '.', '_') : safe;
    }
}
