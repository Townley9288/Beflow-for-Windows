using System.IO.Compression;
using Beflow.Updater;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class PortableUpdaterTests
{
    [Fact]
    public void UpdatesProgramFilesAndPreservesPortableData()
    {
        using var root = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root.Info.FullName, "便携版"));
        File.WriteAllText(Path.Combine(target.FullName, "portable.flag"), string.Empty);
        File.WriteAllText(Path.Combine(target.FullName, "Beflow.exe"), "old");
        Directory.CreateDirectory(Path.Combine(target.FullName, "Data"));
        File.WriteAllText(Path.Combine(target.FullName, "Data", "config.json"), "user-data");
        var package = CreatePackage(root.Info.FullName, new Dictionary<string, string>
        {
            ["Beflow.exe"] = "new",
            ["portable.flag"] = "package-flag",
            ["Data/config.json"] = "package-data",
            ["Assets/icon.txt"] = "asset"
        });

        new PortableUpdateEngine().Apply(new PortableUpdateOptions(package, target.FullName, Path.Combine(root.Info.FullName, "work"), Path.Combine(root.Info.FullName, "update.log")));

        Assert.Equal("new", File.ReadAllText(Path.Combine(target.FullName, "Beflow.exe")));
        Assert.Equal("user-data", File.ReadAllText(Path.Combine(target.FullName, "Data", "config.json")));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(target.FullName, "portable.flag")));
        Assert.Equal("asset", File.ReadAllText(Path.Combine(target.FullName, "Assets", "icon.txt")));
    }

    [Fact]
    public void RollsBackWhenReplacementFails()
    {
        using var root = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root.Info.FullName, "portable"));
        File.WriteAllText(Path.Combine(target.FullName, "portable.flag"), string.Empty);
        File.WriteAllText(Path.Combine(target.FullName, "Beflow.exe"), "old-exe");
        File.WriteAllText(Path.Combine(target.FullName, "second.txt"), "old-second");
        var package = CreatePackage(root.Info.FullName, new Dictionary<string, string> { ["Beflow.exe"] = "new-exe", ["second.txt"] = "new-second" });
        var copies = 0;
        var engine = new PortableUpdateEngine((source, destination) =>
        {
            if (++copies == 2) throw new IOException("simulated copy failure");
            File.Copy(source, destination, true);
        });

        Assert.Throws<IOException>(() => engine.Apply(new PortableUpdateOptions(package, target.FullName, Path.Combine(root.Info.FullName, "work"), Path.Combine(root.Info.FullName, "update.log"))));
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(target.FullName, "Beflow.exe")));
        Assert.Equal("old-second", File.ReadAllText(Path.Combine(target.FullName, "second.txt")));
    }

    [Fact]
    public void RejectsDirectoryWithoutPortableFlag()
    {
        using var root = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root.Info.FullName, "not-portable"));
        var package = CreatePackage(root.Info.FullName, new Dictionary<string, string> { ["Beflow.exe"] = "new" });
        Assert.Throws<InvalidOperationException>(() => new PortableUpdateEngine().Apply(new PortableUpdateOptions(package, target.FullName, Path.Combine(root.Info.FullName, "work"), Path.Combine(root.Info.FullName, "update.log"))));
    }

    private static string CreatePackage(string root, IReadOnlyDictionary<string, string> files)
    {
        var package = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return package;
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory();
        public void Dispose() { try { Info.Delete(true); } catch { } }
    }
}
