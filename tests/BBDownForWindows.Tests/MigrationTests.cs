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
}
