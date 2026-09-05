using System.Text;
using System.Threading.Channels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class ProcessAndTaskTests
{
    [Theory]
    [InlineData("utf-8", 1)]
    [InlineData("utf-8", 3)]
    [InlineData("GB18030", 1)]
    [InlineData("GB18030", 4096)]
    public async Task RedirectedReaderHandlesCrLfAndSplitMultibyteCharacters(string encodingName, int chunkSize)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new ChunkedStream(Encoding.GetEncoding(encodingName).GetBytes("开始下载视频\r\n13%\r96%\n尾部"), chunkSize);
        var lines = new List<string>();

        await ProcessRunner.ReadLinesAsync(stream, lines.Add);

        Assert.Equal(["开始下载视频\n", "13%\n", "96%\n", "尾部"], lines);
    }

    [Fact(Timeout = 5_000)]
    public async Task RedirectedReaderPublishesCarriageReturnBeforeTheProcessFinishes()
    {
        using var stream = new LiveStream();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        var reader = ProcessRunner.ReadLinesAsync(stream, line => { lines.Add(line); first.TrySetResult(line); });
        try
        {
            stream.Feed("13%\r");
            Assert.Equal("13%\n", await first.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(reader.IsCompleted);
            stream.Feed("\n96%\r");
        }
        finally
        {
            stream.Complete();
            await reader;
        }
        Assert.Equal(["13%\n", "96%\n"], lines);
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class LiveStream : MemoryStream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        public void Feed(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var bytes = await _chunks.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }

    [Fact]
    public void OutputDecoderHandlesUtf8AndGbkChinese()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("下载完成\r\n", OutputDecoder.Decode(Encoding.UTF8.GetBytes("下载完成\r\n")));
        Assert.Equal("下载完成\r\n", OutputDecoder.Decode(Encoding.GetEncoding(936).GetBytes("下载完成\r\n")));
    }

    [Fact]
    public async Task ProcessRunnerStreamsOutput()
    {
        var runner = new ProcessRunner();
        var lines = new List<string>();
        var result = await runner.RunAsync(new ProcessRunRequest(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "echo hello"], Path.GetTempPath()), lines.Add, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public async Task ProcessRunnerStreamsPseudoConsoleOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new ProcessRunner();
        var output = new List<string>();
        var result = await runner.RunAsync(new ProcessRunRequest(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "echo pseudo-console & ping 127.0.0.1 -n 2 >nul"],
            Path.GetTempPath(),
            UsePseudoConsole: true), output.Add, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pseudo-console", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task ProcessRunnerWritesInteractivePseudoConsoleInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessRunRequest(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/q"],
            Path.GetTempPath(),
            "echo pseudo-input\nexit\n",
            UsePseudoConsole: true), null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pseudo-input", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PseudoConsoleOutputProcessorReconstructsInPlaceProgress()
    {
        var processor = new PseudoConsoleOutputProcessor(120, 30);
        Assert.Empty(processor.Feed("\u001b[24;"));
        var initial = processor.Feed("1H[----------------------------------------]   0% |");
        var update = processor.Feed("\u001b[24;1H[##########################--------------]  67% / - 5.62 MB/s");

        Assert.Contains(initial, item => item.Contains("0%", StringComparison.Ordinal));
        Assert.Contains(update, item => item.Contains("67%", StringComparison.Ordinal) && item.Contains("5.62 MB/s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationTerminatesPseudoConsoleProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var result = await runner.RunAsync(new ProcessRunRequest(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "ping 127.0.0.1 -n 20 >nul"],
            Path.GetTempPath(),
            UsePseudoConsole: true), null, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task CancellationTerminatesOwnedProcess()
    {
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var result = await runner.RunAsync(new ProcessRunRequest(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["127.0.0.1", "-n", "20"], Path.GetTempPath()), null, cancellation.Token);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task TaskManagerPersistsAndSafelyReadsLog()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            var manager = new TaskManager(paths, new ProcessRunner());
            var snapshot = await manager.RunExclusiveAsync(TaskKind.Download, true, "download", (context, _) =>
            {
                context.AppendLog("测试日志\n");
                return Task.CompletedTask;
            });
            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.Contains("测试日志", manager.ReadSavedLog(snapshot.LogPath));
            Assert.Contains("测试日志", await manager.ReadSavedLogLinesAsync(snapshot.LogPath));
            var migratedLog = Path.Combine(paths.LogsDirectory, "old.log");
            File.WriteAllText(migratedLog, "旧日志");
            Assert.Equal("旧日志", manager.ReadSavedLog(Path.Combine(root.FullName, "BBDownForWindows", "Logs", "old.log")));
            Assert.Equal(["旧日志"], await manager.ReadSavedLogLinesAsync(Path.Combine(root.FullName, "BBDownForWindows", "Logs", "old.log")));
            Assert.Throws<UnauthorizedAccessException>(() => manager.ReadSavedLog(Path.Combine(root.FullName, "outside.log")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.ReadSavedLogLinesAsync(Path.Combine(root.FullName, "outside.log")));
        }
        finally { root.Delete(true); }
    }
}
