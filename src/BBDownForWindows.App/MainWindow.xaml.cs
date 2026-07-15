using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BBDownForWindows.Core;

namespace BBDownForWindows.App;

public sealed partial class MainWindow : Window
{
    private bool _forceClosing;
    public MainWindow()
    {
        InitializeComponent();
        Title = "Beflow for Windows";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        Navigate("download");
        AppWindow.Closing += AppWindow_Closing;
    }

    public void ApplyInitialSize()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        const int preferredHeight = 1200;
        const int preferredWidth = preferredHeight * 16 / 9;
        var availableWidth = Math.Max(1, display.WorkArea.Width - 40);
        var availableHeight = Math.Max(1, display.WorkArea.Height - 40);
        var scale = Math.Min(1d, Math.Min(availableWidth / (double)preferredWidth, availableHeight / (double)preferredHeight));
        var height = Math.Max(1, (int)Math.Floor(preferredHeight * scale));
        var width = Math.Max(1, (int)Math.Floor(height * 16d / 9d));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(display.WorkArea.X + (display.WorkArea.Width - width) / 2, display.WorkArea.Y + (display.WorkArea.Height - height) / 2));
    }

    private void RootNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag) Navigate(tag);
    }

    public void Navigate(string tag, object? parameter = null)
    {
        var page = tag switch
        {
            "dual" => typeof(Pages.DualAudioPage),
            "history" => typeof(Pages.HistoryPage),
            "settings" => typeof(Pages.SettingsPage),
            "about" => typeof(Pages.AboutPage),
            _ => typeof(Pages.DownloadPage)
        };
        ContentFrame.Navigate(page, parameter);
        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
            if (Equals(item.Tag, tag)) RootNavigation.SelectedItem = item;
    }

    public void RestoreHistory(Core.HistoryRecord record) => Navigate(record.TaskType is Core.TaskKind.DualAudioMux or Core.TaskKind.DualAudioRemux ? "dual" : "download", record);

    public void ShowUpdateAvailable(UpdateRelease release)
    {
        UpdateInfoBar.Message = $"v{UpdateService.FormatVersion(release.Version)} 已发布，可以在关于页查看并安装。";
        UpdateInfoBar.IsOpen = true;
    }

    public void RequestUpdateShutdown()
    {
        _forceClosing = true;
        Close();
    }

    private async void RootNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Loaded -= RootNavigation_Loaded;
        await ((App)Application.Current).Services.UpdateCoordinator.CheckOnStartupAsync();
    }

    private void UpdateInfoBar_Click(object sender, RoutedEventArgs e) => Navigate("about");

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClosing) return;
        var manager = ((App)Application.Current).Services.TaskManager;
        if (manager.ActiveTask?.State == Core.TaskState.Running)
        {
            args.Cancel = true;
            await manager.CancelActiveAsync();
            _forceClosing = true;
            Close();
        }
    }
}
