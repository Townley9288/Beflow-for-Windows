using System.Text;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class ProcessAndTaskTests
{
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
            var migratedLog = Path.Combine(paths.LogsDirectory, "old.log");
            File.WriteAllText(migratedLog, "旧日志");
            Assert.Equal("旧日志", manager.ReadSavedLog(Path.Combine(root.FullName, "BBDownForWindows", "Logs", "old.log")));
            Assert.Throws<UnauthorizedAccessException>(() => manager.ReadSavedLog(Path.Combine(root.FullName, "outside.log")));
        }
        finally { root.Delete(true); }
    }
}
