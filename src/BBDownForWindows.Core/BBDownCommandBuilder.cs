namespace BBDownForWindows.Core;

public static class BBDownCommandBuilder
{
    private static readonly Dictionary<string, string> QualityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["杜比视界"] = "杜比视界", ["HDR 真彩"] = "HDR 真彩", ["4K"] = "4K 超清", ["4K 超清"] = "4K 超清",
        ["1080P 高码率"] = "1080P 高码率", ["1080P"] = "1080P 高清", ["1080P 高清"] = "1080P 高清",
        ["720P"] = "720P 高清", ["720P 高清"] = "720P 高清", ["480P"] = "480P 清晰", ["480P 清晰"] = "480P 清晰",
        ["360P"] = "360P 流畅", ["360P 流畅"] = "360P 流畅"
    };
    private static readonly Dictionary<string, string> EncodingMap = new(StringComparer.OrdinalIgnoreCase)
    { ["HEVC"] = "hevc", ["AVC"] = "avc", ["AV1"] = "av1" };

    public static List<string> BuildDownloadArguments(DownloadRequest request, ToolPaths tools)
    {
        var arguments = new List<string> { request.Url };
        if (request.Season) arguments.AddRange(["-p", "ALL"]);
        else if (!string.IsNullOrWhiteSpace(request.Pages)) arguments.AddRange(["-p", request.Pages]);
        if (!string.IsNullOrWhiteSpace(request.MultiFilePattern)) arguments.AddRange(["-M", request.MultiFilePattern]);
        AppendCommon(arguments, request, tools);
        return arguments;
    }

    public static List<string> BuildInfoArguments(DownloadRequest request)
    {
        var arguments = new List<string> { request.Url, "-info", "--show-all" };
        if (request.Season) arguments.AddRange(["-p", "ALL"]);
        else if (!string.IsNullOrWhiteSpace(request.Pages)) arguments.AddRange(["-p", request.Pages]);
        if (request.ApiMode.Equals("TV", StringComparison.OrdinalIgnoreCase)) arguments.Add("-tv");
        else if (request.ApiMode.Equals("APP", StringComparison.OrdinalIgnoreCase)) arguments.Add("-app");
        if (request.AudioBitratePriority == AudioBitratePriority.Lowest) arguments.Add("--audio-ascending");
        return arguments;
    }

    private static void AppendCommon(List<string> arguments, DownloadRequest request, ToolPaths tools)
    {
        if (request.ApiMode.Equals("TV", StringComparison.OrdinalIgnoreCase)) arguments.Add("-tv");
        else if (request.ApiMode.Equals("APP", StringComparison.OrdinalIgnoreCase)) arguments.Add("-app");
        if (QualityMap.TryGetValue(request.Quality, out var quality)) arguments.AddRange(["-q", quality]);
        if (EncodingMap.TryGetValue(request.Encoding, out var encoding)) arguments.AddRange(["--encoding-priority", encoding]);
        if (request.DownloadMode == DownloadMode.VideoOnly) arguments.Add("--video-only");
        else if (request.DownloadMode == DownloadMode.AudioOnly) arguments.Add("--audio-only");
        if (request.AudioBitratePriority == AudioBitratePriority.Lowest) arguments.Add("--audio-ascending");
        if (request.Danmaku) arguments.Add("--download-danmaku");
        if (!request.Subtitle) arguments.Add("--skip-subtitle");
        if (!request.Cover) arguments.Add("--skip-cover");
        if (!string.IsNullOrWhiteSpace(request.WorkDirectory)) arguments.AddRange(["--work-dir", request.WorkDirectory]);
        if (!string.IsNullOrWhiteSpace(request.Language)) arguments.AddRange(["--language", request.Language]);
        if (!request.MultiThread) arguments.AddRange(["--multi-thread", "false"]);
        if (!string.IsNullOrWhiteSpace(request.UposHost)) arguments.AddRange(["--upos-host", request.UposHost]);
        if (!string.IsNullOrWhiteSpace(tools.Ffmpeg)) arguments.AddRange(["--ffmpeg-path", tools.Ffmpeg]);
        if (request.UseAria2c)
        {
            arguments.Add("--use-aria2c");
            var aria = !string.IsNullOrWhiteSpace(request.Aria2cPath) ? request.Aria2cPath : tools.Aria2c;
            if (!string.IsNullOrWhiteSpace(aria)) arguments.AddRange(["--aria2c-path", aria]);
            arguments.AddRange(["--aria2c-args", $"-x{request.Aria2MaxConnection} -s{request.Aria2Split} -j{request.Aria2MaxConcurrentDownloads} -k {request.Aria2MinSplitSize}M"]);
        }
    }
}
