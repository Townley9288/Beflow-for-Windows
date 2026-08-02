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
    public void DisplaysCurrentBilibiliQualityNamesForLegacyBBDownStreams()
    {
        var episode = new DownloadEpisodeInfo
        {
            Page = new PageInfo(1, "1", "第一集", "24m"),
            State = DownloadEpisodeParseState.Ready,
            VideoStreams =
            [
                new VideoStreamInfo(1, "HDR 真彩", "1920x1080", 1920, 1080, "HEVC", "25", "1954 kbps", 1954, "300 MB"),
                new VideoStreamInfo(3, "720P 高清", "1280x720", 1280, 720, "HEVC", "25", "500 kbps", 500, "80 MB"),
                new VideoStreamInfo(6, "480P 清晰", "852x480", 852, 480, "HEVC", "25", "300 kbps", 300, "50 MB")
            ],
            AudioStreams = [new AudioStreamInfo(0, "M4A", "128 kbps", 128, "20 MB")]
        };
        var viewModel = new DownloadEpisodeViewModel(episode);

        viewModel.ApplyRule(new StreamSelectionRule("720P 准高清", "HEVC", "M4A", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);

        Assert.Contains(viewModel.QualityOptions, item => item.Label.StartsWith("720P 准高清", StringComparison.Ordinal));
        Assert.Contains(viewModel.QualityOptions, item => item.Label.StartsWith("480P 标清", StringComparison.Ordinal));
        Assert.Contains(viewModel.QualityOptions, item => item.Label == "HDR 真彩 · 1920×1080 · 1954 kbps");
        Assert.DoesNotContain(viewModel.QualityOptions, item => item.Label.Contains("4K·HDR", StringComparison.Ordinal));
        Assert.StartsWith("720P 准高清", viewModel.SelectedQualityLabel);
    }

    [Fact]
    public void QualityRuleListsUseCurrentBilibiliNames()
    {
        using var fixture = new AppFixture();
        var download = new DownloadViewModel(fixture.Services);
        var settings = new SettingsViewModel(fixture.Services);
        var dualAudio = new DualAudioViewModel(fixture.Services);

        Assert.Contains(download.QualityRuleOptions, item => item.Value == "HDR 真彩" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(download.QualityRuleOptions, item => item.Value == "4K·SDR增强" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(download.QualityRuleOptions, item => item.Value == "4K 超高清" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(download.QualityRuleOptions, item => item.Value == "智能修复");
        Assert.Equal(["1080P 高清", "智能修复", "720P 准高清"], download.QualityRuleOptions.SkipWhile(item => item.Value != "1080P 高清").Take(3).Select(item => item.Value));
        Assert.Contains(download.QualityRuleOptions, item => item.Value == "720P 准高清");
        Assert.Contains(download.QualityRuleOptions, item => item.Value == "480P 标清");
        Assert.DoesNotContain(download.QualityRuleOptions, item => item.Value is "720P 高清" or "480P 清晰");

        Assert.Contains(settings.QualityOptions, item => item.Value == "HDR 真彩" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(settings.QualityOptions, item => item.Value == "4K·SDR增强" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(settings.QualityOptions, item => item.Value == "4K 超高清" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(settings.QualityOptions, item => item.Value == "智能修复");
        Assert.Equal(["1080P 高清", "智能修复", "720P 准高清"], settings.QualityOptions.SkipWhile(item => item.Value != "1080P 高清").Take(3).Select(item => item.Value));
        Assert.Contains(settings.QualityOptions, item => item.Value == "720P 准高清");
        Assert.Contains(settings.QualityOptions, item => item.Value == "480P 标清");
        Assert.DoesNotContain(settings.QualityOptions, item => item.Value is "720P 高清" or "480P 清晰");

        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "HDR 真彩" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "4K·SDR增强" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "4K 超高清" && item.Label.Contains("大视界", StringComparison.Ordinal));
        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "智能修复");
        Assert.Equal(["1080P 高清", "智能修复", "720P 准高清"], dualAudio.QualityOptions.SkipWhile(item => item.Value != "1080P 高清").Take(3).Select(item => item.Value));
        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "720P 准高清");
        Assert.Contains(dualAudio.QualityOptions, item => item.Value == "480P 标清");
        Assert.DoesNotContain(dualAudio.QualityOptions, item => item.Value is "720P 高清" or "480P 清晰");
    }

    [Fact]
    public void DualAudioTrackPresetsApplyNameAndMkvmergeLanguageAsAPair()
    {
        using var fixture = new AppFixture();
        var viewModel = new DualAudioViewModel(fixture.Services);

        Assert.Equal("国语", viewModel.SourceALabel);
        Assert.Equal("zh", viewModel.SourceALanguage);
        Assert.Equal("国语（zh）", viewModel.SourceAPreset?.DisplayName);
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "普通话" && item.Language == "cmn");
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "台配国语" && item.Language == "cmn-TW");
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "粤语" && item.Language == "yue");
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "日语" && item.Language == "ja");
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "英语" && item.Language == "en");
        Assert.Contains(viewModel.AudioTrackPresets, item => item.TrackName == "韩语" && item.Language == "ko");

        viewModel.SourceAPreset = viewModel.AudioTrackPresets.Single(item => item.TrackName == "台配国语");

        Assert.Equal("台配国语", viewModel.SourceALabel);
        Assert.Equal("cmn-TW", viewModel.SourceALanguage);

        viewModel.SourceBLabel = "导演评论";
        viewModel.SourceBLanguage = "en-US";

        Assert.True(viewModel.SourceBPreset?.IsCustom);
        viewModel.SourceBPreset = viewModel.AudioTrackPresets.Single(item => item.IsCustom);
        Assert.Equal("导演评论", viewModel.SourceBLabel);
        Assert.Equal("en-US", viewModel.SourceBLanguage);
    }

    [Theory]
    [InlineData(DownloadMode.VideoAndAudio)]
    [InlineData(DownloadMode.VideoOnly)]
    [InlineData(DownloadMode.AudioOnly)]
    public void MuxedEpisodeShowsEmbeddedAudioAndCountsSizeOnce(DownloadMode mode)
    {
        var episode = new DownloadEpisodeInfo
        {
            Page = new PageInfo(22, "22", "第22集", "24m"),
            State = DownloadEpisodeParseState.Ready,
            IsMuxedStream = true,
            VideoStreams = [new VideoStreamInfo(0, "1080P 高码率", "1920x1080", 1920, 1080, "AVC", string.Empty, "~4109 kbps", 4109, "555.27 MB")]
        };
        var viewModel = new DownloadEpisodeViewModel(episode);

        viewModel.ApplyRule(new StreamSelectionRule("1080P 高码率", "AVC", "auto", AudioBitratePriority.Highest), mode);
        var selection = viewModel.BuildSelection();

        Assert.Equal("内封音频（合流）", viewModel.SelectedAudioLabel);
        Assert.False(viewModel.AudioSelectionEnabled);
        Assert.NotNull(selection.Video);
        Assert.Null(selection.Audio);
        Assert.True(selection.IsMuxedStream);
        Assert.Equal(StreamSelectionPolicy.ParseSizeBytes("555.27 MB"), viewModel.EstimatedSizeBytes);
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

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ClipboardMonitoringOnlyInspectsChangesWhileWindowIsInactive(bool enabled, bool windowActive, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldInspectClipboardChange(enabled, windowActive));
    }

    [Fact]
    public void CurrentDownloadInputIsNotReappliedOrParsedFromClipboard()
    {
        const string url = "https://www.bilibili.com/bangumi/play/ep114762?from_spmid=666.25.episode.0";
        using var fixture = new AppFixture();
        var viewModel = new DownloadViewModel(fixture.Services) { Url = url };

        Assert.True(MainWindow.IsDuplicateClipboardInput(url, url));
        Assert.False(viewModel.ApplyExternalInput(url));
        Assert.Equal(url, viewModel.Url);
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
    public void ChangingDualAudioLinksDefersPopulatedRowTeardownUntilTheBindingUpdateReturns()
    {
        using var fixture = new AppFixture();
        var deferred = new Queue<Action>();
        var viewModel = new DualAudioViewModel(fixture.Services, deferred.Enqueue)
        {
            SourceAUrl = "https://www.bilibili.com/video/BV1oldA",
            SourceBUrl = "https://www.bilibili.com/video/BV1oldB"
        };
        deferred.Clear();
        viewModel.Pairs.Add(CreateDualAudioPairViewModel());

        viewModel.SourceAUrl = "https://www.bilibili.com/video/BV1newA";
        viewModel.SourceBUrl = "https://www.bilibili.com/video/BV1newB";

        Assert.Single(viewModel.Pairs);
        Assert.Equal(2, deferred.Count);
        deferred.Dequeue().Invoke();
        Assert.Single(viewModel.Pairs);
        deferred.Dequeue().Invoke();
        Assert.Empty(viewModel.Pairs);
        Assert.False(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task DualAudioCommandSurfacesUnexpectedPostProcessingFailuresInsteadOfCrashingTheApp()
    {
        using var fixture = new AppFixture();
        const string sourceAUrl = "https://www.bilibili.com/video/BV1sourceA";
        const string sourceBUrl = "https://www.bilibili.com/video/BV1sourceB";
        var viewModel = new DualAudioViewModel(fixture.Services)
        {
            SourceAUrl = sourceAUrl,
            SourceBUrl = sourceBUrl,
            SourceBDelay = double.NaN
        };
        viewModel.Pairs.Add(CreateDualAudioPairViewModel());
        typeof(DualAudioViewModel).GetField("_catalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel, new DualAudioCatalog
            {
                SourceMode = DualAudioSourceMode.Separate,
                SourceAUrl = sourceAUrl,
                SourceBUrl = sourceBUrl
            });

        Assert.True(viewModel.StartCommand.CanExecute(null));
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains("多音轨任务失败", viewModel.Message, StringComparison.Ordinal);
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

    private static DualAudioPairViewModel CreateDualAudioPairViewModel()
    {
        var sourceA = ReadyEpisode(1, "来源 A");
        var sourceB = ReadyEpisode(1, "来源 B");
        var rule = new StreamSelectionRule("1080P 高清", "HEVC", "M4A", AudioBitratePriority.Highest);
        return new DualAudioPairViewModel(
            new DualAudioEpisodePair { PairNumber = 1, SourceA = sourceA, SourceB = sourceB },
            [new DualAudioPairViewModel.EpisodeChoice(1, "P1 · 来源 B", sourceB)],
            rule,
            rule,
            0);
    }

    private static DownloadEpisodeInfo ReadyEpisode(int page, string title) => new()
    {
        Page = new PageInfo(page, page.ToString(), title, "24m"),
        State = DownloadEpisodeParseState.Ready,
        VideoStreams = [new VideoStreamInfo(0, "1080P 高清", "1920x1080", 1920, 1080, "HEVC", "24", "1000 kbps", 1000, "100 MB")],
        AudioStreams = [new AudioStreamInfo(1, "M4A", "192 kbps", 192, "20 MB")]
    };
}
