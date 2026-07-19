using System.Net;
using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class TmdbServiceTests
{
    [Fact]
    public async Task ValidationCanUseUnsavedDraftWithoutChangingTheSettingsStore()
    {
        var persisted = new RenameSettings { TmdbApiKey = "stored-key", RequestTimeoutSeconds = 3 };
        var store = new FixedRenameSettingsStore(persisted);
        var service = new TmdbService(store, _ => new StubHandler(_ => Json("{\"success\":true}")));

        await service.ValidateApiKeyAsync(new RenameSettings { TmdbApiKey = "draft-key", RequestTimeoutSeconds = 3 });

        Assert.Equal("stored-key", (await store.LoadAsync()).TmdbApiKey);
    }

    [Fact]
    public async Task SearchDetailEpisodesAndValidationUseExpectedLanguagesAndCache()
    {
        var requests = new List<Uri>();
        var responder = new Func<HttpRequestMessage, HttpResponseMessage>(request =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/authentication/token/new", StringComparison.Ordinal)) return Json("{\"success\":true}");
            if (path.EndsWith("/search/multi", StringComparison.Ordinal)) return Json("""{"results":[{"id":12,"media_type":"tv","name":"流人","original_name":"Slow Horses","first_air_date":"2024-09-04","overview":"简介","poster_path":"/poster.jpg"}]}""");
            if (path.EndsWith("/tv/12", StringComparison.Ordinal)) return Json("""{"name":"Slow Horses","first_air_date":"2024-09-04","alternative_titles":{"results":[{"iso_3166_1":"US","title":"Slow Horses"}]}}""");
            if (path.EndsWith("/tv/12/season/4", StringComparison.Ordinal)) return Json("""{"episodes":[{"episode_number":1,"name":"身份盗窃"}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(responder);

        await service.ValidateApiKeyAsync();
        var results = await service.SearchAsync("流人");
        var cachedResults = await service.SearchAsync("流人");
        var detail = await service.GetDetailAsync(results.Single());
        var episodes = await service.GetEpisodeNamesAsync(12, 4);

        Assert.Single(results);
        Assert.Single(cachedResults);
        Assert.Equal("Slow Horses", detail.EnglishTitle);
        Assert.Equal("身份盗窃", episodes[1]);
        Assert.Equal(4, requests.Count);
        Assert.Contains(requests, uri => uri.Query.Contains("language=zh-CN", StringComparison.Ordinal));
        Assert.Contains(requests, uri => uri.Query.Contains("language=en-US", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticationErrorNeverExposesApiKey()
    {
        const string secret = "super-secret-key";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), secret);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("测试"));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Contains("API Key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitRetriesOnceThenReportsFixedMessage()
    {
        var calls = 0;
        var service = CreateService(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("测试"));
        Assert.Equal(2, calls);
        Assert.Contains("请求过于频繁", error.Message, StringComparison.Ordinal);
    }

    private static TmdbService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder, string apiKey = "test-key")
    {
        var store = new FixedRenameSettingsStore(new RenameSettings { TmdbApiKey = apiKey, RequestTimeoutSeconds = 3 });
        return new TmdbService(store, _ => new StubHandler(responder));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private sealed class FixedRenameSettingsStore(RenameSettings settings) : IRenameSettingsStore
    {
        public Task<RenameSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings.Clone());
        public Task SaveAsync(RenameSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RenameSettings> UpdateAsync(Func<RenameSettings, RenameSettings> update, CancellationToken cancellationToken = default) => Task.FromResult(update(settings.Clone()));
    }
}
