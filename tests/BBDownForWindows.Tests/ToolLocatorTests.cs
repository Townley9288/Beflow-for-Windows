using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class ToolLocatorTests
{
    [Fact]
    public void PrefersBundledToolsAndConfiguredMkvmerge()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            Directory.CreateDirectory(Path.Combine(paths.ToolsDirectory, "BBDown"));
            Directory.CreateDirectory(Path.Combine(paths.ToolsDirectory, "aria2"));
            Directory.CreateDirectory(Path.Combine(paths.ToolsDirectory, "ffmpeg"));
            var bbdown = Path.Combine(paths.ToolsDirectory, "BBDown", "BBDown.exe");
            var aria = Path.Combine(paths.ToolsDirectory, "aria2", "aria2c.exe");
            var ffmpeg = Path.Combine(paths.ToolsDirectory, "ffmpeg", "ffmpeg.exe");
            var ffprobe = Path.Combine(paths.ToolsDirectory, "ffmpeg", "ffprobe.exe");
            var mkv = Path.Combine(root.FullName, "mkvmerge.exe");
            foreach (var file in new[] { bbdown, aria, ffmpeg, ffprobe, mkv }) File.WriteAllText(file, string.Empty);
            var tools = new ToolLocator(paths).Locate(new AppSettings { MkvmergePath = mkv });
            Assert.Equal(bbdown, tools.BBDown);
            Assert.Equal(aria, tools.Aria2c);
            Assert.Equal(ffmpeg, tools.Ffmpeg);
            Assert.Equal(ffprobe, tools.Ffprobe);
            Assert.Equal(mkv, tools.Mkvmerge);
        }
        finally { root.Delete(true); }
    }

    [Fact(Timeout = 15_000)]
    public async Task VersionDetectionReadsOutputAndCompletes()
    {
        var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "dotnet.exe"))
            .First(File.Exists);
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var locator = new ToolLocator(new ApplicationPaths(root.FullName, root.FullName));
            var version = await locator.GetVersionAsync(dotnet);
            Assert.Matches(@"\d+\.\d+", version);
        }
        finally { root.Delete(true); }
    }
}
