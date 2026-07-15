using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed partial class DualAudioPage : Page
{
    private readonly LogAutoScroller _logAutoScroller;

    public DualAudioPage()
    {
        ViewModel = new DualAudioViewModel(((App)Application.Current).Services);
        ViewModel.ConfirmMkvmergeAvailableAsync = ConfirmMkvmergeAvailableAsync;
        InitializeComponent();
        _logAutoScroller = new LogAutoScroller(LogTextBox);
    }
    public DualAudioViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Console.PropertyChanged += Console_PropertyChanged;
        await ViewModel.InitializeAsync(e.Parameter as HistoryRecord);
        _logAutoScroller.FollowLatest();
        base.OnNavigatedTo(e);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Console.PropertyChanged -= Console_PropertyChanged;
        base.OnNavigatedFrom(e);
    }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.WorkDirectory = value; }
    private async void BrowseExisting_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.ExistingTaskDirectory = value; }
    private async void BrowseMkvmerge_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.MkvmergePath = value; }
    private async Task<bool> ConfirmMkvmergeAvailableAsync()
    {
        var app = (App)Application.Current;
        var tools = app.Services.ToolLocator.Locate(new AppSettings { MkvmergePath = ViewModel.MkvmergePath });
        if (!string.IsNullOrWhiteSpace(tools.Mkvmerge)) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "缺少 MKVToolNix",
            Content = "多音轨封装需要 mkvmerge.exe，但当前没有检测到。请先安装 MKVToolNix，或在设置页手动选择 mkvmerge.exe。",
            PrimaryButtonText = "前往设置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) app.MainWindow.Navigate("settings");
        return false;
    }
    private void CopyLogs_Click(object sender, RoutedEventArgs e) { var package = new DataPackage(); package.SetText(ViewModel.Console.Logs); Clipboard.SetContent(package); }
    private void Console_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Console.IsBusy) && ViewModel.Console.IsBusy) _logAutoScroller.FollowLatest();
    }
}
