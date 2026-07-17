using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadHistoryDetailViewModel : ObservableObject
{
    private HistoryRecord? _record;

    public ObservableCollection<DownloadEpisodeResult> Episodes { get; } = [];
    public HistoryRecord? Record
    {
        get => _record;
        private set
        {
            if (!SetProperty(ref _record, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Url));
            OnPropertyChanged(nameof(Timestamp));
            OnPropertyChanged(nameof(CanRetry));
        }
    }
    public string Title => string.IsNullOrWhiteSpace(Record?.Title) ? "批量下载详情" : Record.Title;
    public string Url => Record?.Url ?? string.Empty;
    public string Timestamp => Record?.TimestampText ?? string.Empty;
    public string Summary
    {
        get
        {
            var success = Episodes.Count(item => item.State == DownloadEpisodeResultState.Completed);
            var failed = Episodes.Count(item => item.State == DownloadEpisodeResultState.Failed);
            var cancelled = Episodes.Count(item => item.State == DownloadEpisodeResultState.Cancelled);
            return $"共 {Episodes.Count} 集 · 成功 {success} · 失败 {failed} · 取消 {cancelled}";
        }
    }
    public bool CanRetry => Episodes.Any(item => item.State == DownloadEpisodeResultState.Failed);

    public void Load(HistoryRecord record)
    {
        Record = record;
        Episodes.Clear();
        var episodes = record.DownloadBatch?.Episodes.OrderBy(item => item.PageNumber) ?? Enumerable.Empty<DownloadEpisodeResult>();
        foreach (var episode in episodes) Episodes.Add(episode);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanRetry));
    }

    public HistoryRecord? BuildFailedRetryRecord()
    {
        if (Record?.DownloadBatch is not { } batch) return null;
        var failed = batch.Episodes.Where(item => item.State == DownloadEpisodeResultState.Failed).ToList();
        if (failed.Count == 0) return null;
        return new HistoryRecord
        {
            Id = Record.Id,
            TaskType = TaskKind.DownloadBatch,
            Url = Record.Url,
            Title = Record.Title,
            Timestamp = Record.Timestamp,
            OutputDirectory = Record.OutputDirectory,
            OutputFiles = Record.OutputFiles,
            DownloadBatch = new DownloadBatchHistory { Options = batch.Options, ParsedAt = batch.ParsedAt, Episodes = failed }
        };
    }
}
