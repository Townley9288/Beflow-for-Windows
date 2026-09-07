using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using BBDownForWindows.Core;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App;

public sealed partial class MainWindow : Window
{
    private bool _forceClosing;
    private bool _closingDialogOpen;
    private bool _navigationInProgress;
    private string _currentNavigationTag = string.Empty;
    private bool _clipboardMonitoring;
    private bool _dragLinkMonitoring = true;
    private bool _isWindowActive;
    private string _lastClipboardInput = string.Empty;
    private string _pendingClipboardInput = string.Empty;
    private bool _applyingClipboardInput;
    public MainWindow()
    {
        InitializeComponent();
        Title = "Beflow";
        var theme = ((App)Application.Current).Services.Theme;
        theme.Attach(WindowRoot, AppWindow);
        theme.Changed += Theme_Changed;
        UpdateThemeUi();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        Navigate("download");
        Activated += MainWindow_Activated;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
        ((App)Application.Current).Services.TaskConsole.PropertyChanged += TaskConsole_PropertyChanged;
    }

    public void ApplyInitialSize()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        const int preferredWidth = 1920;
        const int preferredHeight = 1440;
        var availableWidth = Math.Max(1, display.WorkArea.Width - 40);
        var availableHeight = Math.Max(1, display.WorkArea.Height - 40);
        var scale = Math.Min(1d, Math.Min(availableWidth / (double)preferredWidth, availableHeight / (double)preferredHeight));
        var width = Math.Max(1, (int)Math.Floor(preferredWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(preferredHeight * scale));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(display.WorkArea.X + (display.WorkArea.Width - width) / 2, display.WorkArea.Y + (display.WorkArea.Height - height) / 2));
    }

    private void RootNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string tag) return;
        if (tag == "theme")
        {
            ThemeNavigationItem.ContextFlyout?.ShowAt(ThemeNavigationItem);
            return;
        }
        Navigate(tag);
    }

    public async void Navigate(string tag, object? parameter = null)
    {
        if (_navigationInProgress) return;
        var navigationParameter = tag == "rename-history" && parameter is null
            ? new Pages.HistoryNavigationContext(Pages.HistorySection.Renames)
            : parameter;
        var resolvedTag = tag switch
        {
            "rename-history" => "history",
            "queue" or "dual" or "rename" or "rename-templates" or "history" or "history-detail" or "settings" or "about" or "download" => tag,
            _ => "download"
        };
        if (parameter is null && string.Equals(resolvedTag, _currentNavigationTag, StringComparison.Ordinal))
        {
            SelectNavigationItem(resolvedTag);
            return;
        }

        _navigationInProgress = true;
        try
        {
            if (ContentFrame.Content is IQueueEditorPage editor && !await editor.ConfirmLeaveQueueEditAsync())
            {
                SelectNavigationItem(_currentNavigationTag); return;
            }
            if (ContentFrame.Content is Pages.RenameTemplatesPage templatePage && resolvedTag != "rename-templates" &&
                !await templatePage.ConfirmDiscardChangesAsync())
            {
                SelectNavigationItem(_currentNavigationTag);
                return;
            }

            var page = resolvedTag switch
            {
                "queue" => typeof(Pages.DownloadQueuePage),
                "dual" => typeof(Pages.DualAudioPage),
                "rename" => typeof(Pages.RenamePage),
                "rename-templates" => typeof(Pages.RenameTemplatesPage),
                "history" => typeof(Pages.HistoryPage),
                "history-detail" => typeof(Pages.DownloadHistoryDetailPage),
                "settings" => typeof(Pages.SettingsPage),
                "about" => typeof(Pages.AboutPage),
                _ => typeof(Pages.DownloadPage)
            };
            if (!ContentFrame.Navigate(page, navigationParameter)) return;
            _currentNavigationTag = resolvedTag;
            SelectNavigationItem(resolvedTag == "history-detail" ? "history" : resolvedTag);
        }
        catch (Exception exception)
        {
            await new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "无法切换页面", Content = exception.Message, CloseButtonText = "关闭" }.ShowAsync();
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

    private void SelectNavigationItem(string tag)
    {
        var items = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>();
        foreach (var item in items)
            if (Equals(item.Tag, tag)) RootNavigation.SelectedItem = item;
    }

    public void RestoreHistory(Core.HistoryRecord record) => Navigate(record.TaskType is Core.TaskKind.DualAudioMux or Core.TaskKind.DualAudioRemux ? "dual" : "download", record);

    public void ConfigureClipboardMonitoring(bool enabled)
    {
        if (_clipboardMonitoring == enabled) return;
        _clipboardMonitoring = enabled;
        if (enabled)
        {
            Clipboard.ContentChanged += Clipboard_ContentChanged;
            return;
        }

        Clipboard.ContentChanged -= Clipboard_ContentChanged;
        _lastClipboardInput = string.Empty;
        _pendingClipboardInput = string.Empty;
    }

    public bool DragLinkMonitoringEnabled => _dragLinkMonitoring;
    public void ConfigureDragLinkMonitoring(bool enabled) => _dragLinkMonitoring = enabled;

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
        try { await ((App)Application.Current).Services.DownloadQueue.InitializeAsync(); }
        catch (Exception exception)
        {
            await new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "下载队列无法读取", Content = exception.Message + "\n原队列文件已保留。", CloseButtonText = "关闭" }.ShowAsync();
        }
        var settings = await ((App)Application.Current).Services.Settings.LoadAsync();
        ConfigureClipboardMonitoring(settings.MonitorClipboard);
        ConfigureDragLinkMonitoring(settings.MonitorDragLinks);
        await ((App)Application.Current).Services.UpdateCoordinator.CheckOnStartupAsync();
    }

    private void UpdateInfoBar_Click(object sender, RoutedEventArgs e) => Navigate("about");

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        if (!ShouldInspectClipboardChange(_clipboardMonitoring, _isWindowActive)) return;
        DispatcherQueue.TryEnqueue(async () => await InspectClipboardAsync());
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) =>
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;

    internal static bool ShouldInspectClipboardChange(bool monitoringEnabled, bool windowActive) =>
        monitoringEnabled && !windowActive;

    internal static bool IsDuplicateClipboardInput(string? currentInput, string? incomingInput) =>
        BilibiliInputParser.TryExtract(currentInput, out var current)
        && BilibiliInputParser.TryExtract(incomingInput, out var incoming)
        && current.Equals(incoming, StringComparison.OrdinalIgnoreCase);

    private async Task InspectClipboardAsync()
    {
        try
        {
            if (!_clipboardMonitoring) return;
            var data = Clipboard.GetContent();
            if (!BilibiliDataTransfer.MayContainInput(data))
            {
                _lastClipboardInput = string.Empty;
                _pendingClipboardInput = string.Empty;
                return;
            }
            var inputs = await BilibiliDataTransfer.ExtractInputsAsync(data);
            var input = inputs.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(input))
            {
                _lastClipboardInput = string.Empty;
                _pendingClipboardInput = string.Empty;
                return;
            }
            if (input.Equals(_lastClipboardInput, StringComparison.OrdinalIgnoreCase)) return;

            _lastClipboardInput = input;
            _pendingClipboardInput = input;
            await ApplyPendingClipboardInputAsync();
        }
        catch (Exception)
        {
            // Another process may temporarily own the clipboard. A later change will retry.
        }
    }

    private async Task ShowClipboardInputAsync(string input)
    {
        if (ContentFrame.Content is Pages.DownloadPage page && IsDuplicateClipboardInput(page.ViewModel.Url, input)) return;
        var dualPage = ContentFrame.Content as Pages.DualAudioPage;
        if (dualPage?.ViewModel.IsDuplicateClipboardInput(input) == true) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        if (dualPage is not null)
        {
            await dualPage.ReceiveClipboardInputAsync(input);
            return;
        }
        Navigate("download", new Pages.DownloadInputNavigationContext(input, ParseAutomatically: true));
    }

    private async Task ApplyPendingClipboardInputAsync()
    {
        var console = ((App)Application.Current).Services.TaskConsole;
        if (_applyingClipboardInput || console.IsBusy) return;
        _applyingClipboardInput = true;
        try
        {
            while (_clipboardMonitoring && !console.IsBusy && !string.IsNullOrWhiteSpace(_pendingClipboardInput))
            {
                var input = _pendingClipboardInput;
                _pendingClipboardInput = string.Empty;
                if (input.Equals(_lastClipboardInput, StringComparison.OrdinalIgnoreCase))
                    await ShowClipboardInputAsync(input);
            }
        }
        finally { _applyingClipboardInput = false; }
    }

    private void TaskConsole_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var console = ((App)Application.Current).Services.TaskConsole;
        if (e.PropertyName != nameof(console.IsBusy) || console.IsBusy
            || !_clipboardMonitoring || string.IsNullOrWhiteSpace(_pendingClipboardInput)) return;
        DispatcherQueue.TryEnqueue(async () => await ApplyPendingClipboardInputAsync());
    }

    private async void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag }) return;
        var mode = tag switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };

        try
        {
            await ((App)Application.Current).Services.Theme.SetModeAsync(mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateThemeUi();
            await new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "主题设置保存失败",
                Content = exception.Message,
                CloseButtonText = "确定"
            }.ShowAsync();
        }
    }

    private void Theme_Changed(object? sender, EventArgs e) => UpdateThemeUi();

    private void UpdateThemeUi()
    {
        var mode = ((App)Application.Current).Services.Theme.CurrentMode;
        SystemThemeItem.IsChecked = mode == AppThemeMode.System;
        LightThemeItem.IsChecked = mode == AppThemeMode.Light;
        DarkThemeItem.IsChecked = mode == AppThemeMode.Dark;
        var label = mode switch
        {
            AppThemeMode.Light => "浅色",
            AppThemeMode.Dark => "深色",
            _ => "跟随系统"
        };
        ThemeIcon.Glyph = mode switch
        {
            AppThemeMode.Light => "\uE706",
            AppThemeMode.Dark => "\uE708",
            _ => "\uE977"
        };
        ThemeIcon.FontSize = mode == AppThemeMode.System ? 16 : 18;
        ToolTipService.SetToolTip(ThemeNavigationItem, $"主题：{label}");
        AutomationProperties.SetName(ThemeNavigationItem, $"界面主题：{label}");
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClosing) return;
        args.Cancel = true;
        if (_closingDialogOpen) return;
        _closingDialogOpen = true;
        var services = ((App)Application.Current).Services;
        Pages.SettingsPage? settingsPage = null;
        try
        {
            if (ContentFrame.Content is IQueueEditorPage editor && !await editor.ConfirmLeaveQueueEditAsync()) return;
            if (ContentFrame.Content is Pages.RenameTemplatesPage templates && !await templates.ConfirmDiscardChangesAsync()) return;
            if (services.Work.HasRunningOperations)
            {
                settingsPage = ContentFrame.Content as Pages.SettingsPage;
                if (settingsPage is not null) await settingsPage.SuspendQrDialogAsync();
                var dialog = new ContentDialog
                {
                    XamlRoot = WindowRoot.XamlRoot, Title = "任务仍在运行",
                    Content = "退出会停止当前操作并保存下载队列，已下载文件和断点会保留。下次打开后需手动继续。",
                    PrimaryButtonText = "停止并退出", CloseButtonText = "继续运行", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            // A corrupt queue has never been scheduled or overwritten; it remains available for repair.
            if (services.DownloadQueue.Error.Length == 0) await services.DownloadQueue.ShutdownAsync();
            await services.TaskManager.CancelActiveAsync();
            await services.QueueTaskManager.CancelActiveAsync();
            _forceClosing = true; Close();
        }
        catch (Exception exception)
        {
            await new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "停止任务失败", Content = exception.Message, CloseButtonText = "关闭" }.ShowAsync();
        }
        finally
        {
            if (!_forceClosing) settingsPage?.ResumeQrDialog();
            _closingDialogOpen = false;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_clipboardMonitoring) Clipboard.ContentChanged -= Clipboard_ContentChanged;
        Activated -= MainWindow_Activated;
        ((App)Application.Current).Services.TaskConsole.PropertyChanged -= TaskConsole_PropertyChanged;
    }
}
