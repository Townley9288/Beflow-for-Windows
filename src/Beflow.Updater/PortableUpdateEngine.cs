using System.IO.Compression;

namespace Beflow.Updater;

public sealed record PortableUpdateOptions(string PackagePath, string TargetDirectory, string WorkingDirectory, string LogPath);

public sealed class PortableUpdateEngine
{
    private const string ManifestFileName = "Beflow.files.txt";
    private readonly Action<string, string> _copyUpdateFile;
    private readonly Action<string, string> _copyRestoreFile;

    public PortableUpdateEngine(Action<string, string>? copyUpdateFile = null, Action<string, string>? copyRestoreFile = null)
    {
        _copyUpdateFile = copyUpdateFile ?? ((source, destination) => File.Copy(source, destination, true));
        _copyRestoreFile = copyRestoreFile ?? ((source, destination) => File.Copy(source, destination, true));
    }

    public void Apply(PortableUpdateOptions options)
    {
        var package = Path.GetFullPath(options.PackagePath);
        var target = Path.GetFullPath(options.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var working = Path.GetFullPath(options.WorkingDirectory);
        if (!File.Exists(package)) throw new FileNotFoundException("找不到便携版更新包。", package);
        if (!File.Exists(Path.Combine(target, "portable.flag"))) throw new InvalidOperationException("目标目录不是 Beflow 便携版目录。");

        Directory.CreateDirectory(working);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogPath))!);
        var staging = Path.Combine(working, "staging");
        var backup = Path.Combine(working, "backup");
        DeleteDirectory(staging);
        DeleteDirectory(backup);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);

        Log(options.LogPath, $"开始解压 {package}");
        ZipFile.ExtractToDirectory(package, staging, true);
        if (!File.Exists(Path.Combine(staging, "Beflow.exe"))) throw new InvalidDataException("更新包缺少 Beflow.exe。");

        var sourceFiles = EnumerateFiles(staging)
            .Select(source => (Source: source, Relative: Path.GetRelativePath(staging, source)))
            .Where(item => !IsPreserved(item.Relative))
            .ToList();
        var sourceSet = sourceFiles.Select(item => item.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredFiles = LoadManifest(staging) ?? throw new InvalidDataException($"更新包缺少 {ManifestFileName}。");
        if (!sourceSet.SetEquals(declaredFiles)) throw new InvalidDataException($"更新包的 {ManifestFileName} 与实际文件不一致。");
        var previousFiles = LoadManifest(target) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var obsoleteFiles = previousFiles
            .Except(sourceSet, StringComparer.OrdinalIgnoreCase)
            .Where(relative => !IsPreserved(relative))
            .Select(relative => (File: SafeCombine(target, relative), Relative: relative))
            .Where(item => File.Exists(item.File))
            .ToList();
        var newFiles = sourceFiles
            .Select(item => SafeCombine(target, item.Relative))
            .Where(destination => !File.Exists(destination))
            .ToList();
        var filesToBackup = sourceFiles
            .Select(item => (File: SafeCombine(target, item.Relative), item.Relative))
            .Where(item => File.Exists(item.File))
            .Concat(obsoleteFiles)
            .DistinctBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mutationStarted = false;
        try
        {
            foreach (var item in filesToBackup)
            {
                var backupFile = SafeCombine(backup, item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                File.Copy(item.File, backupFile, true);
            }

            mutationStarted = true;
            foreach (var item in sourceFiles)
            {
                var destination = SafeCombine(target, item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                _copyUpdateFile(item.Source, destination);
                Log(options.LogPath, $"已更新 {item.Relative}");
            }
            foreach (var item in obsoleteFiles)
            {
                File.Delete(item.File);
                Log(options.LogPath, $"已移除旧文件 {item.Relative}");
            }
            PruneEmptyParents(target, obsoleteFiles.Select(item => Path.GetDirectoryName(item.File)!));
        }
        catch (Exception updateException)
        {
            if (!mutationStarted)
            {
                Log(options.LogPath, "更新失败，尚未修改目标目录。");
                try { DeleteDirectory(backup); } catch { }
                throw;
            }

            Log(options.LogPath, "更新失败，开始回滚。");
            var rollbackErrors = new List<Exception>();
            foreach (var file in newFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { rollbackErrors.Add(exception); }
            }
            foreach (var backupFile in EnumerateFiles(backup))
            {
                try
                {
                    var relative = Path.GetRelativePath(backup, backupFile);
                    var destination = SafeCombine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    _copyRestoreFile(backupFile, destination);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { rollbackErrors.Add(exception); }
            }
            if (rollbackErrors.Count > 0)
            {
                Log(options.LogPath, $"回滚未完整完成，备份保留在 {backup}");
                throw new AggregateException("更新失败且未能完整回滚，备份文件已保留。", new[] { updateException }.Concat(rollbackErrors));
            }
            Log(options.LogPath, "回滚完成。");
            try { DeleteDirectory(backup); } catch { }
            throw;
        }
        finally
        {
            DeleteDirectory(staging);
        }

        try { DeleteDirectory(backup); } catch { }
        Log(options.LogPath, "便携版更新完成。");
    }

    private static bool IsPreserved(string relativePath) =>
        relativePath.Equals("portable.flag", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("Data" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafeCombine(string root, string relativePath)
    {
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootPrefix, relativePath));
        if (!result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包包含越界路径。");
        return result;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    });

    private static HashSet<string>? LoadManifest(string root)
    {
        var path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path)) return null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var relative = line.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            _ = SafeCombine(root, relative);
            if (!result.Add(relative)) throw new InvalidDataException($"{ManifestFileName} 包含重复文件：{relative}");
        }
        return result;
    }

    private static void PruneEmptyParents(string root, IEnumerable<string> directories)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var start in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetFullPath(start);
            while (!directory.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
            {
                var relative = Path.GetRelativePath(rootPath, directory);
                if (IsPreserved(relative) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory)!;
            }
        }
    }

    private static void Log(string path, string message) =>
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
}
