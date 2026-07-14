using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class CommandBuilderTests
{
    [Fact]
    public void UsesQualityNamesAndSkipFlags()
    {
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(new DownloadRequest { Url = "BV1", Quality = "4K", Subtitle = false, Cover = false }, new ToolPaths());
        Assert.Contains("4K 超清", arguments);
        Assert.Contains("--skip-subtitle", arguments);
        Assert.Contains("--skip-cover", arguments);
        Assert.DoesNotContain("--interactive", arguments);
    }

    [Fact]
    public void MapsAudioAscendingAndReadableAriaSettings()
    {
        var request = new DownloadRequest
        {
            Url = "BV1", AudioBitratePriority = AudioBitratePriority.Lowest, UseAria2c = true,
            Aria2MaxConnection = 8, Aria2Split = 12, Aria2MaxConcurrentDownloads = 4, Aria2MinSplitSize = 10
        };
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(request, new ToolPaths { Aria2c = @"C:\aria2c.exe" });
        Assert.Contains("--audio-ascending", arguments);
        Assert.Contains("--use-aria2c", arguments);
        Assert.Contains("-x8 -s12 -j4 -k 10M", arguments);
    }

    [Fact]
    public void SeasonAlwaysSelectsAllPages()
    {
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(new DownloadRequest { Url = "ss1", Pages = "3", Season = true }, new ToolPaths());
        var pageIndex = arguments.IndexOf("-p");
        Assert.Equal("ALL", arguments[pageIndex + 1]);
    }
}
