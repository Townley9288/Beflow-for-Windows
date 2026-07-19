using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed record DownloadInputNavigationContext(string Input, bool ParseAutomatically = false);

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
        if (e.Parameter is DownloadInputNavigationContext input
            && ViewModel.ApplyExternalInput(input.Input)
            && input.ParseAutomatically
            && ViewModel.ParseAllCommand.CanExecute(null))
            await ViewModel.ParseAllCommand.ExecuteAsync(null);
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

    private void BilibiliInput_DragOver(object sender, DragEventArgs e)
    {
        if (!((App)Application.Current).MainWindow.DragLinkMonitoringEnabled)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        if (!BilibiliDataTransfer.MayContainInput(e.DataView)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "填入 B 站链接或编号";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async void BilibiliInput_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!((App)Application.Current).MainWindow.DragLinkMonitoringEnabled) return;
        try
        {
            var inputs = await BilibiliDataTransfer.ExtractInputsAsync(e.DataView);
            if (inputs.Count > 0) ViewModel.ApplyExternalInput(inputs[0]);
        }
        catch (Exception)
        {
            // The drag source can disappear before asynchronous data retrieval completes.
        }
    }

}
