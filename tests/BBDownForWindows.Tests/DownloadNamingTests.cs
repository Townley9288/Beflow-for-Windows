using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadNamingTests
{
    [Fact]
    public void RendersFinalMetadataAndActualMediaSpecifications()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var service = new DownloadNamingService();
            var profile = new DownloadNamingProfile
            {
                MainFolderTemplate = "{视频标题}.{AV号}.{BV号}.{SS号}.{UP主昵称}.{UP主UID}.{发布时间}.{下载日期}.{下载时间}.{接口类型}",
                SubfolderTemplate = "{分集序号补零}.{CID}.{EP号}.{分集发布时间}",
                FileNameTemplate = "{分集标题}.{资源类型}.{下载类型}.{画质}.{分辨率}.{帧率}.{视频编码}.{视频码率}.{音频编码}.{音频码率}.{接口类型}"
            };
            var metadata = new BilibiliVideoMetadata
            {
                OwnerName = "出品方",
                OwnerId = "42",
                Aid = "100",
                Bvid = "BV1TEST",
                SeasonId = "200",
                ResourceType = "番剧",
                PublishedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(8)),
                EpisodesByCid = new Dictionary<string, BilibiliEpisodeMetadata>
                {
                    ["300"] = new("300", "101", "BV1EP", "400", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.FromHours(8)))
                }
            };

            var plan = service.BuildPlan(new DownloadNamingContext
            {
                RootDirectory = root.FullName,
                SourceUrl = "https://www.bilibili.com/bangumi/play/ss200",
                VideoTitle = "测试番剧",
                Page = new PageInfo(2, "300", "第二集", "24m"),
                Profile = profile,
                ProfileKind = DownloadNamingProfileKind.MultiEpisode,
                TotalPages = 12,
                DownloadMode = DownloadMode.VideoAndAudio,
                ApiMode = "TV",
                DownloadedAt = new DateTimeOffset(2026, 7, 18, 20, 30, 0, TimeSpan.FromHours(8)),
                Metadata = metadata,
                Video = new VideoStreamInfo(1, "HDR 真彩", "3840x2160", 3840, 2160, "HEVC", "60", "12000 kbps", 12000, "1 GB"),
                Audio = new AudioStreamInfo(2, "E-AC-3", "384 kbps", 384, "50 MB")
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Contains("测试番剧.av100.BV1TEST.ss200.出品方.42.2026-01-02.2026-07-18.20-30-00.TV", plan.RelativePath);
            Assert.Contains(Path.Combine("02.300.ep400.2026-01-03",
                "第二集.番剧.视频+音频.HDR 真彩.3840x2160.60fps.HEVC.12000kbps.E-AC-3.384kbps.TV"), plan.RelativePath);
            Assert.Empty(plan.Warnings);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void MissingFieldsCollapseSeparatorsAndProduceWarnings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var service = new DownloadNamingService();
            var plan = service.BuildPlan(new DownloadNamingContext
            {
                RootDirectory = root.FullName,
                SourceUrl = "https://www.bilibili.com/video/av123",
                VideoTitle = "标题",
                Page = new PageInfo(1, "1", "第一集", string.Empty),
                Profile = new DownloadNamingProfile
                {
                    MainFolderTemplate = "{视频标题}",
                    SubfolderTemplate = string.Empty,
                    FileNameTemplate = "{分集标题}.{EP号}_{音频编码}-{视频编码}"
                },
                ProfileKind = DownloadNamingProfileKind.SingleVideo,
                DownloadMode = DownloadMode.VideoOnly,
                Video = new VideoStreamInfo(0, "720P 高清", "1280x720", 1280, 720, "AVC", "30", "1000 kbps", 1000, "1 MB")
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(Path.Combine("标题", "第一集.AVC"), plan.RelativePath);
            Assert.Contains(plan.Warnings, warning => warning.Contains("EP号", StringComparison.Ordinal));
            Assert.Contains(plan.Warnings, warning => warning.Contains("音频编码", StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("{未知字段}", "[P{分集序号补零}]{分集标题}", "未知字段")]
    [InlineData("..\\越界", "[P{分集序号补零}]{分集标题}", "不能包含路径")]
    [InlineData("{视频标题}", "video.mp4", "不需要填写扩展名")]
    public void InvalidTemplatesCannotBeSaved(string main, string file, string expected)
    {
        var result = new DownloadNamingService().Validate(new DownloadNamingProfile
        {
            MainFolderTemplate = main,
            FileNameTemplate = file
        });

        Assert.False(result.IsValid);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public void ExistingTargetsAreNumberedWithoutOverwrite()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var main = Directory.CreateDirectory(Path.Combine(root.FullName, "主文件夹"));
            File.WriteAllText(Path.Combine(main.FullName, "视频.mp4"), "existing");
            var service = new DownloadNamingService();
            var plan = service.BuildPlan(Context(root.FullName, preferred: string.Empty), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal("视频 (2)", plan.FileStem);
            Assert.Equal(Path.Combine("主文件夹", "视频 (2)"), plan.RelativePath);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void FailedRetryReusesPreferredNameWhenOnlyPartialFileExists()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var main = Directory.CreateDirectory(Path.Combine(root.FullName, "主文件夹"));
            File.WriteAllText(Path.Combine(main.FullName, "视频.aria2"), "partial");
            var service = new DownloadNamingService();
            var plan = service.BuildPlan(Context(root.FullName, Path.Combine("主文件夹", "视频")), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal("视频", plan.FileStem);
            Assert.Equal(Path.Combine("主文件夹", "视频"), plan.RelativePath);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SchemaThreeMigratesToIndependentDefaultProfiles()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.SettingsFile, """{"schemaVersion":3,"workDir":"D:\\Downloads","checkUpdatesOnStartup":false}""");

            var settings = await new SettingsStore(paths).LoadAsync();

            Assert.Equal(4, settings.SchemaVersion);
            Assert.Equal("{视频标题}", settings.DownloadNaming.SingleVideo.MainFolderTemplate);
            Assert.Equal("[P{分集序号补零}]{分集标题}", settings.DownloadNaming.MultiEpisode.FileNameTemplate);
            Assert.False(ReferenceEquals(settings.DownloadNaming.SingleVideo, settings.DownloadNaming.MultiEpisode));
            Assert.False(settings.CheckUpdatesOnStartup);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task DamagedNamingComponentFallsBackWithoutResettingValidComponents()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.SettingsFile,
                """{"schemaVersion":4,"downloadNaming":{"singleVideo":{"mainFolderTemplate":"..\\bad","subfolderTemplate":"{画质}","fileNameTemplate":"自定义-{分集标题}"},"multiEpisode":{"mainFolderTemplate":"{视频标题}","subfolderTemplate":"","fileNameTemplate":"[P{分集序号补零}]{分集标题}"}}}""");

            var settings = await new SettingsStore(paths).LoadAsync();

            Assert.Equal("{视频标题}", settings.DownloadNaming.SingleVideo.MainFolderTemplate);
            Assert.Equal("{画质}", settings.DownloadNaming.SingleVideo.SubfolderTemplate);
            Assert.Equal("自定义-{分集标题}", settings.DownloadNaming.SingleVideo.FileNameTemplate);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UpdatingOtherSettingsDoesNotOverwriteDownloadNaming()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "local"));
            var store = new SettingsStore(paths);
            await store.SaveAsync(new AppSettings
            {
                DownloadNaming = new DownloadNamingSettings
                {
                    SingleVideo = new DownloadNamingProfile { MainFolderTemplate = "单集-{BV号}", FileNameTemplate = "{分集标题}" },
                    MultiEpisode = new DownloadNamingProfile { MainFolderTemplate = "多集-{SS号}", SubfolderTemplate = "S01", FileNameTemplate = "E{分集序号补零}" }
                }
            });

            await store.UpdateAsync(current =>
            {
                var clone = current.Clone();
                clone.CheckUpdatesOnStartup = false;
                return clone;
            });
            var restored = await store.LoadAsync();

            Assert.Equal("单集-{BV号}", restored.DownloadNaming.SingleVideo.MainFolderTemplate);
            Assert.Equal("S01", restored.DownloadNaming.MultiEpisode.SubfolderTemplate);
            Assert.False(restored.CheckUpdatesOnStartup);
        }
        finally { root.Delete(true); }
    }

    private static DownloadNamingContext Context(string root, string preferred) => new()
    {
        RootDirectory = root,
        SourceUrl = "https://www.bilibili.com/video/BV1TEST",
        VideoTitle = "主文件夹",
        Page = new PageInfo(1, "1", "视频", string.Empty),
        Profile = new DownloadNamingProfile
        {
            MainFolderTemplate = "{视频标题}",
            FileNameTemplate = "{分集标题}"
        },
        ProfileKind = DownloadNamingProfileKind.SingleVideo,
        PreferredRelativePath = preferred,
        AllowPartialReuse = !string.IsNullOrWhiteSpace(preferred)
    };
}
