using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadMode { VideoAndAudio, VideoOnly, AudioOnly }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioBitratePriority { Highest, Lowest }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskKind { Info, Download, SeasonDownload, LoginWeb, LoginTv, DualAudioParse, DualAudioMux, DualAudioRemux, RenamePreview, RenameExecute, RenameUndo, DownloadParse, DownloadBatch }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState { Pending, Running, Completed, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DualAudioSourceMode { Separate, Interleaved }

public enum AccountChannel { Web, Tv }

public enum AccountLoginState { NotConfigured, LoggedIn, Expired, Unavailable }

public enum UpdateCheckStatus { UpToDate, UpdateAvailable }

public enum UpdatePackageKind { Installer, Portable }

public enum AppThemeMode { System, Light, Dark }

public sealed record AccountProfile(string DisplayName, string UserId, string AvatarUrl, int Level, string VipLabel);

public sealed record AccountChannelStatus(
    AccountChannel Channel,
    AccountLoginState State,
    AccountProfile? Profile,
    string Message,
    DateTimeOffset CheckedAt,
    DateTimeOffset? CredentialUpdatedAt,
    DateTimeOffset? CredentialExpiresAt = null);

public sealed record AccountStatusSnapshot(AccountChannelStatus Web, AccountChannelStatus Tv, DateTimeOffset CheckedAt);

public sealed class AppSettings
{
    private int _schemaVersion = 4;
    [JsonIgnore] private bool _schemaVersionSpecified;
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion
    {
        get => _schemaVersion;
        set { _schemaVersion = value; _schemaVersionSpecified = true; }
    }
    [JsonIgnore] public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
    [JsonPropertyName("themeMode")] public string SerializedThemeMode
    {
        get => ThemeMode switch
        {
            AppThemeMode.Light => "light",
            AppThemeMode.Dark => "dark",
            _ => "system"
        };
        set => ThemeMode = value?.Trim().ToLowerInvariant() switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
    }
    [JsonPropertyName("quality")] public string Quality { get; set; } = "4K";
    [JsonPropertyName("videoQualityRule")] public string VideoQualityRule { get; set; } = "4K 超清";
    [JsonPropertyName("includeHdrDolbyInAutoSelection")] public bool IncludeHdrDolbyInAutoSelection { get; set; }
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
    [JsonPropertyName("aria2AutoTune")] public bool Aria2AutoTune { get; set; } = true;
    [JsonPropertyName("aria2cPath")] public string Aria2cPath { get; set; } = string.Empty;
    [JsonPropertyName("aria2MaxConnection")] public int Aria2MaxConnection { get; set; } = 16;
    [JsonPropertyName("aria2Split")] public int Aria2Split { get; set; } = 16;
    [JsonPropertyName("aria2MaxConcurrentDownloads")] public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    [JsonPropertyName("aria2MinSplitSize")] public int Aria2MinSplitSize { get; set; } = 5;
    [JsonPropertyName("mkvmergePath")] public string MkvmergePath { get; set; } = string.Empty;
    [JsonPropertyName("checkUpdatesOnStartup")] public bool CheckUpdatesOnStartup { get; set; } = true;
    [JsonPropertyName("downloadNaming")] public DownloadNamingSettings DownloadNaming { get; set; } = new();

    public void EnsureCurrentSchema()
    {
        if (!_schemaVersionSpecified || SchemaVersion < 3) VideoQualityRule = StreamSelectionPolicy.NormalizeQualityRule(Quality);
        VideoQualityRule = StreamSelectionPolicy.NormalizeQualityRule(VideoQualityRule);
        if (!StreamSelectionPolicy.IsKnownQualityRule(VideoQualityRule)) VideoQualityRule = "4K 超清";
        Encoding = Encoding?.Trim().ToUpperInvariant() ?? "AVC";
        if (Encoding is not ("AVC" or "HEVC" or "AV1")) Encoding = "AVC";
        var knownAudio = new[] { "auto", "E-AC-3", "M4A", "FLAC", "AC-3", "DTS" };
        if (!knownAudio.Contains(AudioCodec, StringComparer.OrdinalIgnoreCase)) AudioCodec = "auto";
        DownloadNaming ??= new DownloadNamingSettings();
        DownloadNaming.EnsureDefaults();
        SchemaVersion = 4;
    }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.DownloadNaming = (DownloadNaming ?? new DownloadNamingSettings()).Clone();
        return clone;
    }
}

public sealed class UpdateState
{
    [JsonPropertyName("lastCheckedAt")] public DateTimeOffset? LastCheckedAt { get; set; }
}

