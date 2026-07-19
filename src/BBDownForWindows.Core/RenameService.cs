using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class RenameService(
    IProcessRunner processRunner,
    IToolLocator toolLocator,
    ISettingsStore settingsStore,
    IRenameHistoryStore historyStore) : IRenameService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".mov", ".webm" };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".srt", ".ass", ".ssa", ".vtt", ".xml", ".json", ".jpg", ".jpeg", ".png", ".webp", ".nfo" };

    private static readonly Regex[] EpisodePatterns =
    [
        new(@"[Ee](\d{1,3})", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"第(\d{1,3})[集话話]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\[P0*(\d{1,3})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(\d{1,3})[\s._-]+(?:4[Kk]|8[Kk]|\d{3,4}[Pp])(?:[\s._-]|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"[\s._-](\d{1,3})[\s._-]", RegexOptions.Compiled | RegexOptions.CultureInvariant)
    ];

    private static readonly string[] ReservedNames =
    ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static readonly HashSet<string> KnownTemplateFields = new(StringComparer.Ordinal)
    { "中文名", "英文名", "年份", "季", "集", "集名", "分辨率", "来源", "动态范围", "编码", "音频", "帧率", "扩展名" };

    public Task<IReadOnlyList<RenameFileEntry>> ScanAsync(string directoryPath, IReadOnlyCollection<string>? preferredFiles = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException("请选择有效的视频文件夹");

        var preferred = preferredFiles is null
            ? null
            : preferredFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new DirectoryInfo(directoryPath).EnumerateFiles()
            .Where(file => VideoExtensions.Contains(file.Extension))
            .Select(file => new RenameFileEntry
            {
                SourcePath = file.FullName,
                DetectedEpisode = ExtractEpisodeNumber(file.Name),
                IsSelected = preferred is null || preferred.Contains(file.FullName)
            })
            .OrderBy(file => file.DetectedEpisode is null)
            .ThenBy(file => file.DetectedEpisode ?? 0)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RenameFileEntry>>(files);
    }

    public static void ValidateTemplatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new InvalidOperationException("命名模板不能为空");
        var unknown = Regex.Matches(pattern, @"\{([^}]+)\}")
            .Select(match => match.Groups[1].Value)
            .Where(field => !KnownTemplateFields.Contains(field))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"命名模板包含未知字段：{string.Join('、', unknown)}");
    }

    public async Task<RenamePreview> BuildPreviewAsync(RenamePreviewRequest request, TaskExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidatePreviewRequest(request);
        var selected = request.Files.Where(file => file.IsSelected).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("请至少选择一个视频文件");

        var settings = await settingsStore.LoadAsync(cancellationToken);
        var tools = toolLocator.Locate(settings);
        if (string.IsNullOrWhiteSpace(tools.Ffprobe) || !File.Exists(tools.Ffprobe))
            throw new FileNotFoundException("找不到 ffprobe.exe，无法读取视频规格");

        context.AppendLog($"正在扫描 {selected.Count} 个视频的媒体信息…\n");
        var preview = new RenamePreview { Request = request };
        var allVideos = request.Files.Select(file => Path.GetFullPath(file.SourcePath)).ToList();
        var episodeIndex = 0;
        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            episodeIndex++;
            var episode = request.MediaType == RenameMediaType.Series
                ? request.UseCustomEpisodes ? request.StartEpisode + episodeIndex - 1 : file.DetectedEpisode ?? episodeIndex
                : (int?)null;
            var media = await ProbeMediaAsync(tools.Ffprobe, file.SourcePath, cancellationToken);
            var episodeName = episode is not null && request.EpisodeNames.TryGetValue(episode.Value, out var value) ? value : string.Empty;
            var targetName = RenderFileName(request, file.SourcePath, media, episode, episodeName);
            var targetPath = Path.Combine(request.DirectoryPath, targetName);
            var item = new RenamePreviewItem
            {
                SourcePath = Path.GetFullPath(file.SourcePath),
                TargetPath = Path.GetFullPath(targetPath),
                EpisodeNumber = episode,
                Media = media
            };
            item.Operations.Add(new RenameFileOperation(item.SourcePath, item.TargetPath));
            foreach (var sidecar in FindSidecars(file.SourcePath, allVideos))
            {
                var oldStem = Path.GetFileNameWithoutExtension(file.SourcePath);
                var newStem = Path.GetFileNameWithoutExtension(targetName);
                var suffix = sidecar.Name[oldStem.Length..];
                item.Operations.Add(new RenameFileOperation(sidecar.FullName, Path.Combine(request.DirectoryPath, newStem + suffix), true));
            }
            preview.Items.Add(item);
            context.AppendLog($"{file.Name}  →  {targetName}\n");
        }

        ValidateOperations(preview);
        if (preview.Warnings.Count > 0)
            foreach (var warning in preview.Warnings) context.AppendLog($"提示: {warning}\n");
        if (preview.Errors.Count > 0)
            foreach (var error in preview.Errors) context.AppendLog($"错误: {error}\n");
        return preview;
    }

    public async Task<RenameExecutionResult> ExecuteAsync(RenamePreview preview, TaskExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateOperations(preview);
        if (!preview.CanExecute) throw new InvalidOperationException("当前预览包含冲突，无法执行重命名");
        var operations = preview.Operations.Where(operation => !string.Equals(operation.SourcePath, operation.TargetPath, StringComparison.Ordinal)).ToList();
        if (operations.Count == 0) throw new InvalidOperationException("没有需要执行的文件名变更");

        context.AppendLog($"开始重命名 {operations.Count} 个文件…\n");
        await ExecuteOperationsAsync(operations, context, cancellationToken);
        var request = preview.Request;
        var record = new RenameHistoryRecord
        {
            DirectoryPath = request.DirectoryPath,
            MediaType = request.MediaType,
            ChineseTitle = request.ChineseTitle,
            EnglishTitle = request.EnglishTitle,
            Year = request.Year,
            Season = request.Season,
            TemplateName = request.TemplateName,
            Operations = operations
        };
        await historyStore.AddAsync(record, cancellationToken);
        context.AppendLog($"重命名完成，共处理 {operations.Count} 个文件。\n");
        return new RenameExecutionResult(record.Id, operations.Count, record.DirectoryPath);
    }

    public async Task<RenameExecutionResult> UndoAsync(Guid historyId, TaskExecutionContext context, CancellationToken cancellationToken = default)
    {
        var record = (await historyStore.LoadAsync(cancellationToken)).FirstOrDefault(item => item.Id == historyId)
            ?? throw new InvalidOperationException("找不到对应的重命名历史");
        if (record.UndoneAt is not null) throw new InvalidOperationException("该重命名记录已经撤销");
        var reverse = record.Operations.Select(operation => new RenameFileOperation(operation.TargetPath, operation.SourcePath, operation.IsSidecar)).ToList();
        ValidateStandaloneOperations(reverse);
        context.AppendLog($"正在撤销 {reverse.Count} 个文件名变更…\n");
        await ExecuteOperationsAsync(reverse, context, cancellationToken);
        await historyStore.MarkUndoneAsync(record.Id, DateTimeOffset.Now, cancellationToken);
        context.AppendLog("撤销完成。\n");
        return new RenameExecutionResult(record.Id, reverse.Count, record.DirectoryPath);
    }

    public static int? ExtractEpisodeNumber(string fileName)
    {
        foreach (var pattern in EpisodePatterns)
        {
            var match = pattern.Match(fileName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var episode)) return episode;
        }
        return null;
    }

    public static string ExtractTitleFromFolder(string folderName)
    {
        var cleaned = folderName;
        foreach (var pattern in new[]
        {
            @"\s*番外篇", @"\s*(番外|剧场版|剧版|外传|特別篇)", @"\s*\bSP\b",
            @"\s*(第[一二三四五六七八九十0-9]+季)", @"\s*(Season\s*[0-9IViv]+)",
            @"\s*\([^)]*\)", @"\s*（[^）]*）"
        }) cleaned = Regex.Replace(cleaned, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var chinese = Regex.Matches(cleaned.Trim(), @"[\u4e00-\u9fff]+")
            .Select(match => match.Value)
            .OrderByDescending(value => value.Length)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(chinese) ? chinese : cleaned.Trim();
    }

    public static string SanitizeTitleDirectoryName(string title)
    {
        var value = SanitizeFileComponent(title, "Beflow 下载", 120);
        return value.TrimEnd(' ', '.');
    }

    internal static MediaMetadata ParseMediaMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return MediaMetadata.Default;
            JsonElement? video = null;
            var audioTracks = new List<(string Codec, int Channels, bool Atmos)>();
            foreach (var stream in streams.EnumerateArray())
            {
                var type = ReadString(stream, "codec_type");
                if (type == "video" && video is null) video = stream;
                if (type == "audio")
                {
                    var codec = NormalizeAudioCodec(ReadString(stream, "codec_name"));
                    var channels = ReadInt(stream, "channels");
                    var profile = ReadString(stream, "profile");
                    audioTracks.Add((codec, channels <= 0 ? 2 : channels, profile.Contains("atmos", StringComparison.OrdinalIgnoreCase)));
                }
            }
            if (video is null) return MediaMetadata.Default;
            var width = ReadInt(video.Value, "width");
            var height = ReadInt(video.Value, "height");
            var resolution = FormatResolution(width, height);
            var codecName = ReadString(video.Value, "codec_name").ToUpperInvariant();
            var videoCodec = codecName switch { "H264" => "AVC", "HEVC" => "HEVC", "AV1" => "AV1", "VP9" => "VP9", _ => string.IsNullOrWhiteSpace(codecName) ? "unknown" : codecName };
            var transfer = ReadString(video.Value, "color_transfer");
            var primaries = ReadString(video.Value, "color_primaries");
            var sideData = video.Value.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array
                ? string.Join(' ', list.EnumerateArray().Select(item => ReadString(item, "side_data_type")))
                : string.Empty;
            var dynamicRange = sideData.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || sideData.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)
                ? "DV"
                : (transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) || transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase)) && primaries.Equals("bt2020", StringComparison.OrdinalIgnoreCase)
                    ? "HDR"
                    : string.Empty;
            var fps = FormatFrameRate(ReadString(video.Value, "r_frame_rate"));
            var bestAudio = audioTracks.Count == 0
                ? (Codec: "AAC", Channels: 2, Atmos: false)
                : audioTracks.MaxBy(track => (AudioPriority(track.Codec, track.Atmos), track.Channels));
            var audio = FormatAudio(bestAudio.Codec, bestAudio.Channels, bestAudio.Atmos);
            return new MediaMetadata(resolution, dynamicRange, videoCodec, audio, fps);
        }
        catch (JsonException) { return MediaMetadata.Default; }
    }

    private async Task<MediaMetadata> ProbeMediaAsync(string ffprobe, string sourcePath, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-v", "error", "-show_streams",
            "-show_entries", "stream=index,codec_type,codec_name,width,height,color_transfer,color_primaries,r_frame_rate,channels,profile:stream_side_data=side_data_type",
            "-of", "json", sourcePath
        };
        var result = await processRunner.RunAsync(new ProcessRunRequest(ffprobe, arguments, Path.GetDirectoryName(sourcePath)!), null, cancellationToken);
        if (result.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) return MediaMetadata.Default;
        return ParseMediaMetadata(result.Output);
    }

    private static string RenderFileName(RenamePreviewRequest request, string sourcePath, MediaMetadata media, int? episode, string episodeName)
    {
        var extension = Path.GetExtension(sourcePath);
        var result = request.TemplatePattern;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{中文名}"] = request.ChineseTitle.Trim(),
            ["{英文名}"] = request.EnglishTitle.Trim(),
            ["{年份}"] = request.Year.Trim(),
            ["{季}"] = request.MediaType == RenameMediaType.Series ? $"S{request.Season:00}" : string.Empty,
            ["{集}"] = request.MediaType == RenameMediaType.Series && episode is not null ? $"E{episode:00}" : string.Empty,
            ["{集名}"] = episodeName,
            ["{分辨率}"] = media.Resolution,
            ["{来源}"] = "WEB-DL",
            ["{动态范围}"] = media.DynamicRange,
            ["{编码}"] = media.VideoCodec,
            ["{音频}"] = media.Audio,
            ["{帧率}"] = media.FrameRate,
            ["{扩展名}"] = extension
        };
        foreach (var pair in values) result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        result = Regex.Replace(result, @"\.{2,}", ".");
        result = Regex.Replace(result, @" {2,}", " ");
        result = Regex.Replace(result, @"_{2,}", "_");
        result = Regex.Replace(result, @"-{2,}", "-");
        var currentExtension = Path.GetExtension(result);
        if (!string.IsNullOrEmpty(request.FilenameSuffix))
        {
            var stem = currentExtension.Length > 0 ? result[..^currentExtension.Length] : result;
            result = stem.TrimEnd('.', ' ', '_', '-') + request.FilenameSuffix.Trim() + currentExtension;
        }
        result = result.Trim().Trim('.', ' ');
        return SanitizeFileComponent(result, Path.GetFileName(sourcePath), 200);
    }

    private static string SanitizeFileComponent(string value, string fallback, int maximumLength)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => character < 32 || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = fallback;
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) sanitized = "_" + sanitized;
        if (sanitized.Length <= maximumLength) return sanitized;
        var extension = Path.GetExtension(sanitized);
        var keep = Math.Max(1, maximumLength - extension.Length);
        return sanitized[..keep].TrimEnd('.', ' ') + extension;
    }

    private static void ValidatePreviewRequest(RenamePreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DirectoryPath) || !Directory.Exists(request.DirectoryPath)) throw new DirectoryNotFoundException("视频文件夹不存在");
        ValidateTemplatePattern(request.TemplatePattern);
        if (request.Season < 0) throw new InvalidOperationException("季数不能小于 0");
        if (request.StartEpisode < 1) throw new InvalidOperationException("起始集数必须大于 0");
    }

    private static void ValidateOperations(RenamePreview preview)
    {
        preview.Errors.Clear();
        foreach (var item in preview.Items) item.Errors.Clear();
        var operations = preview.Operations;
        try { ValidateStandaloneOperations(operations); }
        catch (InvalidOperationException exception) { preview.Errors.AddRange(exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries)); }
    }

    private static void ValidateStandaloneOperations(IReadOnlyList<RenameFileOperation> operations)
    {
        var errors = new List<string>();
        var sourceSet = operations.Select(operation => Path.GetFullPath(operation.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            var source = Path.GetFullPath(operation.SourcePath);
            var target = Path.GetFullPath(operation.TargetPath);
            if (!File.Exists(source)) errors.Add($"源文件不存在：{Path.GetFileName(source)}");
            if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
                errors.Add($"目标文件越过所选文件夹：{Path.GetFileName(target)}");
            if (target.Length > 240) errors.Add($"目标路径过长：{Path.GetFileName(target)}");
            var targetStem = Path.GetFileNameWithoutExtension(target);
            if (ReservedNames.Contains(targetStem, StringComparer.OrdinalIgnoreCase)) errors.Add($"目标文件使用 Windows 保留名称：{Path.GetFileName(target)}");
            if (targets.TryGetValue(target, out var existing) && !string.Equals(existing, source, StringComparison.OrdinalIgnoreCase))
                errors.Add($"多个文件会重命名为同一个目标：{Path.GetFileName(target)}");
            else targets[target] = source;
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)) && !sourceSet.Contains(target))
                errors.Add($"目标文件已存在：{Path.GetFileName(target)}");
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join('\n', errors.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<FileInfo> FindSidecars(string videoPath, IReadOnlyList<string> allVideoPaths)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(videoPath)!);
        var selectedStem = Path.GetFileNameWithoutExtension(videoPath)!;
        var videoStems = allVideoPaths.Select(path => Path.GetFileNameWithoutExtension(path)!).ToList();
        var result = new List<FileInfo>();
        foreach (var file in directory.EnumerateFiles().Where(file => SidecarExtensions.Contains(file.Extension)))
        {
            var candidates = videoStems
                .Where(stem => file.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(stem => stem.Length)
                .ToList();
            if (candidates.Count == 0 || !candidates[0].Equals(selectedStem, StringComparison.OrdinalIgnoreCase)) continue;
            if (candidates.Count > 1 && candidates[0].Length == candidates[1].Length) continue;
            result.Add(file);
        }
        return result;
    }

    private static async Task ExecuteOperationsAsync(IReadOnlyList<RenameFileOperation> operations, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        ValidateStandaloneOperations(operations);
        var staged = new List<(RenameFileOperation Operation, string Temporary)>();
        var completed = new List<(RenameFileOperation Operation, string Temporary)>();
        try
        {
            for (var index = 0; index < operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = operations[index];
                if (string.Equals(operation.SourcePath, operation.TargetPath, StringComparison.Ordinal)) continue;
                var temporary = Path.Combine(Path.GetDirectoryName(operation.SourcePath)!, $".__beflow_{Guid.NewGuid():N}_{index}{Path.GetExtension(operation.SourcePath)}");
                File.Move(operation.SourcePath, temporary);
                staged.Add((operation, temporary));
            }
            foreach (var entry in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(entry.Temporary, entry.Operation.TargetPath);
                completed.Add(entry);
                context.AppendLog($"{Path.GetFileName(entry.Operation.SourcePath)} → {Path.GetFileName(entry.Operation.TargetPath)}\n");
            }
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            var rollbackStaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in completed)
            {
                try
                {
                    if (!File.Exists(entry.Operation.TargetPath)) continue;
                    var rollbackTemporary = Path.Combine(Path.GetDirectoryName(entry.Operation.TargetPath)!, $".__beflow_rollback_{Guid.NewGuid():N}{Path.GetExtension(entry.Operation.TargetPath)}");
                    File.Move(entry.Operation.TargetPath, rollbackTemporary);
                    rollbackStaged[entry.Operation.SourcePath] = rollbackTemporary;
                }
                catch (Exception rollback) { rollbackErrors.Add($"{Path.GetFileName(entry.Operation.TargetPath)}: {rollback.Message}"); }
            }
            foreach (var entry in staged.AsEnumerable().Reverse())
            {
                try
                {
                    var current = rollbackStaged.TryGetValue(entry.Operation.SourcePath, out var rollbackTemporary) ? rollbackTemporary : entry.Temporary;
                    if (File.Exists(current)) File.Move(current, entry.Operation.SourcePath);
                }
                catch (Exception rollback) { rollbackErrors.Add($"{Path.GetFileName(entry.Operation.SourcePath)}: {rollback.Message}"); }
            }
            if (exception is OperationCanceledException && rollbackErrors.Count == 0) throw;
            var message = $"重命名失败：{exception.Message}";
            if (rollbackErrors.Count == 0) message += "；已回滚所有变更";
            else message += $"；回滚也出现错误：{string.Join("；", rollbackErrors)}";
            throw new InvalidOperationException(message, exception);
        }
        await Task.CompletedTask;
    }

    private static string FormatResolution(int width, int height)
    {
        if (width <= 0 && height <= 0) return "1080p";
        if (width >= 3800 || height >= 2000) return "2160p";
        if (width >= 2500 || height >= 1300) return "1440p";
        if (width >= 1900 || height >= 900) return "1080p";
        if (width >= 1200 || height >= 650) return "720p";
        return height > 0 ? $"{height}p" : "1080p";
    }

    private static string NormalizeAudioCodec(string value) => value.Trim().ToLowerInvariant() switch
    {
        "aac" => "AAC", "ac3" or "ac-3" => "AC3", "eac3" or "e-ac-3" => "DDP",
        "truehd" or "mlp" => "TrueHD", "dts" or "dca" => "DTS", "flac" => "FLAC",
        var codec => string.IsNullOrWhiteSpace(codec) ? "AAC" : codec.ToUpperInvariant()
    };

    private static int AudioPriority(string codec, bool atmos) => atmos ? 70 : codec switch
    { "TrueHD" => 60, "DTS" => 50, "DDP" => 40, "AC3" => 30, "AAC" => 20, _ => 10 };

    private static string FormatAudio(string codec, int channels, bool atmos)
    {
        var channel = channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", _ => $"{Math.Max(channels, 1)}.0" };
        return $"{codec}.{channel}{(atmos ? ".Atmos" : string.Empty)}";
    }

    private static string FormatFrameRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], out var numerator) || !double.TryParse(parts[1], out var denominator) || denominator == 0) return string.Empty;
        var fps = (int)Math.Round(numerator / denominator);
        return fps is 50 or 60 ? $"{fps}fps" : string.Empty;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }
}
