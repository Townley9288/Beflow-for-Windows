using System.Collections.Concurrent;
using System.Diagnostics;

namespace BBDownForWindows.Core;

public sealed class ToolLocator : IToolLocator
{
    private const string LegacyFolderName = "BBDown_1.6.3_20240814_win-x64";
    private readonly ApplicationPaths _paths;
    private readonly Func<IEnumerable<string>> _fixedDriveRootProvider;
    private readonly Func<string, CancellationToken, Task<string>> _versionReader;
    private readonly ConcurrentDictionary<VersionCacheKey, Lazy<Task<string>>> _versionCache = new();

    public ToolLocator(
        ApplicationPaths paths,
        Func<IEnumerable<string>>? fixedDriveRootProvider = null,
        Func<string, CancellationToken, Task<string>>? versionReader = null)
    {
        _paths = paths;
        _fixedDriveRootProvider = fixedDriveRootProvider ?? DiscoverFixedDriveRoots;
        _versionReader = versionReader ?? ReadVersionAsync;
    }

    public ToolPaths Locate(AppSettings settings) => LocateCore(settings, includeLegacyDrives: true);

    public ToolPaths LocateFast(AppSettings settings) => LocateCore(settings, includeLegacyDrives: false);

    private ToolPaths LocateCore(AppSettings settings, bool includeLegacyDrives)
    {
        var driveRoots = includeLegacyDrives ? _fixedDriveRootProvider().ToArray() : [];
        var legacyRoots = driveRoots.Select(root => Path.Combine(root, "Software", LegacyFolderName)).ToArray();
        var bbDownCandidates = new List<string?>
        {
            Path.Combine(_paths.ToolsDirectory, "BBDown", "BBDown.exe"),
            Path.Combine(_paths.ApplicationDirectory, "BBDown.exe")
        };
        if (includeLegacyDrives)
        {
            bbDownCandidates.AddRange(legacyRoots.Select(root => Path.Combine(root, "BBDown.exe")));
            bbDownCandidates.Add(FindOnPath("BBDown.exe"));
        }

        var aria2Candidates = new List<string?>
        {
            settings.Aria2cPath,
            Path.Combine(_paths.ToolsDirectory, "aria2", "aria2c.exe")
        };
        if (includeLegacyDrives)
        {
            aria2Candidates.AddRange(legacyRoots.Select(root => Path.Combine(root, "tools", "aria2", "aria2-1.37.0-win-64bit-build1", "aria2c.exe")));
            aria2Candidates.Add(FindOnPath("aria2c.exe"));
        }

        return new ToolPaths
        {
            BBDown = FirstExisting(bbDownCandidates.ToArray()),
            Aria2c = FirstExisting(aria2Candidates.ToArray()),
            Ffmpeg = FirstExisting(
                Path.Combine(_paths.ToolsDirectory, "ffmpeg", "ffmpeg.exe"),
                includeLegacyDrives ? FindOnPath("ffmpeg.exe") : null),
            Ffprobe = FirstExisting(
                Path.Combine(_paths.ToolsDirectory, "ffmpeg", "ffprobe.exe"),
                includeLegacyDrives ? FindOnPath("ffprobe.exe") : null),
            Mkvmerge = FindMkvmerge(settings.MkvmergePath, driveRoots)
        };
    }

    public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return Task.FromResult("未找到");
        var file = new FileInfo(Path.GetFullPath(executable));
        var key = new VersionCacheKey(file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks);
        var versionTask = _versionCache.GetOrAdd(key,
            _ => new Lazy<Task<string>>(
                () => Task.Run(() => _versionReader(file.FullName, CancellationToken.None), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return cancellationToken.CanBeCanceled ? versionTask.WaitAsync(cancellationToken) : versionTask;
    }

    private static async Task<string> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var fileName = Path.GetFileName(executable);
        startInfo.ArgumentList.Add(fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || fileName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) ? "-version" : fileName.Equals("BBDown.exe", StringComparison.OrdinalIgnoreCase) ? "--help" : "--version");
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

    private static string FindMkvmerge(string configured, IReadOnlyList<string> driveRoots)
    {
        var candidates = new List<string?>
        {
            configured,
            FindOnPath("mkvmerge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MKVToolNix", "mkvmerge.exe")
        };
        foreach (var root in driveRoots)
            candidates.Add(Path.Combine(root, "Software", "MKVToolNix", "mkvmerge.exe"));
        return FirstExisting(candidates.ToArray());
    }

    private static IEnumerable<string> DiscoverFixedDriveRoots() =>
        DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName);

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

    private readonly record struct VersionCacheKey(string Path, long Length, long LastWriteTicks);
}
