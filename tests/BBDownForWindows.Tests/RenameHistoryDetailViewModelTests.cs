using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class RenameHistoryDetailViewModelTests
{
    [Fact]
    public void BuildsCompleteSeriesSummaryAndClassifiesOperations()
    {
        var record = new RenameHistoryRecord
        {
            ChineseTitle = "银河之心",
            EnglishTitle = "Foreordination",
            DirectoryPath = @"D:\视频\银河之心",
            MediaType = RenameMediaType.Series,
            Year = "2022",
            Season = 2,
            TemplateName = "中英文剧集",
            CreatedAt = new DateTimeOffset(2026, 7, 19, 16, 49, 12, TimeSpan.FromHours(8)),
            Operations =
            [
                new RenameFileOperation(@"D:\视频\银河之心\01.mp4", @"D:\视频\银河之心\银河之心.S02E01.mp4"),
                new RenameFileOperation(@"D:\视频\银河之心\01.srt", @"D:\视频\银河之心\银河之心.S02E01.srt", true)
            ]
        };

        var viewModel = new RenameHistoryDetailViewModel(record);

        Assert.Equal("银河之心", viewModel.Title);
        Assert.Equal("Foreordination", viewModel.HeaderSubtitle);
        Assert.Equal("剧集", viewModel.MediaTypeText);
        Assert.Equal("第 2 季", viewModel.SeasonText);
        Assert.Equal("2 项变更", viewModel.OperationCountText);
        Assert.Equal("1 个媒体文件 · 1 个关联文件", viewModel.OperationSummary);
        Assert.Equal("媒体文件", viewModel.Operations[0].KindText);
        Assert.Equal("关联文件", viewModel.Operations[1].KindText);
        Assert.Equal("银河之心.S02E01.srt", viewModel.Operations[1].TargetName);
    }

    [Fact]
    public void AvoidsRepeatingIdenticalChineseAndEnglishTitleInHeader()
    {
        var record = new RenameHistoryRecord
        {
            ChineseTitle = "九九八十一",
            EnglishTitle = "九九八十一",
            DirectoryPath = @"D:\视频\九九八十一",
            MediaType = RenameMediaType.Series,
            Year = "2021",
            Season = 1,
            Operations = [new RenameFileOperation("old.mp4", "new.mp4")]
        };

        var viewModel = new RenameHistoryDetailViewModel(record);

        Assert.Equal("九九八十一", viewModel.Title);
        Assert.Equal("剧集 · 2021 · 第 1 季", viewModel.HeaderSubtitle);
        Assert.Equal("同中文片名", viewModel.EnglishTitleText);
        Assert.Equal("1 个媒体文件", viewModel.OperationSummary);
        Assert.Equal("—", viewModel.UndoneAtText);
    }
}
