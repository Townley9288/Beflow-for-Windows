using System.Diagnostics;
using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App.Pages;

public sealed record DualAudioNavigationContext(string ExistingTaskDirectory, bool OpenTaskSettings = true);

public sealed partial class DualAudioPage : Page
{
    public DualAudioPage()
    {
        ViewModel = new DualAudioViewModel(((App)Application.Current).Services);
        ViewModel.ConfirmMkvmergeAvailableAsync = ConfirmMkvmergeAvailableAsync;
        InitializeComponent();
    }

    public DualAudioViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Activate();
        await ViewModel.InitializeAsync(e.Parameter as HistoryRecord);
        if (e.Parameter is DualAudioNavigationContext context)
        {
            await ViewModel.PrepareExistingRemuxAsync(context.ExistingTaskDirectory);
            if (context.OpenTaskSettings) await TaskSettingsDialog.ShowAsync();
        }
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private async void ShowTaskSettings_Click(object sender, RoutedEventArgs e) => await TaskSettingsDialog.ShowAsync();
    private void LayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1150;
        SourceCardsWide.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SourceCardsCompact.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (value is not null) ViewModel.WorkDirectory = value;
    }
    private async void BrowseExisting_Click(object sender, RoutedEventArgs e)
    {
        var value = await PickerHelper.PickFolderAsync(((App)Application.Current).MainWindow);
        if (value is not null) await ViewModel.PrepareExistingRemuxAsync(value);
    }
    private async void BrowseMkvmerge_Click(object sender, RoutedEventArgs e)
    {
        var value = await PickerHelper.PickExecutableAsync(((App)Application.Current).MainWindow);
        if (value is not null) ViewModel.MkvmergePath = value;
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = ViewModel.LastResult?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(directory);
        Process.Start(info);
    }

    private void MessageInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

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
        var target = (sender as FrameworkElement)?.Tag as string;
        e.DragUIOverride.Caption = target == "B" ? "填入来源 B" : "填入来源 A；包含两个链接时同时填入 A/B";
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
            ViewModel.ApplyExternalInputs(inputs, (sender as FrameworkElement)?.Tag as string == "B");
        }
        catch (Exception)
        {
            // The drag source can disappear before asynchronous data retrieval completes.
        }
    }

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
}
