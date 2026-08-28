using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.Controls;

public sealed partial class TaskLogViewerContent : UserControl
{
    public TaskLogViewerContent(IReadOnlyList<string> lines)
    {
        Lines = lines;
        Summary = $"共 {lines.Count:N0} 行·仅渲染当前可见内容";
        InitializeComponent();
    }

    public IReadOnlyList<string> Lines { get; }
    public string Summary { get; }
}

public static class TaskLogDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot, string path)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = await ((App)Application.Current).Services.TaskManager.ReadSavedLogLinesAsync(path);
        }
        catch (Exception exception)
        {
            lines = [exception.Message];
        }

        var rootContent = xamlRoot.Content as FrameworkElement;
        var content = new TaskLogViewerContent(lines)
        {
            Width = Math.Min(920, Math.Max(560, (rootContent?.ActualWidth ?? 1080) - 160)),
            Height = Math.Min(620, Math.Max(420, (rootContent?.ActualHeight ?? 760) - 160))
        };
        var dialog = new ContentDialog
        {
            Title = "任务日志",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = xamlRoot,
            MaxWidth = 1040
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1040d;
        await dialog.ShowAsync();
    }
}
