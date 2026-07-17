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

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(((App)Application.Current).Services);
        InitializeComponent();
        _qrTimer.Tick += QrTimer_Tick;
    }
    public SettingsViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); ViewModel.Activate(); _qrTimer.Start(); await ViewModel.InitializeAsync(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _qrTimer.Stop(); ViewModel.Deactivate(); base.OnNavigatedFrom(e); }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetWorkDirectory(value); }
    private async void BrowseAria_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetAria2cPath(value); }
    private async void BrowseMkv_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (!string.IsNullOrWhiteSpace(value)) ViewModel.SetMkvmergePath(value); }
    private async void Apply_Click(object sender, RoutedEventArgs e) { await ViewModel.SaveDownloadSettingsAsync(); ((App)Application.Current).MainWindow.Navigate("download"); }
    private void SettingsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) { SettingsContent.Width = Math.Max(0, e.NewSize.Width); }
    private void SettingsNotification_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void QrTimer_Tick(object? sender, object e)
    {
        var services = ((App)Application.Current).Services;
        var task = services.TaskManager.ActiveTask;
        if (task is null || task.Kind is not (TaskKind.LoginWeb or TaskKind.LoginTv))
        {
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (task.State == TaskState.Running)
        {
            QrPanel.Visibility = Visibility.Visible;
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
        QrPanel.Visibility = task.State is TaskState.Failed or TaskState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
    }
}
