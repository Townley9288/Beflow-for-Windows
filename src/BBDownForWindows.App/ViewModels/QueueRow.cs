using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App.ViewModels;

public sealed class QueueRow(DownloadQueueItem item) : ObservableObject
{
    public DownloadQueueItem Item { get; private set; } = item;
    public string Title => Item.Title;
    public string Links => Item.Url + (Item.DualAudio is null ? "" : "  /  " + Item.DualAudio.SourceBUrl);
    public string OutputDirectory => Item.OutputDirectory;
    public string Summary => $"{(Item.Kind == DownloadQueueKind.Download ? "普通下载" : "多音轨封装")} · 成功 {Item.Succeeded} / 失败 {Item.Failed} / 未完成 {Math.Max(0, Item.Total - Item.Succeeded - Item.Failed)}";
    public string Error => Item.Error;
    public string StateText => Item.State switch
    {
        DownloadQueueState.Waiting => "等待", DownloadQueueState.Editing => "编辑中", DownloadQueueState.Running => "运行中",
        DownloadQueueState.Pausing => "正在停止", DownloadQueueState.Paused => "已暂停", DownloadQueueState.Completed => "完成",
        DownloadQueueState.PartialFailure => "部分失败", DownloadQueueState.Failed => "失败", DownloadQueueState.Cancelled => "已取消", _ => ""
    };
    public Visibility WaitingVisibility => Item.CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => Item.State == DownloadQueueState.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Editing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RetryVisibility => Item.IsTerminal && (Item.Failed > 0 || Item.State == DownloadQueueState.Failed) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing ? Visibility.Visible : Visibility.Collapsed;
    public double Percent { get; private set; }
    public bool Indeterminate { get; private set; }
    public string ProgressText { get; private set; } = "";
    public void Update(DownloadQueueItem value) { Item = value; OnPropertyChanged(string.Empty); }
    public void Progress(QueueProgress progress)
    {
        Percent = progress.Percent ?? 0; Indeterminate = progress.Percent is null;
        ProgressText = $"P{progress.Current} · {progress.Phase} · {progress.Speed} {progress.Eta}";
        OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(Indeterminate)); OnPropertyChanged(nameof(ProgressText));
    }
}
