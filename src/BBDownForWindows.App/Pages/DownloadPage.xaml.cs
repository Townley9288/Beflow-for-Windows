using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using System.Diagnostics;

namespace BBDownForWindows.App.Pages;

public sealed partial class DownloadPage : Page
{
    private readonly LogAutoScroller _logAutoScroller;

    public DownloadPage()
    {
        ViewModel = new DownloadViewModel(((App)Application.Current).Services);
        InitializeComponent();
        _logAutoScroller = new LogAutoScroller(LogTextBox);
    }

    public DownloadViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Activate();
        ViewModel.Console.PropertyChanged += Console_PropertyChanged;
        await ViewModel.InitializeAsync(e.Parameter as HistoryRecord);
        ControlsScrollViewer.ChangeView(null, 0, null, true);
        _logAutoScroller.FollowLatest();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Console.PropertyChanged -= Console_PropertyChanged;
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
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

    private void Console_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Console.IsBusy) && ViewModel.Console.IsBusy) _logAutoScroller.FollowLatest();
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = ViewModel.LastDownloadResult?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void GoRename_Click(object sender, RoutedEventArgs e)
    {
        var result = ViewModel.LastDownloadResult;
        if (result is null || string.IsNullOrWhiteSpace(result.OutputDirectory)) return;
        ((App)Application.Current).MainWindow.Navigate("rename", new RenameNavigationContext(result.OutputDirectory, result.OutputFiles, result.Title));
    }
}
