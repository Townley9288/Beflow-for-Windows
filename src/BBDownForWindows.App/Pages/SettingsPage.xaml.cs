using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly DispatcherTimer _qrTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _qrTimestamp;
    private bool _inputSettingsReady;
    private bool _qrDialogShowing;
    private bool _qrDialogSuppressed;
    private Task? _qrDialogTask;
    private Guid _qrTaskId;
    private bool _qrCancelRequested;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(((App)Application.Current).Services);
        InitializeComponent();
        _qrTimer.Tick += QrTimer_Tick;
    }
    public SettingsViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _inputSettingsReady = false;
        ViewModel.Activate();
        _qrTimer.Start();
        await ViewModel.InitializeAsync();
        _inputSettingsReady = ViewModel.IsActive && ViewModel.IsInitialized;
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _inputSettingsReady = false;
        _qrTimer.Stop();
        HideQrDialog();
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetWorkDirectory(value); }
    private async void BrowseAria_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetAria2cPath(value); }
    private async void BrowseMkv_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetMkvmergePath(value); }
    private async void Apply_Click(object sender, RoutedEventArgs e) { await ViewModel.SaveDownloadSettingsAsync(); ((App)Application.Current).MainWindow.Navigate("download"); }
    private async void ClipboardMonitoring_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_inputSettingsReady || sender is not ToggleSwitch toggle) return;
        ViewModel.Settings.MonitorClipboard = toggle.IsOn;
        await SaveInputSettingsAsync();
    }
    private async void DragLinkMonitoring_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_inputSettingsReady || sender is not ToggleSwitch toggle) return;
        ViewModel.Settings.MonitorDragLinks = toggle.IsOn;
        await SaveInputSettingsAsync();
    }
    private async Task SaveInputSettingsAsync()
    {
        await ViewModel.SaveInputSettingsAsync();
        var window = ((App)Application.Current).MainWindow;
        window.ConfigureClipboardMonitoring(ViewModel.Settings.MonitorClipboard);
        window.ConfigureDragLinkMonitoring(ViewModel.Settings.MonitorDragLinks);
    }
    private void SettingsNotification_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void QrTimer_Tick(object? sender, object e)
    {
        var services = ((App)Application.Current).Services;
        var task = services.TaskManager.ActiveTask;
        if (task is null || task.Kind is not (TaskKind.LoginWeb or TaskKind.LoginTv))
        {
            _qrTaskId = Guid.Empty;
            _qrCancelRequested = false;
            HideQrDialog();
            return;
        }
        if (_qrTaskId != task.Id)
        {
            _qrTaskId = task.Id;
            _qrCancelRequested = false;
            _qrTimestamp = DateTime.MinValue;
        }

        if (task.State == TaskState.Running)
        {
            if (!_qrCancelRequested) ShowQrDialog();
            if (!File.Exists(services.Paths.QrCodeFile))
            {
                QrImage.Source = null;
                QrStatus.Text = task.Kind == TaskKind.LoginTv ? "正在生成 TV 登录二维码…" : "正在生成 WEB 登录二维码…";
                return;
            }

            var timestamp = File.GetLastWriteTimeUtc(services.Paths.QrCodeFile);
            if (timestamp != _qrTimestamp)
            {
                _qrTimestamp = timestamp;
                QrImage.Source = new BitmapImage(new Uri(services.Paths.QrCodeFile));
            }
            QrStatus.Text = task.Kind == TaskKind.LoginTv ? "请使用哔哩哔哩客户端扫描 TV 登录二维码并确认" : "请使用哔哩哔哩客户端扫描 WEB 登录二维码并确认";
            return;
        }

        QrImage.Source = null;
        QrStatus.Text = task.State switch
        {
            TaskState.Failed => $"登录失败：{task.Error}",
            TaskState.Cancelled => "登录已取消",
            _ => "登录流程已完成，正在刷新账号状态…"
        };
        HideQrDialog();
    }

    private void ShowQrDialog()
    {
        if (_qrDialogSuppressed || _qrDialogShowing || XamlRoot is null) return;
        QrDialog.XamlRoot = XamlRoot;
        _qrDialogShowing = true;
        _qrDialogTask = ShowQrDialogAsync();
    }

    private async Task ShowQrDialogAsync()
    {
        try
        {
            await QrDialog.ShowAsync();
        }
        catch (Exception)
        {
            // Another window-level dialog can briefly own this XamlRoot. The timer retries later.
        }
        finally { _qrDialogShowing = false; }
    }

    private void HideQrDialog()
    {
        if (!_qrDialogShowing) return;
        QrDialog.Hide();
    }

    internal async Task SuspendQrDialogAsync()
    {
        _qrDialogSuppressed = true;
        var dialogTask = _qrDialogTask;
        HideQrDialog();
        if (dialogTask is not null) await dialogTask;
    }

    internal void ResumeQrDialog() => _qrDialogSuppressed = false;

    private void QrDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var manager = ((App)Application.Current).Services.TaskManager;
        if (!CanCancelQrTask(manager.ActiveTask, _qrTaskId)) return;
        _qrCancelRequested = true;
        _ = manager.CancelActiveAsync();
    }

    internal static bool CanCancelQrTask(TaskSnapshot? task, Guid qrTaskId) =>
        task is { State: TaskState.Running }
        && task.Id == qrTaskId
        && task.Kind is TaskKind.LoginWeb or TaskKind.LoginTv;
}
