using BBDownForWindows.Core;
using System.Text.Json;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DualAudioTests
{
    [Fact]
    public void RecommendationUsesRealResolutionBeforeHdr()
    {
        var sourceA = new VideoStreamSelection("4K 超清", "3840x2160", "AVC", 5000);
        var sourceB = new VideoStreamSelection("HDR 真彩", "1920x1080", "HEVC", 9000);

        var recommendation = DualAudioRecommendationPolicy.Recommend(sourceA, sourceB, "HEVC");

        Assert.Equal(DualAudioSource.A, recommendation.Source);
        Assert.Contains("真实分辨率", recommendation.Reason);
    }

    [Fact]
    public void RecommendationUsesDynamicRangeThenBitrateAndStableTie()
    {
        var sdr = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 5000);
        var hdr = new VideoStreamSelection("HDR 真彩", "1920x1080", "AVC", 3000);
        Assert.Equal(DualAudioSource.B, DualAudioRecommendationPolicy.Recommend(sdr, hdr, "HEVC").Source);

        var high = new VideoStreamSelection("HDR 真彩", "1920x1080", "AVC", 6000);
        Assert.Equal(DualAudioSource.B, DualAudioRecommendationPolicy.Recommend(hdr, high, "HEVC").Source);
        Assert.Equal(DualAudioSource.A, DualAudioRecommendationPolicy.Recommend(high, high, "HEVC").Source);
    }

    [Fact]
    public void ParsedCatalogsPairByOrderAndLeaveExtraEpisodeUnselected()
    {
        var sourceA = Catalog(Episode(1, "A1"), Episode(2, "A2"), Episode(3, "A3"));
        var sourceB = Catalog(Episode(7, "B1"), Episode(9, "B2"));

        var pairs = DualAudioService.BuildPairs(sourceA, sourceB);

        Assert.Equal(3, pairs.Count);
        Assert.Equal(7, pairs[0].SourceB!.Page.Number);
        Assert.True(pairs[1].IsSelected);
        Assert.Null(pairs[2].SourceB);
        Assert.False(pairs[2].IsSelected);
    }

    [Fact]
    public void RestoredPairingKeepsEverySourceBEpisodeUnique()
    {
        var sourceA = Catalog(Episode(1, "A1"), Episode(2, "A2"), Episode(3, "A3"));
        var sourceB = Catalog(Episode(7, "B1"), Episode(8, "B2"), Episode(9, "B3"));
        var restored = new[]
        {
            new DualAudioPairSelection { SourceAPageNumber = 2, SourceBPageNumber = 7 }
        };

        var pairs = DualAudioService.BuildRestoredPairs(sourceA, sourceB, restored);

        Assert.Equal(7, pairs.Single(item => item.SourceA!.Page.Number == 2).SourceB!.Page.Number);
        var assigned = pairs.Where(item => item.SourceB is not null).Select(item => item.SourceB!.Page.Number).ToList();
        Assert.Equal(assigned.Count, assigned.Distinct().Count());
        Assert.Equal([7, 8, 9], assigned.Order().ToArray());
    }

    [Fact]
    public void MkvmergeArgumentsSetLabelsLanguagesDefaultAndDelay()
    {
        var request = new DualAudioRequest
        {
            PrimaryLabel = "国语", SecondaryLabel = "粤语", PrimaryLanguage = "zh", SecondaryLanguage = "yue",
            SecondaryIsDefault = true, SecondaryAudioDelayMs = 350
        };
        var arguments = DualAudioService.BuildMkvmergeArguments("main.mp4", "audio.m4a", "out.mkv", request);
        Assert.Contains("1:国语", arguments);
        Assert.Contains("0:粤语", arguments);
        Assert.Contains("0:350", arguments);
        Assert.Contains("0:0,1:0,0:1", arguments);
    }

    [Fact]
    public void NewMkvmergeArgumentsKeepAudioABOrderWhenSourceAIsVideo()
    {
        var request = BatchRequest();
        request.DefaultAudioSource = DualAudioSource.B;

        var arguments = DualAudioService.BuildMkvmergeArguments("a-full.mp4", "b-audio.m4a", "out.mkv", request, DualAudioSource.A, 350);

        Assert.Contains("0:0,0:1,1:0", arguments);
        Assert.Contains("1:国语", arguments);
        Assert.Contains("0:粤语", arguments);
        Assert.Contains("0:350", arguments);
    }

    [Fact]
    public void NewMkvmergeArgumentsInvertDelayWhenSourceBIsVideo()
    {
        var request = BatchRequest();

        var arguments = DualAudioService.BuildMkvmergeArguments("b-full.mp4", "a-audio.m4a", "out.mkv", request, DualAudioSource.B, 350);

        Assert.Contains("0:0,1:0,0:1", arguments);
        Assert.Contains("1:粤语", arguments);
        Assert.Contains("0:国语", arguments);
        Assert.Contains("0:-350", arguments);
    }

    [Fact]
    public void SeparateModePairsByEpisodePrefix()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var primary = Directory.CreateDirectory(Path.Combine(root.FullName, "主版本"));
            var secondary = Directory.CreateDirectory(Path.Combine(root.FullName, "副音轨"));
            File.WriteAllText(Path.Combine(primary.FullName, "[P01]第一集.mp4"), "");
            File.WriteAllText(Path.Combine(secondary.FullName, "[P01]粤语.m4a"), "");
            Assert.Single(DualAudioService.FindPairs(primary.FullName, secondary.FullName, DualAudioSourceMode.Separate));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void SeparateModeRejectsMissingEpisodeInsteadOfProducingPartialOutput()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var primary = Directory.CreateDirectory(Path.Combine(root.FullName, "主版本"));
            var secondary = Directory.CreateDirectory(Path.Combine(root.FullName, "副音轨"));
            File.WriteAllText(Path.Combine(primary.FullName, "[P01]第一集.mp4"), "");
            File.WriteAllText(Path.Combine(primary.FullName, "[P02]第二集.mp4"), "");
            File.WriteAllText(Path.Combine(secondary.FullName, "[P1]粤语.m4a"), "");

            var error = Assert.Throws<InvalidOperationException>(() => DualAudioService.FindPairs(primary.FullName, secondary.FullName, DualAudioSourceMode.Separate));

            Assert.Contains("P2", error.Message, StringComparison.Ordinal);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void InterleavedModeRejectsUnequalFileCounts()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var primary = Directory.CreateDirectory(Path.Combine(root.FullName, "主版本"));
            var secondary = Directory.CreateDirectory(Path.Combine(root.FullName, "副音轨"));
            File.WriteAllText(Path.Combine(primary.FullName, "[P01]第一集.mp4"), "");
            File.WriteAllText(Path.Combine(primary.FullName, "[P02]第二集.mp4"), "");
            File.WriteAllText(Path.Combine(secondary.FullName, "[P01]粤语.m4a"), "");

            Assert.Throws<InvalidOperationException>(() => DualAudioService.FindPairs(primary.FullName, secondary.FullName, DualAudioSourceMode.Interleaved));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void RejectsOutOfRangeDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAudioService.BuildMkvmergeArguments("a", "b", "c", new DualAudioRequest { SecondaryAudioDelayMs = 10001 }));
    }

    [Fact]
    public void DownloadRequestsUseIndependentAudioFormats()
    {
        var request = new DualAudioRequest
        {
            SourceMode = DualAudioSourceMode.Separate,
            PrimaryUrl = "primary",
            SecondaryUrl = "secondary",
            PrimaryAudioCodec = "FLAC",
            SecondaryAudioCodec = "E-AC-3"
        };

        var (primary, secondary) = DualAudioService.BuildDownloadRequests(request, "1,2", "1,2", "primary-dir", "secondary-dir");

        Assert.Equal("FLAC", primary.AudioCodec);
        Assert.Equal(DownloadMode.VideoAndAudio, primary.DownloadMode);
        Assert.Equal("primary", primary.Url);
        Assert.Equal("E-AC-3", secondary.AudioCodec);
        Assert.Equal(DownloadMode.AudioOnly, secondary.DownloadMode);
        Assert.Equal("secondary", secondary.Url);
        Assert.False(primary.OrganizeInTitleDirectory);
        Assert.False(secondary.OrganizeInTitleDirectory);
    }

    [Fact]
    public async Task DualAudioHistoryRoundTripsFullPairResults()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var store = new HistoryStore(new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local")));
            await store.AddAsync(new HistoryRecord
            {
                TaskType = TaskKind.DualAudioMux,
                Url = "source-a",
                SecondaryUrl = "source-b",
                DualAudioBatch = new DualAudioBatchHistory
                {
                    Request = BatchRequest(),
                    Pairs = [new DualAudioPairResult { PairNumber = 1, State = DualAudioPairState.Completed, MainVideoSource = DualAudioSource.B }]
                }
            });

            var restored = Assert.Single(await store.LoadAsync());
            Assert.NotNull(restored.DualAudioBatch);
            Assert.Equal(DualAudioSource.B, Assert.Single(restored.DualAudioBatch!.Pairs).MainVideoSource);
            Assert.Contains("1 对", restored.SpecificationTags);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SeparateSourcesParseConcurrentlyAndKeepLogPrefixes()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            var runner = new NoopRunner();
            var bbdown = new ParallelParseService();
            var service = new DualAudioService(paths, bbdown, runner, new StubSettingsStore(), new StubToolLocator());
            var manager = new TaskManager(paths, runner);
            var logs = new List<string>();
            manager.LogAppended += (_, text) => { lock (logs) logs.Add(text); };
            DualAudioCatalog? catalog = null;

            var snapshot = await manager.RunExclusiveAsync(TaskKind.DualAudioParse, false, "dual-parse", async (context, token) =>
            {
                catalog = await service.ParseAsync(new DualAudioParseRequest(DualAudioSourceMode.Separate, "source-a", "source-b", DownloadParseMode.All), null, null, context, token);
            });

            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.Equal(2, bbdown.MaximumConcurrency);
            Assert.Single(catalog!.Pairs);
            Assert.Contains(logs, value => value.Contains("[来源 A]", StringComparison.Ordinal));
            Assert.Contains(logs, value => value.Contains("[来源 B]", StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RetryCanParseOnlyTheFailedSource()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            var runner = new NoopRunner();
            var bbdown = new ParallelParseService();
            var service = new DualAudioService(paths, bbdown, runner, new StubSettingsStore(), new StubToolLocator());
            var manager = new TaskManager(paths, runner);
            DualAudioCatalog? catalog = null;

            var snapshot = await manager.RunExclusiveAsync(TaskKind.DualAudioParse, false, "dual-retry", async (context, token) =>
            {
                catalog = await service.ParseAsync(new DualAudioParseRequest(
                    DualAudioSourceMode.Separate,
                    "source-a",
                    "source-b",
                    DownloadParseMode.Current,
                    OnlySource: DualAudioSource.B), null, null, context, token);
            });

            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.Null(catalog!.SourceA);
            Assert.NotNull(catalog.SourceB);
            Assert.Equal(DownloadParseMode.Current, catalog.ParseMode);
            Assert.Equal(["source-b"], bbdown.RequestedUrls);
            Assert.Equal([DownloadParseMode.Current], bbdown.RequestedModes);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task CancelledSourceDoesNotDiscardTheOtherParsedCatalog()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            var runner = new NoopRunner();
            var service = new DualAudioService(paths, new OneCancelledParseService(), runner, new StubSettingsStore(), new StubToolLocator());
            var manager = new TaskManager(paths, runner);
            DualAudioCatalog? catalog = null;

            await manager.RunExclusiveAsync(TaskKind.DualAudioParse, false, "dual-cancelled-source", async (context, token) =>
            {
                catalog = await service.ParseAsync(new DualAudioParseRequest(DualAudioSourceMode.Separate, "source-a", "source-b", DownloadParseMode.All), null, null, context, token);
            });

            Assert.NotNull(catalog!.SourceA);
            Assert.Null(catalog.SourceB);
            Assert.Equal("解析已取消", catalog.SourceBError);
            Assert.Single(catalog.Pairs);
            Assert.False(catalog.Pairs[0].IsSelected);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task BatchMuxContinuesThroughExactPipelineWritesManifestAndSafelyDeletesSources()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            var output = Directory.CreateDirectory(Path.Combine(root.FullName, "output"));
            var mkvmerge = Path.Combine(app.FullName, "mkvmerge.exe");
            File.WriteAllText(mkvmerge, "tool");
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            var runner = new MuxRunner();
            var service = new DualAudioService(paths, new BatchBBDownService(), runner, new StubSettingsStore(), new StubToolLocator(mkvmerge));
            var manager = new TaskManager(paths, runner);
            var request = BatchRequest();
            request.SourceATitle = "测试双音轨";
            request.WorkDirectory = output.FullName;
            request.MkvmergePath = mkvmerge;
            request.KeepSourceFiles = false;
            request.Pairs = [PairSelection()];
            DualAudioBatchResult? result = null;
            var progress = new List<DualAudioProgressSnapshot>();

            var snapshot = await manager.RunExclusiveAsync(TaskKind.DualAudioMux, true, "dual", async (context, token) =>
            {
                result = await service.DownloadAndMuxAsync(request, new SynchronousProgress<DualAudioProgressSnapshot>(progress.Add), context, token);
            });

            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Succeeded);
            Assert.True(File.Exists(Assert.Single(result.OutputFiles)));
            Assert.True(File.Exists(result.ManifestPath));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(result.TaskDirectory, "来源A")));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(result.TaskDirectory, "来源B")));
            var manifest = await File.ReadAllTextAsync(result.ManifestPath);
            Assert.DoesNotContain("SESSDATA", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("access_token", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(progress);
            Assert.True(progress.Zip(progress.Skip(1), (left, right) => right.OverallPercent >= left.OverallPercent).All(value => value));
            Assert.Equal(100, progress[^1].OverallPercent);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ManifestRemuxPreservesAudioMetadataAndPerPairDelaysByDefault()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            var task = Directory.CreateDirectory(Path.Combine(root.FullName, "task"));
            var mkvmerge = Path.Combine(app.FullName, "mkvmerge.exe");
            File.WriteAllText(mkvmerge, "tool");
            var a1 = Path.Combine(task.FullName, "a1.mp4");
            var a2 = Path.Combine(task.FullName, "a2.mp4");
            var b1 = Path.Combine(task.FullName, "b1.m4a");
            var b2 = Path.Combine(task.FullName, "b2.m4a");
            foreach (var file in new[] { a1, a2, b1, b2 }) File.WriteAllText(file, "media");
            var request = BatchRequest();
            request.SourceALabel = "原始 A";
            request.SourceBLabel = "原始 B";
            request.SourceALanguage = "zh";
            request.SourceBLanguage = "yue";
            request.DefaultAudioSource = DualAudioSource.B;
            request.SourceBDelayMs = 50;
            request.KeepSourceFiles = true;
            var result = new DualAudioBatchResult
            {
                Pairs =
                [
                    new DualAudioPairResult { PairNumber = 1, SourceAPageTitle = "一", State = DualAudioPairState.Completed, MainVideoSource = DualAudioSource.A, SourceBDelayMs = 120, SourceAFiles = [a1], SourceBFiles = [b1] },
                    new DualAudioPairResult { PairNumber = 2, SourceAPageTitle = "二", State = DualAudioPairState.Completed, MainVideoSource = DualAudioSource.A, SourceBDelayMs = -80, SourceAFiles = [a2], SourceBFiles = [b2] }
                ]
            };
            var manifest = new DualAudioTaskManifest { Request = request, Result = result };
            await File.WriteAllTextAsync(Path.Combine(task.FullName, "dual-audio-task.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            var runner = new MuxRunner();
            var service = new DualAudioService(paths, new BatchBBDownService(), runner, new StubSettingsStore(), new StubToolLocator(mkvmerge));

            var preparation = await service.InspectExistingAsync(task.FullName);
            Assert.True(preparation.CanRemux);
            Assert.True(preparation.HasPerPairDelays);
            var manager = new TaskManager(paths, runner);
            var snapshot = await manager.RunExclusiveAsync(TaskKind.DualAudioRemux, true, "remux", (context, token) =>
                service.RemuxExistingAsync(new DualAudioRequest
                {
                    ExistingTaskDirectory = task.FullName,
                    MkvmergePath = mkvmerge
                }, context, token));

            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.Equal(2, runner.Requests.Count);
            Assert.All(runner.Requests, item =>
            {
                Assert.Contains("1:原始 A", item.Arguments);
                Assert.Contains("0:原始 B", item.Arguments);
            });
            Assert.Contains(runner.Requests[0].Arguments, item => item == "0:120");
            Assert.Contains(runner.Requests[1].Arguments, item => item == "0:-80");
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ManifestWithoutRetainedSourceFilesIsNotOfferedForRemux()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var task = Directory.CreateDirectory(Path.Combine(root.FullName, "task"));
            await File.WriteAllTextAsync(Path.Combine(task.FullName, "dual-audio-task.json"),
                JsonSerializer.Serialize(new DualAudioTaskManifest
                {
                    Request = new DualAudioBatchRequest { KeepSourceFiles = false },
                    Result = new DualAudioBatchResult { Pairs = [new DualAudioPairResult { State = DualAudioPairState.Completed }] }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var service = new DualAudioService(new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local")),
                new BatchBBDownService(), new NoopRunner(), new StubSettingsStore(), new StubToolLocator());

            var preparation = await service.InspectExistingAsync(task.FullName);

            Assert.False(preparation.CanRemux);
            Assert.Contains("未保留来源文件", preparation.Error);
        }
        finally { root.Delete(true); }
    }

    private static DualAudioBatchRequest BatchRequest() => new()
    {
        SourceAUrl = "source-a",
        SourceBUrl = "source-b",
        SourceALabel = "国语",
        SourceBLabel = "粤语",
        SourceALanguage = "zh",
        SourceBLanguage = "yue",
        Options = new DownloadRequest { Url = "source-a", Encoding = "HEVC" }
    };

    private static DualAudioPairSelection PairSelection() => new()
    {
        PairNumber = 1,
        SourceAPageNumber = 1,
        SourceAPageTitle = "第一集",
        SourceBPageNumber = 1,
        SourceBPageTitle = "第一集",
        SourceA = Selection("第一集"),
        SourceB = Selection("第一集"),
        IsSelected = true
    };

    private static EpisodeStreamSelection Selection(string title) => new()
    {
        PageNumber = 1,
        PageTitle = title,
        Video = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 1000),
        Audio = new AudioStreamSelection("M4A", 192)
    };

    private static DownloadCatalog Catalog(params DownloadEpisodeInfo[] episodes) => new() { Episodes = episodes.ToList() };
    private static DownloadEpisodeInfo Episode(int page, string title) => new()
    {
        Page = new PageInfo(page, page.ToString(), title, "24m"),
        State = DownloadEpisodeParseState.Ready
    };

    private sealed class ParallelParseService : IBBDownService
    {
        private int _active;
        public int MaximumConcurrency { get; private set; }
        public List<string> RequestedUrls { get; } = [];
        public List<DownloadParseMode> RequestedModes { get; } = [];

        public async Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            lock (RequestedUrls) RequestedUrls.Add(request.Url);
            lock (RequestedModes) RequestedModes.Add(request.Mode);
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            try
            {
                context.AppendLog($"{request.Url} parse\n");
                await Task.Delay(50, cancellationToken);
                var episode = Episode(1, request.Url);
                progress?.Report(new DownloadParseProgress(1, 1, 1, request.Url, episode, "done"));
                return new DownloadCatalog { SourceUrl = request.Url, Title = request.Url, Episodes = [episode] };
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadBatchResult> DownloadBatchAsync(DownloadBatchRequest request, IProgress<DownloadProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadEpisodeInfo> ParseEpisodeAsync(string url, int page, string apiMode, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExactDownloadResult> DownloadExactAsync(ExactDownloadRequest request, IProgress<ExactDownloadProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetTitleAsync(string url, CancellationToken cancellationToken) => Task.FromResult(url);
    }

    private sealed class OneCancelledParseService : IBBDownService
    {
        public Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            if (request.Url == "source-b") throw new OperationCanceledException(cancellationToken);
            var episode = Episode(1, request.Url);
            return Task.FromResult(new DownloadCatalog { SourceUrl = request.Url, Title = request.Url, Episodes = [episode] });
        }

        public Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadBatchResult> DownloadBatchAsync(DownloadBatchRequest request, IProgress<DownloadProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadEpisodeInfo> ParseEpisodeAsync(string url, int page, string apiMode, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExactDownloadResult> DownloadExactAsync(ExactDownloadRequest request, IProgress<ExactDownloadProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetTitleAsync(string url, CancellationToken cancellationToken) => Task.FromResult(url);
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TerminateAllAsync() => Task.CompletedTask;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => Task.FromResult(update(new AppSettings()));
    }

    private sealed class StubToolLocator(string mkvmerge = "") : IToolLocator
    {
        public ToolPaths Locate(AppSettings settings) => new() { Mkvmerge = mkvmerge };
        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private sealed class BatchBBDownService : IBBDownService
    {
        public Task<DownloadEpisodeInfo> ParseEpisodeAsync(string url, int page, string apiMode, TaskExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadEpisodeInfo
            {
                Page = new PageInfo(page, page.ToString(), "第一集", "24m"),
                State = DownloadEpisodeParseState.Ready,
                VideoStreams = [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "HEVC", "24", "1000 kbps", 1000, "100 MB")],
                AudioStreams = [new AudioStreamInfo(1, "M4A", "192 kbps", 192, "20 MB")]
            });

        public Task<ExactDownloadResult> DownloadExactAsync(ExactDownloadRequest request, IProgress<ExactDownloadProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            var extension = request.Options.DownloadMode == DownloadMode.AudioOnly ? ".m4a" : ".mp4";
            var file = Path.Combine(request.OutputDirectory, request.RelativeOutputPath + extension);
            Directory.CreateDirectory(request.OutputDirectory);
            File.WriteAllText(file, "media");
            progress?.Report(new ExactDownloadProgress(DownloadProgressPhase.Completed, 100, "10 MB/s", "0秒", "done"));
            return Task.FromResult(new ExactDownloadResult
            {
                PageNumber = request.Selection.PageNumber,
                Video = request.Selection.Video,
                Audio = request.Selection.Audio,
                OutputDirectory = request.OutputDirectory,
                RelativeOutputPath = request.RelativeOutputPath,
                OutputFiles = [file]
            });
        }

        public Task<string> GetTitleAsync(string url, CancellationToken cancellationToken) => Task.FromResult("测试双音轨");
        public Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadCatalog> ParseDownloadAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadBatchResult> DownloadBatchAsync(DownloadBatchRequest request, IProgress<DownloadProgressSnapshot>? progress, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MuxRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var outputIndex = request.Arguments.ToList().IndexOf("-o");
            if (outputIndex >= 0) File.WriteAllText(request.Arguments[outputIndex + 1], "mkv");
            return Task.FromResult(new ProcessResult(0, string.Empty, false));
        }

        public Task TerminateAllAsync() => Task.CompletedTask;
    }
}
