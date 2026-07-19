using System.Net;
using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadSelectionTests
{
    [Fact]
    public void InternalProgressAggregatesVideoAndAudioAndEstimatesEta()
    {
        var parser = new BBDownInternalProgressParser(100 * 1024 * 1024, 20 * 1024 * 1024, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频", out _);

        Assert.True(parser.TryConsume("[####################--------------------] 50% / - 10 MB/s", out var video));
        Assert.Equal(41.67, video.Percent, 2);
        Assert.Equal("10 MB/s", video.Speed);
        Assert.Equal("约 7秒", video.Eta);

        parser.TryConsume("合并视频分片", out _);
        parser.TryConsume("开始下载P1音频", out _);
        Assert.True(parser.TryConsume("[####################--------------------] 50% / - 5 MB/s", out var audio));
        Assert.Equal(91.67, audio.Percent, 2);
        Assert.Equal("5 MB/s", audio.Speed);
        Assert.Equal("约 2秒", audio.Eta);
    }

    [Fact]
    public void Default4KQualityExcludesHdrAndKeeps4096Width()
    {
        var episode = Episode(
            [
                new VideoStreamInfo(0, "杜比视界", "3840x1606", 3840, 1606, "HEVC", "24", "6000 kbps", 6000, "1 GB"),
                new VideoStreamInfo(1, "4K 超清", "4096x1716", 4096, 1716, "AVC", "24", "5600 kbps", 5600, "900 MB"),
                new VideoStreamInfo(2, "4K 超清", "4096x1716", 4096, 1716, "HEVC", "24", "4200 kbps", 4200, "680 MB")
            ],
            [new AudioStreamInfo(3, "M4A", "192 kbps", 192, "30 MB")]);

        var selected = StreamSelectionPolicy.Select(episode, new StreamSelectionRule("4K 超清", "HEVC", "auto", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        Assert.NotNull(selected.Video);
        Assert.Equal("4096x1716", selected.Video!.Resolution);
        Assert.Equal("HEVC", selected.Video.Codec);
    }

    [Fact]
    public void SpecialQualityCanBeSelectedExplicitly()
    {
        var episode = Episode(
            [
                new VideoStreamInfo(0, "HDR 真彩", "1920x1080", 1920, 1080, "HEVC", "24", "800 kbps", 800, "200 MB"),
                new VideoStreamInfo(1, "1080P 高码率", "1920x1080", 1920, 1080, "HEVC", "24", "700 kbps", 700, "180 MB")
            ],
            [new AudioStreamInfo(0, "M4A", "128 kbps", 128, "20 MB")]);

        var selected = StreamSelectionPolicy.Select(episode, new StreamSelectionRule("HDR 真彩", "HEVC", "auto", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        Assert.Equal("HDR 真彩", selected.Video!.Quality);
    }

    [Fact]
    public void MissingCodecAndAudioFormatFallBackWithReason()
    {
        var episode = Episode(
            [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "AVC", "25", "1000 kbps", 1000, "100 MB")],
            [new AudioStreamInfo(0, "M4A", "64 kbps", 64, "10 MB"), new AudioStreamInfo(1, "M4A", "192 kbps", 192, "30 MB")]);

        var selected = StreamSelectionPolicy.Select(episode, new StreamSelectionRule("4K 超清", "AV1", "E-AC-3", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        Assert.Equal("AVC", selected.Video!.Codec);
        Assert.Equal(192, selected.Audio!.BitrateKbps);
        Assert.Contains("无 4K 超清", selected.FallbackReason);
        Assert.Contains("无 AV1", selected.FallbackReason);
        Assert.Contains("无 E-AC-3", selected.FallbackReason);
    }

    [Fact]
    public void MissingManualStreamDoesNotSilentlyFallBack()
    {
        var episode = Episode(
            [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "AVC", "25", "1000 kbps", 1000, "100 MB")],
            [new AudioStreamInfo(0, "M4A", "128 kbps", 128, "20 MB")]);
        var desired = new EpisodeStreamSelection
        {
            PageNumber = 1,
            Video = new VideoStreamSelection("4K 超清", "3840x2160", "HEVC", 4000, true),
            Audio = new AudioStreamSelection("M4A", 128)
        };

        Assert.Throws<InvalidOperationException>(() => StreamSelectionPolicy.Resolve(episode, desired, new DownloadRequest { Url = "BV1" }));
    }

    [Fact]
    public void ManualVideoBitrateChangeDoesNotSilentlyMatchSameCodec()
    {
        var episode = Episode(
            [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "HEVC", "25", "1100 kbps", 1100, "100 MB")],
            [new AudioStreamInfo(1, "M4A", "192 kbps", 192, "20 MB")]);
        var desired = new EpisodeStreamSelection
        {
            PageNumber = 1,
            Video = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 1000, true),
            Audio = new AudioStreamSelection("M4A", 192)
        };

        Assert.Throws<InvalidOperationException>(() => StreamSelectionPolicy.Resolve(episode, desired, new DownloadRequest { Url = "BV1" }));
    }

    [Fact]
    public void ManualAudioBitrateChangeDoesNotSilentlyMatchSameCodec()
    {
        var episode = Episode(
            [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "HEVC", "25", "1000 kbps", 1000, "100 MB")],
            [new AudioStreamInfo(1, "M4A", "128 kbps", 128, "20 MB")]);
        var desired = new EpisodeStreamSelection
        {
            PageNumber = 1,
            Video = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 1000),
            Audio = new AudioStreamSelection("M4A", 192, true)
        };

        Assert.Throws<InvalidOperationException>(() => StreamSelectionPolicy.Resolve(episode, desired, new DownloadRequest { Url = "BV1" }));
    }

    [Fact]
    public void SelectedSizeUsesReadableBinaryUnits()
    {
        Assert.Equal("1.25 GB", MediaEstimateFormatter.FormatBytes(1342177280));
    }

    [Fact]
    public void ExactCommandOmitsPreferenceArguments()
    {
        var arguments = BBDownCommandBuilder.BuildExactDownloadArguments(new DownloadRequest
        {
            Url = "BV1", Pages = "3", Quality = "4K", Encoding = "HEVC", AudioBitratePriority = AudioBitratePriority.Lowest
        }, new ToolPaths());

        Assert.Contains("--interactive", arguments);
        Assert.DoesNotContain("-q", arguments);
        Assert.DoesNotContain("--encoding-priority", arguments);
        Assert.DoesNotContain("--audio-ascending", arguments);
    }

    [Fact]
    public void InfoCommandIncludesConfiguredFfmpegPath()
    {
        const string ffmpegPath = @"D:\Beflow\tools\ffmpeg\ffmpeg.exe";
        var arguments = BBDownCommandBuilder.BuildInfoArguments(new DownloadRequest
        {
            Url = "ss4232", Season = true, ApiMode = "TV", AudioBitratePriority = AudioBitratePriority.Lowest
        }, new ToolPaths { Ffmpeg = ffmpegPath });

        Assert.Equal("ss4232", arguments[0]);
        Assert.Contains("-info", arguments);
        Assert.Contains("--show-all", arguments);
        Assert.Contains("ALL", arguments);
        Assert.Contains("-tv", arguments);
        Assert.Contains("--audio-ascending", arguments);
        var ffmpegArgument = arguments.IndexOf("--ffmpeg-path");
        Assert.True(ffmpegArgument >= 0);
        Assert.Equal(ffmpegPath, arguments[ffmpegArgument + 1]);
    }

    [Fact]
    public void InfoCommandOmitsFfmpegArgumentWhenPathIsUnavailable()
    {
        var arguments = BBDownCommandBuilder.BuildInfoArguments(
            new DownloadRequest { Url = "BV1", Pages = "1", ApiMode = "APP" },
            new ToolPaths());

        Assert.Contains("-app", arguments);
        Assert.DoesNotContain("--ffmpeg-path", arguments);
    }

    [Fact]
    public void AriaProgressAcrossMultipleTransfersDoesNotReset()
    {
        var parser = new Aria2ProgressParser();
        Assert.True(parser.TryConsume("[#aaaa11 50MiB/100MiB(50%) CN:8 DL:10MiB ETA:5s]", out var first));
        Assert.True(parser.TryConsume("[#bbbb22 10MiB/100MiB(10%) CN:8 DL:8MiB ETA:11s]", out var second));
        Assert.Equal(50, first.Percent, 1);
        Assert.True(second.Percent >= 50);
    }

    [Fact]
    public async Task ServiceParsesAllPagesAndReportsIncrementalEpisodes()
    {
        using var fixture = new ServiceFixture(new ScriptedRunner());
        DownloadCatalog? catalog = null;
        var updates = new List<DownloadParseProgress>();
        var manager = new TaskManager(fixture.Paths, fixture.Runner);

        var snapshot = await manager.RunExclusiveAsync(TaskKind.DownloadParse, false, "parse", async (context, token) =>
        {
            catalog = await fixture.Service.ParseDownloadAsync(new DownloadParseRequest("ss1", DownloadParseMode.All), new SynchronousProgress<DownloadParseProgress>(updates.Add), context, token);
        });

        Assert.Equal(TaskState.Completed, snapshot.State);
        Assert.NotNull(catalog);
        Assert.Equal(2, catalog!.Episodes.Count);
        Assert.Equal(2, updates.Count(item => item.Episode is not null));
        Assert.Contains("ALL", fixture.Runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task CurrentParseOnlyReturnsTheUrlSelectedEpisode()
    {
        using var fixture = new ServiceFixture(new ScriptedRunner());
        DownloadCatalog? catalog = null;
        var manager = new TaskManager(fixture.Paths, fixture.Runner);

        await manager.RunExclusiveAsync(TaskKind.DownloadParse, false, "parse", async (context, token) =>
        {
            catalog = await fixture.Service.ParseDownloadAsync(new DownloadParseRequest("https://example.test/video?p=1", DownloadParseMode.Current), null, context, token);
        });

        Assert.Single(catalog!.Episodes);
        Assert.Equal(1, catalog.Episodes[0].Page.Number);
        Assert.DoesNotContain("-p", fixture.Runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task ParseFailurePersistsUsefulSanitizedDiagnostics()
    {
        var runner = new ScriptedRunner
        {
            ParseExitCode = 1,
            ParseFailureOutput = """
                BBDown version 1.6.3, Bilibili Downloader.
                SESSDATA=real-secret-value
                https://upos-test.bilivideo.com/video.m4s?access_token=media-secret
                [2026-07-18 21:00:00.000] - 请求被拒绝：账号无权访问此内容
                """
        };
        using var fixture = new ServiceFixture(runner);
        var manager = new TaskManager(fixture.Paths, runner);

        var snapshot = await manager.RunExclusiveAsync(TaskKind.DownloadParse, true, "download_parse", async (context, token) =>
        {
            await fixture.Service.ParseDownloadAsync(new DownloadParseRequest("ss1", DownloadParseMode.Current), null, context, token);
        });

        Assert.Equal(TaskState.Failed, snapshot.State);
        Assert.Contains("退出码 1", snapshot.Error);
        Assert.Contains("请求被拒绝", snapshot.Error);
        Assert.DoesNotContain("real-secret-value", snapshot.Error);
        Assert.DoesNotContain("SESSDATA", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bilivideo.com", snapshot.Error);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LogPath));
        var log = File.ReadAllText(snapshot.LogPath);
        Assert.Contains("请求被拒绝", log);
        Assert.DoesNotContain("real-secret-value", log);
        Assert.DoesNotContain("media-secret", log);
        Assert.DoesNotContain("bilivideo.com", log);
    }

    [Fact]
    public async Task BatchDownloadUsesExactIndicesAndContinuesAfterFailure()
    {
        var runner = new ScriptedRunner { FailDownloadPage = 2 };
        using var fixture = new ServiceFixture(runner);
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "output"));
        DownloadBatchResult? batch = null;
        var manager = new TaskManager(fixture.Paths, runner);
        var request = new DownloadBatchRequest
        {
            Title = "测试合集",
            Options = new DownloadRequest { Url = "ss1", WorkDirectory = output.FullName, UseAria2c = true },
            Episodes =
            [
                Selection(1), Selection(2)
            ]
        };

        var snapshot = await manager.RunExclusiveAsync(TaskKind.DownloadBatch, false, "batch", async (context, token) =>
        {
            batch = await fixture.Service.DownloadBatchAsync(request, null, context, token);
        });

        Assert.Equal(TaskState.Completed, snapshot.State);
        Assert.NotNull(batch);
        Assert.Equal(DownloadEpisodeResultState.Completed, batch!.Episodes[0].State);
        Assert.Equal(DownloadEpisodeResultState.Failed, batch.Episodes[1].State);
        var downloads = runner.Requests.Where(item => item.Arguments.Contains("--interactive")).ToList();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, item => Assert.Equal("1\n2\n", item.StandardInput));
        Assert.Contains(Path.Combine("测试合集", "[P01]第一集"), downloads[0].Arguments);
        Assert.Contains(Path.Combine("测试合集", "[P02]第二集"), downloads[1].Arguments);
        Assert.Equal(Path.Combine(output.FullName, "测试合集"), batch.OutputDirectory);
        Assert.Contains(batch.OutputFiles, path => path.EndsWith(Path.Combine("测试合集", "[P01]第一集.mp4"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetadataServiceReadsUgcAndPgcFieldsWithoutCredentials()
    {
        var handler = new MetadataHandler();
        var service = new BilibiliMetadataService(new HttpClient(handler));

        var ugc = await service.GetAsync("https://www.bilibili.com/video/av123");
        var pgc = await service.GetAsync("https://www.bilibili.com/bangumi/play/ss456");

        Assert.Equal("普通视频", ugc!.Title);
        Assert.StartsWith("https://", ugc.CoverUrl);
        Assert.Equal("123", ugc.Aid);
        Assert.Equal("456", ugc.OwnerId);
        Assert.NotNull(ugc.FindEpisode("11"));
        Assert.Equal("番剧", pgc!.Title);
        Assert.Equal("出品方", pgc.OwnerName);
        Assert.Equal("456", pgc.SeasonId);
        Assert.Equal("999", pgc.OwnerId);
        Assert.Equal("ep789", "ep" + pgc.FindEpisode("22")!.EpisodeId);
    }

    [Fact]
    public async Task BatchHistoryRoundTripsWithoutChangingLegacyShape()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            var store = new HistoryStore(paths);
            await store.AddAsync(new HistoryRecord
            {
                TaskType = TaskKind.DownloadBatch,
                Url = "ss1",
                Title = "测试合集",
                DownloadBatch = new DownloadBatchHistory
                {
                    Options = new DownloadRequest { Url = "ss1" },
                    Episodes = [new DownloadEpisodeResult { PageNumber = 1, PageTitle = "第一集", State = DownloadEpisodeResultState.Completed }]
                }
            });

            var restored = Assert.Single(await store.LoadAsync());
            Assert.NotNull(restored.DownloadBatch);
            Assert.Single(restored.DownloadBatch!.Episodes);
            Assert.Contains("1 集", restored.SpecificationTags);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ExactDownloadUsesSharedInteractiveSelectionPipeline()
    {
        using var fixture = new ServiceFixture(new ScriptedRunner());
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "exact"));
        var manager = new TaskManager(fixture.Paths, fixture.Runner);
        ExactDownloadResult? result = null;

        var snapshot = await manager.RunExclusiveAsync(TaskKind.DownloadBatch, false, "exact", async (context, token) =>
        {
            var episode = await fixture.Service.ParseEpisodeAsync("BV1", 1, "WEB", context, token);
            result = await fixture.Service.DownloadExactAsync(new ExactDownloadRequest
            {
                Options = new DownloadRequest { Url = "BV1", WorkDirectory = output.FullName, UseAria2c = true },
                Episode = episode,
                Selection = Selection(1),
                OutputDirectory = output.FullName,
                RelativeOutputPath = "[P01]精准下载"
            }, null, context, token);
        });

        Assert.Equal(TaskState.Completed, snapshot.State);
        Assert.NotNull(result);
        Assert.Single(result!.OutputFiles);
        Assert.Contains(fixture.Runner.Requests, request => request.Arguments.Contains("--interactive"));
    }

    private static DownloadEpisodeInfo Episode(List<VideoStreamInfo> video, List<AudioStreamInfo> audio) => new()
    {
        Page = new PageInfo(1, "1", "第一集", "24m"), VideoStreams = video, AudioStreams = audio, State = DownloadEpisodeParseState.Ready
    };

    private static EpisodeStreamSelection Selection(int page) => new()
    {
        PageNumber = page,
        PageTitle = $"第{page}集",
        Video = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 1000),
        Audio = new AudioStreamSelection("M4A", 192)
    };

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ServiceFixture : IDisposable
    {
        public ServiceFixture(ScriptedRunner runner)
        {
            Root = Directory.CreateTempSubdirectory();
            var app = Directory.CreateDirectory(Path.Combine(Root.FullName, "app"));
            Paths = new ApplicationPaths(app.FullName, Path.Combine(Root.FullName, "local"));
            Paths.EnsureCreated();
            var source = Path.Combine(app.FullName, "BBDown.exe");
            File.WriteAllText(source, "binary");
            Runner = runner;
            Service = new BBDownService(Paths, runner, new FixedToolLocator(source), new FixedSettingsStore());
        }
        public DirectoryInfo Root { get; }
        public ApplicationPaths Paths { get; }
        public ScriptedRunner Runner { get; }
        public BBDownService Service { get; }
        public void Dispose() => Root.Delete(true);
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];
        public int FailDownloadPage { get; set; }
        public int ParseExitCode { get; set; }
        public string ParseFailureOutput { get; set; } = string.Empty;

        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var page = ReadPage(request.Arguments);
            if (request.Arguments.Contains("--interactive"))
            {
                if (page != FailDownloadPage)
                {
                    var workIndex = request.Arguments.ToList().IndexOf("--work-dir");
                    var patternIndex = request.Arguments.ToList().IndexOf("-M");
                    var directory = request.Arguments[workIndex + 1];
                    var relative = patternIndex >= 0 ? request.Arguments[patternIndex + 1] : $"[P{page:00}]第{page}集";
                    var target = Path.Combine(directory, relative + ".mp4");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "video");
                    onOutput?.Invoke("[#aaaa11 100MiB/100MiB(100%) CN:8 DL:10MiB ETA:0s]\n");
                }
                return Task.FromResult(new ProcessResult(page == FailDownloadPage ? 1 : 0, string.Empty, false));
            }

            if (ParseExitCode != 0)
            {
                foreach (var line in ParseFailureOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onOutput?.Invoke(line + "\n");
                return Task.FromResult(new ProcessResult(ParseExitCode, ParseFailureOutput, false));
            }

            var output = request.Arguments.Contains("ALL") ? InfoOutput(1) + InfoOutput(2, includeHeader: false) + "任务完成\n" : InfoOutput(page) + "任务完成\n";
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onOutput?.Invoke(line + "\n");
            return Task.FromResult(new ProcessResult(0, output, false));
        }

        public Task TerminateAllAsync() => Task.CompletedTask;

        private static int ReadPage(IReadOnlyList<string> arguments)
        {
            var index = arguments.ToList().IndexOf("-p");
            return index >= 0 && int.TryParse(arguments[index + 1], out var page) ? page : 1;
        }

        private static string InfoOutput(int page, bool includeHeader = true) => $"""
            {(includeHeader ? "视频标题: 测试合集\n共计 2 个分P, 已选择：ALL\nP1: [11] [第一集] [24m]\nP2: [22] [第二集] [24m]\n" : string.Empty)}开始解析P{page}: 123... ({page} of 2)
            共计1条视频流.
              1. [1080P 高清] [1920x1080] [HEVC] [25] [1000 kbps] [~100 MB]
            共计1条音频流.
              2. [M4A] [192 kbps] [~20 MB]
            """;
    }

    private sealed class FixedToolLocator(string bbDown) : IToolLocator
    {
        public ToolPaths Locate(AppSettings settings) => new() { BBDown = bbDown };
        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult("test");
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => Task.FromResult(update(new AppSettings()));
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.Contains("pgc/view", StringComparison.Ordinal)
                ? """{"code":0,"result":{"title":"番剧","cover":"https://cover/pgc.jpg","season_id":456,"publish":{"pub_time":"2026-01-01"},"up_info":{"uname":"出品方","mid":999,"avatar":"https://face/pgc.jpg"},"episodes":[{"cid":22,"aid":123,"bvid":"BV1EP","id":789,"pub_time":1767225600}]}}"""
                : """{"code":0,"data":{"title":"普通视频","pic":"http://cover/ugc.jpg","aid":123,"bvid":"BV1TEST","pubdate":1767225600,"owner":{"name":"UP主","mid":456,"face":"https://face/ugc.jpg"},"pages":[{"cid":11,"page":1,"part":"第一集"}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
