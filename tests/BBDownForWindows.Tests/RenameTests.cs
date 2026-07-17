using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class RenameTests
{
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
                    Files = files
                }, context, token);
            });
            Assert.Equal(TaskState.Completed, previewTask.State);
            Assert.NotNull(preview);
            Assert.True(preview!.CanExecute);
            Assert.Equal(2, preview.Operations.Count);

            RenameExecutionResult? execution = null;
            var executeTask = await harness.TaskManager.RunExclusiveAsync(TaskKind.RenameExecute, false, "rename", async (context, token) =>
            {
                execution = await harness.Service.ExecuteAsync(preview, context, token);
            });
            Assert.Equal(TaskState.Completed, executeTask.State);
            Assert.True(File.Exists(Path.Combine(root.FullName, "测试剧.S01E01.mp4")));
            Assert.True(File.Exists(Path.Combine(root.FullName, "测试剧.S01E01.zh-CN.srt")));

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

    private static Harness CreateHarness(string root)
    {
        var app = Directory.CreateDirectory(Path.Combine(root, "app"));
        var ffprobe = Path.Combine(app.FullName, "ffprobe.exe");
        File.WriteAllText(ffprobe, "fake");
        var paths = new ApplicationPaths(app.FullName, Path.Combine(root, "local"));
        var runner = new MediaProcessRunner();
        var toolLocator = new FixedToolLocator(ffprobe);
        var settings = new FixedSettingsStore();
        var history = new RenameHistoryStore(paths);
        var service = new RenameService(runner, toolLocator, settings, history);
        return new Harness(service, new TaskManager(paths, runner), runner, toolLocator, settings, history);
    }

    private sealed record Harness(RenameService Service, TaskManager TaskManager, MediaProcessRunner Runner, FixedToolLocator ToolLocator, FixedSettingsStore Settings, RenameHistoryStore History);

    private sealed class MediaProcessRunner : IProcessRunner
    {
        private const string Json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"24000/1001"},{"codec_type":"audio","codec_name":"aac","channels":2}]}""";
        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken) => Task.FromResult(new ProcessResult(0, Json, false));
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
