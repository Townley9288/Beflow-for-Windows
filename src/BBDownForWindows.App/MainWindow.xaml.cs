using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App;

public sealed partial class MainWindow : Window
{
    private bool _forceClosing;
    public MainWindow()
    {
        InitializeComponent();
        Title = "BBDown for Windows";
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        Navigate("download");
        AppWindow.Closing += AppWindow_Closing;
    }

    public void ApplyInitialSize()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var width = Math.Min(display.WorkArea.Width, Math.Min(1680, Math.Max(1200, display.WorkArea.Width - 40)));
        var height = Math.Min(display.WorkArea.Height, Math.Min(1000, Math.Max(760, display.WorkArea.Height - 40)));
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
