using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class StoreTests
{
    [Fact]
    public async Task OldConfigKeepsNewDefaults()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.SettingsFile, "{\"quality\":\"1080P\",\"audioOnly\":\"仅音频\"}");
            var settings = await new SettingsStore(paths).LoadAsync();
            Assert.Equal("1080P", settings.Quality);
            Assert.Equal(DownloadMode.AudioOnly, settings.DownloadMode);
            Assert.Equal(AudioBitratePriority.Highest, settings.AudioBitratePriority);
            Assert.Equal(AppThemeMode.System, settings.ThemeMode);
            Assert.True(settings.SaveTaskLogs);
            Assert.True(settings.CheckUpdatesOnStartup);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(AppThemeMode.System, "system")]
    [InlineData(AppThemeMode.Light, "light")]
    [InlineData(AppThemeMode.Dark, "dark")]
    public async Task ThemeModeRoundTripsAsStableString(AppThemeMode mode, string serializedValue)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var store = new SettingsStore(paths);
            await store.SaveAsync(new AppSettings { ThemeMode = mode, Quality = "1080P" });

            var json = await File.ReadAllTextAsync(paths.SettingsFile);
            var restored = await store.LoadAsync();

            Assert.Contains($"\"themeMode\": \"{serializedValue}\"", json);
            Assert.Equal(mode, restored.ThemeMode);
            Assert.Equal("1080P", restored.Quality);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task InvalidThemeFallsBackWithoutResettingOtherSettings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.SettingsFile, "{\"themeMode\":\"sepia\",\"quality\":\"720P\",\"checkUpdatesOnStartup\":false}");

            var settings = await new SettingsStore(paths).LoadAsync();

            Assert.Equal(AppThemeMode.System, settings.ThemeMode);
            Assert.Equal("720P", settings.Quality);
            Assert.False(settings.CheckUpdatesOnStartup);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SettingsSaveCanPreserveThemeSelectedAfterPageLoad()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var store = new SettingsStore(paths);
            await store.SaveAsync(new AppSettings { Quality = "1080P", ThemeMode = AppThemeMode.System });
            var settingsPageSnapshot = await store.LoadAsync();

            var themeSnapshot = await store.LoadAsync();
            themeSnapshot.ThemeMode = AppThemeMode.Dark;
            await store.SaveAsync(themeSnapshot);

            settingsPageSnapshot.Quality = "4K";
            settingsPageSnapshot.ThemeMode = (await store.LoadAsync()).ThemeMode;
            await store.SaveAsync(settingsPageSnapshot);
            var restored = await store.LoadAsync();

            Assert.Equal(AppThemeMode.Dark, restored.ThemeMode);
            Assert.Equal("4K", restored.Quality);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UpdateStateIsStoredSeparatelyFromSettings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var expected = DateTimeOffset.Parse("2026-07-15T10:00:00Z");
            var store = new UpdateStateStore(paths);
            await store.SaveAsync(new UpdateState { LastCheckedAt = expected });
            Assert.Equal(expected, (await store.LoadAsync()).LastCheckedAt);
            Assert.False(File.Exists(paths.SettingsFile));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void PortableFlagUsesLocalDataDirectory()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "portable.flag"), string.Empty);
            var paths = new ApplicationPaths(root.FullName, Path.Combine(root.FullName, "Local"));
            Assert.True(paths.Portable);
            Assert.Equal(Path.Combine(root.FullName, "Data"), paths.DataRoot);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void InstalledDataUsesBeflowDirectory()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(Path.Combine(root.FullName, "app"), root.FullName);
            Assert.False(paths.Portable);
            Assert.Equal(Path.Combine(root.FullName, "Beflow"), paths.DataRoot);
            Assert.Equal(Path.Combine(root.FullName, "BBDownForWindows"), paths.PreviousInstalledDataRoot);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task DualAudioHistoryKeepsIndependentAudioFormats()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var store = new HistoryStore(paths);
            await store.AddAsync(new HistoryRecord
            {
                TaskType = TaskKind.DualAudioMux,
                DualAudio = new DualAudioRequest
                {
                    PrimaryAudioCodec = "FLAC",
                    SecondaryAudioCodec = "E-AC-3"
                }
            });

            var restored = Assert.Single(await store.LoadAsync()).DualAudio;
            Assert.NotNull(restored);
            Assert.Equal("FLAC", restored.PrimaryAudioCodec);
            Assert.Equal("E-AC-3", restored.SecondaryAudioCodec);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task HistoryStoreNotifiesAfterMutations()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var store = new HistoryStore(paths);
            var changes = 0;
            store.Changed += (_, _) => changes++;
            var record = new HistoryRecord { Title = "标题", Timestamp = DateTimeOffset.Parse("2026-07-15T22:28:15+08:00") };

            await store.AddAsync(record);
            var serialized = await File.ReadAllTextAsync(paths.HistoryFile);
            await store.SaveAllAsync(await store.LoadAsync());
            await store.DeleteAsync(0);
            await store.ClearAsync();

            Assert.Equal(4, changes);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", record.TimestampText);
            Assert.DoesNotContain("timestampText", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally { root.Delete(true); }
    }
}
