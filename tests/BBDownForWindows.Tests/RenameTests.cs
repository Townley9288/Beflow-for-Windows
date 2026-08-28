using BBDownForWindows.Core;
using System.Text.Json;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class RenameTests
{
    [Fact]
    public void TmdbResultHidesDuplicateOriginalTitleButKeepsDifferentTitle()
    {
        var duplicate = new TmdbSearchResult(1, RenameMediaType.Series, "记忆管理局", "记忆管理局", "2025", "", "");
        var different = new TmdbSearchResult(2, RenameMediaType.Movie, "沙丘", "Dune", "2021", "", "");

        Assert.Equal(string.Empty, duplicate.SecondaryTitle);
        Assert.Equal("Dune", different.SecondaryTitle);
        Assert.Equal("2025 · 剧集 · TMDB 1", duplicate.MetadataText);
        Assert.Equal("2021 · 电影 · TMDB 2", different.MetadataText);
    }

    [Fact]
    public void TemplateValidationRejectsUnknownFieldsBeforePreview()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RenameService.ValidateTemplatePattern("{中文名}.{未知字段}{扩展名}"));
        Assert.Contains("未知字段", error.Message);
    }

    [Theory]
    [InlineData("Show.S02E03.2160p.mkv", 3)]
    [InlineData("第12话.mp4", 12)]
    [InlineData("[P07]标题.mp4", 7)]
    [InlineData("03.1080p.WEB-DL.mkv", 3)]
    [InlineData("1080p.WEB-DL.mkv", null)]
    public void ExtractsEpisodesWithoutTreatingResolutionAsEpisode(string name, int? expected) =>
        Assert.Equal(expected, RenameService.ExtractEpisodeNumber(name));

    [Fact]
    public void ExtractsChineseFolderTitleAndSanitizesWindowsDirectoryName()
    {
        Assert.Equal("流人", RenameService.ExtractTitleFromFolder("流人 第四季 (2024)"));
        Assert.Equal("测试_标题", RenameService.SanitizeTitleDirectoryName("测试:标题."));
        Assert.Equal("_CON", RenameService.SanitizeTitleDirectoryName("CON"));
    }

    [Fact]
    public void MediaParserKeeps1440pAndSelectsBestAtmosTrack()
    {
        const string json = """
        {"streams":[
          {"codec_type":"video","codec_name":"hevc","width":2560,"height":1440,"color_transfer":"smpte2084","color_primaries":"bt2020","r_frame_rate":"60000/1000","side_data_list":[]},
          {"codec_type":"audio","codec_name":"aac","channels":2,"profile":"LC"},
          {"codec_type":"audio","codec_name":"eac3","channels":6,"profile":"Dolby Digital Plus + Dolby Atmos"}
        ]}
        """;
        var media = RenameService.ParseMediaMetadata(json);
        Assert.Equal("1440p", media.Resolution);
        Assert.Equal("HDR", media.DynamicRange);
        Assert.Equal("HEVC", media.VideoCodec);
        Assert.Equal("DDP.5.1.Atmos", media.Audio);
        Assert.Equal("60fps", media.FrameRate);
    }

    [Fact]
    public async Task RenameSettingsCreateBuiltInsAndRoundTripSecretLocally()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var store = new RenameSettingsStore(paths);
            var settings = await store.LoadAsync();
            Assert.Contains(settings.Templates, item => item.Id == RenameTemplate.BuiltInSeriesId && item.BuiltIn);
            Assert.Contains(settings.Templates, item => item.Id == RenameTemplate.BuiltInMovieId && item.BuiltIn);
            settings.TmdbApiKey = "local-secret";
            settings.Templates.Add(new RenameTemplate { Name = "自定义", MediaType = RenameMediaType.Series, Pattern = "{中文名}{扩展名}" });
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.Equal("local-secret", loaded.TmdbApiKey);
            Assert.Contains(loaded.Templates, item => item.Name == "自定义");
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task LegacyRenameSettingsGainDisabledReleaseGroupDefaultsWithoutLosingExistingValues()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            paths.EnsureCreated();
            var custom = new RenameTemplate { Name = "旧模板", MediaType = RenameMediaType.Series, Pattern = "{中文名}{扩展名}" };
            await File.WriteAllTextAsync(paths.RenameSettingsFile, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tmdbApiKey = "legacy-key",
                proxyUrl = "http://127.0.0.1:7890",
                activeSeriesTemplateId = custom.Id,
                templates = new[] { custom }
            }, SettingsStore.CreateOptions()));

            var loaded = await new RenameSettingsStore(paths).LoadAsync();

            Assert.Equal(2, loaded.SchemaVersion);
            Assert.False(loaded.DefaultReleaseGroupEnabled);
            Assert.Equal(string.Empty, loaded.ReleaseGroup);
            Assert.Equal("-", loaded.ReleaseGroupSeparator);
            Assert.Equal("legacy-key", loaded.TmdbApiKey);
            Assert.Equal("http://127.0.0.1:7890", loaded.ProxyUrl);
            Assert.Equal(custom.Id, loaded.ActiveSeriesTemplateId);
            Assert.Contains(loaded.Templates, item => item.Id == custom.Id && item.Pattern == custom.Pattern);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("-", "-WF")]
    [InlineData(".", ".WF")]
    [InlineData("_", "_WF")]
    [InlineData(" ", " WF")]
    [InlineData("", "WF")]
    [InlineData("++", "++WF")]
    public void ReleaseGroupCompositionPreservesConfiguredSeparator(string separator, string expected) =>
        Assert.Equal(expected, RenameReleaseGroup.Compose("  WF  ", separator));

    [Theory]
    [InlineData("", "-", true, "请填写发布组名称")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567", "-", false, "不能超过 32")]
    [InlineData("WF", "12345", false, "不能超过 4")]
    [InlineData("W:F", "-", false, "不允许使用的字符")]
    [InlineData("WF", "/", false, "不允许使用的字符")]
    public void ReleaseGroupValidationRejectsInvalidSettings(string group, string separator, bool requireName, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RenameReleaseGroup.Validate(group, separator, requireName));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void RenameSettingsKeepIndependentActiveTemplatesAndFallbackOnlyDeletedType()
    {
        var series = new RenameTemplate { Name = "剧集自定义", MediaType = RenameMediaType.Series, Pattern = "{中文名}.{季}{集}{扩展名}" };
        var movie = new RenameTemplate { Name = "电影自定义", MediaType = RenameMediaType.Movie, Pattern = "{中文名}.{年份}{扩展名}" };
        var settings = new RenameSettings
        {
            ActiveSeriesTemplateId = series.Id,
            ActiveMovieTemplateId = movie.Id,
            Templates = [series, movie]
        };

        settings.EnsureDefaults();
        Assert.Equal(series.Id, settings.ActiveSeriesTemplateId);
        Assert.Equal(movie.Id, settings.ActiveMovieTemplateId);

        settings.Templates.RemoveAll(template => template.Id == series.Id);
        settings.EnsureDefaults();
        Assert.Equal(RenameTemplate.BuiltInSeriesId, settings.ActiveSeriesTemplateId);
        Assert.Equal(movie.Id, settings.ActiveMovieTemplateId);
    }

    [Fact]
    public void RenameSettingsRestoreBuiltInDefinitionsWithoutChangingCustomTemplates()
    {
        var custom = new RenameTemplate { Name = "保留模板", MediaType = RenameMediaType.Series, Pattern = "{中文名}{扩展名}" };
        var settings = new RenameSettings
        {
            Templates =
            [
                new RenameTemplate
                {
                    Id = RenameTemplate.BuiltInSeriesId,
                    Name = "被修改的内置模板",
                    MediaType = RenameMediaType.Movie,
                    Pattern = "错误规则",
                    BuiltIn = false
                },
                custom
            ]
        };

        settings.EnsureDefaults();

        var builtIn = Assert.Single(settings.Templates, template => template.Id == RenameTemplate.BuiltInSeriesId);
        Assert.Equal(RenameTemplate.SeriesDefault().Name, builtIn.Name);
        Assert.Equal(RenameTemplate.SeriesDefault().Pattern, builtIn.Pattern);
        Assert.True(builtIn.BuiltIn);
        Assert.Contains(settings.Templates, template => template.Id == custom.Id && template.Pattern == custom.Pattern);
    }

    [Fact]
    public async Task PreviewRenamesUniqueSidecarAndPersistentUndoRestoresFiles()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var video = Path.Combine(root.FullName, "E01.mp4");
            var subtitle = Path.Combine(root.FullName, "E01.zh-CN.srt");
            File.WriteAllText(video, "video");
            File.WriteAllText(subtitle, "subtitle");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            RenamePreview? preview = null;
            var previewTask = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "preview", async (context, token) =>
            {
                preview = await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
                {
                    DirectoryPath = root.FullName,
                    ChineseTitle = "测试剧",
                    EnglishTitle = "Test Show",
                    Year = "2026",
                    Season = 1,
                    TemplateName = "test",
                    TemplatePattern = "{中文名}.{季}{集}{扩展名}",
                    ReleaseGroup = "WF",
                    ReleaseGroupSeparator = "-",
                    Files = files
                }, context, token);
            });
            Assert.Equal(TaskState.Completed, previewTask.State);
            Assert.NotNull(preview);
            Assert.True(preview!.CanExecute);
            Assert.Equal(2, preview.Operations.Count);
            var previewItem = Assert.Single(preview.Items);
            Assert.Equal(5, previewItem.FileSizeBytes);
            Assert.EndsWith("5 B", previewItem.DetailText, StringComparison.Ordinal);

            RenameExecutionResult? execution = null;
            var executeTask = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameExecute, false, "rename", async (context, token) =>
            {
                execution = await harness.Service.ExecuteAsync(preview, context, token);
            });
            Assert.Equal(TaskState.Completed, executeTask.State);
            Assert.True(File.Exists(Path.Combine(root.FullName, "测试剧.S01E01-WF.mp4")));
            Assert.True(File.Exists(Path.Combine(root.FullName, "测试剧.S01E01-WF.zh-CN.srt")));

            var secondService = new RenameService(harness.Runner, harness.ToolLocator, harness.Settings, harness.History);
            var undoTask = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameUndo, false, "undo", async (context, token) =>
            {
                await secondService.UndoAsync(execution!.HistoryId, context, token);
            });
            Assert.Equal(TaskState.Completed, undoTask.State);
            Assert.True(File.Exists(video));
            Assert.True(File.Exists(subtitle));
            Assert.NotNull((await harness.History.LoadAsync()).Single().UndoneAt);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task PreviewUsesFullWidthColonForEnglishTitleOnWindows()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "movie.mp4"), "video");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            RenamePreview? preview = null;
            await harness.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "preview", async (context, token) =>
            {
                preview = await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
                {
                    DirectoryPath = root.FullName,
                    MediaType = RenameMediaType.Movie,
                    EnglishTitle = "Raised by Demons: Panda Li",
                    TemplateName = "english-title",
                    TemplatePattern = "{英文名}{扩展名}",
                    Files = files
                }, context, token);
            });

            var item = Assert.Single(preview!.Items);
            Assert.Equal("Raised by Demons： Panda Li.mp4", Path.GetFileName(item.TargetPath));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task PreviewAppendsReleaseGroupBeforeExtensionForMovies()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "movie.mp4"), "video");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            var preview = await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
            {
                DirectoryPath = root.FullName,
                MediaType = RenameMediaType.Movie,
                EnglishTitle = "Crowned in a Hundred Days",
                Year = "2026",
                TemplateName = "release-group",
                TemplatePattern = "{英文名}.{年份}.{分辨率}.{来源}.{编码}.{音频}{扩展名}",
                ReleaseGroup = "WF",
                ReleaseGroupSeparator = "-",
                Files = files
            }, new TaskExecutionContext(_ => { }), CancellationToken.None);

            Assert.Equal(
                "Crowned in a Hundred Days.2026.1080p.WEB-DL.AVC.AAC.2.0-WF.mp4",
                Path.GetFileName(Assert.Single(preview.Items).TargetPath));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task InvalidReleaseGroupStopsPreviewBeforeMediaProbe()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "movie.mp4"), "video");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.BuildPreviewAsync(new RenamePreviewRequest
            {
                DirectoryPath = root.FullName,
                MediaType = RenameMediaType.Movie,
                TemplateName = "invalid-release-group",
                TemplatePattern = "{中文名}{扩展名}",
                ReleaseGroup = "W:F",
                Files = files
            }, new TaskExecutionContext(_ => { }), CancellationToken.None));

            Assert.Contains("不允许使用的字符", error.Message);
            Assert.Equal(0, harness.Runner.CallCount);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task PreviewUsesFourConcurrentProbesAndCachesUnchangedFiles()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            for (var episode = 1; episode <= 6; episode++)
                File.WriteAllText(Path.Combine(root.FullName, $"E{episode:00}.mp4"), $"video-{episode}");
            var runner = new MediaProcessRunner(50);
            var harness = CreateHarness(root.FullName, runner);
            var files = await harness.Service.ScanAsync(root.FullName);

            async Task BuildPreviewAsync()
            {
                var task = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "preview", async (context, token) =>
                {
                    await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
                    {
                        DirectoryPath = root.FullName,
                        ChineseTitle = "测试剧",
                        Year = "2026",
                        Season = 1,
                        TemplateName = "test",
                        TemplatePattern = "{中文名}.{季}{集}{扩展名}",
                        Files = files
                    }, context, token);
                });
                Assert.Equal(TaskState.Completed, task.State);
            }

            await BuildPreviewAsync();
            Assert.Equal(6, runner.CallCount);
            Assert.Equal(4, runner.MaximumConcurrency);

            await BuildPreviewAsync();
            Assert.Equal(6, runner.CallCount);

            File.AppendAllText(files[0].SourcePath, "-changed");
            await BuildPreviewAsync();
            Assert.Equal(7, runner.CallCount);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task PreviewMapsContinuousSourceEpisodesAcrossTmdbSeasons()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            foreach (var episode in new[] { 1, 13, 61, 65 }) File.WriteAllText(Path.Combine(root.FullName, $"E{episode:00}.mp4"), "video");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            var map = new Dictionary<int, TmdbEpisodeTarget>
            {
                [1] = new(1, 1, "貔貅驾到"),
                [13] = new(2, 1, "洞中的秘密"),
                [61] = new(6, 1, "寻母之路"),
                [65] = new(6, 5, "野蛟拜师")
            };

            RenamePreview? preview = null;
            await harness.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "preview", async (context, token) =>
            {
                preview = await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
                {
                    DirectoryPath = root.FullName,
                    ChineseTitle = "有兽焉",
                    EnglishTitle = "You Shou Yan",
                    Year = "2023",
                    Season = 1,
                    UseTmdbSeasonMapping = true,
                    TemplateName = "tmdb-seasons",
                    TemplatePattern = "{中文名}.{年份}.{季}{集}.{集名}{扩展名}",
                    Files = files,
                    TmdbEpisodeMap = map
                }, context, token);
            });

            Assert.True(preview!.CanExecute);
            Assert.Collection(preview.Items,
                item => { Assert.Equal("有兽焉.2023.S01E01.貔貅驾到.mp4", item.TargetName); Assert.Equal("原 E01 → TMDB S01E01", item.EpisodeMappingText); },
                item => { Assert.Equal("有兽焉.2023.S02E01.洞中的秘密.mp4", item.TargetName); Assert.Equal("原 E13 → TMDB S02E01", item.EpisodeMappingText); },
                item => Assert.Equal("有兽焉.2023.S06E01.寻母之路.mp4", item.TargetName),
                item => { Assert.Equal("有兽焉.2023.S06E05.野蛟拜师.mp4", item.TargetName); Assert.Equal(6, item.SeasonNumber); Assert.Equal(5, item.EpisodeNumber); });

            await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameExecute, false, "rename", async (context, token) =>
            {
                await harness.Service.ExecuteAsync(preview, context, token);
            });
            var history = Assert.Single(await harness.History.LoadAsync());
            Assert.True(history.UsedTmdbSeasonMapping);
            Assert.Equal([1, 2, 6], history.MappedSeasons);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task TmdbSeasonMappingRejectsUnrecognizedOrOutOfRangeSourceEpisodesWithoutFallback()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "片头.mp4"), "video");
            File.WriteAllText(Path.Combine(root.FullName, "E66.mp4"), "video");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.BuildPreviewAsync(new RenamePreviewRequest
            {
                DirectoryPath = root.FullName,
                ChineseTitle = "有兽焉",
                UseTmdbSeasonMapping = true,
                TemplateName = "tmdb-seasons",
                TemplatePattern = "{中文名}.{季}{集}{扩展名}",
                Files = files,
                TmdbEpisodeMap = new Dictionary<int, TmdbEpisodeTarget> { [65] = new(6, 5, "野蛟拜师") }
            }, new TaskExecutionContext(_ => { }), CancellationToken.None));

            Assert.Contains("未识别源集号", error.Message, StringComparison.Ordinal);
            Assert.Contains("E66", error.Message, StringComparison.Ordinal);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task TwoPhaseRenameSupportsNameSwapAndUndo()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var a = Path.Combine(root.FullName, "A.mp4");
            var b = Path.Combine(root.FullName, "B.mp4");
            File.WriteAllText(a, "content-a");
            File.WriteAllText(b, "content-b");
            var harness = CreateHarness(root.FullName);
            var request = new RenamePreviewRequest { DirectoryPath = root.FullName, TemplateName = "swap", TemplatePattern = "{中文名}{扩展名}" };
            var preview = new RenamePreview
            {
                Request = request,
                Items =
                [
                    new RenamePreviewItem { SourcePath = a, TargetPath = b, Operations = [new RenameFileOperation(a, b)] },
                    new RenamePreviewItem { SourcePath = b, TargetPath = a, Operations = [new RenameFileOperation(b, a)] }
                ]
            };
            RenameExecutionResult? execution = null;
            var task = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameExecute, false, "swap", async (context, token) =>
            {
                execution = await harness.Service.ExecuteAsync(preview, context, token);
            });
            Assert.Equal(TaskState.Completed, task.State);
            Assert.Equal("content-b", File.ReadAllText(a));
            Assert.Equal("content-a", File.ReadAllText(b));

            var undo = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameUndo, false, "swap-undo", async (context, token) =>
            {
                await harness.Service.UndoAsync(execution!.HistoryId, context, token);
            });
            Assert.Equal(TaskState.Completed, undo.State);
            Assert.Equal("content-a", File.ReadAllText(a));
            Assert.Equal("content-b", File.ReadAllText(b));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task MoviePreviewRejectsDuplicateTargets()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "A.mp4"), "a");
            File.WriteAllText(Path.Combine(root.FullName, "B.mp4"), "b");
            var harness = CreateHarness(root.FullName);
            var files = await harness.Service.ScanAsync(root.FullName);
            RenamePreview? preview = null;
            await harness.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "preview", async (context, token) =>
            {
                preview = await harness.Service.BuildPreviewAsync(new RenamePreviewRequest
                {
                    DirectoryPath = root.FullName,
                    MediaType = RenameMediaType.Movie,
                    ChineseTitle = "电影",
                    TemplateName = "movie",
                    TemplatePattern = "{中文名}{扩展名}",
                    Files = files
                }, context, token);
            });
            Assert.NotNull(preview);
            Assert.False(preview!.CanExecute);
            Assert.Contains(preview.Errors, error => error.Contains("同一个目标", StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    private static Harness CreateHarness(string root, MediaProcessRunner? runner = null)
    {
        var app = Directory.CreateDirectory(Path.Combine(root, "app"));
        var ffprobe = Path.Combine(app.FullName, "ffprobe.exe");
        File.WriteAllText(ffprobe, "fake");
        var paths = new ApplicationPaths(app.FullName, Path.Combine(root, "local"));
        runner ??= new MediaProcessRunner();
        var toolLocator = new FixedToolLocator(ffprobe);
        var settings = new FixedSettingsStore();
        var history = new RenameHistoryStore(paths);
        var service = new RenameService(runner, toolLocator, settings, history);
        return new Harness(service, new TaskManager(paths, runner), runner, toolLocator, settings, history);
    }

    private sealed record Harness(RenameService Service, TaskManager TaskManager, MediaProcessRunner Runner, FixedToolLocator ToolLocator, FixedSettingsStore Settings, RenameHistoryStore History);

    private sealed class MediaProcessRunner(int delayMilliseconds = 0) : IProcessRunner
    {
        private const string Json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"24000/1001"},{"codec_type":"audio","codec_name":"aac","channels":2}]}""";
        private int _active;
        private int _callCount;
        private int _maximumConcurrency;
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public async Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _active);
            while (active > Volatile.Read(ref _maximumConcurrency))
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (active <= observed || Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) == observed) break;
            }
            try
            {
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
                return new ProcessResult(0, Json, false);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task TerminateAllAsync() => Task.CompletedTask;
    }

    private sealed class FixedToolLocator(string ffprobe) : IToolLocator
    {
        public ToolPaths Locate(AppSettings settings) => new() { Ffprobe = ffprobe };
        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult("test");
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => Task.FromResult(update(new AppSettings()));
    }
}
