using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadQueueViewModelTests
{
    [Fact]
    public void EveryTaskAppearsInOneCategoryIncludingEditingPausedAndPartialFailures()
    {
        var viewModel = new DownloadQueueViewModel();
        var items = Enum.GetValues<DownloadQueueState>().Select(state => new DownloadQueueItem { State = state }).ToList();
        var resumed = new DownloadQueueItem { State = DownloadQueueState.Waiting, StartedAt = DateTimeOffset.Now };
        items.Add(resumed);
        viewModel.ApplySnapshot(items);

        Assert.Equal([2, 4, 1, 3], viewModel.Tabs.Select(tab => tab.Count));
        var listed = new List<Guid>();
        foreach (var tab in viewModel.Tabs)
        {
            viewModel.SelectedTab = tab;
            Assert.Equal(tab.Count, viewModel.Rows.Count);
            listed.AddRange(viewModel.Rows.Select(row => row.Item.Id));
        }
        Assert.Equal(items.Count, listed.Distinct().Count());
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Active);
        Assert.Contains(viewModel.Rows, row => row.Item.Id == resumed.Id);
        Assert.Contains(viewModel.Rows, row => row.Item.State == DownloadQueueState.Paused);
    }

    [Fact]
    public void HiddenRunningTaskRetainsProgressAndMovesToCompletedWithoutSwitchingTabs()
    {
        var viewModel = new DownloadQueueViewModel();
        var item = new DownloadQueueItem { State = DownloadQueueState.Running, StartedAt = DateTimeOffset.Now };
        viewModel.ApplySnapshot([item]);
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Active);
        var originalRow = Assert.Single(viewModel.Rows);
        viewModel.SelectedTab = viewModel.Tabs[0];
        viewModel.ApplyProgress(new QueueProgress(item.Id, 2, "正在下载", 65, "2 MB/s", "10 秒"));
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Active);
        Assert.Same(originalRow, Assert.Single(viewModel.Rows));
        Assert.Equal(65, originalRow.Percent);
        Assert.Contains("2 MB/s", originalRow.ProgressText);

        var completed = QueueSnapshot.Copy(item);
        completed.State = DownloadQueueState.Completed;
        viewModel.ApplySnapshot([completed]);
        Assert.Equal(DownloadQueueTabKind.Active, viewModel.SelectedTab.Kind);
        Assert.Empty(viewModel.Rows);
        Assert.Equal(Visibility.Visible, viewModel.EmptyVisibility);
        Assert.Equal(0, viewModel.SelectedTab.Count);
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Completed);
        Assert.Same(originalRow, Assert.Single(viewModel.Rows));
        Assert.Equal(Visibility.Collapsed, originalRow.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.EmptyVisibility);
    }

    [Fact]
    public void FilteringPreservesQueueOrderAndRemovingRecordsUpdatesOnlyTheirCategory()
    {
        var viewModel = new DownloadQueueViewModel();
        var first = new DownloadQueueItem();
        var second = new DownloadQueueItem();
        var completed = new DownloadQueueItem { State = DownloadQueueState.Completed };
        var failed = new DownloadQueueItem { State = DownloadQueueState.Failed };
        viewModel.ApplySnapshot([first, completed, second, failed]);
        Assert.Equal([first.Id, second.Id], viewModel.Rows.Select(row => row.Item.Id));
        var firstRow = viewModel.Rows[0];
        viewModel.ApplySnapshot([second, completed, first, failed]);
        Assert.Equal([second.Id, first.Id], viewModel.Rows.Select(row => row.Item.Id));
        Assert.Same(firstRow, viewModel.Rows[1]);

        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Completed);
        viewModel.ApplySnapshot([second, first, failed]);
        Assert.Empty(viewModel.Rows);
        Assert.Equal([2, 0, 0, 1], viewModel.Tabs.Select(tab => tab.Count));
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Unsuccessful);
        Assert.Equal(failed.Id, Assert.Single(viewModel.Rows).Item.Id);
    }
}
