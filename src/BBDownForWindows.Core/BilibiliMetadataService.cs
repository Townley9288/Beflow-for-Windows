using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed partial class BilibiliMetadataService(HttpClient httpClient) : IBilibiliMetadataService
{
    public async Task<BilibiliVideoMetadata?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = url.Trim();
            var bv = BvRegex().Match(target);
            if (bv.Success) return await GetUgcAsync($"bvid={Uri.EscapeDataString(bv.Groups[1].Value)}", cancellationToken);
            var av = AvRegex().Match(target);
            if (av.Success) return await GetUgcAsync($"aid={av.Groups[1].Value}", cancellationToken);
            var ss = SeasonRegex().Match(target);
            if (ss.Success) return await GetPgcAsync($"season_id={ss.Groups[1].Value}", cancellationToken);
            var ep = EpisodeRegex().Match(target);
            if (ep.Success) return await GetPgcAsync($"ep_id={ep.Groups[1].Value}", cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<BilibiliVideoMetadata?> GetUgcAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"https://api.bilibili.com/x/web-interface/view?{query}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryData(document.RootElement, "data", out var data)) return null;
        var owner = data.TryGetProperty("owner", out var ownerElement) ? ownerElement : default;
        var bvid = GetString(data, "bvid");
        var episodes = new Dictionary<string, BilibiliEpisodeMetadata>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                var cid = GetScalar(page, "cid");
                if (string.IsNullOrWhiteSpace(cid)) continue;
                episodes[cid] = new BilibiliEpisodeMetadata(cid, GetScalar(data, "aid"), bvid, string.Empty, ReadTimestamp(data, "pubdate"));
            }
        }
        return new BilibiliVideoMetadata
        {
            Title = GetString(data, "title"),
            CoverUrl = NormalizeImage(GetString(data, "pic")),
            OwnerName = owner.ValueKind == JsonValueKind.Object ? GetString(owner, "name") : string.Empty,
            OwnerId = owner.ValueKind == JsonValueKind.Object ? GetScalar(owner, "mid") : string.Empty,
            OwnerAvatarUrl = owner.ValueKind == JsonValueKind.Object ? NormalizeImage(GetString(owner, "face")) : string.Empty,
            CanonicalUrl = string.IsNullOrWhiteSpace(bvid) ? string.Empty : $"https://www.bilibili.com/video/{bvid}",
            ResourceType = episodes.Count > 1 ? "多P视频" : "视频",
            Aid = GetScalar(data, "aid"),
            Bvid = bvid,
            PublishedAt = ReadTimestamp(data, "pubdate"),
            EpisodesByCid = episodes
        };
    }

    private async Task<BilibiliVideoMetadata?> GetPgcAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"https://api.bilibili.com/pgc/view/web/season?{query}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryData(document.RootElement, "result", out var result)) return null;
        var up = result.TryGetProperty("up_info", out var upElement) ? upElement : default;
        var seasonId = GetScalar(result, "season_id");
        var sourceEpisodeId = query.StartsWith("ep_id=", StringComparison.OrdinalIgnoreCase) ? query[6..] : string.Empty;
        var episodes = new Dictionary<string, BilibiliEpisodeMetadata>(StringComparer.OrdinalIgnoreCase);
        BilibiliEpisodeMetadata? sourceEpisode = null;
        if (result.TryGetProperty("episodes", out var episodeArray) && episodeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in episodeArray.EnumerateArray())
            {
                var cid = GetScalar(item, "cid");
                if (string.IsNullOrWhiteSpace(cid)) continue;
                var episode = new BilibiliEpisodeMetadata(
                    cid,
                    GetScalar(item, "aid"),
                    GetString(item, "bvid"),
                    GetScalar(item, "id"),
                    ReadTimestamp(item, "pub_time") ?? ReadTimestamp(item, "pubdate"));
                episodes[cid] = episode;
                if (!string.IsNullOrWhiteSpace(sourceEpisodeId) && episode.EpisodeId == sourceEpisodeId) sourceEpisode = episode;
            }
        }
        var publish = result.TryGetProperty("publish", out var publishElement) && publishElement.ValueKind == JsonValueKind.Object ? publishElement : default;
        var publishedAt = publish.ValueKind == JsonValueKind.Object
            ? ReadTimestamp(publish, "pub_time") ?? ReadTimestamp(publish, "pub_time_show")
            : null;
        publishedAt ??= episodes.Values.OrderBy(item => item.PublishedAt).FirstOrDefault()?.PublishedAt;
        return new BilibiliVideoMetadata
        {
            Title = GetString(result, "title"),
            CoverUrl = NormalizeImage(GetString(result, "cover")),
            OwnerName = up.ValueKind == JsonValueKind.Object ? GetString(up, "uname") : string.Empty,
            OwnerId = up.ValueKind == JsonValueKind.Object ? GetScalar(up, "mid") : string.Empty,
            OwnerAvatarUrl = up.ValueKind == JsonValueKind.Object ? NormalizeImage(GetString(up, "avatar")) : string.Empty,
            CanonicalUrl = string.IsNullOrWhiteSpace(seasonId) ? string.Empty : $"https://www.bilibili.com/bangumi/play/ss{seasonId}",
            ResourceType = "番剧",
            Aid = sourceEpisode?.Aid ?? string.Empty,
            Bvid = sourceEpisode?.Bvid ?? string.Empty,
            SeasonId = seasonId,
            SourceEpisodeId = sourceEpisodeId,
            PublishedAt = publishedAt,
            EpisodesByCid = episodes
        };
    }

    private static bool TryData(JsonElement root, string property, out JsonElement data)
    {
        data = default;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0) return false;
        return root.TryGetProperty(property, out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string GetScalar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (long.TryParse(text, out seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static string NormalizeImage(string value) => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + value[7..] : value;

    [GeneratedRegex("(BV[0-9A-Za-z]+)", RegexOptions.IgnoreCase)] private static partial Regex BvRegex();
    [GeneratedRegex("(?:^|/|\\b)av(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex AvRegex();
    [GeneratedRegex("(?:^|/|\\b)ss(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex SeasonRegex();
    [GeneratedRegex("(?:^|/|\\b)ep(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex EpisodeRegex();
}
