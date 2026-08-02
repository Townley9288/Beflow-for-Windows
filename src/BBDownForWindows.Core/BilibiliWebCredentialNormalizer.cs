using System.Net;

namespace BBDownForWindows.Core;

public sealed class BilibiliWebCredentialNormalizer : IWebCredentialNormalizer
{
    private const int MaximumRedirects = 8;
    private static readonly Uri TicketExchangeEndpoint = new("https://passport.biligame.com/crossDomain");
    private static readonly HashSet<string> TicketMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ticket",
        "gourl",
        "first_domain"
    };
    private static readonly string[] ExportedCookieNames =
    [
        "DedeUserID",
        "DedeUserID__ckMd5",
        "SESSDATA",
        "bili_jct",
        "sid"
    ];
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public BilibiliWebCredentialNormalizer(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? SharedHttpClient;

    public async Task NormalizeAsync(string credentialPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(credentialPath))
            throw new InvalidOperationException("WEB 登录没有生成账号数据，请重新扫码并在手机端确认");

        string credential;
        try
        {
            credential = (await File.ReadAllTextAsync(credentialPath, cancellationToken)).Trim();
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("无法读取 WEB 登录账号数据", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("无法读取 WEB 登录账号数据", exception);
        }

        if (IsCanonicalCredential(credential)) return;
        var ticketValues = ParsePairs(credential);
        var ticketExpiresAt = TryParseExpires(ticketValues);
        if (!TryBuildTicketExchangeUri(credential, out var exchangeUri))
            throw new InvalidOperationException("BBDown 生成的 WEB 登录数据格式不受支持，请重新扫码");

        var cookies = await ExchangeTicketAsync(exchangeUri, cancellationToken);
        if (!cookies.TryGetValue("SESSDATA", out var sessdata))
            throw new InvalidOperationException("WEB 登录票据未返回有效 Cookie，请重新扫码");

        var normalized = string.Join(';', ExportedCookieNames
            .Where(cookies.ContainsKey)
            .Select(name => $"{name}={cookies[name].Value}"));
        var expiresAt = sessdata.ExpiresAt ?? ticketExpiresAt;
        if (expiresAt is { } validExpiresAt && validExpiresAt > DateTimeOffset.UnixEpoch)
            normalized = $"{normalized};Expires={validExpiresAt.ToUnixTimeSeconds()}";
        await WriteCredentialAsync(credentialPath, normalized, cancellationToken);
    }

    internal static bool TryBuildTicketExchangeUri(string credential, out Uri exchangeUri)
    {
        exchangeUri = TicketExchangeEndpoint;
        var values = ParsePairs(credential);
        if (values.Keys.Any(name => ExportedCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (!values.TryGetValue("ticket", out var ticket) || string.IsNullOrWhiteSpace(ticket) || ticket.Length > 256
            || ticket.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_')
            || !values.TryGetValue("gourl", out var encodedGoUrl)
            || !values.TryGetValue("first_domain", out var encodedFirstDomain))
            return false;

        string goUrl;
        string firstDomain;
        try
        {
            goUrl = Uri.UnescapeDataString(encodedGoUrl);
            firstDomain = Uri.UnescapeDataString(encodedFirstDomain);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!Uri.TryCreate(goUrl, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps
            || !IsAllowedHost(target.Host)
            || !firstDomain.Equals(".bilibili.com", StringComparison.OrdinalIgnoreCase)
               && !firstDomain.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = $"ticket={Uri.EscapeDataString(ticket)}" +
                    $"&gourl={Uri.EscapeDataString(target.AbsoluteUri)}" +
                    $"&first_domain={Uri.EscapeDataString(firstDomain)}";
        exchangeUri = new UriBuilder(TicketExchangeEndpoint) { Query = query }.Uri;
        return true;
    }

    internal static bool IsCanonicalCredential(string credential)
    {
        var values = ParsePairs(credential);
        return values.TryGetValue("SESSDATA", out var sessdata)
            && !string.IsNullOrWhiteSpace(sessdata)
            && values.Keys.All(name => !TicketMetadataNames.Contains(name));
    }

    private async Task<Dictionary<string, CookieValue>> ExchangeTicketAsync(Uri exchangeUri, CancellationToken cancellationToken)
    {
        var cookies = new Dictionary<string, CookieValue>(StringComparer.OrdinalIgnoreCase);
        var current = exchangeUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(current.Host))
                throw new InvalidOperationException("WEB 登录票据返回了不受信任的跳转地址");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", "Beflow-for-Windows/1.0");
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("WEB 登录票据请求超时，请重新扫码");
            }

            using (response)
            {
                CollectCookies(current, response, cookies);
                if (cookies.ContainsKey("SESSDATA")) return cookies;

                if (!IsRedirect(response.StatusCode))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"WEB 登录票据验证失败（HTTP {(int)response.StatusCode}）");
                    return cookies;
                }

                if (response.Headers.Location is null)
                    throw new InvalidOperationException("WEB 登录票据返回了无效跳转");
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
            }
        }

        throw new InvalidOperationException("WEB 登录票据跳转次数过多");
    }

    private static void CollectCookies(Uri responseUri, HttpResponseMessage response, IDictionary<string, CookieValue> cookies)
    {
        if (!IsBilibiliHost(responseUri.Host)) return;
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return;

        var container = new CookieContainer();
        foreach (var header in headers)
        {
            try
            {
                container.SetCookies(responseUri, header);
            }
            catch (CookieException)
            {
                // Ignore malformed or domain-mismatched cookies from the remote response.
            }
        }

        foreach (Cookie cookie in container.GetCookies(responseUri))
        {
            if (!ExportedCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase)
                || !IsBilibiliHost(cookie.Domain.TrimStart('.'))
                || string.IsNullOrWhiteSpace(cookie.Value)
                || cookie.Value.Contains('\r') || cookie.Value.Contains('\n')) continue;
            var expiresAt = cookie.Expires > DateTime.MinValue
                ? new DateTimeOffset(cookie.Expires.ToUniversalTime())
                : (DateTimeOffset?)null;
            cookies[cookie.Name] = new CookieValue(cookie.Value, expiresAt);
        }
    }

    private static Dictionary<string, string> ParsePairs(string credential)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in credential.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            values[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }
        return values;
    }

    private static DateTimeOffset? TryParseExpires(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("Expires", out var raw) || !long.TryParse(raw, out var seconds) || seconds <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool IsAllowedHost(string host) =>
        IsBilibiliHost(host)
        || host.Equals("biligame.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".biligame.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBilibiliHost(string host) =>
        host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task WriteCredentialAsync(string path, string credential, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, credential, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private sealed record CookieValue(string Value, DateTimeOffset? ExpiresAt);
}
