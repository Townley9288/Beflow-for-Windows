using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadMode { VideoAndAudio, VideoOnly, AudioOnly }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioBitratePriority { Highest, Lowest }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskKind { Info, Download, SeasonDownload, LoginWeb, LoginTv, DualAudioMux, DualAudioRemux }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState { Pending, Running, Completed, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioSourceMode { Separate, Interleaved }

public sealed class AppSettings
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("quality")] public string Quality { get; set; } = "4K";
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "AVC";
    [JsonPropertyName("audioOnly")] public string LegacyAudioOnly
    {
        get => DownloadMode switch
        {
            DownloadMode.VideoOnly => "仅视频",
            DownloadMode.AudioOnly => "仅音频",
            _ => "视频+音频"
        };
        set => DownloadMode = value switch
        {
            "仅视频" => DownloadMode.VideoOnly,
            "仅音频" => DownloadMode.AudioOnly,
            _ => DownloadMode.VideoAndAudio
        };
    }
    [JsonIgnore] public DownloadMode DownloadMode { get; set; } = DownloadMode.VideoAndAudio;
    [JsonPropertyName("audioCodec")] public string AudioCodec { get; set; } = "auto";
    [JsonPropertyName("audioBitratePriority")] public string LegacyAudioBitratePriority
    {
        get => AudioBitratePriority == AudioBitratePriority.Lowest ? "lowest" : "highest";
        set => AudioBitratePriority = value == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest;
    }
    [JsonIgnore] public AudioBitratePriority AudioBitratePriority { get; set; } = AudioBitratePriority.Highest;
    [JsonPropertyName("saveTaskLogs")] public bool SaveTaskLogs { get; set; } = true;
    [JsonPropertyName("apiMode")] public string ApiMode { get; set; } = "WEB";
    [JsonPropertyName("danmaku")] public bool Danmaku { get; set; }
    [JsonPropertyName("subtitle")] public bool Subtitle { get; set; }
    [JsonPropertyName("cover")] public bool Cover { get; set; }
    [JsonPropertyName("workDir")] public string WorkDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    [JsonPropertyName("multiThread")] public bool MultiThread { get; set; } = true;
    [JsonPropertyName("uposHost")] public string UposHost { get; set; } = string.Empty;
    [JsonPropertyName("useAria2c")] public bool UseAria2c { get; set; } = true;
    [JsonPropertyName("aria2cPath")] public string Aria2cPath { get; set; } = string.Empty;
    [JsonPropertyName("aria2MaxConnection")] public int Aria2MaxConnection { get; set; } = 16;
    [JsonPropertyName("aria2Split")] public int Aria2Split { get; set; } = 16;
    [JsonPropertyName("aria2MaxConcurrentDownloads")] public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    [JsonPropertyName("aria2MinSplitSize")] public int Aria2MinSplitSize { get; set; } = 5;
    [JsonPropertyName("mkvmergePath")] public string MkvmergePath { get; set; } = string.Empty;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class DownloadRequest
{
    public required string Url { get; set; }
    public string Pages { get; set; } = string.Empty;
    public bool Season { get; set; }
    public string Quality { get; set; } = "4K";
    public string Encoding { get; set; } = "AVC";
    public DownloadMode DownloadMode { get; set; } = DownloadMode.VideoAndAudio;
    public string AudioCodec { get; set; } = "auto";
    public AudioBitratePriority AudioBitratePriority { get; set; } = AudioBitratePriority.Highest;
    public bool Danmaku { get; set; }
    public bool Subtitle { get; set; }
    public bool Cover { get; set; }
    public string WorkDirectory { get; set; } = string.Empty;
    public bool MultiThread { get; set; } = true;
    public string UposHost { get; set; } = string.Empty;
    public bool UseAria2c { get; set; }
    public string Aria2cPath { get; set; } = string.Empty;
    public int Aria2MaxConnection { get; set; } = 16;
    public int Aria2Split { get; set; } = 16;
    public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    public int Aria2MinSplitSize { get; set; } = 5;
    public bool SaveTaskLogs { get; set; } = true;
    public string ApiMode { get; set; } = "WEB";
    public string Language { get; set; } = string.Empty;
    public string MultiFilePattern { get; set; } = string.Empty;
}

public sealed class DualAudioRequest
{
    public DualAudioSourceMode SourceMode { get; set; } = DualAudioSourceMode.Separate;
    public string PrimaryUrl { get; set; } = string.Empty;
    public string SecondaryUrl { get; set; } = string.Empty;
    public string Pages { get; set; } = "ALL";
    public string Quality { get; set; } = "1080P";
    public string Encoding { get; set; } = "AVC";
    public string PrimaryLabel { get; set; } = "国语";
    public string SecondaryLabel { get; set; } = "粤语";
    public string PrimaryLanguage { get; set; } = "zh";
    public string SecondaryLanguage { get; set; } = "zh";
    public bool SecondaryIsDefault { get; set; }
    public int SecondaryAudioDelayMs { get; set; }
    public AudioBitratePriority AudioBitratePriority { get; set; } = AudioBitratePriority.Highest;
    public string WorkDirectory { get; set; } = string.Empty;
    public string ExistingTaskDirectory { get; set; } = string.Empty;
    public string MkvmergePath { get; set; } = string.Empty;
    public bool MultiThread { get; set; } = true;
    public string UposHost { get; set; } = string.Empty;
    public bool UseAria2c { get; set; }
    public string Aria2cPath { get; set; } = string.Empty;
    public int Aria2MaxConnection { get; set; } = 16;
    public int Aria2Split { get; set; } = 16;
    public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    public int Aria2MinSplitSize { get; set; } = 5;
    public bool SaveTaskLogs { get; set; } = true;
}

public sealed class TaskSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TaskKind Kind { get; init; }
    public TaskState State { get; set; } = TaskState.Pending;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string LogPath { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public sealed class HistoryRecord
{
    public TaskKind TaskType { get; set; }
    public string Url { get; set; } = string.Empty;
    public string SecondaryUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string LogPath { get; set; } = string.Empty;
    public DownloadRequest? Download { get; set; }
    public DualAudioRequest? DualAudio { get; set; }
}

public sealed record PageInfo(int Number, string Cid, string Title, string Duration);
public sealed record VideoStreamInfo(int Index, string Quality, string Resolution, int Width, int Height, string Codec, string Fps, string Bitrate, int BitrateKbps, string Size)
{
    public long Pixels => (long)Width * Height;
}
public sealed record AudioStreamInfo(int Index, string Codec, string Bitrate, int BitrateKbps, string Size);

public sealed class VideoInfo
{
    public string Title { get; set; } = string.Empty;
    public List<PageInfo> Pages { get; } = [];
    public List<VideoStreamInfo> VideoStreams { get; } = [];
    public List<AudioStreamInfo> AudioStreams { get; } = [];
    public string RawOutput { get; set; } = string.Empty;
}

public sealed record ResolutionGroup(string Quality, string Codec, string Resolution, List<int> Pages);

public sealed class ToolPaths
{
    public string BBDown { get; set; } = string.Empty;
    public string Aria2c { get; set; } = string.Empty;
    public string Ffmpeg { get; set; } = string.Empty;
    public string Mkvmerge { get; set; } = string.Empty;
}

public sealed record ProcessRunRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string? StandardInput = null);
public sealed record ProcessResult(int ExitCode, string Output, bool Cancelled);
