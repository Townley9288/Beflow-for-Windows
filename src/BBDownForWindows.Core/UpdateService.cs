using System.Net;
using System.Security.Cryptography;

namespace BBDownForWindows.Core;

public sealed class UpdateService : IUpdateService
{
    private const string RepositoryRoot = "https://github.com/Townley9288/Beflow-for-Windows";
    private static readonly TimeSpan CheckCacheDuration = TimeSpan.FromMinutes(5);
    public static readonly Uri LatestReleaseUri = new($"{RepositoryRoot}/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private UpdateCheckResult? _cachedResult;
    private Version? _cachedCurrentVersion;
    private DateTimeOffset _cacheExpiresAt;

    public UpdateService(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_cachedResult is not null && currentVersion == _cachedCurrentVersion && now < _cacheExpiresAt)
                return _cachedResult;

            var result = await FetchLatestAsync(currentVersion, cancellationToken);
            _cachedResult = result;
            _cachedCurrentVersion = currentVersion;
            _cacheExpiresAt = now + CheckCacheDuration;
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> FetchLatestAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Head, LatestReleaseUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentVersion, null, "尚未发布可用的稳定版本");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub 暂时限制了更新检查请求，请稍后重试。");
        response.EnsureSuccessStatusCode();

        var releasePage = response.RequestMessage?.RequestUri ?? request.RequestUri ?? LatestReleaseUri;
        var tag = ParseReleaseTag(releasePage);
        var version = ParseTagVersion(tag);
        var notes = await TryGetReleaseNotesAsync(tag, cancellationToken);
        var release = BuildRelease(tag, version, notes, releasePage);
        return version > currentVersion
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, currentVersion, release, $"发现新版本 v{FormatVersion(version)}")
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentVersion, release, $"当前已是最新版本 v{FormatVersion(currentVersion)}");
    }

    private async Task<string> TryGetReleaseNotesAsync(string tag, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://raw.githubusercontent.com/Townley9288/Beflow-for-Windows/{Uri.EscapeDataString(tag)}/RELEASE_NOTES.md");
        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return string.Empty;
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    internal static string ParseReleaseTag(Uri releasePage)
    {
        var segments = releasePage.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (releasePage.Scheme == Uri.UriSchemeHttps &&
            releasePage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("Townley9288", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("Beflow-for-Windows", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("tag", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(segments[4]);
        throw new InvalidDataException($"GitHub 最新版本跳转地址无效：{releasePage}");
    }

    internal static UpdateRelease BuildRelease(string tag, Version version, string releaseNotes, Uri releasePage)
    {
        var versionText = FormatVersion(version);
        var prefix = $"Beflow-for-Windows-v{versionText}-win-x64";
        var downloadRoot = $"{RepositoryRoot}/releases/download/{Uri.EscapeDataString(tag)}/";
        var installerName = prefix + "-setup.exe";
        var portableName = prefix + "-portable.zip";
        return new UpdateRelease(
            version,
            tag,
            $"Beflow v{versionText}",
            releaseNotes,
            DateTimeOffset.MinValue,
            releasePage,
            new UpdateAsset(UpdatePackageKind.Installer, installerName, new Uri(downloadRoot + installerName), 0, installerName + ".sha256", new Uri(downloadRoot + installerName + ".sha256")),
            new UpdateAsset(UpdatePackageKind.Portable, portableName, new Uri(downloadRoot + portableName), 0, portableName + ".sha256", new Uri(downloadRoot + portableName + ".sha256")));
    }

    public async Task<string> DownloadAndVerifyAsync(UpdateAsset asset, string destinationDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var packagePath = Path.Combine(destinationDirectory, asset.FileName);
        var temporaryPath = packagePath + ".download";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, asset.DownloadUri);
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

            using var checksumRequest = CreateRequest(HttpMethod.Get, asset.ChecksumUri);
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

    public static Version ParseTagVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0)
            throw new InvalidDataException($"无法识别版本标签：{tag}");
        return version;
    }

    public static string FormatVersion(Version version)
    {
        var value = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return version.Revision >= 0 ? $"{value}.{version.Revision}" : value;
    }

    internal static string ParseChecksum(string value)
    {
        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("SHA-256 文件格式无效");
        return token.ToUpperInvariant();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Beflow/1.0 (+https://github.com/Townley9288/Beflow-for-Windows)");
        return request;
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
