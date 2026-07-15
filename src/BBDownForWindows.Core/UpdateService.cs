using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class UpdateService : IUpdateService
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Townley9288/Beflow-for-Windows/releases/latest");
    private readonly HttpClient _httpClient;

    public UpdateService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(LatestReleaseUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentVersion, null, "尚未发布可用的稳定版本");
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.FirstOrDefault() == "0")
            throw new InvalidOperationException("GitHub 更新检查请求已达到频率限制，请稍后重试。");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if ((document.RootElement.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
            (document.RootElement.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentVersion, null, "未发现可用的稳定版本");
        var release = ParseRelease(document.RootElement);
        return release.Version > currentVersion
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, currentVersion, release, $"发现新版本 v{FormatVersion(release.Version)}")
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentVersion, release, $"当前已是最新版本 v{FormatVersion(currentVersion)}");
    }

    public async Task<string> DownloadAndVerifyAsync(UpdateAsset asset, string destinationDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var packagePath = Path.Combine(destinationDirectory, asset.FileName);
        var temporaryPath = packagePath + ".download";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

        try
        {
            using var request = CreateRequest(asset.DownloadUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (total > 0) progress?.Report(Math.Clamp(received * 100d / total, 0d, 100d));
                }
                await output.FlushAsync(cancellationToken);
            }

            using var checksumRequest = CreateRequest(asset.ChecksumUri);
            using var checksumResponse = await _httpClient.SendAsync(checksumRequest, cancellationToken);
            checksumResponse.EnsureSuccessStatusCode();
            var checksumText = await checksumResponse.Content.ReadAsStringAsync(cancellationToken);
            var expected = ParseChecksum(checksumText);
            string actual;
            await using (var hashStream = File.OpenRead(temporaryPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新包 SHA-256 校验失败。期望 {expected}，实际 {actual}。");

            File.Move(temporaryPath, packagePath, true);
            progress?.Report(100d);
            return packagePath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    internal static UpdateRelease ParseRelease(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) throw new InvalidDataException("GitHub 返回了草稿版本。");
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) throw new InvalidDataException("GitHub 返回了测试版本。");
        var tag = RequiredString(root, "tag_name");
        var version = ParseTagVersion(tag);
        var assets = root.GetProperty("assets").EnumerateArray().ToDictionary(value => RequiredString(value, "name"), StringComparer.OrdinalIgnoreCase);
        var prefix = $"Beflow-for-Windows-v{FormatVersion(version)}-win-x64";
        var installer = ParseAsset(assets, UpdatePackageKind.Installer, prefix + "-setup.exe");
        var portable = ParseAsset(assets, UpdatePackageKind.Portable, prefix + "-portable.zip");
        return new UpdateRelease(
            version,
            tag,
            root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString()! : tag,
            root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var publishedAt) ? publishedAt : DateTimeOffset.MinValue,
            new Uri(RequiredString(root, "html_url")),
            installer,
            portable);
    }

    public static Version ParseTagVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException($"无法识别版本标签：{tag}");
        return version;
    }

    public static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    internal static string ParseChecksum(string value)
    {
        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("SHA-256 文件格式无效。");
        return token.ToUpperInvariant();
    }

    private static UpdateAsset ParseAsset(IReadOnlyDictionary<string, JsonElement> assets, UpdatePackageKind kind, string fileName)
    {
        if (!assets.TryGetValue(fileName, out var package)) throw new InvalidDataException($"Release 缺少更新文件：{fileName}");
        var checksumName = fileName + ".sha256";
        if (!assets.TryGetValue(checksumName, out var checksum)) throw new InvalidDataException($"Release 缺少校验文件：{checksumName}");
        return new UpdateAsset(kind, fileName, new Uri(RequiredString(package, "browser_download_url")), package.TryGetProperty("size", out var size) ? size.GetInt64() : 0, checksumName, new Uri(RequiredString(checksum, "browser_download_url")));
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Beflow/1.0 (+https://github.com/Townley9288/Beflow-for-Windows)");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"GitHub Release 缺少字段：{propertyName}");
        return property.GetString()!;
    }
}

public static class UpdateSchedule
{
    public static bool ShouldCheck(bool enabled, DateTimeOffset? lastCheckedAt, DateTimeOffset now)
    {
        if (!enabled) return false;
        if (lastCheckedAt > now.AddMinutes(5)) return true;
        return lastCheckedAt is null || now.ToUniversalTime() - lastCheckedAt.Value.ToUniversalTime() >= TimeSpan.FromHours(24);
    }
}
