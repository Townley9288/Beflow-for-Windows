using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class ToolLocatorTests
{
    [Fact]
    public void FastLookupUsesBundledAndConfiguredPathsWithoutEnumeratingLegacyDrives()
    {
        using var temp = new TempDirectory();
        var paths = new ApplicationPaths(temp.Info.FullName, temp.Info.FullName);
        var bbDown = CreateFile(Path.Combine(paths.ToolsDirectory, "BBDown", "BBDown.exe"));
        var aria2 = CreateFile(Path.Combine(paths.ToolsDirectory, "aria2", "aria2c.exe"));
        var ffmpeg = CreateFile(Path.Combine(paths.ToolsDirectory, "ffmpeg", "ffmpeg.exe"));
        var ffprobe = CreateFile(Path.Combine(paths.ToolsDirectory, "ffmpeg", "ffprobe.exe"));
        var mkvmerge = CreateFile(Path.Combine(temp.Info.FullName, "MKVToolNix", "mkvmerge.exe"));
        var legacyDriveScans = 0;
        var locator = new ToolLocator(paths, () =>
        {
            legacyDriveScans++;
            return ["Z:\\"];
        });

        var tools = locator.LocateFast(new AppSettings { MkvmergePath = mkvmerge });

        Assert.Equal(0, legacyDriveScans);
        Assert.Equal(bbDown, tools.BBDown);
        Assert.Equal(aria2, tools.Aria2c);
        Assert.Equal(ffmpeg, tools.Ffmpeg);
        Assert.Equal(ffprobe, tools.Ffprobe);
        Assert.Equal(mkvmerge, tools.Mkvmerge);

        locator.Locate(new AppSettings { MkvmergePath = mkvmerge });
        Assert.Equal(1, legacyDriveScans);
    }

    [Fact]
    public async Task VersionLookupIsSharedUntilTheExecutableChanges()
    {
        using var temp = new TempDirectory();
        var paths = new ApplicationPaths(temp.Info.FullName, temp.Info.FullName);
        var executable = CreateFile(Path.Combine(temp.Info.FullName, "tool.exe"));
        var reads = 0;
        var locator = new ToolLocator(paths, versionReader: (_, _) =>
            Task.FromResult($"version-{Interlocked.Increment(ref reads)}"));

        var firstPair = await Task.WhenAll(
            locator.GetVersionAsync(executable),
            locator.GetVersionAsync(executable));

        Assert.Equal(["version-1", "version-1"], firstPair);
        Assert.Equal(1, reads);

        File.AppendAllText(executable, "changed");
        var changed = await locator.GetVersionAsync(executable);

        Assert.Equal("version-2", changed);
        Assert.Equal(2, reads);
    }

    [Fact(Timeout = 15_000)]
    public async Task VersionDetectionReadsOutputAndCompletes()
    {
        var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "dotnet.exe"))
            .First(File.Exists);
        using var temp = new TempDirectory();
        var locator = new ToolLocator(new ApplicationPaths(temp.Info.FullName, temp.Info.FullName));

        var version = await locator.GetVersionAsync(dotnet);

        Assert.Matches(@"\d+\.\d+", version);
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();

        public void Dispose()
        {
            try { Info.Delete(true); } catch (IOException) { }
        }
    }
}
