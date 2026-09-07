using System.Diagnostics;
using BBDown;

BBDownAria2c.Executable = args.Length > 3 ? args[3] : "";
await BeflowResumableTransfer.DownloadAsync(args[0], args[1], new BBDownDownloadUtil.DownloadConfig
{
    MultiThread = args[2] == "multi", UseAria2c = args[2] == "aria",
    Aria2cArgs = "--all-proxy= --continue=true --always-resume=true --allow-overwrite=false --auto-save-interval=1 --file-allocation=none --auto-file-renaming=false -x4 -s4 -k1M"
});
Console.WriteLine("TRANSFER_VERIFIED");

namespace BBDown
{
    internal class ProgressBar(object? task) : IDisposable
    {
        public void Report(double percent, long bytes) { }
        public void Dispose() { }
    }
    internal static class BBDownDownloadUtil
    {
        public sealed class DownloadConfig
        { public bool ForceHttp; public bool MultiThread; public bool UseAria2c; public string Aria2cArgs = ""; public object? RelatedTask; }
    }
    internal static class BBDownAria2c
    {
        public static string Executable = "";
        public static async Task DownloadFileByAria2cAsync(string url, string path, string extraArgs)
        {
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"{extraArgs} --console-log-level=error --summary-interval=0 \"{url}\" -d \"{Path.GetDirectoryName(path)}\" -o \"{Path.GetFileName(path)}\"" };
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException($"aria2c exit {process.ExitCode}");
        }
    }
    internal static class BBDownMuxer
    {
        public static void MergeFLV(string[] files, string output) => throw new NotSupportedException("Use the actual BBDown executable for mux integration");
    }
}
namespace BBDown.Core { internal static class Config { public const string COOKIE = ""; } }
