using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed partial class DownloadPage : Page
{
    public DownloadPage()
    {
        ViewModel = new DownloadViewModel(((App)Application.Current).Services);
        InitializeComponent();
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

}
