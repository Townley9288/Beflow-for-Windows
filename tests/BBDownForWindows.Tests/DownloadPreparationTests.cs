using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadPreparationTests
{
    [Theory]
    [InlineData(DownloadMode.VideoAndAudio, 4, 7)]
    [InlineData(DownloadMode.VideoOnly, 4, -1)]
    [InlineData(DownloadMode.AudioOnly, -1, 7)]
    public async Task MatchesLiveSdrStreamEvenWhenHdrComesFirst(DownloadMode mode, int expectedVideo, int expectedAudio)
    {
        DownloadPreparationReply? actual = null;
        var result = await DownloadPreparationSession.RunAsync(async (name, token) =>
        {
            actual = await Exchange(name, Catalog, token);
            return new ProcessResult(0, "done", false);
        }, episode =>
        {
            var desired = new EpisodeStreamSelection { PageNumber = 3,
                Video = new("4K·SDR增强", "3840x2160", "HEVC", 7000, true), Audio = new("M4A", 192, true) };
            var selected = StreamSelectionPolicy.Resolve(episode, desired, new DownloadRequest { Url = "ss1", DownloadMode = mode });
            return new(1, selected.Video?.Index ?? -1, selected.Audio?.Index ?? -1, "节目/第3集.SDR", "--all-proxy=");
        }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedVideo, actual!.VideoIndex);
        Assert.Equal(expectedAudio, actual.AudioIndex);
        Assert.Equal("节目/第3集.SDR", actual.RelativeOutputPath);
    }

    [Fact(Timeout = 5000)]
    public async Task MissingManualSelectionStopsTheWaitingProcess()
    {
        var stopped = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadPreparationSession.RunAsync(async (name, token) =>
        {
            try { await Exchange(name, Catalog, token); return new ProcessResult(0, "", false); }
            finally { stopped = true; }
        }, episode =>
        {
            StreamSelectionPolicy.Resolve(episode, new EpisodeStreamSelection { PageNumber = 3, Video = new("杜比视界", "3840x2160", "HEVC", 7000, true) },
                new DownloadRequest { Url = "ss1", DownloadMode = DownloadMode.VideoOnly });
            throw new Exception("Selection should have failed");
        }, CancellationToken.None));
        Assert.Contains("手动选择的视频流已不可用", error.Message);
        Assert.True(stopped);
    }

    [Fact(Timeout = 5000)]
    public async Task EarlyProcessFailureDoesNotWaitForAPipeConnection()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadPreparationSession.RunAsync(
            (_, _) => Task.FromResult(new ProcessResult(1, "请求被拒绝", false)),
            _ => throw new Exception("Unexpected preparation"), CancellationToken.None));
        Assert.Contains("请求被拒绝", error.Message);
    }

    [Fact(Timeout = 5000)]
    public async Task CancellationStopsAProcessBeforeItConnects()
    {
        using var cancellation = new CancellationTokenSource(100);
        var stopped = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadPreparationSession.RunAsync(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return new ProcessResult(0, "", false); }
            finally { stopped = true; }
        }, _ => throw new Exception("Unexpected preparation"), cancellation.Token));
        Assert.True(stopped);
    }

    [Fact(Timeout = 5000)]
    public async Task CancellationStopsAConnectedProcessBeforeItSendsCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        var stopped = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadPreparationSession.RunAsync(async (name, token) =>
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(token); cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return new ProcessResult(0, "", false); }
            finally { stopped = true; }
        }, _ => throw new Exception("Unexpected preparation"), cancellation.Token));
        Assert.True(stopped);
    }

    [Fact]
    public async Task MuxedCatalogUsesTheCurrentCombinedStream()
    {
        const string catalog = "P3: [33] [第三集] [24m]\n开始解析P3:\n共计1条流(共有3个分段).\n0. [1080P 高码率] [AVC] [~4109 kbps] [555.27 MB]\n";
        var result = await DownloadPreparationSession.RunAsync(async (name, token) =>
        {
            var reply = await Exchange(name, catalog, token);
            Assert.Equal(0, reply.VideoIndex);
            Assert.Equal(-1, reply.AudioIndex);
            return new ProcessResult(0, "", false);
        }, episode =>
        {
            Assert.True(episode.IsMuxedStream);
            Assert.Equal("1920x1080", Assert.Single(episode.VideoStreams).Resolution);
            return new(1, 0, -1, "第3集", "");
        }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<DownloadPreparationReply> Exchange(string name, string catalog, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { Protocol = 1, Page = 3, Output = catalog }).AsMemory(), token);
        return JsonSerializer.Deserialize<DownloadPreparationReply>((await reader.ReadLineAsync(token))!)!;
    }

    private const string Catalog = """
        P3: [33] [第三集] [24m]
        开始解析P3:
        共计2条视频流.
        0. [HDR 真彩] [1920x1080] [HEVC] [25] [6000 kbps] [~100 MB]
        4. [4K·SDR增强] [3840x2160] [HEVC] [] [7000 kbps] [~200 MB]
        共计1条音频流.
        7. [M4A] [192 kbps] [~20 MB]
        """;
}
