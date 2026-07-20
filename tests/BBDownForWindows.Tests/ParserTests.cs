using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class ParserTests
{
    private const string InfoOutput = """
        [2026-07-04] - 视频标题: 无穷之路4：一带一路
        [2026-07-04] - P1: [111] [第一集] [25m00s]
        [2026-07-04] - P2: [222] [第二集] [25m00s]
        [2026-07-04] - 开始解析P1: 111... (1 of 2)
        [2026-07-04] - 共计2条视频流.
          0. [4K 超清] [3840x2160] [AVC] [25] [19744 kbps] [~3.46 GB]
          1. [1080P 高清] [1920x1080] [HEVC] [25] [3326 kbps] [~597 MB]
        [2026-07-04] - 共计2条音频流.
          0. [M4A] [134 kbps] [~24 MB]
          1. [E-AC-3] [448 kbps] [~80 MB]
        [2026-07-04] - 开始解析P2: 222... (2 of 2)
        [2026-07-04] - 共计1条视频流.
          0. [1080P 高清] [1920x1080] [HEVC] [25] [5000 kbps] [~900 MB]
        [2026-07-04] - 共计1条音频流.
          0. [M4A] [132 kbps] [~23 MB]
        """;

    [Fact]
    public void ParsesTitlePagesAndStreams()
    {
        var info = BBDownParser.ParseInfo(InfoOutput);
        Assert.Equal("无穷之路4：一带一路", info.Title);
        Assert.Equal(2, info.Pages.Count);
        Assert.NotEmpty(info.VideoStreams);
        Assert.NotEmpty(info.AudioStreams);
    }

    [Fact]
    public void KeepsStreamsAssignedToTheirPages()
    {
        var video = BBDownParser.ParsePageVideoStreams(InfoOutput);
        var audio = BBDownParser.ParsePageAudioStreams(InfoOutput);
        Assert.Equal(2, video[1].Count);
        Assert.Single(video[2]);
        Assert.Equal("E-AC-3", audio[1][1].Codec);
    }

    [Fact]
    public void SelectsPreferredCodecAndFallsBackPerPage()
    {
        var audio = BBDownParser.ParsePageAudioStreams(InfoOutput);
        var selected = BBDownParser.SelectPreferredAudioIndices(audio, [1, 2], "E-AC-3");
        Assert.Equal(1, selected.Indices[1]);
        Assert.Equal(0, selected.Indices[2]);
        Assert.Equal([2], selected.FallbackPages);
    }

    [Fact]
    public void ResolutionIsPrimaryAndCodecOnlyBreaksTies()
    {
        var streams = new[]
        {
            new VideoStreamInfo(0, "1080P", "1920x1080", 1920, 1080, "AVC", "25", "8000 kbps", 8000, "1 GB"),
            new VideoStreamInfo(1, "4K", "3840x2160", 3840, 2160, "HEVC", "25", "4000 kbps", 4000, "2 GB")
        };
        Assert.Equal(1, BBDownParser.SelectResolutionFirst(streams, "AVC")!.Index);
    }

    [Fact]
    public void InteractiveInputIncludesVideoAndAudioForEachPage()
    {
        Assert.Equal("0\n1\n0\n2\n", BBDownParser.BuildInteractiveInput([1, 2], new Dictionary<int, int> { [1] = 1, [2] = 2 }, false));
    }

    [Fact]
    public void ParsesLegacyMuxedSegmentedStream()
    {
        const string output = """
            开始解析P22: 123... (22 of 48)
            共计1条流(共有3个分段).
              0. [1080P 高码率] [AVC] [~4109 kbps] [555.27 MB]
            任务完成
            """;

        var stream = Assert.Single(BBDownParser.ParsePageVideoStreams(output)[22]);

        Assert.Equal(0, stream.Index);
        Assert.Equal("1080P 高码率", stream.Quality);
        Assert.Equal("1920x1080", stream.Resolution);
        Assert.Equal("AVC", stream.Codec);
        Assert.Equal(4109, stream.BitrateKbps);
        Assert.Equal("555.27 MB", stream.Size);
        Assert.Contains(22, BBDownParser.ParseMuxedStreamPages(output));
    }
}
