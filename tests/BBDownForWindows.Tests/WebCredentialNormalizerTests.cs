using System.Net;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class WebCredentialNormalizerTests
{
    private const string TicketCredential =
        "ticket=0123456789abcdef0123456789abcdef;" +
        "gourl=https%3A%2F%2Fwww.bilibili.com%2F;" +
        "first_domain=.bilibili.com";

    [Fact]
    public async Task CanonicalCookieIsKeptWithoutNetworkRequest()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        const string credential = "SESSDATA=existing-session;bili_jct=existing-csrf";
        await File.WriteAllTextAsync(path, credential);
        var handler = new StubHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        await service.NormalizeAsync(path);

        Assert.Equal(credential, await File.ReadAllTextAsync(path));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TicketRedirectsExportOnlyBilibiliAuthenticationCookies()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        await File.WriteAllTextAsync(path, TicketCredential);
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "passport.biligame.com" => Redirect(
                "https://passport.bilibili.com/crossDomain?ticket=test",
                "SESSDATA=wrong-domain-session; Path=/; Domain=.biligame.com"),
            "passport.bilibili.com" => Redirect(
                "https://www.bilibili.com/",
                "DedeUserID=10001; Path=/; Domain=.bilibili.com",
                "DedeUserID__ckMd5=user-md5; Path=/; Domain=.bilibili.com",
                "SESSDATA=valid-session; Path=/; Domain=.bilibili.com; Expires=Wed, 09 Jun 2027 10:18:14 GMT; HttpOnly; Secure",
                "bili_jct=valid-csrf; Path=/; Domain=.bilibili.com",
                "unrelated=ignored; Path=/; Domain=.bilibili.com"),
            _ => throw new InvalidOperationException("不应访问最终页面")
        });
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        await service.NormalizeAsync(path);

        Assert.Equal(
            "DedeUserID=10001;DedeUserID__ckMd5=user-md5;SESSDATA=valid-session;bili_jct=valid-csrf;Expires=1812536294",
            await File.ReadAllTextAsync(path));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("passport.biligame.com", handler.Requests[0].Host);
        Assert.Contains("ticket=0123456789abcdef0123456789abcdef", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-domain-session", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.DoesNotContain("ticket=", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketExpiresIsPreservedWhenExchangeOmitsCookieExpiry()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        const string credential = TicketCredential + ";Expires=1812536294";
        await File.WriteAllTextAsync(path, credential);
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "passport.biligame.com" => Redirect(
                "https://passport.bilibili.com/"),
            "passport.bilibili.com" => Redirect(
                "https://www.bilibili.com/",
                "SESSDATA=valid-session; Path=/; Domain=.bilibili.com"),
            _ => throw new InvalidOperationException("不应访问最终页面")
        });
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        await service.NormalizeAsync(path);

        Assert.Equal("SESSDATA=valid-session;Expires=1812536294", await File.ReadAllTextAsync(path));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("SESSDATA=valid-session;ticket=stale-ticket")]
    [InlineData("SESSDATA=valid-session;gourl=https%3A%2F%2Fwww.bilibili.com%2F")]
    [InlineData("SESSDATA=valid-session;first_domain=.bilibili.com")]
    public void TicketMetadataNeverCountsAsCanonicalCookie(string credential)
    {
        Assert.False(BilibiliWebCredentialNormalizer.IsCanonicalCredential(credential));
    }

    [Fact]
    public async Task MixedTicketAndCookieIsRejectedWithoutOverwritingCredential()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        const string credential =
            "SESSDATA=valid-session;ticket=stale-ticket;" +
            "gourl=https%3A%2F%2Fwww.bilibili.com%2F;first_domain=.bilibili.com";
        await File.WriteAllTextAsync(path, credential);
        var handler = new StubHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.NormalizeAsync(path));

        Assert.Empty(handler.Requests);
        Assert.Equal(credential, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExternalRedirectIsRejectedWithoutOverwritingTicket()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        await File.WriteAllTextAsync(path, TicketCredential);
        var handler = new StubHandler(_ => Redirect("https://example.test/steal"));
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.NormalizeAsync(path));

        Assert.Contains("不受信任", exception.Message, StringComparison.Ordinal);
        Assert.Equal(TicketCredential, await File.ReadAllTextAsync(path));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TicketTimeoutIsReportedAsLoginFailureAndKeepsOriginalFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        await File.WriteAllTextAsync(path, TicketCredential);
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.NormalizeAsync(path));

        Assert.Contains("超时", exception.Message, StringComparison.Ordinal);
        Assert.Equal(TicketCredential, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("ticket=abc;gourl=https%3A%2F%2Fevil.example%2F;first_domain=.bilibili.com")]
    [InlineData("ticket=abc;gourl=http%3A%2F%2Fwww.bilibili.com%2F;first_domain=.bilibili.com")]
    [InlineData("ticket=a%2Fb;gourl=https%3A%2F%2Fwww.bilibili.com%2F;first_domain=.bilibili.com")]
    public async Task InvalidTicketMetadataNeverCallsNetwork(string credential)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Info.FullName, "BBDown.data");
        await File.WriteAllTextAsync(path, credential);
        var handler = new StubHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var service = new BilibiliWebCredentialNormalizer(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.NormalizeAsync(path));

        Assert.Empty(handler.Requests);
        Assert.Equal(credential, await File.ReadAllTextAsync(path));
    }

    private static HttpResponseMessage Redirect(string location, params string[] cookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        foreach (var cookie in cookies) response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
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

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();
        public void Dispose()
        {
            try { Info.Delete(true); }
            catch (IOException) { }
        }
    }
}
