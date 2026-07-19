using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadHistoryDetailViewModel : ObservableObject
{
    private readonly AppServices _services;
    private HistoryRecord? _record;
    private bool _canRemux;

    public DownloadHistoryDetailViewModel(AppServices services) => _services = services;

    public ObservableCollection<DownloadEpisodeResult> Episodes { get; } = [];
    public ObservableCollection<DualAudioPairResult> DualAudioPairs { get; } = [];
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
            OnPropertyChanged(nameof(CanRemux));
            OnPropertyChanged(nameof(RemuxVisibility));
            OnPropertyChanged(nameof(DownloadVisibility));
            OnPropertyChanged(nameof(DualAudioVisibility));
        }
    }
    public string Title => string.IsNullOrWhiteSpace(Record?.Title) ? (Record?.DualAudioBatch is null ? "批量下载详情" : "多音轨详情") : Record.Title;
    public string Url => Record?.Url ?? string.Empty;
    public string Timestamp => Record?.TimestampText ?? string.Empty;
    public string Summary
    {
        get
        {
            if (Record?.DualAudioBatch is not null)
            {
                var dualSuccess = DualAudioPairs.Count(item => item.State == DualAudioPairState.Completed);
                var dualFailed = DualAudioPairs.Count(item => item.State == DualAudioPairState.Failed);
                var dualCancelled = DualAudioPairs.Count(item => item.State == DualAudioPairState.Cancelled);
                return $"共 {DualAudioPairs.Count} 对 · 成功 {dualSuccess} · 失败 {dualFailed} · 取消 {dualCancelled}";
            }
            var success = Episodes.Count(item => item.State == DownloadEpisodeResultState.Completed);
            var failed = Episodes.Count(item => item.State == DownloadEpisodeResultState.Failed);
            var cancelled = Episodes.Count(item => item.State == DownloadEpisodeResultState.Cancelled);
            return $"共 {Episodes.Count} 集 · 成功 {success} · 失败 {failed} · 取消 {cancelled}";
        }
    }
    public bool CanRetry => Episodes.Any(item => item.State == DownloadEpisodeResultState.Failed)
                            || DualAudioPairs.Any(item => item.State == DualAudioPairState.Failed);
    public bool CanRemux { get => _canRemux; private set => SetProperty(ref _canRemux, value); }
    public string RemuxDirectory
    {
        get
        {
            var manifest = Record?.DualAudioBatch?.ManifestPath;
            if (!string.IsNullOrWhiteSpace(manifest)) return Path.GetDirectoryName(manifest) ?? string.Empty;
            var output = Record?.OutputDirectory;
            return string.IsNullOrWhiteSpace(output) ? string.Empty : Directory.GetParent(output)?.FullName ?? string.Empty;
        }
    }
    public Visibility RemuxVisibility => Record?.DualAudioBatch is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DownloadVisibility => Record?.DownloadBatch is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DualAudioVisibility => Record?.DualAudioBatch is null ? Visibility.Collapsed : Visibility.Visible;

    public async Task LoadAsync(HistoryRecord record)
    {
        CanRemux = false;
        Record = record;
        Episodes.Clear();
        DualAudioPairs.Clear();
        var episodes = record.DownloadBatch?.Episodes.OrderBy(item => item.PageNumber) ?? Enumerable.Empty<DownloadEpisodeResult>();
        foreach (var episode in episodes) Episodes.Add(episode);
        var pairs = record.DualAudioBatch?.Pairs.OrderBy(item => item.PairNumber) ?? Enumerable.Empty<DualAudioPairResult>();
        foreach (var pair in pairs) DualAudioPairs.Add(pair);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemux));
        OnPropertyChanged(nameof(RemuxVisibility));
        if (record.DualAudioBatch is not null && !string.IsNullOrWhiteSpace(RemuxDirectory))
            CanRemux = (await _services.DualAudio.InspectExistingAsync(RemuxDirectory)).CanRemux;
    }

    public HistoryRecord? BuildFailedRetryRecord()
    {
        if (Record?.DualAudioBatch is { } dual)
        {
            var failedPairs = dual.Pairs.Where(item => item.State == DualAudioPairState.Failed).ToList();
            if (failedPairs.Count == 0) return null;
            var failedNumbers = failedPairs.Select(item => item.PairNumber).ToHashSet();
            var request = dual.Request;
            var retryRequest = new DualAudioBatchRequest
            {
                SourceMode = request.SourceMode,
                SourceAUrl = request.SourceAUrl,
                SourceBUrl = request.SourceBUrl,
                SourceATitle = request.SourceATitle,
                SourceBTitle = request.SourceBTitle,
                ApiMode = request.ApiMode,
                Options = request.Options,
                Pairs = request.Pairs.Where(item => failedNumbers.Contains(item.PairNumber)).ToList(),
                SourceALabel = request.SourceALabel,
                SourceBLabel = request.SourceBLabel,
                SourceALanguage = request.SourceALanguage,
                SourceBLanguage = request.SourceBLanguage,
                DefaultAudioSource = request.DefaultAudioSource,
                SourceBDelayMs = request.SourceBDelayMs,
                WorkDirectory = request.WorkDirectory,
                MkvmergePath = request.MkvmergePath,
                KeepSourceFiles = request.KeepSourceFiles
            };
            return new HistoryRecord
            {
                Id = Record.Id,
                TaskType = TaskKind.DualAudioMux,
                Url = Record.Url,
                SecondaryUrl = Record.SecondaryUrl,
                Title = Record.Title,
                Timestamp = Record.Timestamp,
                OutputDirectory = Record.OutputDirectory,
                OutputFiles = Record.OutputFiles,
                DualAudioBatch = new DualAudioBatchHistory { Request = retryRequest, Pairs = failedPairs, ManifestPath = dual.ManifestPath }
            };
        }
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
            DownloadBatch = new DownloadBatchHistory
            {
                Options = batch.Options,
                ParsedAt = batch.ParsedAt,
                DownloadedAt = batch.DownloadedAt,
                TotalPages = batch.TotalPages,
                NamingProfileKind = batch.NamingProfileKind,
                NamingProfile = (batch.NamingProfile ?? DownloadNamingProfile.Default()).Clone(),
                Episodes = failed
            }
        };
    }
}
