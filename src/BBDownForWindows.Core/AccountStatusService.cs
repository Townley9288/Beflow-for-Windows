using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class AccountStatusService : IAccountStatusService
{
    private const string WebStatusUrl = "https://api.bilibili.com/x/web-interface/nav";
    private const string TvStatusUrl = "https://app.bilibili.com/x/v2/account/myinfo";
    private const string TvAppKey = "4409e2ce8ffd12b8";
    private const string TvAppSecret = "59b43e04ad6965f34319062b478f83dd";
    private readonly ApplicationPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public AccountStatusService(ApplicationPaths paths, HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AccountStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        var webTask = GetWebStatusAsync(checkedAt, cancellationToken);
        var tvTask = GetTvStatusAsync(checkedAt, cancellationToken);
        await Task.WhenAll(webTask, tvTask);
        return new AccountStatusSnapshot(await webTask, await tvTask, checkedAt);
    }

    public Task<AccountChannelStatus> GetStatusAsync(AccountChannel channel, CancellationToken cancellationToken = default)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        return channel == AccountChannel.Tv
            ? GetTvStatusAsync(checkedAt, cancellationToken)
            : GetWebStatusAsync(checkedAt, cancellationToken);
    }

    private async Task<AccountChannelStatus> GetWebStatusAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var credential = ReadCredential(_paths.WebCredentialFile, AccountChannel.Web, checkedAt, out var unavailable);
        if (unavailable is not null) return unavailable;
        if (credential is null) return Missing(AccountChannel.Web, checkedAt);
        var expiresAt = WebCredentialExpiresAt(credential);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebStatusUrl);
            request.Headers.TryAddWithoutValidation("Cookie", credential);
            request.Headers.TryAddWithoutValidation("User-Agent", "BBDown-for-Windows/1.0");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return WithExpiry(HttpUnavailable(AccountChannel.Web, checkedAt, _paths.WebCredentialFile, response.StatusCode), expiresAt);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var code = GetInt32(root, "code");
            if (code != 0) return WithExpiry(ApiFailure(AccountChannel.Web, checkedAt, _paths.WebCredentialFile, code, GetString(root, "message")), expiresAt);
            if (!TryGetObject(root, "data", out var data)) return WithExpiry(Incomplete(AccountChannel.Web, checkedAt, _paths.WebCredentialFile), expiresAt);
            if (!GetBoolean(data, "isLogin")) return WithExpiry(Expired(AccountChannel.Web, checkedAt, _paths.WebCredentialFile), expiresAt);

            var displayName = GetString(data, "uname");
            var userId = GetScalarText(data, "mid");
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(userId)) return WithExpiry(Incomplete(AccountChannel.Web, checkedAt, _paths.WebCredentialFile), expiresAt);
            var level = TryGetObject(data, "level_info", out var levelInfo) ? GetInt32(levelInfo, "current_level") : 0;
            var vipLabel = TryGetObject(data, "vip_label", out var vip) ? GetString(vip, "text") : string.Empty;
            if (string.IsNullOrWhiteSpace(vipLabel)) vipLabel = GetInt32(data, "vipStatus") == 1 ? "大会员" : "普通用户";
            var profile = new AccountProfile(displayName, userId, NormalizeAvatar(GetString(data, "face")), level, vipLabel);
            return WithExpiry(LoggedIn(AccountChannel.Web, profile, checkedAt, _paths.WebCredentialFile), expiresAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WithExpiry(NetworkUnavailable(AccountChannel.Web, checkedAt, _paths.WebCredentialFile), expiresAt);
        }
        catch (HttpRequestException)
        {
            return WithExpiry(NetworkUnavailable(AccountChannel.Web, checkedAt, _paths.WebCredentialFile), expiresAt);
        }
        catch (JsonException)
        {
            return WithExpiry(new AccountChannelStatus(AccountChannel.Web, AccountLoginState.Unavailable, null, "账号接口返回了无法识别的数据", checkedAt, CredentialUpdatedAt(_paths.WebCredentialFile)), expiresAt);
        }
    }

    private async Task<AccountChannelStatus> GetTvStatusAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var credential = ReadCredential(_paths.TvCredentialFile, AccountChannel.Tv, checkedAt, out var unavailable);
        if (unavailable is not null) return unavailable;
        if (credential is null) return Missing(AccountChannel.Tv, checkedAt);
        var token = credential.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase) ? credential["access_token=".Length..].Trim() : credential.Trim();
        if (string.IsNullOrWhiteSpace(token)) return Expired(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile);

        try
        {
            var query = BuildTvQuery(token, checkedAt);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TvStatusUrl}?{query}");
            request.Headers.TryAddWithoutValidation("User-Agent", "BBDown-for-Windows/1.0");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return HttpUnavailable(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var code = GetInt32(root, "code");
            if (code != 0) return ApiFailure(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile, code, GetString(root, "message"));
            if (!TryGetObject(root, "data", out var data)) return Incomplete(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile);

            var displayName = GetString(data, "name");
            var userId = GetScalarText(data, "mid");
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(userId)) return Incomplete(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile);
            var level = GetInt32(data, "level");
            var vipLabel = string.Empty;
            var vipStatus = 0;
            if (TryGetObject(data, "vip", out var vip))
            {
                vipStatus = GetInt32(vip, "status");
                if (TryGetObject(vip, "label", out var label)) vipLabel = GetString(label, "text");
            }
            if (string.IsNullOrWhiteSpace(vipLabel)) vipLabel = vipStatus == 1 ? "大会员" : "普通用户";
            var profile = new AccountProfile(displayName, userId, NormalizeAvatar(GetString(data, "face")), level, vipLabel);
            return LoggedIn(AccountChannel.Tv, profile, checkedAt, _paths.TvCredentialFile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NetworkUnavailable(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile);
        }
        catch (HttpRequestException)
        {
            return NetworkUnavailable(AccountChannel.Tv, checkedAt, _paths.TvCredentialFile);
        }
        catch (JsonException)
        {
            return new AccountChannelStatus(AccountChannel.Tv, AccountLoginState.Unavailable, null, "账号接口返回了无法识别的数据", checkedAt, CredentialUpdatedAt(_paths.TvCredentialFile));
        }
    }

    private static string BuildTvQuery(string token, DateTimeOffset timestamp)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_key"] = token,
            ["appkey"] = TvAppKey,
            ["build"] = "102801",
            ["mobi_app"] = "android_tv_yst",
            ["platform"] = "android",
            ["ts"] = timestamp.ToUnixTimeSeconds().ToString()
        };
        var query = string.Join('&', parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var bytes = Encoding.UTF8.GetBytes(query + TvAppSecret);
        var sign = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return $"{query}&sign={sign}";
    }

    private static string? ReadCredential(string path, AccountChannel channel, DateTimeOffset checkedAt, out AccountChannelStatus? unavailable)
    {
        unavailable = null;
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            unavailable = new AccountChannelStatus(channel, AccountLoginState.Unavailable, null, "无法读取本地账号数据", checkedAt, CredentialUpdatedAt(path));
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            unavailable = new AccountChannelStatus(channel, AccountLoginState.Unavailable, null, "无法读取本地账号数据", checkedAt, CredentialUpdatedAt(path));
            return null;
        }
    }

    private static AccountChannelStatus Missing(AccountChannel channel, DateTimeOffset checkedAt) =>
        new(channel, AccountLoginState.NotConfigured, null, "尚未登录", checkedAt, null);

    private static AccountChannelStatus LoggedIn(AccountChannel channel, AccountProfile profile, DateTimeOffset checkedAt, string path) =>
        new(channel, AccountLoginState.LoggedIn, profile, "账号验证成功", checkedAt, CredentialUpdatedAt(path));

    private static AccountChannelStatus Expired(AccountChannel channel, DateTimeOffset checkedAt, string path) =>
        new(channel, AccountLoginState.Expired, null, "登录已失效，请重新扫码", checkedAt, CredentialUpdatedAt(path));

    private static AccountChannelStatus Incomplete(AccountChannel channel, DateTimeOffset checkedAt, string path) =>
        new(channel, AccountLoginState.Unavailable, null, "账号信息不完整，暂时无法显示", checkedAt, CredentialUpdatedAt(path));

    private static AccountChannelStatus NetworkUnavailable(AccountChannel channel, DateTimeOffset checkedAt, string path) =>
        new(channel, AccountLoginState.Unavailable, null, "网络请求失败，暂时无法验证", checkedAt, CredentialUpdatedAt(path));

    private static AccountChannelStatus HttpUnavailable(AccountChannel channel, DateTimeOffset checkedAt, string path, HttpStatusCode statusCode) =>
        new(channel, AccountLoginState.Unavailable, null, $"账号接口暂时不可用（HTTP {(int)statusCode}）", checkedAt, CredentialUpdatedAt(path));

    private static AccountChannelStatus ApiFailure(AccountChannel channel, DateTimeOffset checkedAt, string path, int code, string message)
    {
        var expired = code == -101 || message.Contains("未登录", StringComparison.OrdinalIgnoreCase) || message.Contains("登录失效", StringComparison.OrdinalIgnoreCase) || message.Contains("token", StringComparison.OrdinalIgnoreCase);
        return expired
            ? Expired(channel, checkedAt, path)
            : new AccountChannelStatus(channel, AccountLoginState.Unavailable, null, $"账号接口返回错误（{code}）", checkedAt, CredentialUpdatedAt(path));
    }

    private static DateTimeOffset? CredentialUpdatedAt(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static DateTimeOffset? WebCredentialExpiresAt(string credential)
    {
        foreach (var part in credential.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || !part[..separator].Equals("Expires", StringComparison.OrdinalIgnoreCase)) continue;
            if (long.TryParse(part[(separator + 1)..], out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
        return null;
    }

    private static AccountChannelStatus WithExpiry(AccountChannelStatus status, DateTimeOffset? expiresAt) => status with { CredentialExpiresAt = expiresAt };

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetScalarText(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string NormalizeAvatar(string value) => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? $"https://{value[7..]}" : value;
}
