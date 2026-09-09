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
