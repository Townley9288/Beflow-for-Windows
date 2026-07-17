using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Text.RegularExpressions;

namespace BBDownForWindows.App.Pages;

public sealed partial class AboutPage : Page
{
    private UpdateCoordinator? _updates;

    public AboutPage()
    {
        InitializeComponent();
        VersionBadgeText.Text = AppVersion.Display;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _updates = ((App)Application.Current).Services.UpdateCoordinator;
        _updates.StateChanged += Updates_StateChanged;
        RefreshUpdateUi();
        try
        {
            var services = ((App)Application.Current).Services;
            var settings = await services.Settings.LoadAsync();
            var tools = services.ToolLocator.Locate(settings);
            var versions = await Task.WhenAll(
                services.ToolLocator.GetVersionAsync(tools.BBDown),
                services.ToolLocator.GetVersionAsync(tools.Aria2c),
                services.ToolLocator.GetVersionAsync(tools.Ffmpeg),
                services.ToolLocator.GetVersionAsync(tools.Ffprobe),
                services.ToolLocator.GetVersionAsync(tools.Mkvmerge));
            ToolVersions.Text = $"BBDown：{versions[0]}\naria2c：{versions[1]}\nFFmpeg：{versions[2]}\nffprobe：{versions[3]}\nmkvmerge：{versions[4]}";
        }
        catch (Exception exception)
        {
            ToolVersions.Text = $"工具检测失败：{exception.Message}";
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_updates is not null) _updates.StateChanged -= Updates_StateChanged;
        base.OnNavigatedFrom(e);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { await _updates!.CheckAsync(true); }
        catch (Exception exception) { await ShowDialogAsync("检查更新失败", exception.Message); }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        var release = _updates?.AvailableRelease;
        if (release is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"更新到 v{Core.UpdateService.FormatVersion(release.Version)}？",
            Content = ((App)Application.Current).Services.Paths.Portable
                ? "将下载完整便携包。主程序退出后，更新助手会替换程序文件并保留 Data 目录。"
                : "将下载完整安装包并启动覆盖安装。配置、历史和登录数据会保留。",
            PrimaryButtonText = "下载并更新",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await _updates!.ApplyAvailableAsync(); }
        catch (Exception exception) { await ShowDialogAsync("更新失败", exception.Message); }
    }

    private void Updates_StateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshUpdateUi);

    private void RefreshUpdateUi()
    {
        if (_updates is null) return;
        UpdateStatusText.Text = _updates.StatusText;
        CheckUpdateButton.IsEnabled = !_updates.IsBusy;
        ApplyUpdateButton.IsEnabled = !_updates.IsBusy;
        ApplyUpdateButton.Visibility = _updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = _updates.IsBusy && _updates.Progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = _updates.Progress;
        var release = _updates.AvailableRelease;
        LatestVersionText.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        LatestVersionText.Text = release is null
            ? string.Empty
            : release.PublishedAt == DateTimeOffset.MinValue
                ? $"最新版本：v{Core.UpdateService.FormatVersion(release.Version)}"
                : $"最新版本：v{Core.UpdateService.FormatVersion(release.Version)} · {release.PublishedAt.ToLocalTime():yyyy-MM-dd}";
        ReleaseNotesContainer.Visibility = release is null || string.IsNullOrWhiteSpace(release.ReleaseNotes) ? Visibility.Collapsed : Visibility.Visible;
        ReleaseNotesText.Text = FormatReleaseNotes(release?.ReleaseNotes ?? string.Empty);
        ReleasePageLink.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is not null) ReleasePageLink.NavigateUri = release.ReleasePage;
    }

    private static string FormatReleaseNotes(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => Regex.Replace(line, @"^\s{0,3}#{1,6}\s+", string.Empty))
            .Select(line => Regex.Replace(line, @"^\s*[-*]\s+", "• "));
        return string.Join('\n', lines).Trim();
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "确定" }.ShowAsync();
    }
}
