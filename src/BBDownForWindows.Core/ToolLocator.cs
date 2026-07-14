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
        using var process = Process.Start(startInfo);
        if (process is null) return "无法启动";
        var output = await process.StandardOutput.ReadLineAsync(cancellationToken) ?? await process.StandardError.ReadLineAsync(cancellationToken) ?? string.Empty;
        await process.WaitForExitAsync(cancellationToken);
        if (fileName.Equals("BBDown.exe", StringComparison.OrdinalIgnoreCase) && !output.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            var all = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            output = all.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.Contains("version", StringComparison.OrdinalIgnoreCase)) ?? output;
        }
        return string.IsNullOrWhiteSpace(output) ? Path.GetFileName(executable) : output.Trim();
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
