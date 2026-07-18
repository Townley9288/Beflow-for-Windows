using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

[Flags]
public enum DownloadNamingFieldSections
{
    None = 0,
    MainFolder = 1,
    Subfolder = 2,
    FileName = 4,
    All = MainFolder | Subfolder | FileName
}

public enum DownloadNamingProfileKind { SingleVideo, MultiEpisode }

public sealed record DownloadNamingField(
    string Name,
    string Category,
    DownloadNamingFieldSections Sections)
{
    public string Token => $"{{{Name}}}";
}

public sealed class DownloadNamingProfile
{
    public string MainFolderTemplate { get; set; } = "{视频标题}";
    public string SubfolderTemplate { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = "[P{分集序号补零}]{分集标题}";

    public DownloadNamingProfile Clone() => new()
    {
        MainFolderTemplate = MainFolderTemplate,
        SubfolderTemplate = SubfolderTemplate,
        FileNameTemplate = FileNameTemplate
    };

    public static DownloadNamingProfile Default() => new();
}

public sealed class DownloadNamingSettings
{
    public DownloadNamingProfile SingleVideo { get; set; } = DownloadNamingProfile.Default();
    public DownloadNamingProfile MultiEpisode { get; set; } = DownloadNamingProfile.Default();

    public DownloadNamingProfile GetProfile(DownloadNamingProfileKind kind) =>
        kind == DownloadNamingProfileKind.MultiEpisode ? MultiEpisode : SingleVideo;

    public DownloadNamingSettings Clone() => new()
    {
        SingleVideo = (SingleVideo ?? DownloadNamingProfile.Default()).Clone(),
        MultiEpisode = (MultiEpisode ?? DownloadNamingProfile.Default()).Clone()
    };

    public void EnsureDefaults()
    {
        SingleVideo = RepairProfile(SingleVideo);
        MultiEpisode = RepairProfile(MultiEpisode);
    }

    private static DownloadNamingProfile RepairProfile(DownloadNamingProfile? profile)
    {
        var defaults = DownloadNamingProfile.Default();
        if (profile is null) return defaults;
        var service = new DownloadNamingService();
        var repaired = profile.Clone();
        if (!service.Validate(new DownloadNamingProfile
            {
                MainFolderTemplate = repaired.MainFolderTemplate,
                SubfolderTemplate = defaults.SubfolderTemplate,
                FileNameTemplate = defaults.FileNameTemplate
            }).IsValid)
            repaired.MainFolderTemplate = defaults.MainFolderTemplate;
        if (!service.Validate(new DownloadNamingProfile
            {
                MainFolderTemplate = defaults.MainFolderTemplate,
                SubfolderTemplate = repaired.SubfolderTemplate,
                FileNameTemplate = defaults.FileNameTemplate
            }).IsValid)
            repaired.SubfolderTemplate = defaults.SubfolderTemplate;
        if (!service.Validate(new DownloadNamingProfile
            {
                MainFolderTemplate = defaults.MainFolderTemplate,
                SubfolderTemplate = defaults.SubfolderTemplate,
                FileNameTemplate = repaired.FileNameTemplate
            }).IsValid)
            repaired.FileNameTemplate = defaults.FileNameTemplate;
        return repaired;
    }
}

public sealed class DownloadNamingContext
{
    public required string RootDirectory { get; init; }
    public required string SourceUrl { get; init; }
    public required string VideoTitle { get; init; }
    public required PageInfo Page { get; init; }
    public required DownloadNamingProfile Profile { get; init; }
    public required DownloadNamingProfileKind ProfileKind { get; init; }
    public int TotalPages { get; init; } = 1;
    public DownloadMode DownloadMode { get; init; } = DownloadMode.VideoAndAudio;
    public string ApiMode { get; init; } = "WEB";
    public DateTimeOffset DownloadedAt { get; init; } = DateTimeOffset.Now;
    public BilibiliVideoMetadata? Metadata { get; init; }
    public VideoStreamInfo? Video { get; init; }
    public AudioStreamInfo? Audio { get; init; }
    public string PreferredRelativePath { get; init; } = string.Empty;
    public bool AllowPartialReuse { get; init; }
}

public sealed record DownloadNamingValidationResult(bool IsValid, string Error);

public sealed record DownloadNamingPreview(string RelativePath, string Error, IReadOnlyList<string> Warnings)
{
    public bool IsValid => string.IsNullOrWhiteSpace(Error);
}

public sealed record DownloadOutputPlan(
    string MainDirectory,
    string LeafDirectory,
    string RelativePath,
    string FileStem,
    IReadOnlyList<string> Warnings);

public sealed partial class DownloadNamingService : IDownloadNamingService
{
    private static readonly string[] ReservedNames =
    ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".aria2", ".part", ".tmp", ".download", ".m4s" };

