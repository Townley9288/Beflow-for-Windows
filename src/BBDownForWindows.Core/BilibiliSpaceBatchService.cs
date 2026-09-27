using System.Runtime.CompilerServices;

namespace BBDownForWindows.Core;

public sealed record BilibiliSpaceParseResult(BilibiliSpaceVideo Video, DownloadCatalog? Catalog, string Error);

public sealed class BilibiliSpaceBatchService(IBBDownService downloader)
{
    // BBDownService applies the shared per-process parser limit inside each catalog.
    public async IAsyncEnumerable<BilibiliSpaceParseResult> ParseAsync(IEnumerable<BilibiliSpaceVideo> videos,
        IReadOnlyDictionary<string, DownloadCatalog> existing, string apiMode, TaskExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var video in videos.DistinctBy(v => v.Bvid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            existing.TryGetValue(video.Bvid, out var previous);
            var pending = previous?.AllPages.Where(p => !previous.Episodes.Any(e => e.Page.Number == p.Number && e.State == DownloadEpisodeParseState.Ready)).Select(p => p.Number).ToArray();
            if (previous is not null && previous.AllPages.Count > 0 && pending!.Length == 0) continue;
            DownloadCatalog? catalog = null;
            var error = "";
            try
            {
                catalog = await downloader.ParseDownloadAsync(new(video.Url, DownloadParseMode.All, apiMode,
                    pending is { Length: > 0 } ? string.Join(',', pending) : ""), null, context.WithPrefix(video.Bvid), cancellationToken);
                if (previous is not null)
                {
                    catalog = new DownloadCatalog
                    {
                        SourceUrl = catalog.SourceUrl, ResolvedUrl = catalog.ResolvedUrl, Title = catalog.Title,
                        Metadata = catalog.Metadata, ParsedAt = catalog.ParsedAt,
                        AllPages = previous.AllPages.Concat(catalog.AllPages).DistinctBy(p => p.Number).OrderBy(p => p.Number).ToList(),
                        Episodes = previous.Episodes.Where(e => e.State == DownloadEpisodeParseState.Ready)
                            .Concat(catalog.Episodes).DistinctBy(e => e.Page.Number).OrderBy(e => e.Page.Number).ToList()
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = ex.Message; }
            yield return new(video, catalog, error);
        }
    }

    public static string OutputRoot(string directory, BilibiliSpaceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("请选择有效的完整下载目录。");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("尚未读取 UP 主名称。");
        if (!long.TryParse(profile.Uid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid) || uid <= 0)
            throw new InvalidOperationException("主页 UID 无效。");
        var safe = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) throw new InvalidOperationException("UP 主名称不能用于文件夹名称。");
        var output = Path.Combine(Path.GetFullPath(directory), $"{safe}（{profile.Uid}）");
        for (var current = new DirectoryInfo(output); current is not null; current = current.Parent)
            if (File.Exists(current.FullName)) throw new IOException($"下载目录被同名文件占用：{current.FullName}");
        return output;
    }

    public static DownloadQueueItem BuildQueueItem(BilibiliSpaceProfile profile,
        DownloadCatalog catalog, IReadOnlyList<EpisodeStreamSelection> selections, AppSettings settings,
        string directory, StreamSelectionRule rule, DownloadMode mode)
    {
        if (selections.Count == 0) throw new InvalidOperationException("没有已选分P。");
        var kind = catalog.AllPages.Count > 1 ? DownloadNamingProfileKind.MultiEpisode : DownloadNamingProfileKind.SingleVideo;
        var options = new DownloadRequest
        {
            Url = catalog.ResolvedUrl, TitleHint = catalog.Title, WorkDirectory = OutputRoot(directory, profile),
            Quality = rule.QualityRule, Encoding = rule.PreferredEncoding, AudioCodec = rule.PreferredAudioCodec,
            AudioBitratePriority = rule.AudioBitratePriority, DownloadMode = mode, ApiMode = settings.ApiMode,
            Danmaku = settings.Danmaku, Subtitle = settings.Subtitle, Cover = settings.Cover, MultiThread = settings.MultiThread,
            UseAria2c = settings.UseAria2c, UposHost = settings.UposHost, Aria2cPath = settings.Aria2cPath,
            Aria2AutoTune = settings.Aria2AutoTune, Aria2MaxConnection = settings.Aria2MaxConnection,
            Aria2Split = settings.Aria2Split, Aria2MaxConcurrentDownloads = settings.Aria2MaxConcurrentDownloads,
            Aria2MinSplitSize = settings.Aria2MinSplitSize, SaveTaskLogs = settings.SaveTaskLogs
        };
        return new DownloadQueueItem
        {
            Kind = DownloadQueueKind.Download, Catalog = catalog,
            Download = new DownloadBatchRequest
            {
                Options = options, Title = catalog.Title, ParsedAt = catalog.ParsedAt, TotalPages = catalog.AllPages.Count,
                NamingProfileKind = kind, NamingProfile = settings.DownloadNaming.GetProfile(kind).Clone(),
                Metadata = catalog.Metadata, Episodes = selections.ToList()
            }
        };
    }
}
