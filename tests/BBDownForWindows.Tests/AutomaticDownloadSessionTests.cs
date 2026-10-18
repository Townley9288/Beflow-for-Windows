using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class AutomaticDownloadSessionTests
{
    private static readonly StreamSelectionRule FourK = new("4K 超高清", "HEVC", "M4A", AudioBitratePriority.Highest);
    private static readonly TaskExecutionContext Context = new(_ => { });

    [Fact]
    public async Task ReadsBothSourcesAndDefaultsToWeb()
    {
        var accounts = new Accounts();
        var parser = new Parser(r => Catalog(Episode(r.ApiMode == "WEB" ? "4K·SDR增强" : "4K 超高清")));
        var result = await new AutomaticDownloadSession(accounts, parser).ParseAsync(Request(), null, Context, default);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal("WEB", episode.ApiMode);
        Assert.Equal(["WEB", "TV"], parser.Requests.Select(r => r.ApiMode));
        Assert.Equal([AccountChannel.Web, AccountChannel.Tv], accounts.Checked);
        Assert.Equal(["WEB", "TV"], episode.Sources.Select(s => s.ApiMode));
        Assert.Equal("4K·SDR增强", Assert.Single(episode.VideoStreams).Quality);
        Assert.True(result.AutomaticSession);
    }

    [Theory]
    [InlineData(AccountLoginState.Expired)]
    [InlineData(AccountLoginState.Unavailable)]
    public async Task TvValidationFailureKeepsWebAndShowsWarning(AccountLoginState tv)
    {
        var parser = new Parser(_ => Catalog(Episode()));
        var result = await new AutomaticDownloadSession(new Accounts(tv), parser).ParseAsync(Request(), null, Context, default);
        Assert.Equal(["WEB"], parser.Requests.Select(r => r.ApiMode));
        var row = Row(result);
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.Contains("TV 读取失败", row.NoticeText);
        Assert.Empty(row.BuildSelection().FallbackReason);
    }

    [Theory]
    [InlineData(AccountLoginState.Expired)]
    [InlineData(AccountLoginState.Unavailable)]
    public async Task InvalidWebDoesNotSilentlySelectTvOrAnonymous(AccountLoginState web)
    {
        var parser = new Parser(_ => throw new Exception("Must not parse"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AutomaticDownloadSession(new Accounts(web: web), parser)
            .ParseAsync(Request(), null, Context, default));
        Assert.Empty(parser.Requests);
    }

    [Theory]
    [InlineData(AccountLoginState.NotConfigured)]
    [InlineData(AccountLoginState.LoggedIn)]
    public async Task MissingWebCredentialsPreserveAnonymousWebAsDefault(AccountLoginState tv)
    {
        var parser = new Parser(_ => Catalog(Episode()));
        var result = await new AutomaticDownloadSession(new Accounts(tv, AccountLoginState.NotConfigured), parser)
            .ParseAsync(Request(), null, Context, default);
        Assert.Equal("WEB", result.Episodes[0].ApiMode);
        Assert.Equal(tv == AccountLoginState.LoggedIn ? 2 : 1, parser.Requests.Count);
    }

    [Fact]
    public async Task MissingTvDoesNotAddHeadersOrWarning()
    {
        var parser = new Parser(_ => Catalog(Episode()));
        var result = await new AutomaticDownloadSession(new Accounts(AccountLoginState.NotConfigured), parser).ParseAsync(Request(), null, Context, default);
        var row = Row(result);
        Assert.All(row.QualityOptions, c => Assert.True(c.IsSelectable));
        Assert.DoesNotContain("TV", row.SelectedQualityLabel);
        Assert.Empty(row.NoticeText);
    }

    [Fact]
    public async Task TvHttpErrorKeepsOriginalWebStreamsWithoutRetry()
    {
        var parser = new Parser(r => r.ApiMode == "TV" ? throw new InvalidOperationException("HTTP 412") : Catalog(Episode()));
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal("WEB", episode.ApiMode);
        Assert.Contains("HTTP 412", episode.SourceWarning);
        Assert.Single(episode.Sources);
        Assert.Equal(["WEB", "TV"], parser.Requests.Select(r => r.ApiMode));
    }

    [Fact]
    public async Task WebErrorStopsWithoutUsingTv()
    {
        var parser = new Parser(_ => throw new InvalidOperationException("WEB HTTP 412"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default));
        Assert.Equal(["WEB"], parser.Requests.Select(r => r.ApiMode));
    }

    [Fact]
    public async Task MissingWebSpecificationDoesNotAutomaticallyUseMatchingTv()
    {
        var parser = new Parser(r => Catalog(Episode(r.ApiMode == "WEB" ? "1080P 高清" : "4K 超高清")));
        var row = Row(await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default));
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.Contains("1080P", row.SelectedQualityLabel);
        Assert.Contains("回退", row.FallbackText);
        Assert.Contains(row.QualityOptions, c => c.ApiMode == "TV" && c.Label.Contains("4K"));
    }

    [Fact]
    public async Task FailedTvPageDoesNotHideSuccessfulTvPagesOrWebPages()
    {
        var failed = Episode(number: 2, cid: "cid2"); failed.State = DownloadEpisodeParseState.Failed; failed.Error = "HTTP 412";
        var parser = new Parser(r => r.ApiMode == "WEB" ? Catalog(Episode(), Episode(number: 2, cid: "cid2")) : Catalog(Episode(), failed));
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
        Assert.Equal(2, result.Episodes[0].Sources.Count);
        Assert.Single(result.Episodes[1].Sources);
        Assert.Contains("HTTP 412", result.Episodes[1].SourceWarning);
        Assert.All(result.Episodes, e => Assert.Equal(DownloadEpisodeParseState.Ready, e.State));
    }

    [Fact]
    public async Task WrongCidIsNotAttachedAndWebRemainsUsable()
    {
        var parser = new Parser(r => Catalog(Episode(cid: r.ApiMode == "TV" ? "other" : "cid1")));
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
        Assert.Single(result.Episodes[0].Sources);
        Assert.Contains("身份已变化", result.Episodes[0].SourceWarning);
    }

    [Fact]
    public async Task TvQueryIsLimitedToReadyWebPages()
    {
        var failed = Episode(number: 2, cid: "cid2"); failed.State = DownloadEpisodeParseState.Failed;
        var parser = new Parser(r => r.ApiMode == "WEB" ? Catalog(Episode(), failed) : Catalog(Episode()));
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
        Assert.Equal("1", parser.Requests[1].Pages);
        Assert.DoesNotContain(result.Episodes[1].Sources, s => s.ApiMode == "TV");
    }

    [Fact]
    public async Task IdenticalQualitiesRemainSeparateAndTvSelectionPinsAudioAndVideo()
    {
        var row = Row(await BothCatalogs());
        var choices = row.QualityOptions.Where(c => c.IsSelectable).ToList();
        Assert.Equal(2, choices.Count);
        Assert.NotEqual(choices[0].Value, choices[1].Value);
        Assert.Equal(["WEB 画质", "TV 画质"], row.QualityOptions.Where(c => !c.IsSelectable).Select(c => c.Label));
        Assert.DoesNotContain("TV", choices[0].Label);
        Assert.StartsWith("TV · ", choices[1].Label);
        row.SelectedQuality = choices[1].Value;
        var selected = row.BuildSelection();
        Assert.Equal("TV", selected.ApiMode);
        Assert.Equal(320, selected.Audio!.BitrateKbps);
        Assert.Equal(3000, selected.Video!.BitrateKbps);
        Assert.True(selected.Video.IsManual);
        Assert.Equal("就绪 · TV", row.StatusText);
        row.SelectedQuality = choices[0].Value;
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.Equal(192, row.BuildSelection().Audio!.BitrateKbps);
    }

    [Fact]
    public async Task GroupHeadersCannotBeSelected()
    {
        var row = Row(await BothCatalogs());
        var original = row.SelectedQuality;
        foreach (var header in row.QualityOptions.Where(c => !c.IsSelectable))
        {
            row.SelectedQuality = header.Value;
            Assert.Equal(original, row.SelectedQuality);
        }
    }

    [Fact]
    public async Task SdrEnhancementRemainsInWebGroup()
    {
        var parser = new Parser(r => Catalog(Episode(r.ApiMode == "WEB" ? "4K·SDR增强" : "4K 超高清")));
        var row = Row(await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default));
        Assert.Contains(row.QualityOptions, c => c.ApiMode == "WEB" && c.Label.StartsWith("4K·SDR增强 · "));
        Assert.DoesNotContain(row.QualityOptions, c => c.ApiMode == "TV" && c.Label.Contains("SDR"));
    }

    [Fact]
    public async Task SelectingTvWithoutAudioReportsFailureWithoutSubstitutingWebAudio()
    {
        var parser = new Parser(r =>
        {
            var episode = Episode();
            if (r.ApiMode == "TV") episode.AudioStreams.Clear();
            return Catalog(episode);
        });
        var row = Row(await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default));
        row.IsSelected = true;
        ChooseTv(row);
        Assert.Equal("TV", row.BuildSelection().ApiMode);
        Assert.Null(row.BuildSelection().Audio);
        Assert.False(row.IsSelected);
        Assert.Contains("音频", row.StatusText);
        row.SelectedQuality = row.QualityOptions.First(c => c.ApiMode == "WEB" && c.IsSelectable).Value;
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.NotNull(row.BuildSelection().Audio);
        Assert.Equal("就绪 · WEB", row.StatusText);
    }

    [Fact]
    public async Task ApplyingBatchRuleUsesWebWithoutMoreRequests()
    {
        var parser = new Parser(_ => Catalog(Episode()));
        var coordinator = new AutomaticDownloadSession(new Accounts(), parser);
        var catalog = await coordinator.ParseAsync(Request(), null, Context, default);
        var row = Row(catalog);
        ChooseTv(row);
        await coordinator.PrepareAsync(catalog, FourK, DownloadMode.VideoAndAudio, Context, default);
        row.ApplyRule(FourK, DownloadMode.VideoAndAudio);
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.Equal(2, parser.Requests.Count);
    }

    [Fact]
    public async Task ManualTvSnapshotRestoresWithBothGroupsAndResolvesOnlyTvStreams()
    {
        var catalog = await BothCatalogs();
        var row = Row(catalog); ChooseTv(row);
        var selection = QueueSnapshot.Copy(row.BuildSelection());
        var saved = QueueSnapshot.Copy(catalog);
        var restored = Row(saved); restored.ApplyRestored(selection, DownloadMode.VideoAndAudio);
        Assert.Equal("TV", restored.BuildSelection().ApiMode);
        Assert.Equal(selection.Video, restored.BuildSelection().Video);
        Assert.Equal(2, restored.QualityOptions.Count(c => c.IsSelectable));
        var decision = StreamSelectionPolicy.Resolve(saved.Episodes[0], selection, new() { Url = "BV1" });
        Assert.Equal(3000, decision.Video!.BitrateKbps);
        Assert.Equal(320, decision.Audio!.BitrateKbps);
        Assert.Equal("WEB", saved.Episodes[0].ApiMode);
    }

    [Fact]
    public async Task SharedMultiAudioCatalogDoesNotShareActiveSelectionBetweenRows()
    {
        var catalog = await BothCatalogs();
        var first = Row(catalog); var second = Row(catalog);
        ChooseTv(first);
        Assert.Equal("TV", first.BuildSelection().ApiMode);
        Assert.Equal("WEB", second.BuildSelection().ApiMode);
        Assert.Equal(192, second.BuildSelection().Audio!.BitrateKbps);
        second.ApplyRule(FourK, DownloadMode.VideoAndAudio);
        Assert.Equal("TV", first.BuildSelection().ApiMode);
    }

    [Fact]
    public async Task TvSourcesArrivingAfterWebProgressRefreshWithoutLosingManualWebChoice()
    {
        var web = Episode(); DownloadSessionPolicy.Remember(web, "WEB");
        var row = new DownloadEpisodeViewModel(web); row.ApplyRule(FourK, DownloadMode.VideoAndAudio);
        row.SelectedEncoding = "HEVC";
        var selected = row.BuildSelection();
        var selectedChoice = row.QualityOptions.Single(c => c.Value == row.SelectedQuality);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        row.QualityOptions.CollectionChanged += (_, args) => changes.Add(args.Action);
        var tv = Episode(); web.Sources.Add(new("TV", tv.VideoStreams, tv.AudioStreams, false));
        web.SourceWarning = "TV 部分条目失败";
        row.RefreshAvailableSources(web);
        Assert.Equal(selected.Video, row.BuildSelection().Video);
        Assert.Equal("WEB", row.BuildSelection().ApiMode);
        Assert.Contains("TV 部分条目失败", row.NoticeText);
        Assert.Contains(row.QualityOptions, c => c.ApiMode == "TV" && c.IsSelectable);
        Assert.Same(selectedChoice, row.QualityOptions.Single(c => c.Value == row.SelectedQuality));
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal("HEVC", row.SelectedEncoding);
        Assert.Equal(selected.Audio, row.BuildSelection().Audio);
    }

    [Fact]
    public async Task AudioOnlyCanExplicitlyChooseTvSource()
    {
        var row = Row(await BothCatalogs());
        row.ApplyRule(FourK, DownloadMode.AudioOnly);
        Assert.True(row.QualitySelectionEnabled);
        ChooseTv(row);
        Assert.Null(row.BuildSelection().Video);
        Assert.Equal("TV", row.BuildSelection().ApiMode);
        Assert.Equal(320, row.BuildSelection().Audio!.BitrateKbps);
    }

    [Fact]
    public async Task CancelledWebReadDoesNotStartTv()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = new Parser(_ => { cancellation.Cancel(); return Catalog(Episode()); });
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, cancellation.Token);
        Assert.Equal("WEB", result.Episodes[0].ApiMode);
        Assert.Single(parser.Requests);
    }

    [Fact]
    public async Task CancellationDuringTvReadKeepsWebWithoutErrorWarning()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = new Parser(r =>
        {
            if (r.ApiMode == "TV") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return Catalog(Episode());
        });
        var result = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, cancellation.Token);
        Assert.Single(result.Episodes[0].Sources);
        Assert.Empty(result.Episodes[0].SourceWarning);
    }

    [Fact]
    public async Task ValidationCacheRechecksBothWhenCredentialsChange()
    {
        var accounts = new Accounts(); var parser = new Parser(_ => Catalog(Episode()));
        var stamp = DateTime.UtcNow;
        var coordinator = new AutomaticDownloadSession(accounts, parser, _ => stamp);
        await coordinator.ParseAsync(Request(), null, Context, default);
        await coordinator.ParseAsync(Request(), null, Context, default);
        Assert.Equal(2, accounts.Checked.Count);
        stamp = stamp.AddSeconds(1);
        await coordinator.ParseAsync(Request(), null, Context, default);
        Assert.Equal(4, accounts.Checked.Count);
    }

    private static DownloadParseRequest Request() => new("https://www.bilibili.com/video/BVtest", DownloadParseMode.All, "AUTO", Rule: FourK);
    private static DownloadEpisodeViewModel Row(DownloadCatalog catalog)
    {
        var row = new DownloadEpisodeViewModel(catalog.Episodes[0]);
        row.ApplyRule(FourK, DownloadMode.VideoAndAudio);
        return row;
    }
    private static void ChooseTv(DownloadEpisodeViewModel row) => row.SelectedQuality = row.QualityOptions.First(c => c.ApiMode == "TV" && c.IsSelectable).Value;
    private static async Task<DownloadCatalog> BothCatalogs()
    {
        var parser = new Parser(r =>
        {
            var episode = Episode();
            if (r.ApiMode == "TV")
            {
                episode.VideoStreams = [episode.VideoStreams[0] with { BitrateKbps = 3000, Bitrate = "3000 kbps" }];
                episode.AudioStreams = [new(0, "M4A", "320 kbps", 320, "1 MB")];
            }
            return Catalog(episode);
        });
        return await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
    }

    [Fact]
    public void OldSavedSelectionKeepsItsOriginalCatalog()
    {
        var episode = Episode();
        var row = new DownloadEpisodeViewModel(episode); row.ApplyRule(FourK, DownloadMode.VideoAndAudio);
        var selection = row.BuildSelection();
        Assert.Empty(selection.ApiMode);
        Assert.Empty(episode.Sources);
        var restored = new DownloadEpisodeViewModel(QueueSnapshot.Copy(episode));
        restored.ApplyRestored(QueueSnapshot.Copy(selection), DownloadMode.VideoAndAudio);
        Assert.Equal(selection.Video, restored.BuildSelection().Video);
        Assert.Equal("就绪", restored.StatusText);
    }

    [Theory]
    [InlineData("TV", "WEB", true)]
    [InlineData("WEB", "TV", false)]
    [InlineData("", "TV", true)]
    public async Task QueueDownloadProcessUsesPinnedEpisodeSession(string selectedApi, string globalApi, bool tvFlag)
    {
        var root = Directory.CreateTempSubdirectory("beflow-session-queue-");
        try
        {
            var executable = Path.Combine(root.FullName, "BBDown.exe"); File.WriteAllText(executable, "test");
            var paths = new ApplicationPaths(root.FullName, root.FullName); paths.EnsureCreated();
            var settings = new SettingsStore(paths);
            var runner = new CaptureRunner(); var locator = new Locator(executable);
            var downloader = new QueuedMediaDownloader(paths, runner, locator, new ParseConcurrencyLimiter(settings));
            await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
                new DownloadRequest { Url = "BVtest", ApiMode = globalApi, WorkDirectory = root.FullName, UseAria2c = false },
                new EpisodeStreamSelection { PageNumber = 1, ApiMode = selectedApi }, "cid1",
                new QueueSourceCheckpoint { Directory = Path.Combine(root.FullName, "private-source") }, () => Task.CompletedTask,
                new Sink<ExactDownloadProgress>(), Context, default));
            Assert.Equal(tvFlag, runner.Arguments.Contains("-tv"));
            Assert.Contains("--beflow-queue", runner.Arguments);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SpaceQueueKeepsDifferentPageSessionsAndSourceSnapshots()
    {
        var parser = new Parser(r => r.ApiMode == "TV" ? Catalog(Episode(), Episode("1080P 高清", number: 2, cid: "cid2"))
            : Catalog(Episode(), Episode(number: 2, cid: "cid2")));
        var catalog = await new AutomaticDownloadSession(new Accounts(), parser).ParseAsync(Request(), null, Context, default);
        var selections = catalog.Episodes.Select(e =>
        {
            var row = new DownloadEpisodeViewModel(e); row.ApplyRule(FourK, DownloadMode.VideoAndAudio);
            if (e.Page.Number == 1) ChooseTv(row);
            return row.BuildSelection();
        }).ToList();
        var root = Directory.CreateTempSubdirectory("beflow-space-session-");
        try
        {
            var profile = new BilibiliSpaceProfile("538596213", "UP", "", "");
            var item = BilibiliSpaceBatchService.BuildQueueItem(profile, catalog, selections, new(), root.FullName, FourK, DownloadMode.VideoAndAudio);
            DownloadQueueService.ValidateItem(item);
            var saved = QueueSnapshot.Copy(item);
            Assert.Equal(["TV", "WEB"], saved.Download!.Episodes.Select(e => e.ApiMode));
            Assert.Equal(2, saved.Catalog!.Episodes[1].Sources.Count);
            Assert.EndsWith("UP（538596213）", saved.Download.Options.WorkDirectory);
        }
        finally { root.Delete(true); }
    }

    private sealed class Sink<T> : IProgress<T> { public void Report(T value) { } }
    private sealed class CaptureRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Arguments = request.Arguments; return Task.FromResult(new ProcessResult(1, "test stop before media", false));
        }
        public Task TerminateAllAsync() => Task.CompletedTask;
    }
    private sealed class Locator(string executable) : IToolLocator
    {
        public ToolPaths Locate(AppSettings settings) => new() { BBDown = executable };
        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult("test");
    }
    private static DownloadCatalog Catalog(params DownloadEpisodeInfo[] episodes) => new()
    {
        ResolvedUrl = "https://www.bilibili.com/video/BVtest", Episodes = episodes.ToList(), AllPages = episodes.Select(e => e.Page).ToList()
    };
    private static DownloadEpisodeInfo Episode(string quality = "4K 超高清", string codec = "HEVC", string audio = "M4A", int number = 1, string cid = "cid1") => new()
    {
        Page = new(number, cid, $"分P {number}", "1m"), State = DownloadEpisodeParseState.Ready,
        VideoStreams = [new(0, quality, quality.StartsWith("4K") ? "3840x2160" : "1920x1080", 3840, 2160, codec, "30", "6000 kbps", 6000, "1 MB")],
        AudioStreams = [new(0, audio, "192 kbps", 192, "1 MB")]
    };
    private sealed class Accounts(AccountLoginState tv = AccountLoginState.LoggedIn, AccountLoginState web = AccountLoginState.LoggedIn) : IAccountStatusService
    {
        public List<AccountChannel> Checked { get; } = [];
        public Task<AccountChannelStatus> GetStatusAsync(AccountChannel channel, CancellationToken cancellationToken = default)
        {
            Checked.Add(channel);
            return Task.FromResult(new AccountChannelStatus(channel, channel == AccountChannel.Tv ? tv : web, null, "测试状态", DateTimeOffset.Now, null));
        }
        public Task<AccountStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ProgressSink(Action<DownloadParseProgress> report) : IProgress<DownloadParseProgress>
    {
        public void Report(DownloadParseProgress value) => report(value);
    }
    private sealed class Parser(Func<DownloadParseRequest, DownloadCatalog> parse) : IBBDownService
    {
        public List<DownloadParseRequest> Requests { get; } = [];
        public Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            Requests.Add(request); var catalog = parse(request);
            foreach (var e in catalog.Episodes) progress?.Report(new(1, catalog.Episodes.Count, e.Page.Number, e.Page.Title, e, "解析"));
            return Task.FromResult(catalog);
        }
        public Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadBatchResult> DownloadBatchAsync(DownloadBatchRequest request, IProgress<DownloadProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadEpisodeInfo> ParseEpisodeAsync(string url, int page, string apiMode, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExactDownloadResult> DownloadExactAsync(ExactDownloadRequest request, IProgress<ExactDownloadProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetTitleAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
