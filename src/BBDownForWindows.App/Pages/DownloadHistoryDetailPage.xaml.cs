using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;

namespace BBDownForWindows.App.Pages;

public sealed partial class DownloadHistoryDetailPage : Page
{
    public DownloadHistoryDetailPage()
    {
        ViewModel = new DownloadHistoryDetailViewModel(((App)Application.Current).Services);
        InitializeComponent();
    }

    public DownloadHistoryDetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is HistoryRecord record) await ViewModel.LoadAsync(record);
        base.OnNavigatedTo(e);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).MainWindow.Navigate("history");
    private void Load_Click(object sender, RoutedEventArgs e) { if (ViewModel.Record is not null) ((App)Application.Current).MainWindow.RestoreHistory(ViewModel.Record); }
    private void Retry_Click(object sender, RoutedEventArgs e) { var record = ViewModel.BuildFailedRetryRecord(); if (record is not null) ((App)Application.Current).MainWindow.RestoreHistory(record); }
    private void Remux_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRemux) return;
        ((App)Application.Current).MainWindow.Navigate("dual", new DualAudioNavigationContext(ViewModel.RemuxDirectory));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = ViewModel.Record?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private async void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Record is null) return;
        string content;
        try { content = ((App)Application.Current).Services.TaskManager.ReadSavedLog(ViewModel.Record.LogPath); }
        catch (Exception exception) { content = exception.Message; }
        await new ContentDialog
        {
            Title = "任务日志",
            Content = new ScrollViewer { Content = new TextBlock { Text = content, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }
}
