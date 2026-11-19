using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class Aria2ProgressParserTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void FastOrSkippedVideoDoesNotTakeTheAudioProgressWeight()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        Assert.True(parser.TryConsume("开始下载P1音频...", out var start));
        Assert.Equal(83.33, start.Percent!.Value, 2);

        Assert.True(parser.TryConsume("[#abc123 10MiB/20MiB(50%) CN:4 DL:8MiB ETA:1s]", out var audio));
        Assert.Equal(91.67, audio.Percent!.Value, 2);
        Assert.Equal("8MiB/s", audio.Speed);
        Assert.Equal("1s（当前音频）", audio.Eta);
    }

    [Fact]
    public void FastAudioCompletesFromExplicitStageWithoutPeriodicOutput()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#abc123 90MiB/100MiB(90%) CN:4 DL:8MiB ETA:1s]", out _);
        parser.TryConsume("合并视频分片...", out _);
        parser.TryConsume("开始下载P1音频...", out _);
        Assert.True(parser.TryConsume("[2026-09-05 13:07:10.884] - 下载P1完毕", out var final));
        Assert.Equal(100d, final.Percent);
        Assert.Empty(final.Speed);
        Assert.Empty(final.Eta);
        Assert.False(parser.TryConsume("开始合并音视频...", out _));
    }

    [Theory]
    [InlineData(DownloadMode.VideoOnly, "视频", "当前视频")]
    [InlineData(DownloadMode.AudioOnly, "音频", "当前音频")]
    public void SingleTrackReportsItsOwnProgressAndEta(DownloadMode mode, string label, string etaLabel)
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, mode);
        parser.TryConsume($"开始下载P1{label}...", out _);
        Assert.True(parser.TryConsume("[#abc123 40MiB/100MiB(40%) CN:4 DL:8MiB/s ETA:7s]", out var progress));
        Assert.Equal(40d, progress.Percent);
        Assert.Equal("8MiB/s", progress.Speed);
        Assert.Equal($"7s（{etaLabel}）", progress.Eta);
    }

    [Fact]
    public void CarriageReturnBatchUsesTheLatestProgress()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 0, DownloadMode.VideoOnly);
        Assert.True(parser.TryConsume("开始下载P1视频...\r\n[#aaaa11 13MiB/100MiB(13%) CN:4 DL:8MiB ETA:11s]\r    \r[#aaaa11 96MiB/100MiB(96%) CN:1 DL:4MiB ETA:1s]\r", out var progress));
        Assert.Equal(96d, progress.Percent);
        Assert.Equal("4MiB/s", progress.Speed);
    }

    [Fact]
    public void RestartAndResumeKeepProgressInTheSameTrack()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#aaaa11 60MiB/100MiB(60%) CN:4 DL:8MiB ETA:5s]", out _);
        parser.TryConsume("开始下载P1视频...", out var restarting);
        Assert.Equal(0d, restarting.Percent);
        Assert.Empty(restarting.Speed);
        Assert.True(parser.TryConsume("[#bbbb22 60MiB/100MiB(60%) CN:4 DL:0B]", out var resumed));
        Assert.Equal(50d, resumed.Percent);
        Assert.Equal("0B/s", resumed.Speed);
        Assert.Empty(resumed.Eta);
    }

    [Fact]
    public void GidChangeDoesNotMeanVideoHasCompleted()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#aaaa11 50MiB/100MiB(50%) CN:4 DL:8MiB ETA:6s]", out _);
        parser.TryConsume("[#bbbb22 60MiB/100MiB(60%) CN:4 DL:8MiB ETA:5s]", out var progress);
        Assert.Equal(50d, progress.Percent);
    }

    [Fact]
    public void CancellationAndErrorTextDoNotCompleteTransfers()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#aaaa11 50MiB/100MiB(50%) CN:4 DL:8MiB ETA:6s]", out _);
        Assert.False(parser.TryConsume("任务已取消", out _));
        Assert.False(parser.TryConsume("[ERROR] Download aborted.", out _));
        parser.TryConsume("[#aaaa11 50MiB/100MiB(50%) CN:4 DL:0B]", out var progress);
        Assert.Equal(41.67, progress.Percent!.Value, 2);
    }

    [Fact]
    public void UnrelatedTransfersBeforeTheVideoStageAreIgnored()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 20 * MiB, DownloadMode.VideoAndAudio);
        Assert.False(parser.TryConsume("[#aaaa11 1MiB/2MiB(50%) CN:4 DL:8MiB]", out _));
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#bbbb22 50MiB/100MiB(50%) CN:4 DL:8MiB]", out var progress);
        Assert.Equal(41.67, progress.Percent!.Value, 2);
    }

    [Fact]
    public void UnknownTrackWeightsDoNotProduceAFalseOverallPercentage()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 0, DownloadMode.VideoAndAudio);
        parser.TryConsume("开始下载P1视频...", out _);
        parser.TryConsume("[#aaaa11 100MiB/100MiB(100%) CN:4 DL:8MiB]", out var progress);
        Assert.Null(progress.Percent);
        parser.TryConsume("下载P1完毕", out var completed);
        Assert.Equal(100d, completed.Percent);
    }

    [Fact]
    public void MuxedSegmentsDoNotReachOneHundredAtTheEndOfTheFirstSegment()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 0, DownloadMode.VideoOnly);
        parser.TryConsume("开始下载P1视频, 片段(1/2)...", out _);
        parser.TryConsume("[#aaaa11 20MiB/40MiB(50%) CN:4 DL:8MiB]", out var first);
        Assert.Equal(20d, first.Percent);
        parser.TryConsume("合并视频分片...", out var firstCompleted);
        Assert.Equal(40d, firstCompleted.Percent);
        parser.TryConsume("开始下载P1视频, 片段(2/2)...", out _);
        parser.TryConsume("[#bbbb22 30MiB/60MiB(50%) CN:4 DL:8MiB]", out var second);
        Assert.Equal(70d, second.Percent);
        parser.TryConsume("下载P1完毕", out var completed);
        Assert.Equal(100d, completed.Percent);
    }

    [Fact]
    public void MissingSegmentSizeRemainsIndeterminateUntilAllSegmentsFinish()
    {
        var parser = new Aria2ProgressParser(100 * MiB, 0, DownloadMode.VideoOnly);
        parser.TryConsume("开始下载P1视频, 片段(1/2)...", out _);
        parser.TryConsume("合并视频分片...", out _);
        parser.TryConsume("开始下载P1视频, 片段(2/2)...", out _);
        parser.TryConsume("[#bbbb22 30MiB/60MiB(50%) CN:4 DL:8MiB]", out var progress);
        Assert.Null(progress.Percent);
        parser.TryConsume("下载P1完毕", out var completed);
        Assert.Equal(100d, completed.Percent);
    }
}
