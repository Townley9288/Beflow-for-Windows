namespace BBDownForWindows.Core;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? applicationDirectory = null, string? localApplicationData = null)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        LocalApplicationData = Path.GetFullPath(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Portable = File.Exists(Path.Combine(ApplicationDirectory, "portable.flag"));
        DataRoot = Portable
            ? Path.Combine(ApplicationDirectory, "Data")
            : Path.Combine(LocalApplicationData, "Beflow");
        PreviousInstalledDataRoot = Path.Combine(LocalApplicationData, "BBDownForWindows");
        RuntimeDirectory = Path.Combine(DataRoot, "Runtime");
        LogsDirectory = Path.Combine(DataRoot, "Logs");
        SettingsFile = Path.Combine(DataRoot, "config.json");
        HistoryFile = Path.Combine(DataRoot, "history.json");
        ToolsDirectory = Path.Combine(ApplicationDirectory, "tools");
    }

    public string ApplicationDirectory { get; }
    public string LocalApplicationData { get; }
    public string DataRoot { get; }
    public string PreviousInstalledDataRoot { get; }
    public string RuntimeDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public string HistoryFile { get; }
    public string ToolsDirectory { get; }
    public bool Portable { get; }
    public string QrCodeFile => Path.Combine(RuntimeDirectory, "qrcode.png");
    public string WebCredentialFile => Path.Combine(RuntimeDirectory, "BBDown.data");
    public string TvCredentialFile => Path.Combine(RuntimeDirectory, "BBDownTV.data");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
