using BBDownForWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BBDownForWindows.App.Pages;

public sealed partial class RenameHistoryPage : Page
{
    public RenameHistoryPage()
    {
        ViewModel = new RenameViewModel(((App)Application.Current).Services);
        InitializeComponent();
    }

    public RenameViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
        await ViewModel.LoadHistoryAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private void BackToRename_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("rename");
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadHistoryAsync();
    private void HistoryPrevious_Click(object sender, RoutedEventArgs e) => ViewModel.PreviousHistoryPage();
    private void HistoryNext_Click(object sender, RoutedEventArgs e) => ViewModel.NextHistoryPage();

    private async void HistoryDetail_Click(object sender, RoutedEventArgs e)
    {
        var record = ViewModel.SelectedHistory;
        if (record is null) return;
        var details = string.Join(Environment.NewLine, record.Operations.Select(operation => $"{Path.GetFileName(operation.SourcePath)}  →  {Path.GetFileName(operation.TargetPath)}"));
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = record.DisplayTitle,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock
                {
                    Text = details,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = "关闭"
        }.ShowAsync();
    }

    private async void UndoHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedHistory is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销重命名",
            Content = "Beflow 会先确认当前文件仍存在且原文件名未被占用，然后恢复这条记录中的全部文件名。",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.UndoSelectedHistoryAsync();
    }

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedHistory is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除历史记录",
            Content = "只会删除这条历史记录，不会修改任何媒体文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.DeleteSelectedHistoryAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearHistoryAsync();
    }
}
