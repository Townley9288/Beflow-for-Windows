using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class TmdbService : ITmdbService
{
    private readonly IRenameSettingsStore _settingsStore;
    private readonly Func<RenameSettings, HttpMessageHandler>? _handlerFactory;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public TmdbService(IRenameSettingsStore settingsStore, Func<RenameSettings, HttpMessageHandler>? handlerFactory = null)
    {
        _settingsStore = settingsStore;
        _handlerFactory = handlerFactory;
    }

    public async Task ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        using var document = await GetJsonAsync(settings, "/3/authentication/token/new", new Dictionary<string, string>(), cancellationToken);
        if (!document.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidOperationException("TMDB API Key 验证失败");
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("请输入要搜索的片名");
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var cacheKey = BuildCacheKey("search", settings, query.Trim().ToUpperInvariant());
        if (TryGetCache(cacheKey, out IReadOnlyList<TmdbSearchResult>? cached) && cached is not null) return cached;
        using var document = await GetJsonAsync(settings, "/3/search/multi", new Dictionary<string, string>
        {
            ["query"] = query.Trim(), ["language"] = "zh-CN", ["include_adult"] = "false"
        }, cancellationToken);
        var results = new List<TmdbSearchResult>();
        if (document.RootElement.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var mediaTypeText = ReadString(item, "media_type");
                if (mediaTypeText is not ("tv" or "movie") || !item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id)) continue;
                var mediaType = mediaTypeText == "tv" ? RenameMediaType.Series : RenameMediaType.Movie;
                var chineseTitle = FirstNonEmpty(ReadString(item, "name"), ReadString(item, "title"), ReadString(item, "original_name"), ReadString(item, "original_title"));
                var originalTitle = FirstNonEmpty(ReadString(item, "original_name"), ReadString(item, "original_title"), chineseTitle);
                var date = FirstNonEmpty(ReadString(item, "first_air_date"), ReadString(item, "release_date"));
                var poster = ReadString(item, "poster_path");
                results.Add(new TmdbSearchResult(
                    id,
                    mediaType,
                    chineseTitle,
                    originalTitle,
                    date.Length >= 4 ? date[..4] : string.Empty,
                    ReadString(item, "overview"),
                    string.IsNullOrWhiteSpace(poster) ? string.Empty : $"https://image.tmdb.org/t/p/w185{poster}"));
            }
        }
        SetCache(cacheKey, results.ToArray(), TimeSpan.FromMinutes(10));
        return results;
    }

    public async Task<TmdbTitleDetail> GetDetailAsync(TmdbSearchResult result, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var type = result.MediaType == RenameMediaType.Series ? "tv" : "movie";
        var cacheKey = BuildCacheKey("detail", settings, $"{type}:{result.Id}");
        if (TryGetCache(cacheKey, out TmdbTitleDetail? cached) && cached is not null) return cached;
        using var document = await GetJsonAsync(settings, $"/3/{type}/{result.Id}", new Dictionary<string, string>
        {
            ["language"] = "en-US", ["append_to_response"] = "alternative_titles"
        }, cancellationToken);
        var root = document.RootElement;
        var englishTitle = FindUsAlternativeTitle(root, result.MediaType);
        englishTitle = FirstNonEmpty(
            englishTitle,
            ReadString(root, "name"), ReadString(root, "title"),
            ReadString(root, "original_name"), ReadString(root, "original_title"),
            result.OriginalTitle);
        var date = FirstNonEmpty(ReadString(root, "first_air_date"), ReadString(root, "release_date"));
        var detail = new TmdbTitleDetail(
            result.Id,
            result.MediaType,
            result.ChineseTitle,
            englishTitle,
            !string.IsNullOrWhiteSpace(result.Year) ? result.Year : date.Length >= 4 ? date[..4] : string.Empty);
        SetCache(cacheKey, detail, TimeSpan.FromHours(6));
        return detail;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetEpisodeNamesAsync(int tmdbId, int season, CancellationToken cancellationToken = default)
    {
        var settings = await LoadConfiguredSettingsAsync(cancellationToken);
        var cacheKey = BuildCacheKey("episodes", settings, $"{tmdbId}:{season}");
        if (TryGetCache(cacheKey, out IReadOnlyDictionary<int, string>? cached) && cached is not null) return new Dictionary<int, string>(cached);
        using var document = await GetJsonAsync(settings, $"/3/tv/{tmdbId}/season/{season}", new Dictionary<string, string> { ["language"] = "zh-CN" }, cancellationToken);
        var episodes = new Dictionary<int, string>();
        if (document.RootElement.TryGetProperty("episodes", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("episode_number", out var number) && number.TryGetInt32(out var episode) && episode > 0)
                    episodes[episode] = ReadString(item, "name");
        }
        SetCache(cacheKey, new Dictionary<int, string>(episodes), TimeSpan.FromHours(6));
        return episodes;
    }

    private async Task<RenameSettings> LoadConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey)) throw new InvalidOperationException("请先在设置页配置 TMDB API Key");
        return settings;
    }

    private async Task<JsonDocument> GetJsonAsync(RenameSettings settings, string path, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>(parameters) { ["api_key"] = settings.TmdbApiKey.Trim() };
        var uri = BuildUri(path, query);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var client = CreateClient(settings);
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("TMDB API Key 无效或没有访问权限");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException("TMDB 请求过于频繁，请稍后重试");
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"TMDB 请求失败（HTTP {(int)response.StatusCode}）");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("TMDB 请求超时");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("TMDB 返回的数据无法解析");
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException("无法连接 TMDB，请检查网络或代理设置");
            }
        }
        throw new InvalidOperationException("无法连接 TMDB，请稍后重试");
    }

    private HttpClient CreateClient(RenameSettings settings)
    {
        HttpMessageHandler handler;
        if (_handlerFactory is not null) handler = _handlerFactory(settings);
        else
        {
            var httpHandler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
            {
                if (!Uri.TryCreate(settings.ProxyUrl.Trim(), UriKind.Absolute, out var proxyUri))
                    throw new InvalidOperationException("TMDB HTTP 代理地址无效");
                httpHandler.Proxy = new WebProxy(proxyUri);
                httpHandler.UseProxy = true;
            }
            handler = httpHandler;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)) };
    }

    private static Uri BuildUri(string path, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join('&', parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"https://api.themoviedb.org{path}?{query}");
    }

    private static string FindUsAlternativeTitle(JsonElement root, RenameMediaType type)
    {
        if (!root.TryGetProperty("alternative_titles", out var alternatives)) return string.Empty;
        var collectionName = type == RenameMediaType.Series ? "results" : "titles";
        if (!alternatives.TryGetProperty(collectionName, out var items) || items.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var item in items.EnumerateArray())
        {
            if (!ReadString(item, "iso_3166_1").Equals("US", StringComparison.OrdinalIgnoreCase)) continue;
            var title = FirstNonEmpty(ReadString(item, "title"), ReadString(item, "name"));
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        return string.Empty;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private string BuildCacheKey(string operation, RenameSettings settings, string value)
    {
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.TmdbApiKey.Trim())));
        return $"{operation}|{secretHash}|{settings.ProxyUrl.Trim()}|{settings.RequestTimeoutSeconds}|{value}";
    }

    private bool TryGetCache<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow && entry.Value is T typed)
        {
            value = typed;
            return true;
        }
        _cache.TryRemove(key, out _);
        value = default;
        return false;
    }

    private void SetCache<T>(string key, T value, TimeSpan lifetime)
    {
        if (_cache.Count > 512) _cache.Clear();
        _cache[key] = new CacheEntry(DateTimeOffset.UtcNow.Add(lifetime), value!);
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, object Value);
}
