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
        return new BilibiliVideoMetadata(
            GetString(data, "title"),
            NormalizeImage(GetString(data, "pic")),
            owner.ValueKind == JsonValueKind.Object ? GetString(owner, "name") : string.Empty,
            owner.ValueKind == JsonValueKind.Object ? NormalizeImage(GetString(owner, "face")) : string.Empty,
            string.IsNullOrWhiteSpace(bvid) ? string.Empty : $"https://www.bilibili.com/video/{bvid}");
    }

    private async Task<BilibiliVideoMetadata?> GetPgcAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"https://api.bilibili.com/pgc/view/web/season?{query}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryData(document.RootElement, "result", out var result)) return null;
        var up = result.TryGetProperty("up_info", out var upElement) ? upElement : default;
        var seasonId = result.TryGetProperty("season_id", out var id) && id.TryGetInt64(out var value) ? value : 0;
        return new BilibiliVideoMetadata(
            GetString(result, "title"),
            NormalizeImage(GetString(result, "cover")),
            up.ValueKind == JsonValueKind.Object ? GetString(up, "uname") : string.Empty,
            up.ValueKind == JsonValueKind.Object ? NormalizeImage(GetString(up, "avatar")) : string.Empty,
            seasonId > 0 ? $"https://www.bilibili.com/bangumi/play/ss{seasonId}" : string.Empty);
    }

    private static bool TryData(JsonElement root, string property, out JsonElement data)
    {
        data = default;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0) return false;
        return root.TryGetProperty(property, out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string NormalizeImage(string value) => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + value[7..] : value;

    [GeneratedRegex("(BV[0-9A-Za-z]+)", RegexOptions.IgnoreCase)] private static partial Regex BvRegex();
    [GeneratedRegex("(?:^|/|\\b)av(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex AvRegex();
    [GeneratedRegex("(?:^|/|\\b)ss(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex SeasonRegex();
    [GeneratedRegex("(?:^|/|\\b)ep(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex EpisodeRegex();
}
