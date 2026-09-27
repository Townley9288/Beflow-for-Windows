using BBDownForWindows.App;
using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class PersonalSpaceViewModelTests
{
    private static BilibiliSpaceUser User(string uid, string name) => new(uid, name, "", "简介", 1234, 31, "");

    [Fact]
    public async Task NameSearchWaitsForExplicitChoiceAndKeepsCurrentSelection()
    {
        using var f = new Fixture();
        await f.LoadAsync();
        var vm = f.ViewModel;
        vm.VisibleVideos[0].IsSelected = true;
        var selected = Assert.Single(vm.SelectedVideos);
        vm.Input = "同名 UP";
        f.Space.SearchHandler = (_, _, _) => Task.FromResult(new BilibiliUserSearchPage([User("456", "同名 UP"), User("789", "同名 UP")], 1, 20, 2, 1));
        Assert.Equal("搜索 UP 主", vm.InputActionText);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("123", vm.Profile!.Uid);
        Assert.Single(f.Space.ProfileRequests);
        Assert.Same(selected, Assert.Single(vm.SelectedVideos));
        Assert.Equal(2, vm.UserResults.Count);
        await vm.OpenUserCommand.ExecuteAsync(vm.UserResults[1]);
        Assert.Equal("789", vm.Profile!.Uid);
        Assert.Equal("https://space.bilibili.com/789", vm.Input);
        Assert.Empty(vm.UserResults);
        Assert.Empty(vm.SelectedVideos);
    }

    [Fact]
    public async Task SearchPagingKeepsCandidatesOnFailureAndRetriesSamePage()
    {
        using var f = new Fixture();
        var vm = f.ViewModel;
        var fail = true;
        f.Space.SearchHandler = (_, page, _) =>
        {
            if (page == 2 && fail) throw new IOException("HTTP 412 风控");
            return Task.FromResult(new BilibiliUserSearchPage(page == 1 ? [User("1", "UP")] : [User("1", "UP"), User("2", "UP")], page, 20, 21, 2));
        };
        vm.Input = "UP";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.MoreUsersCommand.CanExecute(null));
        await vm.MoreUsersCommand.ExecuteAsync(null);
        Assert.Single(vm.UserResults);
        Assert.Contains("HTTP 412", vm.Message);
        Assert.DoesNotContain("没有找到", vm.Message);
        Assert.True(vm.RetryUserSearchCommand.CanExecute(null));
        fail = false;
        await vm.RetryUserSearchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.UserResults.Count);
        Assert.Equal([1, 2, 2], f.Space.SearchRequests.Select(request => request.Page));
        Assert.False(vm.MoreUsersCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingSearchKeepsResultsAndAllowsExplicitRetry()
    {
        using var f = new Fixture();
        var vm = f.ViewModel;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Space.SearchHandler = async (_, page, token) =>
        {
            if (page == 2) { blocked.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new([User("1", "UP")], page, 20, 21, 2);
        };
        vm.Input = "UP";
        await vm.ReadCommand.ExecuteAsync(null);
        var pending = vm.MoreUsersCommand.ExecuteAsync(null);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.OpenUserCommand.CanExecute(vm.UserResults[0]));
        vm.CancelCommand.Execute(null);
        await pending;
        Assert.Single(vm.UserResults);
        Assert.Contains("已取消", vm.Message);
        Assert.True(vm.RetryUserSearchCommand.CanExecute(null));
        f.Space.SearchHandler = (_, page, _) => Task.FromResult(new BilibiliUserSearchPage([User("2", "UP")], page, 20, 21, 2));
        await vm.RetryUserSearchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.UserResults.Count);
    }

    [Fact]
    public async Task ClosingOrEditingSearchDoesNotDiscardLoadedProfileOrManualStreams()
    {
        using var f = new Fixture();
        await f.LoadAsync();
        var vm = f.ViewModel;
        vm.VisibleVideos[0].IsSelected = true;
        await vm.ParseCommand.ExecuteAsync(null);
        var selected = Assert.Single(vm.SelectedVideos);
        var row = selected.Rows[0];
        vm.Input = "UP";
        await vm.ReadCommand.ExecuteAsync(null);
        vm.CloseUserSearchCommand.Execute(null);
        Assert.Equal("https://space.bilibili.com/123", vm.Input);
        Assert.Same(row, Assert.Single(vm.SelectedVideos).Rows[0]);
        vm.Input = "UP";
        await vm.ReadCommand.ExecuteAsync(null);
        vm.Input = "另一个 UP";
        Assert.Empty(vm.UserResults);
        Assert.Same(selected, Assert.Single(vm.SelectedVideos));
    }

    [Theory]
    [InlineData("https://space.bilibili.com/456?spm_id_from=test", true)]
    [InlineData("https://space.bilibili.com.evil.test/456", false)]
    [InlineData("https://space.bilibili.com/0", false)]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", false)]
    public async Task LinksDoNotBecomeUserSearchKeywords(string input, bool valid)
    {
        using var f = new Fixture();
        f.ViewModel.Input = input;
        await f.ViewModel.ReadCommand.ExecuteAsync(null);
        Assert.Empty(f.Space.SearchRequests);
        Assert.Equal(valid ? "456" : null, f.ViewModel.Profile?.Uid);
        if (!valid) Assert.Contains("有效", f.ViewModel.Message);
    }

    [Fact]
    public async Task EmptySearchResultsAreDistinctFromFailures()
    {
        using var f = new Fixture();
        f.Space.SearchHandler = (_, page, _) => Task.FromResult(new BilibiliUserSearchPage([], page, 20, 0, 0));
        f.ViewModel.Input = "不存在的 UP";
        await f.ViewModel.ReadCommand.ExecuteAsync(null);
        Assert.Contains("没有找到", f.ViewModel.Message);
        Assert.Empty(f.ViewModel.UserResults);
        Assert.False(f.ViewModel.RetryUserSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectionSurvivesPagingFiltersAndDuplicateCollectionMembers()
    {
        using var f = new Fixture();
        await f.LoadAsync();
        var vm = f.ViewModel;
        Assert.Equal(30, vm.VisibleVideos.Count);
        vm.SelectPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null);
        Assert.Single(vm.VisibleVideos);
        vm.SelectPageCommand.Execute(null);
        Assert.Equal(31, vm.SelectedVideos.Count);
        vm.ClearSelectionCommand.Execute(null);
        vm.SearchText = "视频 01";
        vm.SelectFilteredCommand.Execute(null);
        var chosen = Assert.Single(vm.SelectedVideos);
        Assert.Equal(Video(1).Bvid, chosen.Bvid);
        await vm.SwitchTabAsync(1);
        await vm.OpenCollectionCommand.ExecuteAsync(Assert.Single(vm.VisibleCollections));
        Assert.Same(chosen, vm.VisibleVideos[0]);
        vm.SelectFilteredCommand.Execute(null);
        Assert.Equal(2, vm.SelectedVideos.Count);
        await vm.SwitchTabAsync(0);
        vm.CategoryId = 1;
        vm.ClearSelectionCommand.Execute(null);
        vm.SelectFilteredCommand.Execute(null);
        Assert.Equal(16, vm.SelectedVideos.Count);
        Assert.All(vm.SelectedVideos, v => Assert.Equal(1, v.Video.CategoryId));
    }

    [Fact]
    public async Task InterruptedDirectoryKeepsResultsAndContinuesFromFailedPage()
    {
        using var f = new Fixture();
        f.Space.FailPage = 2;
        await f.LoadAsync();
        var vm = f.ViewModel;
        Assert.False(vm.DirectoryComplete);
        Assert.Contains("未读取完整", vm.DirectoryStatus);
        Assert.Equal(30, vm.VisibleVideos.Count);
        Assert.False(vm.SelectFilteredCommand.CanExecute(null));
        vm.VisibleVideos[0].IsSelected = true;
        var chosen = vm.VisibleVideos[0];
        f.Space.FailPage = 0;
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal([1, 2, 2], f.Space.UploadPages);
        Assert.True(vm.DirectoryComplete);
        Assert.Same(chosen, Assert.Single(vm.SelectedVideos));
    }

    [Fact]
    public async Task RetryPreservesManualP1AndQueuesEachVideoWithIndependentPages()
    {
        using var f = new Fixture();
        await f.LoadAsync();
        var vm = f.ViewModel;
        vm.SortOrder = "目录顺序";
        vm.VisibleVideos[0].IsSelected = true;
        vm.VisibleVideos[1].IsSelected = true;
        await vm.ParseCommand.ExecuteAsync(null);
        Assert.Equal(2, f.Downloader.Requests.Count);
        var first = vm.SelectedVideos[0];
        var p1 = first.Rows[0];
        var manual = p1.BuildSelection();
        manual.Video = manual.Video! with { IsManual = true };
        manual.Audio = manual.Audio! with { IsManual = true };
        p1.ApplyRestored(manual, DownloadMode.VideoAndAudio);
        Assert.Contains("未就绪 1", vm.ReadySummary);
        vm.BrowseCommand.Execute(null);
        await vm.ParseCommand.ExecuteAsync(null);
        Assert.Equal(3, f.Downloader.Requests.Count);
        Assert.Equal("2", f.Downloader.Requests[^1].Pages);
        Assert.Same(p1, first.Rows[0]);
        Assert.True(first.Rows[0].BuildSelection().Video!.IsManual);
        Assert.True(first.Rows[0].BuildSelection().Audio!.IsManual);
        Assert.Contains("可入队 2 个视频 / 3 个分P", vm.ReadySummary);
        await f.Services.DownloadQueue.PauseAsync();
        await vm.EnqueueCommand.ExecuteAsync(null);
        var jobs = f.Services.DownloadQueue.Snapshot.Items;
        Assert.Equal(2, jobs.Count);
        Assert.Equal([1, 2], jobs[0].Download!.Episodes.Select(e => e.PageNumber));
        Assert.Equal(1, Assert.Single(jobs[1].Download!.Episodes).PageNumber);
        Assert.Equal(first.Bvid, jobs[0].Catalog!.Metadata!.Bvid);
        Assert.Equal(vm.SelectedVideos[1].Bvid, jobs[1].Catalog!.Metadata!.Bvid);
        Assert.All(jobs, j => Assert.Equal(Path.Combine(f.Root, "UP__name（123）"), j.Download!.Options.WorkDirectory));
        Assert.Equal(f.Root, (await f.Services.Settings.LoadAsync()).WorkDirectory);
        Assert.All(vm.SelectedVideos, v => Assert.True(v.IsEnqueued));
        Assert.False(vm.EnqueueCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledBatchRetainsFinishedVideoAndOnlyParsesRemainingVideo()
    {
        using var f = new Fixture();
        await f.LoadAsync();
        var vm = f.ViewModel;
        vm.SortOrder = "目录顺序";
        vm.VisibleVideos[1].IsSelected = true;
        vm.VisibleVideos[2].IsSelected = true;
        f.Downloader.BlockUrl = Video(3).Url;
        var parsing = vm.ParseCommand.ExecuteAsync(null);
        await f.Downloader.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.CancelCommand.Execute(null);
        await parsing;
        Assert.NotNull(vm.SelectedVideos[0].Catalog);
        Assert.Null(vm.SelectedVideos[1].Catalog);
        f.Downloader.BlockUrl = "";
        await vm.ParseCommand.ExecuteAsync(null);
        Assert.Equal(3, f.Downloader.Requests.Count);
        Assert.All(vm.SelectedVideos, v => Assert.NotNull(v.Catalog));
    }

    private static BilibiliSpaceVideo Video(int i) => new(i.ToString(), $"BV1xx411c{i:D3}", $"视频 {i:D2}", "", 30, DateTimeOffset.UnixEpoch.AddDays(i), i % 2);
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();
        public string Root => directory.FullName;
        public AppServices Services { get; }
        public SpaceStub Space { get; } = new();
        public DownloadStub Downloader { get; } = new();
        public PersonalSpaceViewModel ViewModel { get; }
        public Fixture()
        {
            Services = new(new ApplicationPaths(Path.Combine(Root, "app"), Path.Combine(Root, "data")));
            ViewModel = new(Space, Downloader, Services.Settings, Services.TaskManager, Services.DownloadQueue);
        }
        public async Task LoadAsync()
        {
            await Services.Settings.SaveAsync(new() { WorkDirectory = Root, VideoQualityRule = "1080P 高清" });
            await ViewModel.InitializeAsync();
            await ViewModel.ReceiveInputAsync("https://space.bilibili.com/123", true);
        }
        public void Dispose() { Services.HttpClient.Dispose(); Services.UpdateHttpClient.Dispose(); directory.Delete(true); }
    }
    private sealed class SpaceStub : IBilibiliSpaceService
    {
        public Func<string, int, CancellationToken, Task<BilibiliUserSearchPage>> SearchHandler { get; set; } = (keyword, page, token) => Task.FromResult(new BilibiliUserSearchPage([User("456", keyword)], page, 20, 1, 1));
        public List<(string Keyword, int Page)> SearchRequests { get; } = [];
        public List<string> ProfileRequests { get; } = [];
        public Task<BilibiliUserSearchPage> SearchUsersAsync(string keyword, int page, CancellationToken cancellationToken = default)
        {
            SearchRequests.Add((keyword, page));
            return SearchHandler(keyword, page, cancellationToken);
        }
        public int FailPage { get; set; }
        public List<int> UploadPages { get; } = [];
        public Task<BilibiliSpaceProfile> GetProfileAsync(string uid, CancellationToken cancellationToken = default)
        {
            ProfileRequests.Add(uid);
            return Task.FromResult(new BilibiliSpaceProfile(uid, "UP:/name", "", ""));
        }
        public Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetUploadsAsync(string uid, int page, CancellationToken cancellationToken = default)
        {
            UploadPages.Add(page);
            if (page == FailPage) throw new IOException("test interrupted page");
            return Task.FromResult(new BilibiliSpacePage<BilibiliSpaceVideo>(Enumerable.Range(1, 31).Skip((page - 1) * 30).Take(30).Select(Video).ToList(), page, 30, 31, [new(1, "分类", 16)]));
        }
        public Task<BilibiliSpacePage<BilibiliSpaceCollection>> GetCollectionsAsync(string uid, int page, CancellationToken cancellationToken = default) => Task.FromResult(new BilibiliSpacePage<BilibiliSpaceCollection>([new("1", uid, BilibiliSpaceCollectionKind.Season, "合集", "", "", 2)], page, 20, 1));
        public Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetCollectionVideosAsync(BilibiliSpaceCollection collection, int page, CancellationToken cancellationToken = default) => Task.FromResult(new BilibiliSpacePage<BilibiliSpaceVideo>([Video(1), Video(2)], page, 30, 2));
    }
    private sealed class DownloadStub : IBBDownService
    {
        public List<DownloadParseRequest> Requests { get; } = [];
        public string BlockUrl { get; set; } = "";
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal(DownloadParseMode.All, request.Mode);
            if (request.Url == BlockUrl) { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var pages = Enumerable.Range(1, request.Url == Video(1).Url ? 2 : 1).Select(n => new PageInfo(n, n.ToString(), $"P{n}", "30s")).ToList();
            var episodes = pages.Where(p => request.Pages.Length == 0 || p.Number.ToString() == request.Pages).Select(p => new DownloadEpisodeInfo
            {
                Page = p, State = p.Number == 2 && request.Pages.Length == 0 ? DownloadEpisodeParseState.Failed : DownloadEpisodeParseState.Ready,
                Error = p.Number == 2 && request.Pages.Length == 0 ? "test failure" : "",
                VideoStreams = [new(0, "1080P 高清", "1920x1080", 1920, 1080, "AVC", "30", "1000 kbps", 1000, "4 MB")],
                AudioStreams = [new(1, "M4A", "128 kbps", 128, "1 MB")]
            }).ToList();
            return new() { SourceUrl = request.Url, ResolvedUrl = request.Url, Title = "test title", AllPages = pages, Episodes = episodes, Metadata = new() { Bvid = request.Url.Split('/')[^1] } };
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
