using BBDownForWindows.App.ViewModels;
using BBDownForWindows.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public enum HistorySection { Downloads, Renames }

public sealed record HistoryNavigationContext(HistorySection Section);

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        var services = ((App)Application.Current).Services;
        ViewModel = new HistoryViewModel(services);
        RenameViewModel = new RenameViewModel(services);
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }
    public RenameViewModel RenameViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Activate();
        RenameViewModel.Activate();
        if (e.Parameter is HistoryNavigationContext context)
            HistoryTabs.SelectedIndex = context.Section == HistorySection.Renames ? 1 : 0;
        await Task.WhenAll(ViewModel.LoadAsync(), RenameViewModel.LoadHistoryAsync());
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        RenameViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRecord is not null) ((App)Application.Current).MainWindow.RestoreHistory(ViewModel.SelectedRecord);
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRecord is { } record && (record.DownloadBatch is not null || record.DualAudioBatch is not null))
            ((App)Application.Current).MainWindow.Navigate("history-detail", ViewModel.SelectedRecord);
    }

    private async void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRecord is null) return;
        string content;
        try { content = ((App)Application.Current).Services.TaskManager.ReadSavedLog(ViewModel.SelectedRecord.LogPath); }
        catch (Exception exception) { content = exception.Message; }
        await new ContentDialog
        {
            Title = "任务日志",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = content,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空下载历史",
            Content = "确定清空全部下载历史记录吗？日志文件不会立即删除。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearCommand.ExecuteAsync(null);
    }

    private void BackToRename_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("rename");
    private async void RenameRefresh_Click(object sender, RoutedEventArgs e) => await RenameViewModel.LoadHistoryAsync();
    private void RenameHistoryPrevious_Click(object sender, RoutedEventArgs e) => RenameViewModel.PreviousHistoryPage();
    private void RenameHistoryNext_Click(object sender, RoutedEventArgs e) => RenameViewModel.NextHistoryPage();

    private async void RenameHistoryDetail_Click(object sender, RoutedEventArgs e)
    {
        var record = RenameViewModel.SelectedHistory;
        if (record is null) return;
        var content = new RenameHistoryDetailContent(record)
        {
            Width = Math.Min(840, Math.Max(560, ActualWidth - 160)),
            Height = Math.Min(650, Math.Max(480, ActualHeight - 160))
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名详情",
            Content = content,
            CloseButtonText = "关闭",
            MaxWidth = 960
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960d;
        await dialog.ShowAsync();
    }

    private async void RenameUndoHistory_Click(object sender, RoutedEventArgs e)
    {
        if (RenameViewModel.SelectedHistory is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销重命名",
            Content = "Beflow 会先确认当前文件仍存在且原文件名未被占用，然后恢复这条记录中的全部文件名。",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RenameViewModel.UndoSelectedHistoryAsync();
    }

    private async void RenameDeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (RenameViewModel.SelectedHistory is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除重命名记录",
            Content = "只会删除这条历史记录，不会修改任何媒体文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RenameViewModel.DeleteSelectedHistoryAsync();
    }

    private async void RenameClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空重命名历史",
            Content = "只会删除历史记录，不会再次修改任何媒体文件。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RenameViewModel.ClearHistoryAsync();
    }
}