    private static readonly HashSet<string> KnownMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".mov", ".webm", ".m4a", ".flac", ".aac", ".mp3" };

    private static readonly IReadOnlyList<DownloadNamingField> SupportedFields =
    [
        new("视频标题", "标题与分集", DownloadNamingFieldSections.All),
        new("分集标题", "标题与分集", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("分集序号", "标题与分集", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("分集序号补零", "标题与分集", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("资源类型", "标题与分集", DownloadNamingFieldSections.All),
        new("下载类型", "标题与分集", DownloadNamingFieldSections.All),
        new("AV号", "编号", DownloadNamingFieldSections.All),
        new("BV号", "编号", DownloadNamingFieldSections.All),
        new("CID", "编号", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("SS号", "编号", DownloadNamingFieldSections.All),
        new("EP号", "编号", DownloadNamingFieldSections.All),
        new("UP主昵称", "账号与时间", DownloadNamingFieldSections.All),
        new("UP主UID", "账号与时间", DownloadNamingFieldSections.All),
        new("发布时间", "账号与时间", DownloadNamingFieldSections.All),
        new("分集发布时间", "账号与时间", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("下载日期", "账号与时间", DownloadNamingFieldSections.All),
        new("下载时间", "账号与时间", DownloadNamingFieldSections.All),
        new("画质", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("分辨率", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("帧率", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("视频编码", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("视频码率", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("音频编码", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("音频码率", "媒体规格", DownloadNamingFieldSections.Subfolder | DownloadNamingFieldSections.FileName),
        new("接口类型", "媒体规格", DownloadNamingFieldSections.All)
    ];

    public IReadOnlyList<DownloadNamingField> Fields => SupportedFields;

    public DownloadNamingValidationResult Validate(DownloadNamingProfile profile)
    {
        try
        {
            ValidateTemplate(profile.MainFolderTemplate, DownloadNamingFieldSections.MainFolder, required: true, "视频主文件夹");
            ValidateTemplate(profile.SubfolderTemplate, DownloadNamingFieldSections.Subfolder, required: false, "子文件夹");
            ValidateTemplate(profile.FileNameTemplate, DownloadNamingFieldSections.FileName, required: true, "文件名");
            if (KnownMediaExtensions.Contains(Path.GetExtension(profile.FileNameTemplate.Trim())))
                throw new InvalidOperationException("文件名模板不需要填写扩展名，扩展名会由 BBDown 自动添加");
            return new DownloadNamingValidationResult(true, string.Empty);
        }
        catch (InvalidOperationException exception)
        {
            return new DownloadNamingValidationResult(false, exception.Message);
        }
    }

    public DownloadNamingPreview Preview(DownloadNamingProfile profile, DownloadNamingProfileKind kind, string rootDirectory)
    {
        var validation = Validate(profile);
        if (!validation.IsValid) return new DownloadNamingPreview(string.Empty, validation.Error, []);
        try
        {
            var context = CreateSampleContext(profile, kind, rootDirectory);
            var rendered = Render(context);
            return new DownloadNamingPreview(rendered.RelativePath, string.Empty, rendered.Warnings);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PathTooLongException or ArgumentException)
        {
            return new DownloadNamingPreview(string.Empty, exception.Message, []);
        }
    }

    public string ResolveMainDirectory(DownloadNamingContext context)
    {
        var validation = Validate(context.Profile);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Error);
        var rendered = RenderComponent(context.Profile.MainFolderTemplate, DownloadNamingFieldSections.MainFolder, context, "Beflow 下载", 120);
        return EnsureSafePath(context.RootDirectory, rendered.Value);
    }

    public DownloadOutputPlan BuildPlan(DownloadNamingContext context, ISet<string> reservedPaths)
    {
        var validation = Validate(context.Profile);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Error);

        if (!string.IsNullOrWhiteSpace(context.PreferredRelativePath))
        {
            var preferred = TryPreferredPlan(context, reservedPaths);
            if (preferred is not null) return preferred;
        }

        var rendered = Render(context);
        var leaf = rendered.LeafDirectory;
        var stem = rendered.FileStem;
        var uniqueStem = ResolveUniqueStem(leaf, stem, reservedPaths, allowPartialReuse: false);
        var relativeDirectory = Path.GetRelativePath(Path.GetFullPath(context.RootDirectory), leaf);
        var relativePath = relativeDirectory == "." ? uniqueStem : Path.Combine(relativeDirectory, uniqueStem);
        EnsurePathLength(Path.Combine(context.RootDirectory, relativePath));
        return new DownloadOutputPlan(rendered.MainDirectory, leaf, relativePath, uniqueStem, rendered.Warnings);
    }

    internal static bool IsStructurallyValid(DownloadNamingProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.MainFolderTemplate) || string.IsNullOrWhiteSpace(profile.FileNameTemplate)) return false;
        var service = new DownloadNamingService();
        return service.Validate(profile).IsValid;
    }

    private DownloadOutputPlan? TryPreferredPlan(DownloadNamingContext context, ISet<string> reservedPaths)
    {
        try
        {
            var root = Path.GetFullPath(context.RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var combined = Path.GetFullPath(Path.Combine(root, context.PreferredRelativePath));
            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = Path.GetRelativePath(root, combined);
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var stem = parts[^1];
            var leaf = Path.GetDirectoryName(combined)!;
            var main = Path.Combine(root, parts[0]);
            var reservation = Path.Combine(leaf, stem);
            if (HasConflict(leaf, stem, context.AllowPartialReuse) || !reservedPaths.Add(reservation)) return null;
            EnsurePathLength(combined);
            return new DownloadOutputPlan(main, leaf, relative, stem, []);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private RenderedPath Render(DownloadNamingContext context)
    {
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var main = RenderComponent(context.Profile.MainFolderTemplate, DownloadNamingFieldSections.MainFolder, context, "Beflow 下载", 120);
        warnings.UnionWith(main.MissingFields.Select(field => $"字段“{field}”暂无可用值"));
        if (main.WasSanitized) warnings.Add("视频主文件夹中的 Windows 非法字符已替换或移除");
        var sub = RenderComponent(context.Profile.SubfolderTemplate, DownloadNamingFieldSections.Subfolder, context, string.Empty, 100, allowEmpty: true);
        warnings.UnionWith(sub.MissingFields.Select(field => $"字段“{field}”暂无可用值"));
        if (sub.WasSanitized) warnings.Add("子文件夹中的 Windows 非法字符已替换或移除");
        var file = RenderComponent(context.Profile.FileNameTemplate, DownloadNamingFieldSections.FileName, context,
            $"[P{FormatPageNumber(context.Page.Number, context.TotalPages)}]{context.Page.Title}", 180);
        warnings.UnionWith(file.MissingFields.Select(field => $"字段“{field}”暂无可用值"));
        if (file.WasSanitized) warnings.Add("文件名中的 Windows 非法字符已替换或移除");

        var mainDirectory = EnsureSafePath(context.RootDirectory, main.Value);
        var leaf = string.IsNullOrWhiteSpace(sub.Value) ? mainDirectory : EnsureSafePath(mainDirectory, sub.Value);
        EnsurePathLength(Path.Combine(leaf, file.Value));
        var relativeDirectory = Path.GetRelativePath(Path.GetFullPath(context.RootDirectory), leaf);
        var relative = relativeDirectory == "." ? file.Value : Path.Combine(relativeDirectory, file.Value);
        return new RenderedPath(mainDirectory, leaf, relative, file.Value, warnings.ToList());
    }

    private RenderedComponent RenderComponent(string template, DownloadNamingFieldSections section, DownloadNamingContext context,
        string fallback, int maximumLength, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(template)) return new RenderedComponent(allowEmpty ? string.Empty : fallback, [], false);
        var result = template;
        var missing = new List<string>();
        foreach (Match match in FieldRegex().Matches(template))
        {
            var name = match.Groups[1].Value;
            var value = GetValue(name, section, context);
            if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
            result = result.Replace(match.Value, value, StringComparison.Ordinal);
        }
        result = CollapseSeparators(result);
        if (allowEmpty && string.IsNullOrWhiteSpace(result)) return new RenderedComponent(string.Empty, missing.Distinct().ToList(), false);
        var sanitized = SanitizeComponent(result, fallback, maximumLength);
        return new RenderedComponent(sanitized, missing.Distinct().ToList(), !string.Equals(result, sanitized, StringComparison.Ordinal));
    }

    private static string GetValue(string field, DownloadNamingFieldSections section, DownloadNamingContext context)
    {
        var episode = context.Metadata?.FindEpisode(context.Page.Cid);
        var main = section == DownloadNamingFieldSections.MainFolder;
        var sourceIds = ParseSourceIds(context.SourceUrl);
        var aid = main ? context.Metadata?.Aid ?? sourceIds.Aid : episode?.Aid ?? context.Metadata?.Aid ?? sourceIds.Aid;
        var bvid = main ? context.Metadata?.Bvid ?? sourceIds.Bvid : episode?.Bvid ?? context.Metadata?.Bvid ?? sourceIds.Bvid;
        var seasonId = context.Metadata?.SeasonId ?? sourceIds.SeasonId;
        var episodeId = main ? context.Metadata?.SourceEpisodeId ?? sourceIds.EpisodeId : episode?.EpisodeId ?? context.Metadata?.SourceEpisodeId ?? sourceIds.EpisodeId;
        var published = main ? context.Metadata?.PublishedAt : episode?.PublishedAt ?? context.Metadata?.PublishedAt;
        return field switch
        {
            "视频标题" => context.VideoTitle,
            "分集标题" => context.Page.Title,
            "分集序号" => context.Page.Number.ToString(),
            "分集序号补零" => FormatPageNumber(context.Page.Number, context.TotalPages),
            "资源类型" => context.Metadata?.ResourceType ?? (context.ProfileKind == DownloadNamingProfileKind.MultiEpisode ? "多集内容" : "视频"),
            "下载类型" => context.DownloadMode switch { DownloadMode.VideoOnly => "仅视频", DownloadMode.AudioOnly => "仅音频", _ => "视频+音频" },
            "AV号" => PrefixId("av", aid),
            "BV号" => bvid ?? string.Empty,
            "CID" => string.IsNullOrWhiteSpace(context.Page.Cid) ? string.Empty : context.Page.Cid,
            "SS号" => PrefixId("ss", seasonId),
            "EP号" => PrefixId("ep", episodeId),
            "UP主昵称" => context.Metadata?.OwnerName ?? string.Empty,
            "UP主UID" => context.Metadata?.OwnerId ?? string.Empty,
            "发布时间" => FormatDate(published),
            "分集发布时间" => FormatDate(episode?.PublishedAt),
            "下载日期" => context.DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd"),
            "下载时间" => context.DownloadedAt.ToLocalTime().ToString("HH-mm-ss"),
            "画质" => context.Video?.Quality ?? string.Empty,
            "分辨率" => context.Video?.Resolution ?? string.Empty,
            "帧率" => FormatFps(context.Video?.Fps),
            "视频编码" => context.Video?.Codec ?? string.Empty,
            "视频码率" => FormatBitrate(context.Video?.BitrateKbps ?? 0),
            "音频编码" => context.Audio?.Codec ?? string.Empty,
            "音频码率" => FormatBitrate(context.Audio?.BitrateKbps ?? 0),
            "接口类型" => context.ApiMode.Trim().ToUpperInvariant(),
            _ => string.Empty
        };
    }

    private static void ValidateTemplate(string template, DownloadNamingFieldSections section, bool required, string label)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            if (required) throw new InvalidOperationException($"{label}模板不能为空");
            return;
        }
        if (template.Contains('/') || template.Contains('\\') || template.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(template) || DrivePrefixRegex().IsMatch(template))
            throw new InvalidOperationException($"{label}只能描述单个名称，不能包含路径分隔符、.. 或绝对路径");
        var unknown = FieldRegex().Matches(template)
            .Select(match => match.Groups[1].Value)
            .Where(name => SupportedFields.All(field => !field.Name.Equals(name, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"{label}包含未知字段：{string.Join('、', unknown)}");
        var unavailable = FieldRegex().Matches(template)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => SupportedFields.First(field => field.Name == name).Sections.HasFlag(section) == false)
            .ToList();
        if (unavailable.Count > 0) throw new InvalidOperationException($"{label}不支持字段：{string.Join('、', unavailable)}");
    }

    private static string ResolveUniqueStem(string leaf, string stem, ISet<string> reservedPaths, bool allowPartialReuse)
    {
        for (var index = 1; index < 10000; index++)
        {
            var candidate = index == 1 ? stem : $"{stem} ({index})";
            var key = Path.Combine(leaf, candidate);
            if (reservedPaths.Contains(key) || HasConflict(leaf, candidate, allowPartialReuse)) continue;
            reservedPaths.Add(key);
            return candidate;
        }
        throw new IOException("无法生成不冲突的下载文件名");
    }

    private static bool HasConflict(string directory, string stem, bool allowPartialReuse)
    {
        if (!Directory.Exists(directory)) return false;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if (!(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))) continue;
                if (allowPartialReuse && File.Exists(path) && PartialExtensions.Contains(Path.GetExtension(path))) continue;
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    private static string EnsureSafePath(string root, string component)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, component));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("命名规则生成的路径超出下载目录");
        EnsurePathLength(path);
        return path;
    }

    private static void EnsurePathLength(string path)
    {
        if (Path.GetFullPath(path).Length > 230) throw new PathTooLongException("命名规则生成的路径过长，请缩短下载目录或模板");
    }

    private static string SanitizeComponent(string value, string fallback, int maximumLength)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => character < 32 || invalid.Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = fallback;
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) sanitized = "_" + sanitized;
        if (sanitized.Length > maximumLength) sanitized = sanitized[..maximumLength].TrimEnd('.', ' ');
        return sanitized;
    }

    private static string CollapseSeparators(string value)
    {
        var result = EmptyBracketRegex().Replace(value, string.Empty);
        result = RepeatedDotRegex().Replace(result, ".");
        result = RepeatedSpaceRegex().Replace(result, " ");
        result = RepeatedUnderscoreRegex().Replace(result, "_");
        result = RepeatedDashRegex().Replace(result, "-");
        result = MixedSeparatorsRegex().Replace(result, "$1");
        return result.Trim().Trim('.', ' ', '_', '-');
    }

    private static DownloadNamingContext CreateSampleContext(DownloadNamingProfile profile, DownloadNamingProfileKind kind, string rootDirectory)
    {
        var metadata = new BilibiliVideoMetadata
        {
            Title = "示例视频",
            OwnerName = "示例UP主",
            OwnerId = "123456",
            Aid = "123456789",
            Bvid = "BV1Example",
            SeasonId = kind == DownloadNamingProfileKind.MultiEpisode ? "12345" : string.Empty,
            SourceEpisodeId = kind == DownloadNamingProfileKind.MultiEpisode ? "67890" : string.Empty,
            ResourceType = kind == DownloadNamingProfileKind.MultiEpisode ? "番剧" : "视频",
            PublishedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(8)),
            EpisodesByCid = new Dictionary<string, BilibiliEpisodeMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["987654"] = new("987654", "123456789", "BV1Example", "67890", new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.FromHours(8)))
            }
        };
        return new DownloadNamingContext
        {
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? @"D:\Downloads" : rootDirectory,
            SourceUrl = kind == DownloadNamingProfileKind.MultiEpisode ? "https://www.bilibili.com/bangumi/play/ss12345" : "https://www.bilibili.com/video/BV1Example",
            VideoTitle = "示例视频",
            Page = new PageInfo(1, "987654", "第一集", "24m"),
            Profile = profile,
            ProfileKind = kind,
            TotalPages = kind == DownloadNamingProfileKind.MultiEpisode ? 12 : 1,
            Metadata = metadata,
            Video = new VideoStreamInfo(1, "4K 超清", "3840x2160", 3840, 2160, "HEVC", "60", "12000 kbps", 12000, "1 GB"),
            Audio = new AudioStreamInfo(2, "E-AC-3", "384 kbps", 384, "50 MB"),
            DownloadedAt = new DateTimeOffset(2026, 7, 18, 20, 30, 0, TimeSpan.FromHours(8))
        };
    }

    private static SourceIds ParseSourceIds(string url)
    {
        var av = AvRegex().Match(url);
        var bv = BvRegex().Match(url);
        var ss = SeasonRegex().Match(url);
        var ep = EpisodeRegex().Match(url);
        return new SourceIds(
            av.Success ? av.Groups[1].Value : string.Empty,
            bv.Success ? bv.Groups[1].Value : string.Empty,
            ss.Success ? ss.Groups[1].Value : string.Empty,
            ep.Success ? ep.Groups[1].Value : string.Empty);
    }

    private static string PrefixId(string prefix, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value : prefix + value;
    }

    private static string FormatPageNumber(int number, int total)
    {
        var width = Math.Max(2, Math.Max(total, number).ToString().Length);
        return number.ToString(new string('0', width));
    }

    private static string FormatDate(DateTimeOffset? value) => value?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd") ?? string.Empty;
    private static string FormatBitrate(int value) => value > 0 ? $"{value}kbps" : string.Empty;
    private static string FormatFps(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Contains("fps", StringComparison.OrdinalIgnoreCase) ? value : $"{value}fps";

    private sealed record RenderedComponent(string Value, IReadOnlyList<string> MissingFields, bool WasSanitized);
    private sealed record RenderedPath(string MainDirectory, string LeafDirectory, string RelativePath, string FileStem, IReadOnlyList<string> Warnings);
    private sealed record SourceIds(string Aid, string Bvid, string SeasonId, string EpisodeId);

    [GeneratedRegex(@"\{([^{}]+)\}")] private static partial Regex FieldRegex();
    [GeneratedRegex(@"^[A-Za-z]:")] private static partial Regex DrivePrefixRegex();
    [GeneratedRegex(@"\[\s*\]|\(\s*\)|【\s*】")] private static partial Regex EmptyBracketRegex();
    [GeneratedRegex(@"\.{2,}")] private static partial Regex RepeatedDotRegex();
    [GeneratedRegex(@" {2,}")] private static partial Regex RepeatedSpaceRegex();
    [GeneratedRegex(@"_{2,}")] private static partial Regex RepeatedUnderscoreRegex();
    [GeneratedRegex(@"-{2,}")] private static partial Regex RepeatedDashRegex();
    [GeneratedRegex(@"([._ -])[._ -]+")] private static partial Regex MixedSeparatorsRegex();
    [GeneratedRegex(@"(?:^|/|\b)av(\d+)", RegexOptions.IgnoreCase)] private static partial Regex AvRegex();
    [GeneratedRegex(@"(BV[0-9A-Za-z]+)", RegexOptions.IgnoreCase)] private static partial Regex BvRegex();
    [GeneratedRegex(@"(?:^|/|\b)ss(\d+)", RegexOptions.IgnoreCase)] private static partial Regex SeasonRegex();
    [GeneratedRegex(@"(?:^|/|\b)ep(\d+)", RegexOptions.IgnoreCase)] private static partial Regex EpisodeRegex();
}
