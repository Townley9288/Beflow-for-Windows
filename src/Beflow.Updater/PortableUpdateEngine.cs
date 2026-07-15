using System.IO.Compression;

namespace Beflow.Updater;

public sealed record PortableUpdateOptions(string PackagePath, string TargetDirectory, string WorkingDirectory, string LogPath);

public sealed class PortableUpdateEngine
{
    private readonly Action<string, string> _copyUpdateFile;

    public PortableUpdateEngine(Action<string, string>? copyUpdateFile = null) =>
        _copyUpdateFile = copyUpdateFile ?? ((source, destination) => File.Copy(source, destination, true));

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

        var newFiles = new List<string>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, source);
                if (IsPreserved(relative)) continue;
                var destination = SafeCombine(target, relative);
                var backupFile = SafeCombine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(destination, backupFile, true);
                }
                else
                {
                    newFiles.Add(destination);
                }
                _copyUpdateFile(source, destination);
                Log(options.LogPath, $"已更新 {relative}");
            }
        }
        catch
        {
            Log(options.LogPath, "更新失败，开始回滚。");
            foreach (var file in newFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            foreach (var backupFile in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backup, backupFile);
                var destination = SafeCombine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backupFile, destination, true);
            }
            Log(options.LogPath, "回滚完成。");
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

    private static void Log(string path, string message) =>
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
}
