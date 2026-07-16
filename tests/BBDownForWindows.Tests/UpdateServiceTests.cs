using System.Net;
using System.Security.Cryptography;
using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task FindsNewReleaseFromLatestRedirectAndReadsRawReleaseNotes()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head
            ? Latest("v1.2.3")
            : Text(HttpStatusCode.OK, "# v1.2.3\n\n- 更新说明"));

        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 2, 3), result.Release!.Version);
        Assert.Equal("# v1.2.3\n\n- 更新说明", result.Release.ReleaseNotes);
        Assert.Equal(new Uri("https://github.com/Townley9288/Beflow-for-Windows/releases/tag/v1.2.3"), result.Release.ReleasePage);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                Assert.Equal("github.com", request.RequestUri!.Host);
                Assert.Equal("/Townley9288/Beflow-for-Windows/releases/latest", request.RequestUri.AbsolutePath);
                Assert.DoesNotContain("api.github.com", request.RequestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Beflow/", request.Headers.UserAgent.ToString());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
                Assert.Equal("/Townley9288/Beflow-for-Windows/v1.2.3/RELEASE_NOTES.md", request.RequestUri.AbsolutePath);
            });
    }

    [Theory]
    [InlineData("1.0.2", "1.0.1", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("1.0.2", "1.0.2", UpdateCheckStatus.UpToDate)]
    [InlineData("1.0.2", "1.1.0", UpdateCheckStatus.UpToDate)]
    public async Task ComparesLatestVersionWithCurrentVersion(string latest, string current, UpdateCheckStatus expected)
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest($"v{latest}") : Text(HttpStatusCode.NotFound));

        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(Version.Parse(current));

        Assert.Equal(expected, result.Status);
        Assert.Equal(Version.Parse(latest), result.Release!.Version);
    }

    [Fact]
    public async Task BuildsFixedInstallerPortableAndChecksumAddresses()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest("v1.2.3") : Text(HttpStatusCode.NotFound));

        var release = (await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0))).Release!;

        Assert.Equal("Beflow-for-Windows-v1.2.3-win-x64-setup.exe", release.Installer.FileName);
        Assert.Equal("Beflow-for-Windows-v1.2.3-win-x64-setup.exe.sha256", release.Installer.ChecksumFileName);
        Assert.Equal("https://github.com/Townley9288/Beflow-for-Windows/releases/download/v1.2.3/Beflow-for-Windows-v1.2.3-win-x64-setup.exe", release.Installer.DownloadUri.AbsoluteUri);
        Assert.Equal("https://github.com/Townley9288/Beflow-for-Windows/releases/download/v1.2.3/Beflow-for-Windows-v1.2.3-win-x64-setup.exe.sha256", release.Installer.ChecksumUri.AbsoluteUri);
        Assert.Equal("Beflow-for-Windows-v1.2.3-win-x64-portable.zip", release.Portable.FileName);
        Assert.Equal("Beflow-for-Windows-v1.2.3-win-x64-portable.zip.sha256", release.Portable.ChecksumFileName);
        Assert.Equal("https://github.com/Townley9288/Beflow-for-Windows/releases/download/v1.2.3/Beflow-for-Windows-v1.2.3-win-x64-portable.zip", release.Portable.DownloadUri.AbsoluteUri);
        Assert.Equal("https://github.com/Townley9288/Beflow-for-Windows/releases/download/v1.2.3/Beflow-for-Windows-v1.2.3-win-x64-portable.zip.sha256", release.Portable.ChecksumUri.AbsoluteUri);
        Assert.Equal(0, release.Installer.Size);
        Assert.Equal(0, release.Portable.Size);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task MissingOrFailedReleaseNotesDoNotBlockVersionCheck(HttpStatusCode notesStatus)
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest("v1.2.3") : Text(notesStatus, "unavailable"));

        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(string.Empty, result.Release!.ReleaseNotes);
    }

    [Fact]
    public async Task ReleaseNotesNetworkFailureDoesNotBlockVersionCheck()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head
            ? Latest("v1.2.3")
            : throw new HttpRequestException("offline"));

        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(string.Empty, result.Release!.ReleaseNotes);
    }

    [Fact]
    public async Task LatestNotFoundMeansNoStableRelease()
    {
        var result = await new UpdateService(new HttpClient(new StubHandler(_ => Text(HttpStatusCode.NotFound))))
            .CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("尚未发布", result.Message);
    }

    [Theory]
    [InlineData("https://github.com/Townley9288/Beflow-for-Windows/releases/latest")]
    [InlineData("https://github.com/another/repository/releases/tag/v1.2.3")]
    [InlineData("https://example.com/Townley9288/Beflow-for-Windows/releases/tag/v1.2.3")]
    [InlineData("https://github.com/Townley9288/Beflow-for-Windows/releases/tag/v1.2.3/extra")]
    public async Task RejectsInvalidLatestRedirectAddress(string address)
    {
        var handler = new StubHandler(_ => Latest(new Uri(address)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("v1.0")]
    [InlineData("nightly")]
    [InlineData("v1.0.0.1.2")]
    public async Task RejectsUnsupportedLatestTag(string tag)
    {
        var handler = new StubHandler(_ => Latest(tag));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task FiveMinuteCacheAvoidsRepeatedLatestAndNotesRequests()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-15T10:00:00Z"));
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest("v1.2.3") : Text(HttpStatusCode.OK, "notes"));
        var service = new UpdateService(new HttpClient(handler), time);

        var first = await service.CheckAsync(new Version(1, 0, 0));
        time.Advance(TimeSpan.FromMinutes(4));
        var second = await service.CheckAsync(new Version(1, 0, 0));

        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Head));
        Assert.Equal(1, handler.Requests.Count(request => request.RequestUri!.Host == "raw.githubusercontent.com"));
    }

    [Fact]
    public async Task CacheExpiryRequestsLatestAgain()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-15T10:00:00Z"));
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest("v1.2.3") : Text(HttpStatusCode.OK, "notes"));
        var service = new UpdateService(new HttpClient(handler), time);

        await service.CheckAsync(new Version(1, 0, 0));
        time.Advance(TimeSpan.FromMinutes(5));
        await service.CheckAsync(new Version(1, 0, 0));

        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Head));
        Assert.Equal(2, handler.Requests.Count(request => request.RequestUri!.Host == "raw.githubusercontent.com"));
    }

    [Fact]
    public void DailyScheduleHonorsPreferenceAndTwentyFourHours()
    {
        var now = DateTimeOffset.Parse("2026-07-15T10:00:00Z");
        Assert.False(UpdateSchedule.ShouldCheck(false, null, now));
        Assert.True(UpdateSchedule.ShouldCheck(true, null, now));
        Assert.False(UpdateSchedule.ShouldCheck(true, now.AddHours(-23), now));
        Assert.True(UpdateSchedule.ShouldCheck(true, now.AddHours(-24), now));
        Assert.True(UpdateSchedule.ShouldCheck(true, now.AddDays(1), now));
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.0.0", 1, 0, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int patch) =>
        Assert.Equal(new Version(major, minor, patch), UpdateService.ParseTagVersion(tag));

    [Fact]
    public async Task FourPartVersionsKeepRevisionInDisplayAndAssetNames()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head ? Latest("v1.0.3.1") : Text(HttpStatusCode.NotFound));

        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 3));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 0, 3, 1), result.Release!.Version);
        Assert.Equal("1.0.3.1", UpdateService.FormatVersion(result.Release.Version));
        Assert.Equal("Beflow-for-Windows-v1.0.3.1-win-x64-setup.exe", result.Release.Installer.FileName);
        Assert.Equal("Beflow-for-Windows-v1.0.3.1-win-x64-portable.zip", result.Release.Portable.FileName);
    }

    [Fact]
    public async Task DownloadsAndVerifiesSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("verified update package");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, hash + "  package.zip")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var service = new UpdateService(new HttpClient(handler));
        using var root = new TempDirectory();
        var asset = new UpdateAsset(UpdatePackageKind.Portable, "package.zip", new Uri("https://example.invalid/package.zip"), bytes.Length, "package.zip.sha256", new Uri("https://example.invalid/package.zip.sha256"));

        var result = await service.DownloadAndVerifyAsync(asset, root.Info.FullName);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
    }

    [Fact]
    public async Task RejectsWrongSha256AndDeletesPartialFile()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, new string('0', 64))
            : Text(HttpStatusCode.OK, "bad"));
        var service = new UpdateService(new HttpClient(handler));
        using var root = new TempDirectory();
        var asset = new UpdateAsset(UpdatePackageKind.Portable, "package.zip", new Uri("https://example.invalid/package.zip"), 3, "package.zip.sha256", new Uri("https://example.invalid/package.zip.sha256"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndVerifyAsync(asset, root.Info.FullName));
        Assert.Empty(root.Info.GetFiles());
    }

    private static HttpResponseMessage Latest(string tag) =>
        Latest(new Uri($"https://github.com/Townley9288/Beflow-for-Windows/releases/tag/{tag}"));

    private static HttpResponseMessage Latest(Uri finalAddress) => new(HttpStatusCode.OK)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Head, finalAddress)
    };

    private static HttpResponseMessage Text(HttpStatusCode statusCode, string value = "") => new(statusCode)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/plain")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();
        public void Dispose() { try { Info.Delete(true); } catch { } }
    }
}
