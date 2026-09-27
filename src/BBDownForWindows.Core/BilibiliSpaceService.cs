using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed class BilibiliSpaceService(HttpClient httpClient, ApplicationPaths paths, TimeProvider? timeProvider = null) : IBilibiliSpaceService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim signatureGate = new(1, 1);
    private string mixinKey = "";
    private string signedCredential = "";
    private DateTimeOffset keyExpires;
    public const int PageSize = 30;

    public async Task<BilibiliUserSearchPage> SearchUsersAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("请输入 UP 主名称。", nameof(keyword));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        const string referer = "https://search.bilibili.com/";
        var credential = await ReadCredentialAsync(cancellationToken);
        var key = await GetKeyAsync(referer, credential, cancellationToken, "UP 主搜索");
        var query = Sign(new Dictionary<string, string>
        {
            ["search_type"] = "bili_user", ["keyword"] = keyword.Trim(),
            ["page"] = page.ToString(CultureInfo.InvariantCulture), ["page_size"] = "20", ["web_location"] = "1430654"
        }, key, clock.GetUtcNow().ToUnixTimeSeconds());
        using var json = await GetAsync("x/web-interface/wbi/search/type?" + query,
            referer, cancellationToken, credential, operation: "UP 主搜索");
        var data = json.RootElement.GetProperty("data");
        var number = Number(data, "page");
        var size = Number(data, "pagesize");
        var total = Number(data, "numResults");
        var pages = Number(data, "numPages");
        // The WEB search response omits result when both result and page counts are zero.
        var users = total == 0 && pages == 0 && !data.TryGetProperty("result", out _)
            ? new List<BilibiliSpaceUser>()
            : Required(data, "result", JsonValueKind.Array).EnumerateArray().Select(user => new BilibiliSpaceUser(
            Scalar(user, "mid"), PlainSearchText(Text(user, "uname", true)), Image(user, "upic"), PlainSearchText(Text(user, "usign")),
            Long(user, "fans"), Number(user, "videos"),
            user.TryGetProperty("official_verify", out var verification) && verification.ValueKind == JsonValueKind.Object
                ? PlainSearchText(Text(verification, "desc")) : "")).DistinctBy(user => user.Uid).ToList();
        if (number != page || size <= 0 || total < 0 || pages < 0
            || (total == 0 && (users.Count != 0 || pages != 0)) || (total > 0 && (users.Count == 0 || pages < page)))
            throw new InvalidDataException("UP 主搜索分页数据不完整，请手动重试。");
        return new(users, number, size, total, pages);
    }

    private static string PlainSearchText(string value) => WebUtility.HtmlDecode(
        Regex.Replace(value, @"</?em(?:\s[^>]*)?>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public async Task<BilibiliSpaceProfile> GetProfileAsync(string uid, CancellationToken cancellationToken = default)
    {
        ValidateUid(uid);
        using var json = await GetAsync($"x/web-interface/card?mid={uid}", $"https://space.bilibili.com/{uid}/", cancellationToken);
        var card = Required(json.RootElement.GetProperty("data"), "card", JsonValueKind.Object);
        return new(uid, Text(card, "name", required: true), Image(card, "face"), Text(card, "sign"));
    }

    public async Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetUploadsAsync(string uid, int page, CancellationToken cancellationToken = default)
    {
        ValidatePage(uid, page);
        var credential = await ReadCredentialAsync(cancellationToken);
        var key = await GetKeyAsync($"https://space.bilibili.com/{uid}/", credential, cancellationToken);
        var query = Sign(new Dictionary<string, string>
        {
            ["mid"] = uid, ["order"] = "pubdate", ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = PageSize.ToString(), ["tid"] = "0"
        }, key, clock.GetUtcNow().ToUnixTimeSeconds());
        using var json = await GetAsync("x/space/wbi/arc/search?" + query, $"https://space.bilibili.com/{uid}/", cancellationToken, credential);
        var data = json.RootElement.GetProperty("data");
        var list = Required(data, "list", JsonValueKind.Object);
        var paging = Required(data, "page", JsonValueKind.Object);
        var videos = Required(list, "vlist", JsonValueKind.Array).EnumerateArray().Select(ParseUpload).ToList();
        var categories = new List<BilibiliSpaceCategory>();
        var tlist = Required(list, "tlist", JsonValueKind.Object);
        foreach (var category in tlist.EnumerateObject())
            categories.Add(new(Number(category.Value, "tid"), Text(category.Value, "name", true), Number(category.Value, "count")));
        return new(videos, Number(paging, "pn"), Number(paging, "ps"), Number(paging, "count"), categories);
    }

    public async Task<BilibiliSpacePage<BilibiliSpaceCollection>> GetCollectionsAsync(string uid, int page, CancellationToken cancellationToken = default)
    {
        ValidatePage(uid, page);
        using var json = await GetAsync($"x/polymer/web-space/seasons_series_list?mid={uid}&page_num={page}&page_size=20", $"https://space.bilibili.com/{uid}/", cancellationToken);
        var lists = Required(json.RootElement.GetProperty("data"), "items_lists", JsonValueKind.Object);
        var items = new List<BilibiliSpaceCollection>();
        foreach (var (field, kind, id) in new[] { ("seasons_list", BilibiliSpaceCollectionKind.Season, "season_id"), ("series_list", BilibiliSpaceCollectionKind.Series, "series_id") })
        {
            foreach (var item in Required(lists, field, JsonValueKind.Array).EnumerateArray())
            {
                var meta = Required(item, "meta", JsonValueKind.Object);
                items.Add(new(Scalar(meta, id), uid, kind, Text(meta, "name", true), Image(meta, "cover"), Text(meta, "description"), Number(meta, "total")));
            }
        }
        var paging = Required(lists, "page", JsonValueKind.Object);
        return new(items, Number(paging, "page_num"), Number(paging, "page_size"), Number(paging, "total"));
    }

    public async Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetCollectionVideosAsync(BilibiliSpaceCollection collection, int page, CancellationToken cancellationToken = default)
    {
        ValidatePage(collection.Uid, page);
        ValidateUid(collection.Id);
        var season = collection.Kind == BilibiliSpaceCollectionKind.Season;
        var endpoint = season
            ? $"x/polymer/web-space/seasons_archives_list?mid={collection.Uid}&season_id={collection.Id}&sort_reverse=false&page_num={page}&page_size={PageSize}"
            : $"x/series/archives?mid={collection.Uid}&series_id={collection.Id}&only_normal=true&sort=asc&pn={page}&ps={PageSize}";
        using var json = await GetAsync(endpoint, $"https://space.bilibili.com/{collection.Uid}/", cancellationToken);
        var data = json.RootElement.GetProperty("data");
        var videos = Required(data, "archives", JsonValueKind.Array).EnumerateArray().Select(v => new BilibiliSpaceVideo(
            Scalar(v, "aid"), Text(v, "bvid", true), Text(v, "title", true), Image(v, "pic"),
            Number(v, "duration"), DateTimeOffset.FromUnixTimeSeconds(Long(v, "pubdate")), 0)).ToList();
        var paging = Required(data, "page", JsonValueKind.Object);
        return new(videos, Number(paging, season ? "page_num" : "num"), Number(paging, season ? "page_size" : "size"), Number(paging, "total"));
    }

    private async Task<string> GetKeyAsync(string referer, string credential, CancellationToken token, string operation = "主页")
    {
        await signatureGate.WaitAsync(token);
        try
        {
            if (mixinKey.Length > 0 && keyExpires > clock.GetUtcNow() && signedCredential == credential) return mixinKey;
            using var json = await GetAsync("x/web-interface/nav", referer, token, credential, allowAnonymousNav: true, operation: operation);
            var images = Required(json.RootElement.GetProperty("data"), "wbi_img", JsonValueKind.Object);
            var raw = Path.GetFileNameWithoutExtension(new Uri(Text(images, "img_url", true)).AbsolutePath)
                + Path.GetFileNameWithoutExtension(new Uri(Text(images, "sub_url", true)).AbsolutePath);
            int[] indexes = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];
            if (raw.Length < 59) throw new InvalidDataException("B 站签名数据不完整。");
            mixinKey = string.Concat(indexes.Select(i => raw[i]));
            signedCredential = credential;
            keyExpires = clock.GetUtcNow().AddMinutes(10);
            return mixinKey;
        }
        finally { signatureGate.Release(); }
    }

    internal static string Sign(IReadOnlyDictionary<string, string> values, string key, long timestamp)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values) parameters[pair.Key] = pair.Value;
        parameters["wts"] = timestamp.ToString(CultureInfo.InvariantCulture);
        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(string.Concat(p.Value.Where(c => !"!'()*".Contains(c))))}"));
        return query + "&w_rid=" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + key))).ToLowerInvariant();
    }

    private async Task<string> ReadCredentialAsync(CancellationToken token) => File.Exists(paths.WebCredentialFile)
        ? (await File.ReadAllTextAsync(paths.WebCredentialFile, token)).Trim() : "";

    private async Task<JsonDocument> GetAsync(string endpoint, string referer, CancellationToken token, string? credential = null, bool allowAnonymousNav = false, string operation = "主页")
    {
        credential ??= await ReadCredentialAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/" + endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.Referrer = new Uri(referer);
        if (credential.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", credential);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode == 412 ? "触发 B 站风控，请确认 WEB 登录有效，稍后手动重试。" : "请稍后手动重试。";
            throw new HttpRequestException($"{operation}接口请求失败：HTTP {(int)response.StatusCode}。{hint}", null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        JsonDocument json;
        try { json = await JsonDocument.ParseAsync(stream, cancellationToken: token); }
        catch (JsonException ex) { throw new InvalidDataException($"{operation}接口返回了无法识别的数据，请手动重试。", ex); }
        try
        {
            var root = json.RootElement;
            var code = Number(root, "code");
            if (code != 0 && !(allowAnonymousNav && code == -101))
            {
                var hint = code switch
                {
                    -101 or -111 => "请在设置中重新进行 WEB 登录后重试。",
                    -352 or -412 => "触发 B 站风控，请确认 WEB 登录有效，稍后手动重试。",
                    _ => "请检查链接，稍后手动重试。"
                };
                throw new InvalidOperationException($"{operation}接口错误 {code}：{Text(root, "message")}。{hint}");
            }
            Required(root, "data", JsonValueKind.Object);
            return json;
        }
        catch { json.Dispose(); throw; }
    }

    private static BilibiliSpaceVideo ParseUpload(JsonElement v) => new(Scalar(v, "aid"), Text(v, "bvid", true),
        Text(v, "title", true), Image(v, "pic"), Duration(Text(v, "length", true)),
        DateTimeOffset.FromUnixTimeSeconds(Long(v, "created")), Number(v, "typeid"));
    internal static int Duration(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) throw new InvalidDataException("视频时长格式无效。");
        int seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 0) throw new InvalidDataException("视频时长格式无效。");
            seconds = checked(seconds * 60 + number);
        }
        return seconds;
    }
    private static string Image(JsonElement e, string name)
    {
        var url = Text(e, name);
        return url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
    }
    private static JsonElement Required(JsonElement e, string name, JsonValueKind kind) => e.TryGetProperty(name, out var value) && value.ValueKind == kind
        ? value : throw new InvalidDataException($"主页接口缺少有效字段：{name}。");
    private static string Text(JsonElement e, string name, bool required = false)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && (!required || !string.IsNullOrWhiteSpace(v.GetString()))) return v.GetString()!;
        if (required) throw new InvalidDataException($"主页接口缺少有效字段：{name}。");
        return "";
    }
    private static string Scalar(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number && long.TryParse(v.ToString(), out var n) && n > 0) return n.ToString(CultureInfo.InvariantCulture);
        throw new InvalidDataException($"主页接口缺少有效编号：{name}。");
    }
    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
        ? n : throw new InvalidDataException($"主页接口缺少有效数字：{name}。");
    private static int Number(JsonElement e, string name) => checked((int)Long(e, name));
    private static void ValidatePage(string uid, int page) { ValidateUid(uid); if (page < 1) throw new ArgumentOutOfRangeException(nameof(page)); }
    private static void ValidateUid(string uid)
    {
        if (!long.TryParse(uid, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) throw new ArgumentException("请输入有效的主页 UID。");
    }
}
