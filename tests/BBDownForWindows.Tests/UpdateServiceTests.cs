using System.Net;
using System.Security.Cryptography;
using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task FindsNewStableReleaseAndSelectsBothPackages()
    {
        var handler = new StubHandler(_ => Json(ReleaseJson("v1.2.3")));
        var result = await new UpdateService(new HttpClient(handler)).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 2, 3), result.Release!.Version);
        Assert.EndsWith("-setup.exe", result.Release.Installer.FileName);
        Assert.EndsWith("-portable.zip", result.Release.Portable.FileName);
        Assert.Contains("Beflow/", handler.Requests[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task SameOrOlderVersionIsUpToDate()
    {
        var service = new UpdateService(new HttpClient(new StubHandler(_ => Json(ReleaseJson("v1.0.0")))));
        var result = await service.CheckAsync(new Version(1, 1, 0));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task DraftAndPrereleaseAreIgnored()
    {
        foreach (var marker in new[] { "\"draft\": true", "\"prerelease\": true" })
        {
            var json = ReleaseJson("v9.0.0").Replace(marker.Contains("draft", StringComparison.Ordinal) ? "\"draft\": false" : "\"prerelease\": false", marker, StringComparison.Ordinal);
            var result = await new UpdateService(new HttpClient(new StubHandler(_ => Json(json)))).CheckAsync(new Version(1, 0, 0));
            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Null(result.Release);
        }
    }

    [Fact]
    public async Task ApiFailureIsPropagatedWithoutChangingOtherFeatures()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateService(new HttpClient(new StubHandler(_ => response))).CheckAsync(new Version(1, 0, 0)));
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

    [Theory]
    [InlineData("v1.0")]
    [InlineData("nightly")]
    [InlineData("v1.0.0.1")]
    public void RejectsUnsupportedTags(string tag) => Assert.Throws<InvalidDataException>(() => UpdateService.ParseTagVersion(tag));

    [Fact]
    public async Task MissingReleaseAssetIsReported()
    {
        var json = ReleaseJson("v1.2.3").Replace("Beflow-for-Windows-v1.2.3-win-x64-portable.zip", "missing.zip", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateService(new HttpClient(new StubHandler(_ => Json(json)))).CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task DownloadsAndVerifiesSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("verified update package");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hash + "  package.zip") }
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
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('0', 64)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("bad") });
        var service = new UpdateService(new HttpClient(handler));
        using var root = new TempDirectory();
        var asset = new UpdateAsset(UpdatePackageKind.Portable, "package.zip", new Uri("https://example.invalid/package.zip"), 3, "package.zip.sha256", new Uri("https://example.invalid/package.zip.sha256"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndVerifyAsync(asset, root.Info.FullName));
        Assert.Empty(root.Info.GetFiles());
    }

    private static string ReleaseJson(string tag)
    {
        var prefix = $"Beflow-for-Windows-{tag}-win-x64";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "name": "Beflow {{tag}}",
          "body": "更新说明",
          "html_url": "https://github.com/Townley9288/Beflow-for-Windows/releases/tag/{{tag}}",
          "published_at": "2026-07-15T10:00:00Z",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "{{prefix}}-setup.exe", "browser_download_url": "https://example.invalid/setup.exe", "size": 100 },
            { "name": "{{prefix}}-setup.exe.sha256", "browser_download_url": "https://example.invalid/setup.exe.sha256", "size": 64 },
            { "name": "{{prefix}}-portable.zip", "browser_download_url": "https://example.invalid/portable.zip", "size": 100 },
            { "name": "{{prefix}}-portable.zip.sha256", "browser_download_url": "https://example.invalid/portable.zip.sha256", "size": 64 }
          ]
        }
        """;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();
        public void Dispose() { try { Info.Delete(true); } catch { } }
    }
}
