using System.Reflection;

namespace BBDownForWindows.App;

public static class AppVersion
{
    public static Version Current { get; } = ReadCurrent();
    public static string Display => $"v{Core.UpdateService.FormatVersion(Current)}";

    private static Version ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        if (Version.TryParse(informational, out var parsed)) return Normalize(parsed);
        return Normalize(assembly.GetName().Version ?? new Version(1, 0, 0));
    }

    private static Version Normalize(Version version) => version.Revision >= 0
        ? new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), version.Revision)
        : new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
}
