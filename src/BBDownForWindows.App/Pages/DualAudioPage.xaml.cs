using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed partial class DualAudioPage : Page
{
    public DualAudioPage()
    {
        ViewModel = new DualAudioViewModel(((App)Application.Current).Services);
        InitializeComponent();
    }
    public DualAudioViewModel ViewModel { get; }
    protected override async void OnNavigatedTo(NavigationEventArgs e) { await ViewModel.InitializeAsync(e.Parameter as HistoryRecord); base.OnNavigatedTo(e); }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.WorkDirectory = value; }
    private async void BrowseExisting_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.ExistingTaskDirectory = value; }
    private async void BrowseMkvmerge_Click(object sender, RoutedEventArgs e) { var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow); if (value is not null) ViewModel.MkvmergePath = value; }
    private void CopyLogs_Click(object sender, RoutedEventArgs e) { var package = new DataPackage(); package.SetText(ViewModel.Console.Logs); Clipboard.SetContent(package); }
    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e) { LogTextBox.SelectionStart = LogTextBox.Text.Length; LogTextBox.SelectionLength = 0; }
}
