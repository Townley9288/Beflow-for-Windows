using System.Diagnostics;

namespace BBDownForWindows.Core;

public sealed class ToolLocator(ApplicationPaths paths) : IToolLocator
{
    public ToolPaths Locate(AppSettings settings)
    {
        var legacyRoots = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => Path.Combine(drive.RootDirectory.FullName, "Software", "BBDown_1.6.3_20240814_win-x64"))
            .ToArray();
        var bbDownCandidates = new List<string?>
        {
            Path.Combine(paths.ToolsDirectory, "BBDown", "BBDown.exe"),
            Path.Combine(paths.ApplicationDirectory, "BBDown.exe")
        };
        bbDownCandidates.AddRange(legacyRoots.Select(root => Path.Combine(root, "BBDown.exe")));
        bbDownCandidates.Add(FindOnPath("BBDown.exe"));

        var aria2Candidates = new List<string?>
        {
            settings.Aria2cPath,
            Path.Combine(paths.ToolsDirectory, "aria2", "aria2c.exe")
        };
        aria2Candidates.AddRange(legacyRoots.Select(root => Path.Combine(root, "tools", "aria2", "aria2-1.37.0-win-64bit-build1", "aria2c.exe")));
        aria2Candidates.Add(FindOnPath("aria2c.exe"));

        return new ToolPaths
        {
            BBDown = FirstExisting(bbDownCandidates.ToArray()),
            Aria2c = FirstExisting(aria2Candidates.ToArray()),
            Ffmpeg = FirstExisting(
                Path.Combine(paths.ToolsDirectory, "ffmpeg", "ffmpeg.exe"),
                FindOnPath("ffmpeg.exe")),
            Mkvmerge = FindMkvmerge(settings.MkvmergePath)
        };
    }

    public async Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return "未找到";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var fileName = Path.GetFileName(executable);
        startInfo.ArgumentList.Add(fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ? "-version" : fileName.Equals("BBDown.exe", StringComparison.OrdinalIgnoreCase) ? "--help" : "--version");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return "无法启动";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = await standardOutput;
                var error = await standardError;
                var lines = string.Concat(output, "\n", error)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.FirstOrDefault(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
                    ?? lines.FirstOrDefault()
                    ?? fileName;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return "检测超时";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return $"检测失败：{exception.Message}";
        }
    }

    private static string FindMkvmerge(string configured)
    {
        var candidates = new List<string?>
        {
            configured,
            FindOnPath("mkvmerge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MKVToolNix", "mkvmerge.exe")
        };
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Software", "MKVToolNix", "mkvmerge.exe"));
        return FirstExisting(candidates.ToArray());
    }

    private static string FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) ?? string.Empty;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return string.Empty;
    }
}
