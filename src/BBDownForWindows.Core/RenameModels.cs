using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenameMediaType { Series, Movie }

public sealed class RenameTemplate
{
    public const string BuiltInSeriesId = "builtin-series";
    public const string BuiltInMovieId = "builtin-movie";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public RenameMediaType MediaType { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public bool BuiltIn { get; set; }

    public RenameTemplate Clone() => new()
    {
        Id = Id,
        Name = Name,
        MediaType = MediaType,
        Pattern = Pattern,
        BuiltIn = BuiltIn
    };

    public static RenameTemplate SeriesDefault() => new()
    {
        Id = BuiltInSeriesId,
        Name = "中英文剧集",
        MediaType = RenameMediaType.Series,
        Pattern = "{中文名}.{英文名}.{年份}.{季}{集}.{分辨率}.{来源}.{动态范围}.{编码}.{音频}{扩展名}",
        BuiltIn = true
    };

    public static RenameTemplate MovieDefault() => new()
    {
        Id = BuiltInMovieId,
        Name = "中英文电影",
        MediaType = RenameMediaType.Movie,
        Pattern = "{中文名}.{英文名}.{年份}.{分辨率}.{来源}.{动态范围}.{编码}.{音频}{扩展名}",
        BuiltIn = true
    };
}

public sealed class RenameSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string TmdbApiKey { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 8;
    public string ActiveSeriesTemplateId { get; set; } = RenameTemplate.BuiltInSeriesId;
    public string ActiveMovieTemplateId { get; set; } = RenameTemplate.BuiltInMovieId;
    public List<RenameTemplate> Templates { get; set; } = [];

    public RenameSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        TmdbApiKey = TmdbApiKey,
        ProxyUrl = ProxyUrl,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        ActiveSeriesTemplateId = ActiveSeriesTemplateId,
        ActiveMovieTemplateId = ActiveMovieTemplateId,
        Templates = Templates.Select(template => template.Clone()).ToList()
    };

    public void EnsureDefaults()
    {
        Templates ??= [];
        Templates.RemoveAll(template => template is null || string.IsNullOrWhiteSpace(template.Id));
        EnsureBuiltIn(RenameTemplate.SeriesDefault());
        EnsureBuiltIn(RenameTemplate.MovieDefault());
        if (!Templates.Any(template => template.Id == ActiveSeriesTemplateId && template.MediaType == RenameMediaType.Series))
            ActiveSeriesTemplateId = RenameTemplate.BuiltInSeriesId;
        if (!Templates.Any(template => template.Id == ActiveMovieTemplateId && template.MediaType == RenameMediaType.Movie))
            ActiveMovieTemplateId = RenameTemplate.BuiltInMovieId;
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 3, 60);
    }

    private void EnsureBuiltIn(RenameTemplate builtIn)
    {
        var existing = Templates.FirstOrDefault(template => template.Id == builtIn.Id);
        if (existing is null)
        {
            Templates.Insert(0, builtIn);
            return;
        }
        existing.Name = builtIn.Name;
        existing.MediaType = builtIn.MediaType;
        existing.Pattern = builtIn.Pattern;
        existing.BuiltIn = true;
    }
}

public sealed class RenameFileEntry
{
    public required string SourcePath { get; init; }
    public string Name => Path.GetFileName(SourcePath);
    public int? DetectedEpisode { get; init; }
    public bool IsSelected { get; set; } = true;
}

public sealed record MediaMetadata(
    string Resolution,
    string DynamicRange,
    string VideoCodec,
    string Audio,
    string FrameRate)
{
    public static MediaMetadata Default { get; } = new("1080p", string.Empty, "unknown", "AAC.2.0", string.Empty);
}

public sealed class RenamePreviewRequest
{
    public required string DirectoryPath { get; init; }
    public RenameMediaType MediaType { get; init; } = RenameMediaType.Series;
    public string ChineseTitle { get; init; } = string.Empty;
    public string EnglishTitle { get; init; } = string.Empty;
    public string Year { get; init; } = string.Empty;
    public int Season { get; init; } = 1;
    public string TemplateName { get; init; } = string.Empty;
    public string TemplatePattern { get; init; } = string.Empty;
    public string FilenameSuffix { get; init; } = string.Empty;
    public bool UseCustomEpisodes { get; init; }
    public int StartEpisode { get; init; } = 1;
    public IReadOnlyList<RenameFileEntry> Files { get; init; } = [];
    public IReadOnlyDictionary<int, string> EpisodeNames { get; init; } = new Dictionary<int, string>();
}

public sealed record RenameFileOperation(string SourcePath, string TargetPath, bool IsSidecar = false);

public sealed class RenamePreviewItem
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public int? EpisodeNumber { get; init; }
    public MediaMetadata Media { get; init; } = MediaMetadata.Default;
    public List<RenameFileOperation> Operations { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public string SourceName => Path.GetFileName(SourcePath);
    public string TargetName => Path.GetFileName(TargetPath);
    public bool IsValid => Errors.Count == 0;
    public string DetailText => string.Join(" · ", new[] { Media.Resolution, Media.DynamicRange, Media.VideoCodec, Media.Audio, Media.FrameRate }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class RenamePreview
{
    public required RenamePreviewRequest Request { get; init; }
    public List<RenamePreviewItem> Items { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool CanExecute => Items.Count > 0 && Errors.Count == 0 && Items.All(item => item.IsValid);
    public IReadOnlyList<RenameFileOperation> Operations => Items.SelectMany(item => item.Operations).ToList();
}

public sealed class RenameHistoryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? UndoneAt { get; set; }
    public string DirectoryPath { get; set; } = string.Empty;
    public RenameMediaType MediaType { get; set; }
    public string ChineseTitle { get; set; } = string.Empty;
    public string EnglishTitle { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public int Season { get; set; } = 1;
    public string TemplateName { get; set; } = string.Empty;
    public List<RenameFileOperation> Operations { get; set; } = [];
    [JsonIgnore] public string DisplayTitle => string.Join(" / ", new[] { ChineseTitle, EnglishTitle }.Where(value => !string.IsNullOrWhiteSpace(value)));
    [JsonIgnore] public string TimestampText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore] public string StateText => UndoneAt is null ? "已执行" : "已撤销";
}

public sealed record RenameExecutionResult(Guid HistoryId, int OperationCount, string DirectoryPath);

public sealed record RenameNavigationContext(
    string DirectoryPath,
    IReadOnlyList<string> PreferredFiles,
    string SuggestedTitle);

public sealed record TmdbSearchResult(
    int Id,
    RenameMediaType MediaType,
    string ChineseTitle,
    string OriginalTitle,
    string Year,
    string Overview,
    string PosterUrl);

public sealed record TmdbTitleDetail(
    int Id,
    RenameMediaType MediaType,
    string ChineseTitle,
    string EnglishTitle,
    string Year);
