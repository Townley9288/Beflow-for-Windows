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
            Assert.True(settings.SaveTaskLogs);
        }
        finally
        {
            root.Delete(true);
        }
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
}
