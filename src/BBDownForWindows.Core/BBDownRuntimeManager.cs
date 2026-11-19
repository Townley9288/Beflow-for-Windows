namespace BBDownForWindows.Core;

/// <summary>
/// Keeps BBDown beside its credential files. BBDown 1.6.3 resolves
/// BBDown.data and BBDownTV.data from the executable directory rather than
/// from the process working directory.
/// </summary>
public sealed class BBDownRuntimeManager(ApplicationPaths paths)
{
    private static readonly object Sync = new();

    public string PrepareExecutable(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("随包的 Beflow BBDown.exe 缺失或校验失败，请重新安装完整发布包。", sourcePath);

        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(paths.RuntimeBBDownExecutable);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return destination;

        lock (Sync)
        {
            paths.EnsureCreated();
            if (IsCurrent(source, destination)) return destination;

            var temporary = Path.Combine(
                paths.RuntimeDirectory,
                $"BBDown.exe.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return destination;
        }
    }

    private static bool IsCurrent(string source, string destination)
    {
        if (!File.Exists(destination)) return false;
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        return sourceInfo.Length == destinationInfo.Length
            && sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc;
    }
}
