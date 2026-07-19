using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioSource { A, B }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioMainVideoMode { Recommended, SourceA, SourceB }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioPairState { Pending, Validating, Downloading, Muxing, Completed, Failed, Cancelled, Unpaired }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioProgressPhase { Validating, DownloadingSourceA, DownloadingSourceB, Muxing, Completed, Failed, Cancelled }

public sealed record DualAudioParseRequest(
    DualAudioSourceMode SourceMode,
    string SourceAUrl,
    string SourceBUrl,
    DownloadParseMode Mode,
    string ApiMode = "WEB",
    DualAudioSource? OnlySource = null);

public sealed class DualAudioCatalog
{
    public DualAudioSourceMode SourceMode { get; init; } = DualAudioSourceMode.Separate;
    public DownloadParseMode ParseMode { get; init; } = DownloadParseMode.Current;
    public string SourceAUrl { get; init; } = string.Empty;
    public string SourceBUrl { get; init; } = string.Empty;
    public DownloadCatalog? SourceA { get; init; }
    public DownloadCatalog? SourceB { get; init; }
    public string SourceAError { get; init; } = string.Empty;
    public string SourceBError { get; init; } = string.Empty;
    public List<DualAudioEpisodePair> Pairs { get; init; } = [];
}

public sealed class DualAudioEpisodePair
{
    public int PairNumber { get; set; }
    public DownloadEpisodeInfo? SourceA { get; set; }
    public DownloadEpisodeInfo? SourceB { get; set; }
    public bool IsSelected { get; set; } = true;
    [JsonIgnore] public bool IsPaired => SourceA is not null && SourceB is not null;
}

public sealed class DualAudioPairSelection
{
    public int PairNumber { get; set; }
    public int SourceAPageNumber { get; set; }
    public string SourceAPageTitle { get; set; } = string.Empty;
    public int SourceBPageNumber { get; set; }
    public string SourceBPageTitle { get; set; } = string.Empty;
    public EpisodeStreamSelection SourceA { get; set; } = new();
    public EpisodeStreamSelection SourceB { get; set; } = new();
    public DualAudioMainVideoMode MainVideoMode { get; set; } = DualAudioMainVideoMode.Recommended;
    public DualAudioSource MainVideoSource { get; set; } = DualAudioSource.A;
    public string RecommendationReason { get; set; } = string.Empty;
    public int? SourceBDelayOverrideMs { get; set; }
    public bool IsSelected { get; set; } = true;
    public string RelativeOutputPath { get; set; } = string.Empty;
}

public sealed class DualAudioBatchRequest
{
    public DualAudioSourceMode SourceMode { get; set; } = DualAudioSourceMode.Separate;
    public string SourceAUrl { get; set; } = string.Empty;
    public string SourceBUrl { get; set; } = string.Empty;
    public string SourceATitle { get; set; } = string.Empty;
    public string SourceBTitle { get; set; } = string.Empty;
    public string ApiMode { get; set; } = "WEB";
    public DownloadRequest Options { get; set; } = new() { Url = string.Empty };
    public List<DualAudioPairSelection> Pairs { get; set; } = [];
    public string SourceALabel { get; set; } = "国语";
    public string SourceBLabel { get; set; } = "粤语";
    public string SourceALanguage { get; set; } = "zh";
    public string SourceBLanguage { get; set; } = "yue";
    public DualAudioSource DefaultAudioSource { get; set; } = DualAudioSource.A;
    public int SourceBDelayMs { get; set; }
    public string WorkDirectory { get; set; } = string.Empty;
    public string MkvmergePath { get; set; } = string.Empty;
    public bool KeepSourceFiles { get; set; } = true;
}

public sealed class DualAudioPairResult
{
    public int PairNumber { get; set; }
    public int SourceAPageNumber { get; set; }
    public string SourceAPageTitle { get; set; } = string.Empty;
    public int SourceBPageNumber { get; set; }
    public string SourceBPageTitle { get; set; } = string.Empty;
    public DualAudioPairState State { get; set; } = DualAudioPairState.Pending;
    public DualAudioSource MainVideoSource { get; set; } = DualAudioSource.A;
    public VideoStreamSelection? SourceAVideo { get; set; }
    public AudioStreamSelection? SourceAAudio { get; set; }
    public VideoStreamSelection? SourceBVideo { get; set; }
    public AudioStreamSelection? SourceBAudio { get; set; }
    public int SourceBDelayMs { get; set; }
    public string RecommendationReason { get; set; } = string.Empty;
    public List<string> SourceAFiles { get; set; } = [];
    public List<string> SourceBFiles { get; set; } = [];
    public string OutputFile { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    [JsonIgnore] public string PairText => $"第 {PairNumber} 对";
    [JsonIgnore] public string MainVideoText => MainVideoSource == DualAudioSource.A ? "来源 A" : "来源 B";
    [JsonIgnore] public string SourceASpecification => FormatSpecification(SourceAVideo, SourceAAudio);
    [JsonIgnore] public string SourceBSpecification => FormatSpecification(SourceBVideo, SourceBAudio);
    [JsonIgnore] public string StatusText => State switch
    {
        DualAudioPairState.Completed => "成功",
        DualAudioPairState.Failed => $"失败：{Error}",
        DualAudioPairState.Cancelled => "已取消",
        DualAudioPairState.Validating => "确认规格",
        DualAudioPairState.Downloading => "下载中",
        DualAudioPairState.Muxing => "封装中",
        DualAudioPairState.Unpaired => "未配对",
        _ => "等待"
    };

    private static string FormatSpecification(VideoStreamSelection? video, AudioStreamSelection? audio) =>
        string.Join(" / ", new[] { video?.DisplayName, audio?.DisplayName }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class DualAudioBatchResult
{
    public string Title { get; set; } = string.Empty;
    public string TaskDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public List<DualAudioPairResult> Pairs { get; set; } = [];
    public List<string> OutputFiles { get; set; } = [];
    public bool Cancelled { get; set; }
    [JsonIgnore] public int Succeeded => Pairs.Count(item => item.State == DualAudioPairState.Completed);
    [JsonIgnore] public int Failed => Pairs.Count(item => item.State == DualAudioPairState.Failed);
}

public sealed record DualAudioProgressSnapshot(
    DualAudioProgressPhase Phase,
    int CompletedPairs,
    int TotalPairs,
    int CurrentPair,
    double OverallPercent,
    double? CurrentPercent,
    string Speed,
    string Eta,
    string Message);

public sealed record DualAudioRecommendation(DualAudioSource Source, string Reason);

public sealed class DualAudioTaskManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DualAudioBatchRequest Request { get; set; } = new();
    public DualAudioBatchResult Result { get; set; } = new();
}

public sealed class DualAudioRemuxPreparation
{
    public bool CanRemux { get; init; }
    public bool IsManifest { get; init; }
    public string Error { get; init; } = string.Empty;
    public string SourceALabel { get; init; } = "国语";
    public string SourceBLabel { get; init; } = "粤语";
    public string SourceALanguage { get; init; } = "zh";
    public string SourceBLanguage { get; init; } = "yue";
    public DualAudioSource DefaultAudioSource { get; init; } = DualAudioSource.A;
    public int SourceBDelayMs { get; init; }
    public bool HasPerPairDelays { get; init; }
}

public sealed class DualAudioBatchHistory
{
    public DualAudioBatchRequest Request { get; set; } = new();
    public List<DualAudioPairResult> Pairs { get; set; } = [];
    public string ManifestPath { get; set; } = string.Empty;
}
