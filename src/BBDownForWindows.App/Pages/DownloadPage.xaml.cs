using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed partial class DownloadPage : Page
{
    private readonly DispatcherTimer _qrTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _qrTimestamp;
    private Guid? _loginTaskId;
    private DateTime _credentialTimestamp;
    private bool _loginCompleted;

    public DownloadPage()
    {
        ViewModel = new DownloadViewModel(((App)Application.Current).Services);
        InitializeComponent();
        _qrTimer.Tick += QrTimer_Tick;
        _qrTimer.Start();
    }

    public DownloadViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.InitializeAsync(e.Parameter as HistoryRecord);
        ControlsScrollViewer.ChangeView(null, 0, null, true);
        base.OnNavigatedTo(e);
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (!string.IsNullOrWhiteSpace(folder)) ViewModel.WorkDirectory = folder;
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage(); package.SetText(ViewModel.Console.Logs); Clipboard.SetContent(package);
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.SelectionStart = LogTextBox.Text.Length;
        LogTextBox.SelectionLength = 0;
    }

    private void QrTimer_Tick(object? sender, object e)
    {
        var paths = ((App)Application.Current).Services.Paths;
        var task = ((App)Application.Current).Services.TaskManager.ActiveTask;
        var loginTask = task is not null && task.Kind is TaskKind.LoginWeb or TaskKind.LoginTv;
        if (!loginTask)
        {
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var credential = task!.Kind == TaskKind.LoginTv ? paths.TvCredentialFile : paths.WebCredentialFile;
        if (_loginTaskId != task.Id)
        {
            _loginTaskId = task.Id;
            _credentialTimestamp = File.Exists(credential) ? File.GetLastWriteTimeUtc(credential) : DateTime.MinValue;
            _loginCompleted = false;
        }
        if (File.Exists(credential) && File.GetLastWriteTimeUtc(credential) > _credentialTimestamp)
        {
            _loginCompleted = true;
            QrImage.Source = null;
            QrStatus.Text = "检测到账号数据已更新，登录应已完成";
            QrPanel.Visibility = Visibility.Visible;
            return;
        }
        if (_loginCompleted) return;
        if (!File.Exists(paths.QrCodeFile))
        {
            QrPanel.Visibility = task.State == TaskState.Running ? Visibility.Visible : Visibility.Collapsed;
            QrStatus.Text = task.State == TaskState.Running ? "等待二维码生成…" : "登录流程已结束，请查看日志";
            return;
        }
        var timestamp = File.GetLastWriteTimeUtc(paths.QrCodeFile);
        if (timestamp != _qrTimestamp)
        {
            _qrTimestamp = timestamp;
            QrImage.Source = new BitmapImage(new Uri(paths.QrCodeFile));
        }
        QrStatus.Text = "扫码后请在手机端确认";
        QrPanel.Visibility = Visibility.Visible;
    }
}
