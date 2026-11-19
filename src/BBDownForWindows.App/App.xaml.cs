using System.Diagnostics;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App;

public partial class App : Application
{
    private MainWindow? _window;
    private Window? _startupErrorWindow;
    private static ApplicationPaths? _paths;
    private string? _settingsWarning;
    private static readonly object LogSync = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            LogException(args.Exception);
            if (_window is not null && args.Exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                args.Handled = true;
                _window.DispatcherQueue.TryEnqueue(() => _window.ShowOperationError(args.Exception.Message));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) LogException(exception);
        };
    }

    public AppServices Services { get; private set; } = null!;
    public MainWindow MainWindow => _window ?? throw new InvalidOperationException("主窗口尚未创建");

    internal static async Task RunUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception)
        {
            LogException(exception);
            if (Current is App app && app._window is { } window)
                window.ShowOperationError(exception.Message);
        }
    }

    internal static string CrashLogDirectory =>
        _paths?.LogsDirectory ?? new ApplicationPaths().LogsDirectory;

    internal static void LogException(Exception exception)
    {
        try
        {
            lock (LogSync)
            {
                var logs = CrashLogDirectory;
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
            }
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError(logError.Message);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var paths = _paths = new ApplicationPaths();
            paths.EnsureCreated();
            new LegacyMigrationService(paths).Migrate();
            Services = new AppServices(paths, exception =>
            {
                var warning = exception.Message + "\n本次运行使用默认设置。修复配置文件后可恢复读写。";
                if (Interlocked.Exchange(ref _settingsWarning, warning) == warning) return;
                LogException(exception);
                if (_window is { } window)
                    window.DispatcherQueue.TryEnqueue(() => window.ShowOperationError(warning));
            });
            await Services.Theme.InitializeAsync();
            _window = new MainWindow();
            _window.Activate();
            _window.ApplyInitialSize();
            if (_settingsWarning is { } startupWarning) _window.ShowOperationError(startupWarning);
            var pageArgument = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (pageArgument is not null) _window.Navigate(pageArgument[7..].ToLowerInvariant());
        }
        catch (Exception exception)
        {
            LogException(exception);
            if (_window is { } window)
            {
                window.ShowOperationError(exception.Message);
                return;
            }

            _startupErrorWindow = new Window { Title = "Beflow 无法启动" };
            _startupErrorWindow.Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Text = $"Beflow 无法启动。\n\n{exception.Message}\n\n诊断日志：{CrashLogDirectory}"
                }
            };
            _startupErrorWindow.Activate();
        }
    }
}
