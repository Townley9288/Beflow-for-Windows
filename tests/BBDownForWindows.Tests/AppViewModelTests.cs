using BBDownForWindows.App;
using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class AppViewModelTests
{
    [Fact]
    public void RestoredManualStreamsKeepUnavailableOriginalSignatureUntilUserOverridesIt()
    {
        var episode = new DownloadEpisodeInfo
        {
            Page = new PageInfo(1, "1", "第一集", "24m"),
            State = DownloadEpisodeParseState.Ready,
            VideoStreams = [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "HEVC", "24", "1100 kbps", 1100, "100 MB")],
            AudioStreams = [new AudioStreamInfo(1, "M4A", "128 kbps", 128, "20 MB")]
        };
        var viewModel = new DownloadEpisodeViewModel(episode);
        viewModel.ApplyRestored(new EpisodeStreamSelection
        {
            PageNumber = 1,
            PageTitle = "第一集",
            Video = new VideoStreamSelection("1080P 高清", "1920x1080", "HEVC", 1000, true),
            Audio = new AudioStreamSelection("M4A", 192, true)
        }, DownloadMode.VideoAndAudio);

        var restored = viewModel.BuildSelection();
        Assert.Equal(1000, restored.Video!.BitrateKbps);
        Assert.Equal(192, restored.Audio!.BitrateKbps);
        Assert.Equal("历史手动规格已失效", viewModel.StatusText);

        viewModel.SelectedQuality = Assert.Single(viewModel.QualityOptions).Value;
        viewModel.SelectedAudio = Assert.Single(viewModel.AudioOptions).Value;
        var overridden = viewModel.BuildSelection();
        Assert.Equal(1100, overridden.Video!.BitrateKbps);
        Assert.Equal(128, overridden.Audio!.BitrateKbps);
        Assert.Equal("就绪", viewModel.StatusText);
    }

    [Fact]
    public void ApplyingBatchEncodingUsesValueBackedEpisodeChoice()
    {
        var episode = new DownloadEpisodeInfo
        {
            Page = new PageInfo(4, "4", "第四集", "24m"),
            State = DownloadEpisodeParseState.Ready,
            VideoStreams =
            [
                new VideoStreamInfo(0, "HDR 真彩", "3840x2160", 3840, 2160, "HEVC", "25", "7768 kbps", 7768, "1.41 GB"),
                new VideoStreamInfo(1, "HDR 真彩", "3840x2160", 3840, 2160, "AVC", "25", "5000 kbps", 5000, "900 MB")
            ],
            AudioStreams = [new AudioStreamInfo(2, "M4A", "321 kbps", 321, "60 MB")]
        };
        var viewModel = new DownloadEpisodeViewModel(episode);
        viewModel.ApplyRule(new StreamSelectionRule("HDR 真彩", "AVC", "auto", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        viewModel.ApplyRule(new StreamSelectionRule("HDR 真彩", "HEVC", "auto", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        Assert.Equal("HEVC", viewModel.SelectedEncoding);
        Assert.Equal("HEVC", viewModel.BuildSelection().Video!.Codec);
        Assert.Contains(viewModel.EncodingOptions, item => item.Value == "HEVC" && item.Label == "HEVC");
    }

    [Fact]
    public void ExternalDownloadInputDoesNotReplaceCurrentValueWithUnsupportedText()
    {
        using var fixture = new AppFixture();
        var viewModel = new DownloadViewModel(fixture.Services) { Url = "BV1xx411c7mD" };

        var applied = viewModel.ApplyExternalInput("https://example.com/not-bilibili");

        Assert.False(applied);
        Assert.Equal("BV1xx411c7mD", viewModel.Url);
    }

    [Fact]
    public void TwoDroppedInputsPopulateBothDualAudioSources()
    {
        using var fixture = new AppFixture();
        var viewModel = new DualAudioViewModel(fixture.Services);

        var applied = viewModel.ApplyExternalInputs(
        [
            "分享 A：https://b23.tv/source-a",
            "https://www.bilibili.com/video/BV1xx411c7mD"
        ]);

        Assert.True(applied);
        Assert.Equal("两个独立链接", viewModel.SourceModeText);
        Assert.Equal("https://b23.tv/source-a", viewModel.SourceAUrl);
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD", viewModel.SourceBUrl);
    }

    [Fact]
    public async Task TemplateEditorRejectsUnknownFieldsBeforeWritingSettings()
    {
        using var fixture = new AppFixture();
        var custom = new RenameTemplate { Name = "自定义", MediaType = RenameMediaType.Series, Pattern = "{中文名}{扩展名}" };
        await fixture.Services.RenameSettings.UpdateAsync(settings =>
        {
            settings.Templates.Add(custom);
            return settings;
        });
        var viewModel = new RenameTemplatesViewModel(fixture.Services);
        await viewModel.InitializeAsync(new RenameTemplatesNavigationContext(RenameMediaType.Series, custom.Id));
        viewModel.TemplatePattern = "{未知字段}{扩展名}";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(viewModel.SaveChangesAsync);

        Assert.Contains("未知字段", error.Message);
        var stored = await fixture.Services.RenameSettings.LoadAsync();
        Assert.Equal("{中文名}{扩展名}", stored.Templates.Single(item => item.Id == custom.Id).Pattern);
    }

    [Fact]
    public async Task SavingTmdbCardPreservesTemplatesAndActiveTemplateIds()
    {
        using var fixture = new AppFixture();
        var custom = new RenameTemplate { Name = "保留模板", MediaType = RenameMediaType.Series, Pattern = "{中文名}{扩展名}" };
        await fixture.Services.RenameSettings.UpdateAsync(settings =>
        {
            settings.Templates.Add(custom);
            settings.ActiveSeriesTemplateId = custom.Id;
            return settings;
        });
        var viewModel = new SettingsViewModel(fixture.Services);
        viewModel.RenameSettings.TmdbApiKey = "new-key";
        viewModel.RenameSettings.ProxyUrl = "http://127.0.0.1:7890";

        await viewModel.SaveRenameCommand.ExecuteAsync(null);

        var stored = await fixture.Services.RenameSettings.LoadAsync();
        Assert.Equal("new-key", stored.TmdbApiKey);
        Assert.Equal(custom.Id, stored.ActiveSeriesTemplateId);
        Assert.Contains(stored.Templates, item => item.Id == custom.Id && item.Pattern == custom.Pattern);
    }

    [Fact]
    public async Task RepeatedPreviewInvocationIsIgnoredWhileFirstPreviewIsStarting()
    {
        using var fixture = new AppFixture();
        var viewModel = new RenameViewModel(fixture.Services);
        await viewModel.InitializeAsync(null);
        typeof(RenameViewModel).GetField("_tmdbId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel, 1);
        viewModel.Files.Add(new RenameFileItemViewModel(new RenameFileEntry
        {
            SourcePath = Path.Combine(fixture.RootPath, "video.mkv"),
            IsSelected = true
        }));

        var first = viewModel.PreviewAsync();
        var second = viewModel.PreviewAsync();
        await Task.WhenAll(first, second);

        Assert.False(viewModel.IsPreviewing);
        Assert.NotEqual(string.Empty, viewModel.Message);
    }

    private sealed class AppFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();
        public AppFixture()
        {
            var app = Directory.CreateDirectory(Path.Combine(_root.FullName, "app"));
            Services = new AppServices(new ApplicationPaths(app.FullName, Path.Combine(_root.FullName, "local")));
        }

        public AppServices Services { get; }
        public string RootPath => _root.FullName;
        public void Dispose()
        {
            Services.HttpClient.Dispose();
            Services.UpdateHttpClient.Dispose();
            _root.Delete(true);
        }
    }
}
