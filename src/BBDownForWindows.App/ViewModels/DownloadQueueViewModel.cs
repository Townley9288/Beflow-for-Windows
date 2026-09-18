using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    internal const int PageSize = 5;
    private readonly Dictionary<Guid, QueueRow> allRows = [];
    private List<QueueRow> orderedRows = [];
    private DownloadQueueTab selectedTab;
    private int pageNumber = 1;

    public DownloadQueueViewModel()
    {
        selectedTab = Tabs[0];
        PreviousPageCommand = new RelayCommand(PreviousPage, () => PageNumber > 1);
        NextPageCommand = new RelayCommand(NextPage, () => UsesPaging && PageNumber < TotalPages);
    }

    public IReadOnlyList<DownloadQueueTab> Tabs { get; } =
    [
        new(DownloadQueueTabKind.Waiting, "等待中", "\uE823", "暂无等待中的任务"),
        new(DownloadQueueTabKind.Active, "进行中", "\uE896", "暂无进行中或已暂停的任务"),
        new(DownloadQueueTabKind.Completed, "已完成", "\uE73E", "暂无已完成的任务"),
        new(DownloadQueueTabKind.Unsuccessful, "失败 / 已取消", "\uE7BA", "暂无失败或已取消的任务")
    ];

    public ObservableCollection<QueueRow> Rows { get; } = [];
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool UsesPaging => SelectedTab.Kind == DownloadQueueTabKind.Completed;
    public Visibility PagerVisibility => UsesPaging ? Visibility.Visible : Visibility.Collapsed;
    public int PageNumber
    {
        get => pageNumber;
        private set
        {
            if (!SetProperty(ref pageNumber, value)) return;
            OnPropertyChanged(nameof(PageText));
            PreviousPageCommand.NotifyCanExecuteChanged();
            NextPageCommand.NotifyCanExecuteChanged();
        }
    }
    public int TotalPages => UsesPaging ? Math.Max(1, (int)Math.Ceiling(SelectedTab.Count / (double)PageSize)) : 1;
    public string PageText => $"第 {PageNumber} / {TotalPages} 页 · 每页 {PageSize} 条";
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public DownloadQueueTab SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is not null && SetProperty(ref selectedTab, value))
            {
                pageNumber = 1;
                OnPropertyChanged(nameof(PageNumber));
                RefreshRows();
            }
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

    public void SelectLandingTab()
    {
        var kind = Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Waiting).Count > 0 ? DownloadQueueTabKind.Waiting
            : Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Active).Count > 0 ? DownloadQueueTabKind.Active
            : DownloadQueueTabKind.Completed;
        SelectedTab = Tabs.Single(tab => tab.Kind == kind);
    }

    public void ApplyProgress(QueueProgress progress)
    {
        if (allRows.TryGetValue(progress.Id, out var row)) row.Progress(progress);
    }

    private void PreviousPage() { if (PageNumber > 1) { PageNumber--; RefreshRows(); } }
    private void NextPage() { if (UsesPaging && PageNumber < TotalPages) { PageNumber++; RefreshRows(); } }

    private void RefreshRows()
    {
        if (UsesPaging && PageNumber > TotalPages) pageNumber = TotalPages;
        var visible = orderedRows.Where(row => Category(row.Item) == SelectedTab.Kind);
        if (UsesPaging)
            visible = visible
                .OrderByDescending(row => row.Item.FinishedAt ?? row.Item.AddedAt)
                .Skip((PageNumber - 1) * PageSize)
                .Take(PageSize);
        var page = visible.ToList();
        var visibleIds = page.Select(row => row.Item.Id).ToHashSet();
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (!visibleIds.Contains(Rows[index].Item.Id)) Rows.RemoveAt(index);
        for (var index = 0; index < page.Count; index++)
        {
            var row = page[index];
            var previous = Rows.IndexOf(row);
            if (previous < 0) Rows.Insert(index, row);
            else if (previous != index) Rows.Move(previous, index);
        }
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(PagerVisibility));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PageNumber));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
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