public sealed record UpdateAsset(
    UpdatePackageKind Kind,
    string FileName,
    Uri DownloadUri,
    long Size,
    string ChecksumFileName,
    Uri ChecksumUri);

public sealed record UpdateRelease(
    Version Version,
    string TagName,
    string DisplayName,
    string ReleaseNotes,
    DateTimeOffset PublishedAt,
    Uri ReleasePage,
    UpdateAsset Installer,
    UpdateAsset Portable);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    UpdateRelease? Release,
    string Message);

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
    public bool Aria2AutoTune { get; set; } = true;
    public string Aria2cPath { get; set; } = string.Empty;
    public int Aria2MaxConnection { get; set; } = 16;
    public int Aria2Split { get; set; } = 16;
    public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    public int Aria2MinSplitSize { get; set; } = 5;
    public bool SaveTaskLogs { get; set; } = true;
    public string ApiMode { get; set; } = "WEB";
    public string Language { get; set; } = string.Empty;
    public string MultiFilePattern { get; set; } = string.Empty;
    public bool OrganizeInTitleDirectory { get; set; }
    [JsonIgnore] public string TitleHint { get; set; } = string.Empty;
}

public sealed record DownloadResult(
    string Title,
    string OutputDirectory,
    IReadOnlyList<string> OutputFiles,
    bool HasVideo,
    string RenameDirectory = "");

