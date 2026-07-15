using System.Net;
using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class AccountStatusTests
{
    [Theory]
    [InlineData("登录成功: SESSDATA=secret-value", "登录成功: SESSDATA=[已隐藏]")]
    [InlineData("登录成功: AccessToken=secret-value", "登录成功: AccessToken=[已隐藏]")]
    [InlineData("access_token=secret-value", "access_token=[已隐藏]")]
    public void LoginOutputIsSanitized(string source, string expected)
    {
        Assert.Equal(expected, BBDownService.SanitizeLoginOutput(source));
    }

    [Fact]
    public async Task MissingCredentialFilesDoNotCallNetwork()
    {
        using var temp = new TempDirectory();
        var root = temp.Info;
        var handler = new StubHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var service = CreateService(root, handler);

        var status = await service.GetStatusAsync();

        Assert.Equal(AccountLoginState.NotConfigured, status.Web.State);
        Assert.Equal(AccountLoginState.NotConfigured, status.Tv.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ParsesWebAndTvProfilesAndSignsTvRequest()
    {
        using var temp = new TempDirectory();
        var root = temp.Info;
        var paths = CreatePaths(root);
        File.WriteAllText(paths.WebCredentialFile, "SESSDATA=web-test-secret;bili_jct=test;Expires=1767323045");
        File.WriteAllText(paths.TvCredentialFile, "access_token=tv-test-token");
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/nav", StringComparison.Ordinal)
            ? Json("""{"code":0,"data":{"isLogin":true,"uname":"WEB用户","mid":123,"face":"http://example.test/web.png","level_info":{"current_level":6},"vipStatus":1,"vip_label":{"text":"年度大会员"}}}""")
            : Json("""{"code":0,"message":"OK","data":{"name":"TV用户","mid":456,"face":"https://example.test/tv.png","level":5,"vip":{"status":1,"label":{"text":"大会员"}}}}"""));
        var now = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var service = new AccountStatusService(paths, new HttpClient(handler), new FixedTimeProvider(now));

        var status = await service.GetStatusAsync();

        Assert.Equal(AccountLoginState.LoggedIn, status.Web.State);
        Assert.Equal("WEB用户", status.Web.Profile!.DisplayName);
        Assert.Equal("123", status.Web.Profile.UserId);
        Assert.Equal(6, status.Web.Profile.Level);
        Assert.StartsWith("https://", status.Web.Profile.AvatarUrl, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767323045), status.Web.CredentialExpiresAt);
        Assert.Equal(AccountLoginState.LoggedIn, status.Tv.State);
        Assert.Equal("TV用户", status.Tv.Profile!.DisplayName);
        Assert.Equal("456", status.Tv.Profile.UserId);
        var tvRequest = Assert.Single(handler.Requests, uri => uri.AbsolutePath.Contains("/myinfo", StringComparison.Ordinal));
        Assert.Contains("ts=1767323045", tvRequest.Query, StringComparison.Ordinal);
        Assert.Contains("sign=f972c23f4230160a0cc56a4c70ac6cee", tvRequest.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitAuthenticationFailuresAreExpired()
    {
        using var temp = new TempDirectory();
        var root = temp.Info;
        var paths = CreatePaths(root);
        File.WriteAllText(paths.WebCredentialFile, "SESSDATA=expired-web-secret");
        File.WriteAllText(paths.TvCredentialFile, "access_token=expired-tv-token");
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/nav", StringComparison.Ordinal)
            ? Json("""{"code":0,"data":{"isLogin":false}}""")
            : Json("""{"code":-101,"message":"账号未登录"}"""));
        var service = new AccountStatusService(paths, new HttpClient(handler));

        var status = await service.GetStatusAsync();

        Assert.Equal(AccountLoginState.Expired, status.Web.State);
        Assert.Equal(AccountLoginState.Expired, status.Tv.State);
    }

    [Fact]
    public async Task IncompleteResponsesAreUnavailable()
    {
        using var temp = new TempDirectory();
        var root = temp.Info;
        var paths = CreatePaths(root);
        File.WriteAllText(paths.WebCredentialFile, "SESSDATA=web-secret");
        File.WriteAllText(paths.TvCredentialFile, "access_token=tv-token");
        var handler = new StubHandler(_ => Json("""{"code":0,"data":{"isLogin":true}}"""));
        var service = new AccountStatusService(paths, new HttpClient(handler));

        var status = await service.GetStatusAsync();

        Assert.Equal(AccountLoginState.Unavailable, status.Web.State);
        Assert.Equal(AccountLoginState.Unavailable, status.Tv.State);
        Assert.Contains("不完整", status.Web.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkAndTimeoutFailuresNeverExposeCredentials()
    {
        using var temp = new TempDirectory();
        var root = temp.Info;
        var paths = CreatePaths(root);
        const string webSecret = "web-super-secret";
        const string tvSecret = "tv-super-secret";
        File.WriteAllText(paths.WebCredentialFile, $"SESSDATA={webSecret}");
        File.WriteAllText(paths.TvCredentialFile, $"access_token={tvSecret}");
        var call = 0;
        var handler = new StubHandler(_ => ++call == 1 ? throw new HttpRequestException("request failed") : throw new TaskCanceledException("timeout"));
        var service = new AccountStatusService(paths, new HttpClient(handler));

        var status = await service.GetStatusAsync();
        var combined = $"{status.Web.Message}\n{status.Tv.Message}";

        Assert.Equal(AccountLoginState.Unavailable, status.Web.State);
        Assert.Equal(AccountLoginState.Unavailable, status.Tv.State);
        Assert.DoesNotContain(webSecret, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(tvSecret, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static AccountStatusService CreateService(DirectoryInfo root, HttpMessageHandler handler) =>
        new(CreatePaths(root), new HttpClient(handler), new FixedTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z")));

    private static ApplicationPaths CreatePaths(DirectoryInfo root)
    {
        var paths = new ApplicationPaths(root.FullName, root.FullName);
        paths.EnsureCreated();
        return paths;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();
        public void Dispose()
        {
            try { Info.Delete(true); } catch (IOException) { }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }
}
