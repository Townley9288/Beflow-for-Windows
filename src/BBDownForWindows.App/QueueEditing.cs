using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace BBDownForWindows.App;

public sealed record QueueNavigationContext(DownloadQueueItem Item, bool Edit);

public interface IQueueEditorPage
{
    Task<bool> ConfirmLeaveQueueEditAsync();
}

public sealed class QueueEditSession(DownloadQueueService queue)
{
    public Guid? EditingId { get; private set; }
    public Guid? ParentId { get; private set; }
    private string baseline = "";
    public void Begin(QueueNavigationContext context) { EditingId = context.Edit ? context.Item.Id : null; ParentId = context.Item.ParentId; }
    public void SetBaseline(DownloadQueueItem item) => baseline = Fingerprint(item);
    public async Task<bool> ConfirmLeaveAsync(DownloadQueueItem current, XamlRoot root)
    {
        if (EditingId is not { } id) return true;
        if (Fingerprint(current) != baseline)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = "放弃任务修改？", Content = "此任务尚未保存修改。放弃后恢复原配置和队列位置。",
                PrimaryButtonText = "放弃修改", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        await queue.CancelEditAsync(id); EditingId = null; return true;
    }
    public async Task SaveAsync(DownloadQueueItem item)
    {
        item.ParentId = ParentId;
        if (EditingId is { } id) { await queue.SaveEditAsync(id, item); EditingId = null; }
        else await queue.EnqueueAsync(item);
    }
    private static string Fingerprint(DownloadQueueItem item) => JsonSerializer.Serialize(new { item.Download, item.DualAudio });
    public static AppSettings SettingsFrom(DownloadRequest request) => new()
    {
        ApiMode = request.ApiMode, WorkDirectory = request.WorkDirectory, MultiThread = request.MultiThread,
        UposHost = request.UposHost, UseAria2c = request.UseAria2c, Aria2AutoTune = request.Aria2AutoTune,
        Aria2cPath = request.Aria2cPath, Aria2MaxConnection = request.Aria2MaxConnection, Aria2Split = request.Aria2Split,
        Aria2MaxConcurrentDownloads = request.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = request.Aria2MinSplitSize,
        SaveTaskLogs = request.SaveTaskLogs
    };
}