public sealed class DualAudioRequest
{
    public DualAudioSourceMode SourceMode { get; set; } = DualAudioSourceMode.Separate;
    public string PrimaryUrl { get; set; } = string.Empty;
    public string SecondaryUrl { get; set; } = string.Empty;
    public string Pages { get; set; } = "ALL";
    public string Quality { get; set; } = "4K";
    public string Encoding { get; set; } = "AVC";
    public string PrimaryAudioCodec { get; set; } = "auto";
    public string SecondaryAudioCodec { get; set; } = "auto";
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
    public bool Aria2AutoTune { get; set; } = true;
    public string Aria2cPath { get; set; } = string.Empty;
    public int Aria2MaxConnection { get; set; } = 16;
    public int Aria2Split { get; set; } = 16;
    public int Aria2MaxConcurrentDownloads { get; set; } = 16;
    public int Aria2MinSplitSize { get; set; } = 5;
    public bool SaveTaskLogs { get; set; } = true;
    public bool OverrideManifestAudioMetadata { get; set; }
    public bool OverrideManifestDelay { get; set; }
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
    [JsonPropertyName("id")] public Guid Id { get; set; }
    public TaskKind TaskType { get; set; }
    public string Url { get; set; } = string.Empty;
    public string SecondaryUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string TimestampText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore]
    public string TaskTypeText => TaskType switch
    {
        TaskKind.Info => "信息解析",
        TaskKind.Download => "普通下载",
        TaskKind.SeasonDownload => "整季下载",
        TaskKind.LoginWeb => "WEB 登录",
        TaskKind.LoginTv => "TV 登录",
        TaskKind.DualAudioParse => "多音轨解析",
        TaskKind.DualAudioMux => "多音轨封装",
        TaskKind.DualAudioRemux => "重新封装",
        TaskKind.RenamePreview => "重命名预览",
        TaskKind.RenameExecute => "执行重命名",
        TaskKind.RenameUndo => "撤销重命名",
        TaskKind.DownloadParse => "下载解析",
        TaskKind.DownloadBatch => "批量下载",
        _ => TaskType.ToString()
    };
    public string LogPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> OutputFiles { get; set; } = [];
    public DownloadRequest? Download { get; set; }
    public DownloadBatchHistory? DownloadBatch { get; set; }
    public DualAudioRequest? DualAudio { get; set; }
    public DualAudioBatchHistory? DualAudioBatch { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> SpecificationTags
    {
        get
        {
            List<string> tags = [];
            if (DownloadBatch is not null)
            {
                var attempted = DownloadBatch.Episodes.Count;
                var succeeded = DownloadBatch.Episodes.Count(item => item.State == DownloadEpisodeResultState.Completed);
                var failed = DownloadBatch.Episodes.Count(item => item.State == DownloadEpisodeResultState.Failed);
                tags.Add($"{attempted} 集");
                if (succeeded > 0) tags.Add($"成功 {succeeded}");
                if (failed > 0) tags.Add($"失败 {failed}");
                if (DownloadBatch.Options.DownloadMode != DownloadMode.AudioOnly)
                    AddBatchVideoSpecification(tags, DownloadBatch.Episodes);
                if (DownloadBatch.Options.DownloadMode != DownloadMode.VideoOnly)
                    AddBatchAudioSpecification(tags, DownloadBatch.Episodes);
                tags.Add(DownloadBatch.Options.DownloadMode switch
                {
                    DownloadMode.VideoOnly => "仅视频",
                    DownloadMode.AudioOnly => "仅音频",
                    _ => "视频+音频"
                });
                return tags;
            }
            if (Download is not null)
            {
                if (Download.DownloadMode == DownloadMode.AudioOnly)
                {
                    tags.Add(string.Equals(Download.AudioCodec, "auto", StringComparison.OrdinalIgnoreCase) ? "音频自动" : Download.AudioCodec);
                }
                else
                {
                    AddIfPresent(tags, Download.Quality);
                    AddIfPresent(tags, Download.Encoding);
                }

                tags.Add(Download.DownloadMode switch
                {
                    DownloadMode.VideoOnly => "仅视频",
                    DownloadMode.AudioOnly => "仅音频",
                    _ => "视频+音频"
                });
                return tags;
            }

            if (DualAudioBatch is not null)
            {
                var succeeded = DualAudioBatch.Pairs.Count(item => item.State == DualAudioPairState.Completed);
                var failed = DualAudioBatch.Pairs.Count(item => item.State == DualAudioPairState.Failed);
                tags.Add($"{DualAudioBatch.Pairs.Count} 对");
                if (succeeded > 0) tags.Add($"成功 {succeeded}");
                if (failed > 0) tags.Add($"失败 {failed}");
                tags.Add("双音轨封装");
                return tags;
            }
            if (DualAudio is not null)
            {
                if (TaskType == TaskKind.DualAudioRemux) return ["仅重新封装"];
                AddIfPresent(tags, DualAudio.Quality);
                AddIfPresent(tags, DualAudio.Encoding);
                tags.Add("双音轨封装");
            }
            return tags;
        }
    }

    private static void AddIfPresent(List<string> tags, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) tags.Add(value.Trim());
    }

    private static void AddBatchVideoSpecification(List<string> tags, IEnumerable<DownloadEpisodeResult> episodes)
    {
        var specifications = episodes
            .Where(item => item.Video is not null)
            .Select(item => item.Video!)
            .GroupBy(video => new
            {
                Quality = video.Quality.Trim(),
                Resolution = video.Resolution.Trim(),
                Codec = video.Codec.Trim()
            })
            .Select(group => group.Key)
            .ToList();
        if (specifications.Count == 0) return;
        if (specifications.Count > 1)
        {
            tags.Add("多种视频规格");
            return;
        }

        var specification = specifications[0];
        var quality = JoinSpecificationParts(specification.Quality, NormalizeResolution(specification.Resolution));
        AddIfPresent(tags, quality);
        AddIfPresent(tags, specification.Codec);
    }

    private static void AddBatchAudioSpecification(List<string> tags, IEnumerable<DownloadEpisodeResult> episodes)
    {
        var audio = episodes
            .Where(item => item.Audio is not null)
            .Select(item => item.Audio!)
            .ToList();
        if (audio.Count == 0) return;
        var codecs = audio.Select(item => item.Codec.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codecs.Count > 1)
        {
            tags.Add("多种音频规格");
            return;
        }
        if (codecs.Count == 0) return;

        var bitrates = audio.Select(item => item.BitrateKbps).Where(value => value > 0).Distinct().ToList();
        tags.Add(bitrates.Count == 1 ? $"{codecs[0]} · {bitrates[0]}kbps" : codecs[0]);
    }

    private static string JoinSpecificationParts(params string[] values) =>
        string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string NormalizeResolution(string value) => value.Replace('x', '×').Replace('X', '×');
}

public sealed record PageInfo(int Number, string Cid, string Title, string Duration);
public sealed record VideoStreamInfo(int Index, string Quality, string Resolution, int Width, int Height, string Codec, string Fps, string Bitrate, int BitrateKbps, string Size)
{
    public long Pixels => (long)Width * Height;
    [JsonIgnore] public long EstimatedSizeBytes => StreamSelectionPolicy.ParseSizeBytes(Size);
}
public sealed record AudioStreamInfo(int Index, string Codec, string Bitrate, int BitrateKbps, string Size)
{
    [JsonIgnore] public long EstimatedSizeBytes => StreamSelectionPolicy.ParseSizeBytes(Size);
    [JsonIgnore] public string DisplayName => $"{Codec} · {Bitrate} · {Size.TrimStart('~')}";
}

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
    public string Ffprobe { get; set; } = string.Empty;
    public string Mkvmerge { get; set; } = string.Empty;
}

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput = null,
    bool UsePseudoConsole = false,
    int PseudoConsoleColumns = 160,
    int PseudoConsoleRows = 40);
public sealed record ProcessResult(int ExitCode, string Output, bool Cancelled);
