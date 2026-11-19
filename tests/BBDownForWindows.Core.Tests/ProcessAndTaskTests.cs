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
        var request = Shell("echo hello");
        var result = await runner.RunAsync(request, lines.Add, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(lines);
    }

    [WindowsFact]
    public async Task ProcessRunnerStreamsPseudoConsoleOutput()
    {
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

    [WindowsFact]
    public async Task PseudoConsoleProcessMayExitBeforeProducingOutput()
    {
        var request = Shell("exit /b 17") with { UsePseudoConsole = true };
        var result = await new ProcessRunner().RunAsync(request, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(17, result.ExitCode);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task AClosedInputPipePreservesTheExitCodeAndOutput()
    {
        var request = Shell(OperatingSystem.IsWindows()
            ? "echo early & exit /b 19"
            : "echo early; exit 19") with { StandardInput = new string('x', 1024 * 1024) };
        var result = await new ProcessRunner().RunAsync(request, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(19, result.ExitCode);
        Assert.Contains("early", result.Output);
    }

    [Theory]
    [InlineData(70000, false)]
    [InlineData(4400000, true)]
    public async Task CompleteStructuredCaptureRetainsTheBeginningOrReportsOverflow(int size, bool overflow)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{\"value\":\"" + new string('a', size) + "\"}");
            var name = Path.GetFileName(file);
            var command = OperatingSystem.IsWindows() ? $"type {name}" : $"cat {name}";
            var request = Shell(command) with
            {
                WorkingDirectory = Path.GetDirectoryName(file)!,
                RequireCompleteOutput = true
            };
            var task = new ProcessRunner().RunAsync(request, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            if (overflow) await Assert.ThrowsAsync<InvalidDataException>(() => task);
            else
            {
                using var json = System.Text.Json.JsonDocument.Parse((await task).Output);
                Assert.Equal(size, json.RootElement.GetProperty("value").GetString()!.Length);
            }
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OutputBufferKeepsOnlyTheMostRecentText()
    {
        var buffer = new ProcessOutputBuffer();
        buffer.Append(new string('x', ProcessOutputBuffer.Capacity * 2));
        buffer.Append("last");
        var output = buffer.ToString();
        Assert.Equal(ProcessOutputBuffer.Capacity, output.Length);
        Assert.EndsWith("last", output);
    }

    [WindowsFact]
    public async Task ProcessRunnerWritesInteractivePseudoConsoleInput()
    {
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

    [WindowsFact]
    public async Task CancellationTerminatesPseudoConsoleProcess()
    {
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
        var request = OperatingSystem.IsWindows()
            ? new ProcessRunRequest(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["127.0.0.1", "-n", "20"], Path.GetTempPath())
            : Shell("exec sleep 20");
        var result = await runner.RunAsync(request, null, cancellation.Token);
        Assert.True(result.Cancelled);
    }

    private static ProcessRunRequest Shell(string command) => OperatingSystem.IsWindows()
        ? new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", command], Path.GetTempPath())
        : new("/bin/sh", ["-c", command], Path.GetTempPath());

    [Fact]
    public async Task CancelledTasksPersistTheirStatusAndCleanupKeepsReferencedLogs()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            paths.EnsureCreated();
            var historyLog = Path.Combine(paths.LogsDirectory, "history.log");
            var queueLog = Path.Combine(paths.LogsDirectory, "queue.log");
            var unusedLog = Path.Combine(paths.LogsDirectory, "unused.log");
            foreach (var file in new[] { historyLog, queueLog, unusedLog })
            {
                await File.WriteAllTextAsync(file, "saved log");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-35));
            }
            await new HistoryStore(paths).AddAsync(new HistoryRecord { LogPath = historyLog });
            await new DownloadQueueStore(paths).SaveAsync(new DownloadQueueDocument
            {
                Items = [new DownloadQueueItem { LogPaths = [queueLog] }]
            });

            var manager = new TaskManager(paths, new ProcessRunner());
            using var cancellation = new CancellationTokenSource();
            var task = await manager.RunExclusiveAsync(TaskKind.Download, true, "cancel", (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);

            Assert.Equal(TaskState.Cancelled, task.State);
            Assert.Contains("任务已取消", manager.ReadSavedLog(task.LogPath));
            Assert.True(File.Exists(historyLog));
            Assert.True(File.Exists(queueLog));
            Assert.False(File.Exists(unusedLog));
        }
        finally { root.Delete(true); }
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

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows ConPTY is required.";
    }
}
