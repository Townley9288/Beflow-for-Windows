namespace BBDownForWindows.Core;

public sealed class LegacyMigrationService
{
    private const string LegacyFolderName = "BBDown_1.6.3_20240814_win-x64";
    private readonly ApplicationPaths _paths;
    private readonly IReadOnlyList<string> _candidates;

    public LegacyMigrationService(ApplicationPaths paths, IEnumerable<string?>? candidates = null)
    {
        _paths = paths;
        _candidates = (candidates ?? GetDefaultCandidates(paths))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Migrate()
    {
        _paths.EnsureCreated();
        var copied = new List<string>();
        foreach (var sourceRoot in _candidates)
        {
            if (!Directory.Exists(sourceRoot)) continue;
            CopyIfMissing(Path.Combine(sourceRoot, "config.json"), _paths.SettingsFile, copied);
            CopyIfMissing(Path.Combine(sourceRoot, "history.json"), _paths.HistoryFile, copied);
            CopyIfMissing(Path.Combine(sourceRoot, "BBDown.data"), _paths.WebCredentialFile, copied);
            CopyIfMissing(Path.Combine(sourceRoot, "BBDownTV.data"), _paths.TvCredentialFile, copied);
            CopyIfMissing(Path.Combine(sourceRoot, "Runtime", "BBDown.data"), _paths.WebCredentialFile, copied);
            CopyIfMissing(Path.Combine(sourceRoot, "Runtime", "BBDownTV.data"), _paths.TvCredentialFile, copied);
            CopyLogsIfMissing(Path.Combine(sourceRoot, "Logs"), copied);
        }
        return copied;
    }

    private void CopyLogsIfMissing(string sourceDirectory, ICollection<string> copied)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*.log"))
            CopyIfMissing(source, Path.Combine(_paths.LogsDirectory, Path.GetFileName(source)), copied);
    }

    private static void CopyIfMissing(string source, string destination, ICollection<string> copied)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
        copied.Add(Path.GetFileName(destination));
    }

    private static IEnumerable<string?> GetDefaultCandidates(ApplicationPaths paths)
    {
        if (!paths.Portable) yield return paths.PreviousInstalledDataRoot;
        yield return Directory.GetParent(paths.ApplicationDirectory)?.FullName;
        yield return Environment.CurrentDirectory;
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
            yield return Path.Combine(drive.RootDirectory.FullName, "Software", LegacyFolderName);
    }
}
