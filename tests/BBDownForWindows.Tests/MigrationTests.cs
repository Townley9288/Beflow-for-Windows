using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void CopiesLegacyFilesWithoutDeletingSources()
    {
        var root = Directory.CreateTempSubdirectory();
        var legacy = Directory.CreateDirectory(Path.Combine(root.FullName, "legacy"));
        var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
        try
        {
            File.WriteAllText(Path.Combine(legacy.FullName, "config.json"), "{}");
            File.WriteAllText(Path.Combine(legacy.FullName, "BBDown.data"), "secret");
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            var copied = new LegacyMigrationService(paths, [legacy.FullName]).Migrate();
            Assert.Contains("config.json", copied);
            Assert.True(File.Exists(paths.WebCredentialFile));
            Assert.True(File.Exists(Path.Combine(legacy.FullName, "BBDown.data")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void MigratesPreviousWinUiDataLayout()
    {
        var root = Directory.CreateTempSubdirectory();
        var previous = Directory.CreateDirectory(Path.Combine(root.FullName, "BBDownForWindows"));
        var runtime = Directory.CreateDirectory(Path.Combine(previous.FullName, "Runtime"));
        var logs = Directory.CreateDirectory(Path.Combine(previous.FullName, "Logs"));
        var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
        try
        {
            File.WriteAllText(Path.Combine(previous.FullName, "config.json"), "{}");
            File.WriteAllText(Path.Combine(previous.FullName, "history.json"), "[]");
            File.WriteAllText(Path.Combine(runtime.FullName, "BBDown.data"), "web");
            File.WriteAllText(Path.Combine(runtime.FullName, "BBDownTV.data"), "tv");
            File.WriteAllText(Path.Combine(logs.FullName, "old.log"), "log");
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));

            new LegacyMigrationService(paths, [previous.FullName]).Migrate();

            Assert.True(File.Exists(paths.SettingsFile));
            Assert.True(File.Exists(paths.HistoryFile));
            Assert.True(File.Exists(paths.WebCredentialFile));
            Assert.True(File.Exists(paths.TvCredentialFile));
            Assert.True(File.Exists(Path.Combine(paths.LogsDirectory, "old.log")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void CompletedMigrationDoesNotReimportDeletedCredentialsOrLogs()
    {
        var root = Directory.CreateTempSubdirectory();
        var legacy = Directory.CreateDirectory(Path.Combine(root.FullName, "legacy"));
        var logs = Directory.CreateDirectory(Path.Combine(legacy.FullName, "Logs"));
        var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
        try
        {
            File.WriteAllText(Path.Combine(legacy.FullName, "BBDown.data"), "secret");
            File.WriteAllText(Path.Combine(logs.FullName, "old.log"), "log");
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            var migration = new LegacyMigrationService(paths, [legacy.FullName]);
            migration.Migrate();
            File.Delete(paths.WebCredentialFile);
            File.Delete(Path.Combine(paths.LogsDirectory, "old.log"));

            var copied = migration.Migrate();

            Assert.Empty(copied);
            Assert.False(File.Exists(paths.WebCredentialFile));
            Assert.False(File.Exists(Path.Combine(paths.LogsDirectory, "old.log")));
            Assert.True(File.Exists(paths.MigrationMarkerFile));
        }
        finally { root.Delete(true); }
    }
}
