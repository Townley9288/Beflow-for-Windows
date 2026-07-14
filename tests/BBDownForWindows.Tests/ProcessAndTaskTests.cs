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
            Assert.Throws<UnauthorizedAccessException>(() => manager.ReadSavedLog(Path.Combine(root.FullName, "outside.log")));
        }
        finally { root.Delete(true); }
    }
}
