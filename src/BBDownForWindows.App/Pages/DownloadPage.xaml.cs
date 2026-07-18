using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;

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
        ViewModel.Activate();
        await ViewModel.InitializeAsync(e.Parameter as HistoryRecord);
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (!string.IsNullOrWhiteSpace(folder)) ViewModel.WorkDirectory = folder;
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
        if (result is null || string.IsNullOrWhiteSpace(result.RenameDirectory)) return;
        ((App)Application.Current).MainWindow.Navigate("rename", new RenameNavigationContext(result.RenameDirectory, result.OutputFiles, result.Title));
    }

    private void ManageNamingRules_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).MainWindow.Navigate("rename-templates", new DownloadNamingNavigationContext(ViewModel.ActiveNamingProfileKind));

    private void DownloadNotification_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void OpenMessageLog_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.MessageLogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var startInfo = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private async void ShowDownloadSettings_Click(object sender, RoutedEventArgs e)
    {
        DownloadSettingsDialog.XamlRoot = XamlRoot;
        await DownloadSettingsDialog.ShowAsync();
    }

}
