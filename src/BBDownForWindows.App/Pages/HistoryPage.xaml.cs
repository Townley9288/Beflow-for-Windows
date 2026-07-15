using BBDownForWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage() { ViewModel = new HistoryViewModel(((App)Application.Current).Services); InitializeComponent(); }
    public HistoryViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Activate();
        await ViewModel.LoadAsync();
        base.OnNavigatedTo(e);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (ViewModel.SelectedRecord is not null) ((App)Application.Current).MainWindow.RestoreHistory(ViewModel.SelectedRecord); }
    private async void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRecord is null) return;
        string content;
        try { content = ((App)Application.Current).Services.TaskManager.ReadSavedLog(ViewModel.SelectedRecord.LogPath); }
        catch (Exception exception) { content = exception.Message; }
        var dialog = new ContentDialog { Title = "任务日志", Content = new ScrollViewer { Content = new TextBlock { Text = content, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { Title = "清空历史", Content = "确定清空全部历史记录吗？日志文件不会立即删除。", PrimaryButtonText = "清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearCommand.ExecuteAsync(null);
    }
}
