using BBDownForWindows.Core;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                Services.Paths.EnsureCreated();
                File.AppendAllText(Path.Combine(Services.Paths.LogsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:O} {args.Exception}\n");
            }
            catch { }
        };
    }

    public AppServices Services { get; private set; } = null!;
    public MainWindow MainWindow => _window ?? throw new InvalidOperationException("主窗口尚未创建");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = new ApplicationPaths();
        paths.EnsureCreated();
        new LegacyMigrationService(paths).Migrate();
        Services = new AppServices(paths);
        _window = new MainWindow();
        _window.Activate();
        _window.ApplyInitialSize();
        var pageArgument = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArgument is not null) _window.Navigate(pageArgument[7..].ToLowerInvariant());
    }
}
