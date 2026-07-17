using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadParseMode { Current, All }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadEpisodeParseState { Pending, Parsing, Ready, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadEpisodeResultState { Pending, Validating, Downloading, Muxing, Completed, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadProgressPhase { Validating, Downloading, Muxing, Completed, Failed, Cancelled }

public sealed record BilibiliVideoMetadata(
    string Title,
    string CoverUrl,
    string OwnerName,
    string OwnerAvatarUrl,
    string CanonicalUrl);

public sealed record DownloadParseRequest(string Url, DownloadParseMode Mode, string ApiMode = "WEB", string Pages = "");

public sealed class DownloadEpisodeInfo
{
    public required PageInfo Page { get; init; }
    public List<VideoStreamInfo> VideoStreams { get; init; } = [];
    public List<AudioStreamInfo> AudioStreams { get; init; } = [];
    public DownloadEpisodeParseState State { get; set; } = DownloadEpisodeParseState.Pending;
    public string Error { get; set; } = string.Empty;
}

public sealed class DownloadCatalog
{
    public string SourceUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public BilibiliVideoMetadata? Metadata { get; init; }
    public DateTimeOffset ParsedAt { get; init; } = DateTimeOffset.Now;
    public List<PageInfo> AllPages { get; init; } = [];
    public List<DownloadEpisodeInfo> Episodes { get; init; } = [];
}

public sealed record DownloadParseProgress(
    int Completed,
    int Total,
    int CurrentPage,
    string CurrentTitle,
    DownloadEpisodeInfo? Episode,
    string Message);

public sealed record StreamSelectionRule(
    string QualityRule,
    string PreferredEncoding,
    string PreferredAudioCodec,
    AudioBitratePriority AudioBitratePriority);

public sealed record VideoStreamSelection(
    string Quality,
    string Resolution,
    string Codec,
    int BitrateKbps,
    bool IsManual = false)
{
    [JsonIgnore] public string DisplayName => $"{Quality} · {Resolution} · {Codec} · {BitrateKbps} kbps";
}

public sealed record AudioStreamSelection(
    string Codec,
    int BitrateKbps,
    bool IsManual = false)
{
    [JsonIgnore] public string DisplayName => $"{Codec} · {BitrateKbps} kbps";
}

public sealed class EpisodeStreamSelection
{
    public int PageNumber { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public VideoStreamSelection? Video { get; set; }
    public AudioStreamSelection? Audio { get; set; }
    public string FallbackReason { get; set; } = string.Empty;
}

public sealed class DownloadBatchRequest
{
    public required DownloadRequest Options { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset ParsedAt { get; init; } = DateTimeOffset.Now;
    public List<EpisodeStreamSelection> Episodes { get; init; } = [];
}

public sealed class DownloadEpisodeResult
{
    public int PageNumber { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public DownloadEpisodeResultState State { get; set; } = DownloadEpisodeResultState.Pending;
    public VideoStreamSelection? Video { get; set; }
    public AudioStreamSelection? Audio { get; set; }
    public string FallbackReason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public List<string> OutputFiles { get; set; } = [];
    [JsonIgnore] public string PageText => $"P{PageNumber}";
    [JsonIgnore] public string VideoText => Video?.DisplayName ?? "—";
    [JsonIgnore] public string AudioText => Audio?.DisplayName ?? "—";
    [JsonIgnore] public string StatusText => State switch
    {
        DownloadEpisodeResultState.Completed => "成功",
        DownloadEpisodeResultState.Failed => $"失败：{Error}",
        DownloadEpisodeResultState.Cancelled => "已取消",
        DownloadEpisodeResultState.Downloading => "下载中",
        DownloadEpisodeResultState.Muxing => "合并中",
        DownloadEpisodeResultState.Validating => "确认规格",
        _ => "等待"
    };
}

public sealed class DownloadBatchResult
{
    public string Title { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public List<DownloadEpisodeResult> Episodes { get; init; } = [];
    public List<string> OutputFiles { get; init; } = [];
    [JsonIgnore] public bool HasVideo => OutputFiles.Any(DownloadFileKinds.IsVideoFile);
}

public sealed record DownloadProgressSnapshot(
    DownloadProgressPhase Phase,
    int CompletedEpisodes,
    int TotalEpisodes,
    int CurrentPage,
    string CurrentTitle,
    double OverallPercent,
    double? CurrentPercent,
    string Speed,
    string Eta,
    string Message);

public sealed class DownloadBatchHistory
{
    public DownloadRequest Options { get; set; } = new() { Url = string.Empty };
    public DateTimeOffset ParsedAt { get; set; }
    public List<DownloadEpisodeResult> Episodes { get; set; } = [];
}

public static class DownloadFileKinds
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".mov", ".webm" };

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));
}
