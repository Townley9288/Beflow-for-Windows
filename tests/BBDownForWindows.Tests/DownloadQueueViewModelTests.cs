using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Microsoft.UI.Xaml;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadQueueViewModelTests
{
    [Theory]
    [InlineData(DownloadQueueKind.Download)]
    [InlineData(DownloadQueueKind.DualAudio)]
    public void CompletedTaskRenamesPublishedVideosInTheirActualDirectory(DownloadQueueKind kind)
    {
        var directory = kind == DownloadQueueKind.Download ? @"D:\视频\有兽焉\正片" : @"D:\视频\多音轨任务\多音轨MKV";
        var video = Path.Combine(directory, "[P01]第一集.mkv");
        var item = new DownloadQueueItem
        {
            Kind = kind, State = DownloadQueueState.Completed,
            Download = kind == DownloadQueueKind.Download ? new DownloadBatchRequest { Title = "有兽焉", Options = new() { Url = "" } } : null,
            DualAudio = kind == DownloadQueueKind.DualAudio ? new DualAudioBatchRequest { SourceATitle = "有兽焉" } : null,
            Checkpoint = new()
            {
                OutputDirectory = @"D:\视频",
                Units =
                [
                    new() { Completed = true, Files = [new(video, 1, 0, ""), new(Path.ChangeExtension(video, ".ass"), 1, 0, "")],
                        SourceA = new() { Files = [new(@"D:\视频\来源A\source.mp4", 1, 0, "")] } },
                    new() { Completed = false, Files = [new(@"D:\视频\临时\unfinished.mp4", 1, 0, "")] }
                ]
            }
        };

        var row = new QueueRow(item);
        var context = Assert.IsType<RenameNavigationContext>(row.RenameContext);
        Assert.Equal(directory, context.DirectoryPath);
        Assert.Equal([video], context.PreferredFiles);
        Assert.Equal("有兽焉", context.SuggestedTitle);
        Assert.Equal(Visibility.Visible, row.RenameVisibility);
    }

    [Theory]
    [InlineData(@"D:\视频\audio.m4a", null)]
    [InlineData(@"D:\视频\第一集\video.mp4", @"D:\视频\第二集\video.mp4")]
    public void RenameEntryRequiresVideosInOneDirectory(string firstPath, string? secondPath)
    {
        var unit = new QueueUnitCheckpoint { Completed = true, Files = [new(firstPath, 1, 0, "")] };
        if (secondPath is not null) unit.Files.Add(new(secondPath, 1, 0, ""));
        var row = new QueueRow(new() { State = DownloadQueueState.Completed, Checkpoint = new() { Units = [unit] } });

        Assert.Null(row.RenameContext);
        Assert.Equal(Visibility.Collapsed, row.RenameVisibility);
    }

    [Fact]
    public void RenameEntryBecomesAvailableWhenTaskCompletes()
    {
        var item = new DownloadQueueItem
        {
            State = DownloadQueueState.Running,
            Checkpoint = new() { Units = [new() { Completed = true, Files = [new(@"D:\视频\video.mp4", 1, 0, "")] }] }
        };
        var row = new QueueRow(item);
        Assert.Null(row.RenameContext);
        var completed = QueueSnapshot.Copy(item);
        completed.State = DownloadQueueState.Completed;
        row.Update(completed);

        Assert.NotNull(row.RenameContext);
        Assert.Equal(Visibility.Visible, row.RenameVisibility);
    }

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
            Assert.Equal(viewModel.UsesPaging ? Math.Min(tab.Count, DownloadQueueViewModel.PageSize) : tab.Count, viewModel.Rows.Count);
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
        Assert.Equal(Visibility.Collapsed, originalRow.ErrorVisibility);
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
        Assert.Equal(Visibility.Collapsed, viewModel.PagerVisibility);
    }

    [Fact]
    public void CompletedTabPagesNewestFinishedFirstAndWaitingTabDoesNotPage()
    {
        var viewModel = new DownloadQueueViewModel();
        var waiting = Enumerable.Range(0, 12).Select(_ => new DownloadQueueItem()).ToList();
        var completed = Enumerable.Range(0, 12).Select(index => new DownloadQueueItem
        {
            State = DownloadQueueState.Completed,
            AddedAt = DateTimeOffset.UnixEpoch.AddHours(index),
            FinishedAt = DateTimeOffset.UnixEpoch.AddHours(index)
        }).ToList();
        viewModel.ApplySnapshot([.. waiting, .. completed]);
        Assert.Equal(12, viewModel.Rows.Count);
        Assert.Equal(Visibility.Collapsed, viewModel.PagerVisibility);
        Assert.Equal(waiting[0].Id, viewModel.Rows[0].Item.Id);

        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Completed);
        Assert.Equal(Visibility.Visible, viewModel.PagerVisibility);
        Assert.Equal(12, viewModel.SelectedTab.Count);
        Assert.Equal(DownloadQueueViewModel.PageSize, viewModel.Rows.Count);
        Assert.Equal(3, viewModel.TotalPages);
        Assert.Equal("第 1 / 3 页 · 每页 5 条", viewModel.PageText);
        Assert.Equal(completed[11].Id, viewModel.Rows[0].Item.Id);
        Assert.Equal(completed[7].Id, viewModel.Rows[^1].Item.Id);
        Assert.False(viewModel.PreviousPageCommand.CanExecute(null));
        Assert.True(viewModel.NextPageCommand.CanExecute(null));

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.PageNumber);
        Assert.Equal(completed[6].Id, viewModel.Rows[0].Item.Id);
        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal(completed[1].Id, viewModel.Rows[0].Item.Id);
        Assert.Equal(completed[0].Id, viewModel.Rows[^1].Item.Id);
        Assert.False(viewModel.NextPageCommand.CanExecute(null));

        viewModel.SelectedTab = viewModel.Tabs[0];
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.Kind == DownloadQueueTabKind.Completed);
        Assert.Equal(1, viewModel.PageNumber);
        Assert.Equal(completed[11].Id, viewModel.Rows[0].Item.Id);

        viewModel.NextPageCommand.Execute(null);
        viewModel.NextPageCommand.Execute(null);
        viewModel.ApplySnapshot(waiting.Concat(completed.Take(6)).ToList());
        Assert.Equal(2, viewModel.TotalPages);
        Assert.Equal(2, viewModel.PageNumber);
        Assert.Equal(completed[0].Id, Assert.Single(viewModel.Rows).Item.Id);
    }

    [Fact]
    public void QueueRowShowsStateSpecificLocalTime()
    {
        var added = new DateTimeOffset(2026, 9, 11, 21, 0, 0, TimeSpan.FromHours(8));
        var started = added.AddMinutes(5);
        var finished = added.AddHours(1);
        var waiting = new QueueRow(new() { AddedAt = added });
        Assert.Equal("加入时间", waiting.TimeCaption);
        Assert.Equal(added.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), waiting.TimeText);

        var running = new QueueRow(new() { State = DownloadQueueState.Running, AddedAt = added, StartedAt = started });
        Assert.Equal("开始时间", running.TimeCaption);
        Assert.Equal(started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), running.TimeText);

        var completed = new QueueRow(new() { State = DownloadQueueState.Completed, AddedAt = added, FinishedAt = finished });
        Assert.Equal("完成时间", completed.TimeCaption);
        Assert.Equal(finished.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), completed.TimeText);

        var legacy = new QueueRow(new() { State = DownloadQueueState.Completed, AddedAt = added });
        Assert.Equal(added.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), legacy.TimeText);
    }

    [Theory]
    [InlineData(DownloadQueueState.Waiting, DownloadQueueTabKind.Waiting)]
    [InlineData(DownloadQueueState.Editing, DownloadQueueTabKind.Waiting)]
    [InlineData(DownloadQueueState.Running, DownloadQueueTabKind.Active)]
    [InlineData(DownloadQueueState.Paused, DownloadQueueTabKind.Active)]
    [InlineData(DownloadQueueState.Completed, DownloadQueueTabKind.Completed)]
    [InlineData(DownloadQueueState.Failed, DownloadQueueTabKind.Completed)]
    public void LandingTabPrefersWaitingThenActiveThenCompleted(DownloadQueueState state, DownloadQueueTabKind expected)
    {
        var viewModel = new DownloadQueueViewModel();
        var item = new DownloadQueueItem { State = state };
        if (state == DownloadQueueState.Waiting) item.StartedAt = null;
        viewModel.ApplySnapshot([item, new() { State = DownloadQueueState.Failed }]);
        viewModel.SelectLandingTab();
        Assert.Equal(expected, viewModel.SelectedTab.Kind);
    }

    [Fact]
    public void LandingTabKeepsWaitingWhenLaterQueuesAreAlsoOccupied()
    {
        var viewModel = new DownloadQueueViewModel();
        viewModel.ApplySnapshot(
        [
            new() { State = DownloadQueueState.Waiting },
            new() { State = DownloadQueueState.Running },
            new() { State = DownloadQueueState.Completed }
        ]);
        viewModel.SelectLandingTab();
        Assert.Equal(DownloadQueueTabKind.Waiting, viewModel.SelectedTab.Kind);
    }

    [Fact]
    public void EmptyQueueLandsOnCompleted()
    {
        var viewModel = new DownloadQueueViewModel();
        viewModel.ApplySnapshot([]);
        viewModel.SelectLandingTab();
        Assert.Equal(DownloadQueueTabKind.Completed, viewModel.SelectedTab.Kind);
    }

    [Fact]
    public void LandingTabDoesNotFollowLaterSnapshotChanges()
    {
        var viewModel = new DownloadQueueViewModel();
        viewModel.ApplySnapshot([new() { State = DownloadQueueState.Completed }]);
        viewModel.SelectLandingTab();
        Assert.Equal(DownloadQueueTabKind.Completed, viewModel.SelectedTab.Kind);

        viewModel.ApplySnapshot([new() { State = DownloadQueueState.Waiting }, new() { State = DownloadQueueState.Completed }]);
        Assert.Equal(DownloadQueueTabKind.Completed, viewModel.SelectedTab.Kind);
        Assert.Equal(DownloadQueueState.Completed, Assert.Single(viewModel.Rows).Item.State);
    }
}
