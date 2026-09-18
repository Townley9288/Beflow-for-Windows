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
    public string TimeCaption => Item.State switch
    {
        DownloadQueueState.Waiting or DownloadQueueState.Editing => "加入时间",
        DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Paused => "开始时间",
        DownloadQueueState.Completed => "完成时间",
        _ => "结束时间"
    };
    public string TimeText => DisplayTime(Item).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static DateTimeOffset DisplayTime(DownloadQueueItem item) => item.State switch
    {
        DownloadQueueState.Waiting or DownloadQueueState.Editing => item.AddedAt,
        DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Paused => item.StartedAt ?? item.AddedAt,
        _ => item.FinishedAt ?? item.AddedAt
    };
    public Visibility WaitingVisibility => Item.CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => Item.State == DownloadQueueState.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Editing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RetryVisibility => Item.IsTerminal && (Item.Failed > 0 || Item.State == DownloadQueueState.Failed) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Item.State is DownloadQueueState.Running or DownloadQueueState.Pausing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(Item.Error) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RenameVisibility => RenameContext is not null ? Visibility.Visible : Visibility.Collapsed;
    public RenameNavigationContext? RenameContext
    {
        get
        {
            if (Item.State != DownloadQueueState.Completed) return null;
            var files = Item.Checkpoint.Units.Where(unit => unit.Completed)
                .SelectMany(unit => unit.Files.Select(file => file.Path))
                .Where(DownloadFileKinds.IsVideoFile)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var directories = files.Select(Path.GetDirectoryName)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return directories.Count == 1 ? new RenameNavigationContext(directories[0]!, files, Item.Title) : null;
        }
    }
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
