using BBDownForWindows.Core;

namespace BBDownForWindows.App.ViewModels;

public sealed class RenameHistoryDetailViewModel
{
    public RenameHistoryDetailViewModel(RenameHistoryRecord record)
    {
        Record = record;
        Operations = record.Operations
            .Select((operation, index) => new RenameHistoryOperationDetailViewModel(operation, index + 1))
            .ToList();

        var mediaFileCount = Operations.Count(operation => !operation.IsSidecar);
        var sidecarCount = Operations.Count - mediaFileCount;
        OperationSummary = sidecarCount == 0
            ? $"{mediaFileCount} 个媒体文件"
            : $"{mediaFileCount} 个媒体文件 · {sidecarCount} 个关联文件";
    }

    public RenameHistoryRecord Record { get; }
    public IReadOnlyList<RenameHistoryOperationDetailViewModel> Operations { get; }
    public string Title => FirstPresent(Record.ChineseTitle, Record.EnglishTitle, Path.GetFileName(Record.DirectoryPath), "未命名记录");
    public string HeaderSubtitle => !string.IsNullOrWhiteSpace(Record.EnglishTitle) &&
                                    !Record.EnglishTitle.Equals(Record.ChineseTitle, StringComparison.OrdinalIgnoreCase)
        ? Record.EnglishTitle
        : HeaderMetadata;
    public string HeaderMetadata => Record.MediaType == RenameMediaType.Series
        ? $"剧集 · {ValueOrUnknown(Record.Year)} · 第 {Math.Max(1, Record.Season)} 季"
        : $"电影 · {ValueOrUnknown(Record.Year)}";
    public string ChineseTitleText => ValueOrUnknown(Record.ChineseTitle);
    public string EnglishTitleText => !string.IsNullOrWhiteSpace(Record.EnglishTitle) &&
                                      Record.EnglishTitle.Equals(Record.ChineseTitle, StringComparison.OrdinalIgnoreCase)
        ? "同中文片名"
        : ValueOrUnknown(Record.EnglishTitle);
    public string MediaTypeText => Record.MediaType == RenameMediaType.Series ? "剧集" : "电影";
    public string YearText => ValueOrUnknown(Record.Year);
    public string SeasonText => Record.MediaType == RenameMediaType.Series ? $"第 {Math.Max(1, Record.Season)} 季" : "不适用";
    public string TemplateText => ValueOrUnknown(Record.TemplateName);
    public string CreatedAtText => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string UndoneAtText => Record.UndoneAt is null ? "—" : Record.UndoneAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string StateText => Record.UndoneAt is null ? "已执行" : "已撤销";
    public string DirectoryPath => ValueOrUnknown(Record.DirectoryPath);
    public string OperationCountText => $"{Operations.Count} 项变更";
    public string OperationSummary { get; }

    private static string FirstPresent(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value));

    private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "未记录" : value;
}

public sealed class RenameHistoryOperationDetailViewModel
{
    public RenameHistoryOperationDetailViewModel(RenameFileOperation operation, int sequence)
    {
        SourcePath = operation.SourcePath;
        TargetPath = operation.TargetPath;
        SourceName = Path.GetFileName(operation.SourcePath);
        TargetName = Path.GetFileName(operation.TargetPath);
        SequenceText = sequence.ToString("D2");
        IsSidecar = operation.IsSidecar;
    }

    public string SequenceText { get; }
    public string KindText => IsSidecar ? "关联文件" : "媒体文件";
    public bool IsSidecar { get; }
    public string SourceName { get; }
    public string TargetName { get; }
    public string SourcePath { get; }
    public string TargetPath { get; }
}
