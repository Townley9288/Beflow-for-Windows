using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DualAudioTests
{
    [Fact]
    public void MkvmergeArgumentsSetLabelsLanguagesDefaultAndDelay()
    {
        var request = new DualAudioRequest
        {
            PrimaryLabel = "国语", SecondaryLabel = "粤语", PrimaryLanguage = "zh", SecondaryLanguage = "yue",
            SecondaryIsDefault = true, SecondaryAudioDelayMs = 350
        };
        var arguments = DualAudioService.BuildMkvmergeArguments("main.mp4", "audio.m4a", "out.mkv", request);
        Assert.Contains("1:国语", arguments);
        Assert.Contains("0:粤语", arguments);
        Assert.Contains("0:350", arguments);
        Assert.Contains("0:0,1:0,0:1", arguments);
    }

    [Fact]
    public void SeparateModePairsByEpisodePrefix()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var primary = Directory.CreateDirectory(Path.Combine(root.FullName, "主版本"));
            var secondary = Directory.CreateDirectory(Path.Combine(root.FullName, "副音轨"));
            File.WriteAllText(Path.Combine(primary.FullName, "[P01]第一集.mp4"), "");
            File.WriteAllText(Path.Combine(secondary.FullName, "[P01]粤语.m4a"), "");
            Assert.Single(DualAudioService.FindPairs(primary.FullName, secondary.FullName, DualAudioSourceMode.Separate));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void RejectsOutOfRangeDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAudioService.BuildMkvmergeArguments("a", "b", "c", new DualAudioRequest { SecondaryAudioDelayMs = 10001 }));
    }

    [Fact]
    public void DownloadRequestsUseIndependentAudioFormats()
    {
        var request = new DualAudioRequest
        {
            SourceMode = DualAudioSourceMode.Separate,
            PrimaryUrl = "primary",
            SecondaryUrl = "secondary",
            PrimaryAudioCodec = "FLAC",
            SecondaryAudioCodec = "E-AC-3"
        };

        var (primary, secondary) = DualAudioService.BuildDownloadRequests(request, "1,2", "1,2", "primary-dir", "secondary-dir");

        Assert.Equal("FLAC", primary.AudioCodec);
        Assert.Equal(DownloadMode.VideoAndAudio, primary.DownloadMode);
        Assert.Equal("primary", primary.Url);
        Assert.Equal("E-AC-3", secondary.AudioCodec);
        Assert.Equal(DownloadMode.AudioOnly, secondary.DownloadMode);
        Assert.Equal("secondary", secondary.Url);
    }
}
