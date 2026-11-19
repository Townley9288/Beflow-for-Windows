using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BBDownForWindows.Core;

public sealed class ToolLocator : IToolLocator
{
    private readonly ApplicationPaths _paths;
    private readonly Func<string, CancellationToken, Task<string>> _versionReader;
    private readonly ConcurrentDictionary<VersionCacheKey, Lazy<Task<string>>> _versionCache = new();
    private (VersionCacheKey Executable, VersionCacheKey Manifest, bool Valid)? _bundledBBDown;
    private readonly object _validationLock = new();

    public ToolLocator(ApplicationPaths paths, Func<string, CancellationToken, Task<string>>? versionReader = null)
    {
        _paths = paths;
        _versionReader = versionReader ?? ReadVersionAsync;
    }

    public ToolPaths Locate(AppSettings settings) => new()
    {
        BBDown = FindBundledBBDown(),
        Aria2c = FindConfiguredOrBundled(settings.Aria2cPath, Path.Combine(_paths.ToolsDirectory, "aria2", "aria2c.exe")),
        Ffmpeg = FirstExisting(Path.Combine(_paths.ToolsDirectory, "ffmpeg", "ffmpeg.exe")),
        Ffprobe = FirstExisting(Path.Combine(_paths.ToolsDirectory, "ffmpeg", "ffprobe.exe")),
        Mkvmerge = FindConfiguredOrBundled(settings.MkvmergePath,
            FindInProgramFiles(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86))
    };

    public ToolPaths LocateFast(AppSettings settings) => Locate(settings);

    public Task<ToolPaths> LocateAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.Run(() => Locate(settings), cancellationToken);

    private string FindBundledBBDown()
    {
        var path = Path.Combine(_paths.ToolsDirectory, "BBDown", "BBDown.exe");
        var manifest = path + ".sha256";
        if (!File.Exists(path) || !File.Exists(manifest)) return string.Empty;
        var executableKey = VersionCacheKey.From(path);
        var manifestKey = VersionCacheKey.From(manifest);
        lock (_validationLock)
        {
            if (_bundledBBDown is { } cached && cached.Executable == executableKey && cached.Manifest == manifestKey)
                return cached.Valid ? path : string.Empty;

            using var executable = File.OpenRead(path);
            var expected = File.ReadAllText(manifest).Trim();
            var valid = expected.Length == 64 && Convert.ToHexString(SHA256.HashData(executable))
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
            _bundledBBDown = (executableKey, manifestKey, valid);
            return valid ? path : string.Empty;
        }
    }

    public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return Task.FromResult("未找到");
        var file = new FileInfo(Path.GetFullPath(executable));
        var key = VersionCacheKey.From(file.FullName);
        var versionTask = _versionCache.GetOrAdd(key,
            _ => new Lazy<Task<string>>(
                () => Task.Run(() => _versionReader(file.FullName, CancellationToken.None), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return cancellationToken.CanBeCanceled ? versionTask.WaitAsync(cancellationToken) : versionTask;
    }

    private static async Task<string> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(executable);
        var argument = fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || fileName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)
            ? "-version" : fileName.Equals("BBDown.exe", StringComparison.OrdinalIgnoreCase) ? "--help" : "--version";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await new ProcessRunner().RunAsync(
                new ProcessRunRequest(executable, [argument], Path.GetDirectoryName(executable)!), null, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) return "检测超时";
            var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.FirstOrDefault(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault()
                ?? fileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return $"检测失败：{exception.Message}";
        }
    }

    private static string FindInProgramFiles(params Environment.SpecialFolder[] folders) =>
        FirstExisting(folders.Select(Environment.GetFolderPath).Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "MKVToolNix", "mkvmerge.exe")).ToArray());

    private static string FindConfiguredOrBundled(string configured, string bundled) =>
        string.IsNullOrWhiteSpace(configured) ? FirstExisting(bundled) : FirstExisting(configured);

    private static string FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(candidate => Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) ?? string.Empty;

    private readonly record struct VersionCacheKey(string Path, long Length, long LastWriteTicks)
    {
        public static VersionCacheKey From(string path)
        {
            var file = new FileInfo(path);
            return new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }
}
