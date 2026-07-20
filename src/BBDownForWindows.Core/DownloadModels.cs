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

public sealed class BilibiliVideoMetadata
{
    public string Title { get; init; } = string.Empty;
    public string CoverUrl { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public string OwnerAvatarUrl { get; init; } = string.Empty;
    public string CanonicalUrl { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string Aid { get; init; } = string.Empty;
    public string Bvid { get; init; } = string.Empty;
    public string SeasonId { get; init; } = string.Empty;
    public string SourceEpisodeId { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyDictionary<string, BilibiliEpisodeMetadata> EpisodesByCid { get; init; } =
        new Dictionary<string, BilibiliEpisodeMetadata>(StringComparer.OrdinalIgnoreCase);

    public BilibiliEpisodeMetadata? FindEpisode(string cid) =>
        !string.IsNullOrWhiteSpace(cid) && EpisodesByCid.TryGetValue(cid, out var episode) ? episode : null;
}

public sealed record BilibiliEpisodeMetadata(
    string Cid,
    string Aid,
    string Bvid,
    string EpisodeId,
    DateTimeOffset? PublishedAt);

public sealed record DownloadParseRequest(string Url, DownloadParseMode Mode, string ApiMode = "WEB", string Pages = "");

public sealed class DownloadEpisodeInfo
{
    public required PageInfo Page { get; init; }
    public List<VideoStreamInfo> VideoStreams { get; init; } = [];
    public List<AudioStreamInfo> AudioStreams { get; init; } = [];
    public bool IsMuxedStream { get; init; }
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
    public bool IsMuxedStream { get; set; }
    public string FallbackReason { get; set; } = string.Empty;
    public string RelativeOutputPath { get; set; } = string.Empty;
}

public sealed class DownloadBatchRequest
{
    public required DownloadRequest Options { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset ParsedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset DownloadedAt { get; init; } = DateTimeOffset.Now;
    public int TotalPages { get; init; } = 1;
    public DownloadNamingProfileKind NamingProfileKind { get; init; }
    public DownloadNamingProfile NamingProfile { get; init; } = DownloadNamingProfile.Default();
    public BilibiliVideoMetadata? Metadata { get; init; }
    public List<EpisodeStreamSelection> Episodes { get; init; } = [];
}

public sealed class DownloadEpisodeResult
{
    public int PageNumber { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public DownloadEpisodeResultState State { get; set; } = DownloadEpisodeResultState.Pending;
    public VideoStreamSelection? Video { get; set; }
    public AudioStreamSelection? Audio { get; set; }
    public bool IsMuxedStream { get; set; }
    public string FallbackReason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string RelativeOutputPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> OutputFiles { get; set; } = [];
    [JsonIgnore] public string PageText => $"P{PageNumber}";
    [JsonIgnore] public string VideoText => Video?.DisplayName ?? "—";
    [JsonIgnore] public string AudioText => IsMuxedStream ? "内封音频（合流）" : Audio?.DisplayName ?? "—";
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
    public string RenameDirectory { get; init; } = string.Empty;
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

public sealed class ExactDownloadRequest
{
    public required DownloadRequest Options { get; init; }
    public required DownloadEpisodeInfo Episode { get; init; }
    public required EpisodeStreamSelection Selection { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RelativeOutputPath { get; init; }
}

public sealed record ExactDownloadProgress(
    DownloadProgressPhase Phase,
    double? Percent,
    string Speed,
    string Eta,
    string Message);

public sealed class ExactDownloadResult
{
    public int PageNumber { get; init; }
    public VideoStreamSelection? Video { get; init; }
    public AudioStreamSelection? Audio { get; init; }
    public bool IsMuxedStream { get; init; }
    public string FallbackReason { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string RelativeOutputPath { get; init; } = string.Empty;
    public List<string> OutputFiles { get; init; } = [];
}

public sealed class DownloadBatchHistory
{
    public DownloadRequest Options { get; set; } = new() { Url = string.Empty };
    public DateTimeOffset ParsedAt { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }
    public int TotalPages { get; set; } = 1;
    public DownloadNamingProfileKind NamingProfileKind { get; set; }
    public DownloadNamingProfile NamingProfile { get; set; } = DownloadNamingProfile.Default();
    public List<DownloadEpisodeResult> Episodes { get; set; } = [];
}

public static class DownloadFileKinds
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".mov", ".webm" };

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));
}
