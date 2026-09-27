using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class BilibiliSpaceTests
{
    [Fact]
    public async Task UserSearchEscapesKeywordUsesWebCredentialAndReturnsPublicDetails()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Paths.WebCredentialFile, "session=test-only");
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/x/web-interface/search/type", request.RequestUri!.AbsolutePath);
            Assert.Contains("keyword=" + Uri.EscapeDataString("UP & 中文"), request.RequestUri.Query);
            Assert.Contains("page=2", request.RequestUri.Query);
            Assert.Equal("https://search.bilibili.com/", request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("session=test-only", request.Headers.GetValues("Cookie").Single());
            return """{"code":0,"data":{"page":2,"pagesize":20,"numResults":21,"numPages":2,"result":[{"mid":123,"uname":"<em class=\"keyword\">UP</em> &amp; 中文","upic":"//image.test/avatar","usign":"简介 &amp; 签名","fans":1000,"videos":70,"official_verify":{"type":0,"desc":"认证信息"}}]}}""";
        }));
        var page = await new BilibiliSpaceService(client, fixture.Paths).SearchUsersAsync(" UP & 中文 ", 2);
        var user = Assert.Single(page.Items);
        Assert.Equal("UP & 中文", user.Name);
        Assert.Equal("https://image.test/avatar", user.AvatarUrl);
        Assert.Equal("简介 & 签名", user.Description);
        Assert.Equal("认证信息", user.Verification);
        Assert.Equal(1000, user.Fans);
        Assert.Equal(70, user.Videos);
        Assert.False(page.HasMore);
        Assert.Equal(21, page.Total);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("{\"page\":1,\"pagesize\":20,\"numResults\":0,\"numPages\":0,\"result\":[]}", true)]
    [InlineData("{\"page\":1,\"pagesize\":20,\"numResults\":0,\"numPages\":0}", false)]
    [InlineData("{\"page\":1,\"pagesize\":20,\"numResults\":21,\"numPages\":2,\"result\":[]}", false)]
    [InlineData("{\"page\":2,\"pagesize\":20,\"numResults\":0,\"numPages\":0,\"result\":[]}", false)]
    public async Task SearchRequiresValidPageAndResultShape(string data, bool empty)
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => "{\"code\":0,\"data\":" + data + "}"));
        var service = new BilibiliSpaceService(client, fixture.Paths);
        if (empty) Assert.Empty((await service.SearchUsersAsync("UP", 1)).Items);
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.SearchUsersAsync("UP", 1));
    }

    [Theory]
    [InlineData(-352, "风控")]
    [InlineData(-101, "WEB 登录")]
    public async Task SearchApiFailureIsNotAnEmptyResult(int code, string hint)
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => JsonSerializer.Serialize(new { code, message = "failure" })));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new BilibiliSpaceService(client, fixture.Paths).SearchUsersAsync("UP", 1));
        Assert.Contains("UP 主搜索", ex.Message);
        Assert.Contains(hint, ex.Message);
    }

    [Fact]
    public async Task SearchReportsHttpRiskControlAndSupportsCancellation()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => "<html>blocked</html>", HttpStatusCode.PreconditionFailed));
        var service = new BilibiliSpaceService(client, fixture.Paths);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchUsersAsync("UP", 1));
        Assert.Contains("HTTP 412", ex.Message);
        Assert.Contains("风控", ex.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchUsersAsync("UP", 1, cancellation.Token));
    }

    [Fact]
    public void WbiSigningSortsEscapesAndSanitizesValues()
    {
        var query = "a=%E4%B8%AD%20%E6%96%87&b=abc&wts=1702204169";
        var expectedHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + "test-key"))).ToLowerInvariant();
        Assert.Equal(query + "&w_rid=" + expectedHash, BilibiliSpaceService.Sign(new Dictionary<string, string> { ["b"] = "a!b'c()*", ["a"] = "中 文" }, "test-key", 1702204169));
        Assert.Equal(13527, BilibiliSpaceService.Duration("225:27"));
        Assert.Equal(3723, BilibiliSpaceService.Duration("1:02:03"));
    }

    [Fact]
    public void DirectoryKeepsResumePositionAndDeduplicatesAcrossPages()
    {
        var directory = new BilibiliSpaceDirectory<string>(v => v);
        directory.Append(new(["a", "b"], 1, 2, 4));
        Assert.False(directory.Complete); Assert.Equal(2, directory.NextPage);
        Assert.Throws<InvalidDataException>(() => directory.Append(new([], 2, 2, 4)));
        Assert.Equal(2, directory.NextPage); Assert.Equal(["a", "b"], directory.Items);
        directory.Append(new(["b", "c"], 2, 2, 4));
        Assert.True(directory.Complete); Assert.Equal(["a", "b", "c"], directory.Items);
    }

    [Fact]
    public async Task UploadsUseOwnCountAndAttachCredentialOnlyToApiRequests()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Paths.WebCredentialFile, "session=test-only");
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("api.bilibili.com", request.RequestUri!.Host);
            Assert.Equal("session=test-only", request.Headers.GetValues("Cookie").Single());
            requests.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/x/web-interface/card" => """{"code":0,"data":{"archive_count":190,"card":{"name":"UP","face":"//image.test/a","sign":"intro"}}}""",
                "/x/web-interface/nav" => """{"code":0,"data":{"wbi_img":{"img_url":"https://image.test/0123456789abcdef0123456789abcdef.png","sub_url":"https://image.test/fedcba9876543210fedcba9876543210.png"}}}""",
                _ => """{"code":0,"data":{"page":{"pn":1,"ps":30,"count":70},"list":{"tlist":{"160":{"tid":160,"name":"生活","count":70}},"vlist":[{"aid":1,"bvid":"BV1xx411c7mD","title":"视频","pic":"//image.test/cover","length":"225:27","created":1700000000,"typeid":160}]}}}"""
            };
        }));
        var service = new BilibiliSpaceService(client, fixture.Paths);
        var profile = await service.GetProfileAsync("123"); var page = await service.GetUploadsAsync("123", 1);
        Assert.Equal("UP", profile.Name); Assert.Equal(70, page.Total); Assert.True(page.HasMore);
        Assert.Equal(13527, Assert.Single(page.Items).DurationSeconds);
        Assert.Empty(client.DefaultRequestHeaders);
        Assert.Equal(3, requests.Count);
    }

    [Theory]
    [InlineData(-352, "风控")]
    [InlineData(-101, "WEB 登录")]
    [InlineData(-404, "-404")]
    public async Task ApiFailureIsNotAnEmptyDirectory(int code, string expected)
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => JsonSerializer.Serialize(new { code, message = "failure" })));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new BilibiliSpaceService(client, fixture.Paths).GetCollectionsAsync("123", 1));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task CollectionsAndSeriesUseTheirOwnPaginationShapes()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/polymer/web-space/seasons_series_list" => """{"code":0,"data":{"items_lists":{"page":{"page_num":1,"page_size":20,"total":2},"seasons_list":[{"meta":{"season_id":10,"name":"合集","total":31}}],"series_list":[{"meta":{"series_id":20,"name":"系列","total":1}}]}}}""",
            "/x/series/archives" => """{"code":0,"data":{"page":{"num":1,"size":30,"total":0},"archives":[]}}""",
            _ => """{"code":0,"data":{"page":{"page_num":1,"page_size":30,"total":31},"archives":[{"aid":1,"bvid":"BV1xx411c7mD","title":"视频","duration":30,"pubdate":1700000000}]}}"""
        }));
        var service = new BilibiliSpaceService(client, fixture.Paths);
        var collections = await service.GetCollectionsAsync("123", 1);
        Assert.Equal(2, collections.Items.Count);
        Assert.True((await service.GetCollectionVideosAsync(collections.Items[0], 1)).HasMore);
        var series = await service.GetCollectionVideosAsync(collections.Items[1], 1);
        Assert.False(series.HasMore); Assert.Empty(series.Items);
    }

    [Fact]
    public async Task EmptyUploadsFinishWithoutUsingProfileStatistics()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? """{"code":-101,"data":{"wbi_img":{"img_url":"https://image.test/0123456789abcdef0123456789abcdef.png","sub_url":"https://image.test/fedcba9876543210fedcba9876543210.png"}}}"""
            : """{"code":0,"data":{"page":{"pn":1,"ps":30,"count":0},"list":{"tlist":{},"vlist":[]}}}"""));
        var directory = new BilibiliSpaceDirectory<BilibiliSpaceVideo>(v => v.Bvid);
        directory.Append(await new BilibiliSpaceService(client, fixture.Paths).GetUploadsAsync("123", 1));
        Assert.True(directory.Complete);
        Assert.Empty(directory.Items);
    }

    [Fact]
    public async Task InvalidShapeHttpFailureAndCancellationRemainErrors()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => """{"code":0,"data":{}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BilibiliSpaceService(client, fixture.Paths).GetCollectionsAsync("123", 1));
        using var httpFailure = new HttpClient(new Handler(_ => "", HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() => new BilibiliSpaceService(httpFailure, fixture.Paths).GetProfileAsync("123"));
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BilibiliSpaceService(client, fixture.Paths).GetCollectionsAsync("123", 1, source.Token));
    }

    [Fact]
    public void UpDirectorySanitizesNameAndDoesNotAlterGlobalDirectory()
    {
        var root = Path.GetFullPath("space-test");
        var output = BilibiliSpaceBatchService.OutputRoot(root, new("123", "UP:/Name.", "", ""));
        Assert.Equal(Path.Combine(root, "UP__Name（123）"), output);
        Assert.Throws<InvalidOperationException>(() => BilibiliSpaceBatchService.OutputRoot("relative", new("123", "UP", "", "")));
    }

    [Fact]
    public void UpDirectoryRejectsFileConflictsAndMalformedUid()
    {
        using var fixture = new Fixture();
        var root = fixture.Paths.RuntimeDirectory;
        var profile = new BilibiliSpaceProfile("123", "UP", "", "");
        var occupied = BilibiliSpaceBatchService.OutputRoot(root, profile);
        File.WriteAllText(occupied, "keep");
        Assert.Throws<IOException>(() => BilibiliSpaceBatchService.OutputRoot(root, profile));
        Assert.Equal("keep", File.ReadAllText(occupied));
        Assert.Throws<InvalidOperationException>(() => BilibiliSpaceBatchService.OutputRoot(root, profile with { Uid = "../bad" }));
    }

    private sealed class Handler(Func<HttpRequestMessage, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory();
        public ApplicationPaths Paths { get; }
        public Fixture() { Paths = new(root.FullName, root.FullName); Paths.EnsureCreated(); }
        public void Dispose() => root.Delete(true);
    }
}
