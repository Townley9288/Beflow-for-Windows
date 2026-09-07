using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App.ViewModels;

public enum DownloadQueueTabKind { Waiting, Active, Completed, Unsuccessful }

public sealed class DownloadQueueTab(DownloadQueueTabKind kind, string title, string iconGlyph, string emptyText) : ObservableObject
{
    private int count;
    public DownloadQueueTabKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string IconGlyph { get; } = iconGlyph;
    public string EmptyText { get; } = emptyText;
    public string CountText => $"{Count} 个任务";
    public string AutomationName => $"{Title}，{CountText}";
    public int Count
    {
        get => count;
        internal set
        {
            if (!SetProperty(ref count, value)) return;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(AutomationName));
        }
    }
}

public sealed class DownloadQueueViewModel : ObservableObject
{
    private readonly Dictionary<Guid, QueueRow> allRows = [];
    private List<QueueRow> orderedRows = [];
    private DownloadQueueTab selectedTab;

    public DownloadQueueViewModel() => selectedTab = Tabs[0];

    public IReadOnlyList<DownloadQueueTab> Tabs { get; } =
    [
        new(DownloadQueueTabKind.Waiting, "等待中", "\uE823", "暂无等待中的任务"),
        new(DownloadQueueTabKind.Active, "进行中", "\uE896", "暂无进行中或已暂停的任务"),
        new(DownloadQueueTabKind.Completed, "已完成", "\uE73E", "暂无已完成的任务"),
        new(DownloadQueueTabKind.Unsuccessful, "失败 / 已取消", "\uE7BA", "暂无失败或已取消的任务")
    ];

    public ObservableCollection<QueueRow> Rows { get; } = [];
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public DownloadQueueTab SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is not null && SetProperty(ref selectedTab, value)) RefreshRows();
        }
    }

    public void ApplySnapshot(IReadOnlyList<DownloadQueueItem> items)
    {
        var ids = items.Select(item => item.Id).ToHashSet();
        foreach (var id in allRows.Keys.Where(id => !ids.Contains(id)).ToArray()) allRows.Remove(id);
        orderedRows = new(items.Count);
        foreach (var item in items)
        {
            if (allRows.TryGetValue(item.Id, out var row)) row.Update(item);
            else allRows.Add(item.Id, row = new QueueRow(item));
            orderedRows.Add(row);
        }
        var counts = items.CountBy(Category).ToDictionary();
        foreach (var tab in Tabs) tab.Count = counts.GetValueOrDefault(tab.Kind);
        RefreshRows();
    }

    public void ApplyProgress(QueueProgress progress)
    {
        if (allRows.TryGetValue(progress.Id, out var row)) row.Progress(progress);
    }

    private void RefreshRows()
    {
        var visible = orderedRows.Where(row => Category(row.Item) == SelectedTab.Kind).ToList();
        var visibleIds = visible.Select(row => row.Item.Id).ToHashSet();
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (!visibleIds.Contains(Rows[index].Item.Id)) Rows.RemoveAt(index);
        for (var index = 0; index < visible.Count; index++)
        {
            var row = visible[index];
            var previous = Rows.IndexOf(row);
            if (previous < 0) Rows.Insert(index, row);
            else if (previous != index) Rows.Move(previous, index);
        }
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private static DownloadQueueTabKind Category(DownloadQueueItem item) => item.State switch
    {
        DownloadQueueState.Waiting => item.StartedAt is null ? DownloadQueueTabKind.Waiting : DownloadQueueTabKind.Active,
        DownloadQueueState.Editing => DownloadQueueTabKind.Waiting,
        DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Paused => DownloadQueueTabKind.Active,
        DownloadQueueState.Completed => DownloadQueueTabKind.Completed,
        DownloadQueueState.PartialFailure or DownloadQueueState.Failed or DownloadQueueState.Cancelled => DownloadQueueTabKind.Unsuccessful,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.State, "未知队列状态")
    };
}
