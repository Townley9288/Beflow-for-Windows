using System.Net;
using System.Text.Json;
using BBDown.Core;
using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class TvStreamMetadataTests
{
    [Theory]
    [InlineData("""{"width":3840,"height":2160,"frame_rate":"25/1"}""", "3840x2160", "25/1")]
    [InlineData("""{"width":"852","height":"480","frame_rate":"30000/1001"}""", "852x480", "30000/1001")]
    [InlineData("""{"width":1920,"height":1080,"frame_rate":25}""", "1920x1080", "25")]
    [InlineData("""{"width":3840,"height":2160}""", "3840x2160", "")]
    [InlineData("""{"frame_rate":"25"}""", "", "25")]
    [InlineData("""{"width":0,"height":-1,"frame_rate":"0/0"}""", "", "")]
    [InlineData("""{}""", "", "")]
    public void PreservesApiMetadataWithoutGuessingDimensions(string json, string resolution, string fps)
    {
        using var document = JsonDocument.Parse(json);
        var metadata = BeflowTvStreamMetadata.ReadApi(document.RootElement);
        Assert.Equal(resolution, metadata.Resolution);
        Assert.Equal(fps, metadata.FrameRate);
        Assert.Equal(resolution.Length > 0 && fps.Length > 0, BeflowTvStreamMetadata.IsComplete(metadata.Resolution, metadata.FrameRate));
    }

    [Theory]
    [InlineData("""{"streams":[{"width":3840,"height":2160,"r_frame_rate":"25/1"}]}""", "3840x2160", "25/1")]
    [InlineData("""{"streams":[{"width":1920,"height":1080,"r_frame_rate":"30000/1001"}]}""", "1920x1080", "30000/1001")]
    public void ReadsActualHeaderDimensionsAndFrameRate(string json, string resolution, string fps)
    {
        var metadata = BeflowTvStreamMetadata.ReadProbe(json);
        Assert.Equal(resolution, metadata.Resolution);
        Assert.Equal(fps, metadata.FrameRate);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"streams":[]}""")]
    [InlineData("""{"streams":[{},{}]}""")]
    [InlineData("""{"streams":[{"width":3840,"height":2160,"r_frame_rate":"0/0"}]}""")]
    [InlineData("""{"streams":[{"width":3840,"height":0,"r_frame_rate":"25/1"}]}""")]
    public void IncompleteProbeNeverInventsParameters(string json) =>
        Assert.Throws<InvalidOperationException>(() => BeflowTvStreamMetadata.ReadProbe(json));

    [Fact]
    public async Task HeaderRequestIsBoundedEvenIfServerIgnoresRangeAndDoesNotCarryCredentials()
    {
        var source = new CountingStream(2 * BeflowTvStreamMetadata.MaximumHeaderBytes);
        var handler = new Handler(request =>
        {
            Assert.Equal("bytes=0-1048575", request.Headers.Range!.ToString());
            Assert.Equal("test-agent", request.Headers.UserAgent.ToString());
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.DoesNotContain("access_key", request.RequestUri!.Query);
            return new(HttpStatusCode.OK) { Content = new StreamContent(source) };
        });
        using var http = new HttpClient(handler);

        var bytes = await BeflowTvStreamMetadata.ReadHeaderAsync(http, new("https://media.test/header"), "test-agent", default);

        Assert.Equal(BeflowTvStreamMetadata.MaximumHeaderBytes, bytes.Length);
        Assert.Equal(bytes.Length, source.BytesRead);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://media.test/header?platform=android_tv_yst", false)]
    [InlineData("https://media.test/header?platform=android", false)]
    [InlineData("https://media.test/header", true)]
    public async Task HeaderRefererMatchesTheExistingMediaDownloader(string url, bool expectReferer)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(expectReferer, request.Headers.Referrer is not null);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });
        using var http = new HttpClient(handler);
        await BeflowTvStreamMetadata.ReadHeaderAsync(http, new(url), "Mozilla/5.0", default);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Found)]
    public async Task HttpErrorsStopWithoutTryingAnotherAddress(HttpStatusCode status)
    {
        var handler = new Handler(_ => new(status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => BeflowTvStreamMetadata.ReadHeaderAsync(http, new("https://media.test/header"), "test-agent", default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmptyOrWrongRangeIsRejected()
    {
        using var empty = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BeflowTvStreamMetadata.ReadHeaderAsync(empty, new("https://media.test/header"), "test-agent", default));
        using var wrong = new HttpClient(new Handler(_ =>
        {
            var content = new ByteArrayContent([1, 2]);
            content.Headers.ContentRange = new(10, 11, 100);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BeflowTvStreamMetadata.ReadHeaderAsync(wrong, new("https://media.test/header"), "test-agent", default));
    }

    [Fact]
    public async Task HeaderReadHonorsCancellation()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeflowTvStreamMetadata.ReadHeaderAsync(http, new("https://media.test/header"), "test-agent", cancel.Token));
    }

    [Fact]
    public void ProbeUsesSelectedHostAndPreservesMediaPathAndSignature()
    {
        var uri = BeflowTvStreamMetadata.ProbeUri("https://original.test:123/video.m4s?signature=test", "selected.test");
        Assert.Equal("selected.test", uri.Host);
        Assert.Equal("/video.m4s?signature=test", uri.PathAndQuery);
        Assert.Equal(443, uri.Port);
        Assert.Equal("original.test", BeflowTvStreamMetadata.ProbeUri("https://original.test/video.m4s", "").Host);
    }

    [Fact]
    public void TvMetadataFlowsThroughParserUiAndQueueSnapshotWithoutChangingSignature()
    {
        const string output = """
            视频标题: 大理寺日志
            P1: [178579852] [1 洛阳寻兄] [00m00s]
            开始解析P1: 625160382... (1 of 1)
            共计3条视频流.
              0. [HDR 真彩] [3840x2160] [HEVC] [25] [6115 kbps] [~779.15 MB]
              1. [4K 超高清] [3840x2160] [AVC] [25/1] [5608 kbps] [~714.56 MB]
              2. [4K 超高清] [3840x2160] [HEVC] [25/1] [3150 kbps] [~401.40 MB]
            共计1条音频流.
              0. [M4A] [323 kbps] [~42.11 MB]
            任务完成
            """;
        var parser = new StreamingDownloadParser(DownloadParseMode.Current, null);
        foreach (var line in output.Split('\n')) parser.Consume(line + "\n");
        parser.Complete();
        var episode = Assert.Single(parser.Episodes);
        DownloadSessionPolicy.Remember(episode, "TV");
        Assert.Equal(DownloadEpisodeParseState.Ready, episode.State);
        Assert.Equal(3, episode.VideoStreams.Count);
        Assert.Equal("25/1", episode.VideoStreams[1].Fps);
        var row = new DownloadEpisodeViewModel(episode);
        row.ApplyRule(new("4K 超高清", "AVC", "auto", AudioBitratePriority.Highest), DownloadMode.VideoAndAudio);
        var selection = QueueSnapshot.Copy(row.BuildSelection());
        Assert.Equal("就绪 · TV", row.StatusText);
        Assert.Equal("TV · 4K 超高清 · 3840×2160 · 5608 kbps", row.SelectedQualityLabel);
        Assert.Equal("TV", selection.ApiMode);
        Assert.Equal("3840x2160", selection.Video!.Resolution);
        Assert.Empty(selection.FallbackReason);
        var restored = new DownloadEpisodeViewModel(QueueSnapshot.Copy(episode));
        restored.ApplyRestored(selection, DownloadMode.VideoAndAudio);
        Assert.Equal(selection.Video, restored.BuildSelection().Video);
        var decision = StreamSelectionPolicy.Resolve(episode, selection, new() { Url = "test", ApiMode = "TV" });
        Assert.Equal(1, decision.Video!.Index);
        Assert.Equal(0, decision.Audio!.Index);
        Assert.Empty(decision.FallbackReason);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class CountingStream(int length) : MemoryStream(new byte[length])
    {
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = base.Read(buffer.Span); BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
