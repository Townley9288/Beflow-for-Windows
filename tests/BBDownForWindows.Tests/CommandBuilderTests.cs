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
        var ariaArguments = arguments[arguments.IndexOf("--aria2c-args") + 1];
        Assert.Contains("-x8 -s12 -j4 -k 10M", ariaArguments);
        Assert.Contains("--file-allocation=none --disk-cache=64M", ariaArguments);
    }

    [Theory]
    [InlineData(35, 4)]
    [InlineData(171, 8)]
    [InlineData(1400, 16)]
    public void AutoTunesAriaConnectionsByLargestStreamSize(int sizeMb, int expectedConnections)
    {
        var request = new DownloadRequest
        {
            Url = "BV1", UseAria2c = true, Aria2AutoTune = true,
            Aria2MaxConnection = 16, Aria2Split = 16, Aria2MaxConcurrentDownloads = 16, Aria2MinSplitSize = 5
        };

        var result = Aria2TuningPolicy.Apply(request, sizeMb * 1024L * 1024L);

        Assert.True(result.Applied);
        Assert.Equal(expectedConnections, request.Aria2MaxConnection);
        Assert.Equal(expectedConnections, request.Aria2Split);
        Assert.Equal(4, request.Aria2MaxConcurrentDownloads);
        Assert.Contains($"连接 {expectedConnections}", result.Description);
    }

    [Fact]
    public void AutoTuneTreatsManualValuesAsUpperLimits()
    {
        var request = new DownloadRequest
        {
            Url = "BV1", Aria2AutoTune = true,
            Aria2MaxConnection = 2, Aria2Split = 3, Aria2MaxConcurrentDownloads = 2
        };

        Aria2TuningPolicy.Apply(request, 2L * 1024 * 1024 * 1024);

        Assert.Equal(2, request.Aria2MaxConnection);
        Assert.Equal(3, request.Aria2Split);
        Assert.Equal(2, request.Aria2MaxConcurrentDownloads);
    }

    [Fact]
    public void DisabledAutoTuneKeepsManualAriaArguments()
    {
        var request = new DownloadRequest
        {
            Url = "BV1", UseAria2c = true, Aria2AutoTune = false,
            Aria2MaxConnection = 12, Aria2Split = 10, Aria2MaxConcurrentDownloads = 6, Aria2MinSplitSize = 8
        };

        var tuning = Aria2TuningPolicy.Apply(request, 200L * 1024 * 1024);
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(request, new ToolPaths { Aria2c = @"C:\aria2c.exe" });
        var ariaArguments = arguments[arguments.IndexOf("--aria2c-args") + 1];

        Assert.False(tuning.Applied);
        Assert.Equal("-x12 -s10 -j6 -k 8M", ariaArguments);
    }

    [Fact]
    public void SeasonAlwaysSelectsAllPages()
    {
        var arguments = BBDownCommandBuilder.BuildDownloadArguments(new DownloadRequest { Url = "ss1", Pages = "3", Season = true }, new ToolPaths());
        var pageIndex = arguments.IndexOf("-p");
        Assert.Equal("ALL", arguments[pageIndex + 1]);
    }
}
