using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BBDownForWindows.App.ViewModels;

public sealed class DualAudioPairViewModel : ObservableObject
{
    public sealed record EpisodeChoice(int PageNumber, string Label, DownloadEpisodeInfo Episode);

    private StreamSelectionRule _sourceARule;
    private StreamSelectionRule _sourceBRule;
    private bool _isSelected;
    private string _mainVideoModeText = "推荐";
    private DualAudioSource _mainVideoSource = DualAudioSource.A;
    private string _recommendationReason = string.Empty;
    private bool _useCustomDelay;
    private double _delayValue;
    private string _statusText;
    private int _selectedSourceBPageNumber;
    private bool _suppressReassign;

    public DualAudioPairViewModel(DualAudioEpisodePair pair, IReadOnlyList<EpisodeChoice> sourceBChoices,
        StreamSelectionRule sourceARule, StreamSelectionRule sourceBRule, int globalDelay)
    {
        PairNumber = pair.PairNumber;
        SourceAInfo = pair.SourceA;
        SourceBChoices = sourceBChoices;
        _sourceARule = sourceARule;
        _sourceBRule = sourceBRule;
        _delayValue = globalDelay;
        _statusText = pair.IsPaired ? "就绪" : "未配对";
        _isSelected = pair.IsSelected && pair.IsPaired;
        if (pair.SourceA is not null)
        {
            SourceA = new DownloadEpisodeViewModel(pair.SourceA);
            SourceA.SelectionChanged += Source_SelectionChanged;
            SourceA.ApplyRule(sourceARule, DownloadMode.VideoAndAudio);
        }
        AssignSourceB(pair.SourceB, sourceBRule, false);
        UpdateRecommendation();
    }

    public event EventHandler<int>? SourceBReassignRequested;
    public int PairNumber { get; }
    public string PairText => $"第 {PairNumber} 对";
    public DownloadEpisodeInfo? SourceAInfo { get; }
    public DownloadEpisodeInfo? SourceBInfo => SourceB?.Episode;
    public DownloadEpisodeViewModel? SourceA { get; }
    public DownloadEpisodeViewModel? SourceB { get; private set; }
    public IReadOnlyList<EpisodeChoice> SourceBChoices { get; }
    public string SourceATitle => SourceAInfo?.Page.Title ?? "缺少来源 A";
    public string SourceBTitle => SourceB?.Title ?? "未配对";
    public IReadOnlyList<string> MainVideoModes { get; } = ["推荐", "来源 A", "来源 B"];

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(IsDownloadable)); }
    }

    public bool IsDownloadable => SourceA is not null && SourceB is not null
                                  && !SourceA.Episode.IsMuxedStream && !SourceB.Episode.IsMuxedStream;
    public int SelectedSourceBPageNumber
    {
        get => _selectedSourceBPageNumber;
        set
        {
            if (!SetProperty(ref _selectedSourceBPageNumber, value) || _suppressReassign) return;
            SourceBReassignRequested?.Invoke(this, value);
        }
    }

    public string MainVideoModeText
    {
        get => _mainVideoModeText;
        set { if (SetProperty(ref _mainVideoModeText, value)) UpdateRecommendation(); }
    }

    public DualAudioSource MainVideoSource
    {
        get => _mainVideoSource;
        private set { if (SetProperty(ref _mainVideoSource, value)) { OnPropertyChanged(nameof(MainVideoSourceText)); NotifySizeChanged(); } }
    }
    public string MainVideoSourceText => MainVideoSource == DualAudioSource.A ? "来源 A" : "来源 B";
    public string RecommendationReason { get => _recommendationReason; private set => SetProperty(ref _recommendationReason, value); }
    public bool UseCustomDelay { get => _useCustomDelay; set => SetProperty(ref _useCustomDelay, value); }
    public double DelayValue { get => _delayValue; set => SetProperty(ref _delayValue, Math.Clamp(value, -10000, 10000)); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public long EstimatedSizeBytes =>
        ((MainVideoSource == DualAudioSource.A ? SourceA?.SelectedVideoEstimatedSizeBytes : SourceB?.SelectedVideoEstimatedSizeBytes) ?? 0)
        + (SourceA?.SelectedAudioEstimatedSizeBytes ?? 0)
        + (SourceB?.SelectedAudioEstimatedSizeBytes ?? 0);
    public string EstimatedSizeText => MediaEstimateFormatter.FormatBytes(EstimatedSizeBytes);

    public void ApplyRules(StreamSelectionRule sourceARule, StreamSelectionRule sourceBRule, string mainVideoMode, int globalDelay)
    {
        _sourceARule = sourceARule;
        SourceA?.ApplyRule(sourceARule, DownloadMode.VideoAndAudio);
        _sourceBRule = sourceBRule;
        SourceB?.ApplyRule(sourceBRule, DownloadMode.VideoAndAudio);
        MainVideoModeText = mainVideoMode;
        if (!UseCustomDelay) DelayValue = globalDelay;
        UpdateRecommendation();
    }

    public void AssignSourceB(DownloadEpisodeInfo? episode, StreamSelectionRule? rule = null, bool notifyRequest = false)
    {
        _suppressReassign = true;
        try
        {
            if (SourceB is not null) SourceB.SelectionChanged -= Source_SelectionChanged;
            SourceB = episode is null ? null : new DownloadEpisodeViewModel(episode);
            if (SourceB is not null)
            {
                SourceB.SelectionChanged += Source_SelectionChanged;
                SourceB.ApplyRule(rule ?? _sourceBRule, DownloadMode.VideoAndAudio);
            }
            SelectedSourceBPageNumber = episode?.Page.Number ?? 0;
            OnPropertyChanged(nameof(SourceB));
            OnPropertyChanged(nameof(SourceBInfo));
            OnPropertyChanged(nameof(SourceBTitle));
            OnPropertyChanged(nameof(IsDownloadable));
            if (!IsDownloadable) IsSelected = false;
            StatusText = SourceA is not null && SourceB is not null && !IsDownloadable
                ? "旧式合流分段不支持多音轨"
                : IsDownloadable ? "就绪" : "未配对";
            UpdateRecommendation();
        }
        finally { _suppressReassign = false; }
        if (notifyRequest) SourceBReassignRequested?.Invoke(this, SelectedSourceBPageNumber);
    }

    public DualAudioPairSelection BuildSelection(int globalDelay)
    {
        if (SourceA is null || SourceB is null) throw new InvalidOperationException($"第 {PairNumber} 对尚未完成配对");
        if (!IsDownloadable) throw new InvalidOperationException("旧式音视频合流分段暂不支持多音轨拆分");
        var sourceA = SourceA.BuildSelection();
        var sourceB = SourceB.BuildSelection();
        return new DualAudioPairSelection
        {
            PairNumber = PairNumber,
            SourceAPageNumber = sourceA.PageNumber,
            SourceAPageTitle = sourceA.PageTitle,
            SourceBPageNumber = sourceB.PageNumber,
            SourceBPageTitle = sourceB.PageTitle,
            SourceA = sourceA,
            SourceB = sourceB,
            MainVideoMode = MainVideoModeText switch
            {
                "来源 A" => DualAudioMainVideoMode.SourceA,
                "来源 B" => DualAudioMainVideoMode.SourceB,
                _ => DualAudioMainVideoMode.Recommended
            },
            MainVideoSource = MainVideoSource,
            RecommendationReason = RecommendationReason,
            SourceBDelayOverrideMs = UseCustomDelay ? checked((int)DelayValue) : null,
            IsSelected = IsSelected
        };
    }

    public void ApplyResult(DualAudioPairResult result)
    {
        StatusText = result.StatusText;
        MainVideoSource = result.MainVideoSource;
        RecommendationReason = result.RecommendationReason;
    }

    public void SetRuntimeStatus(DualAudioProgressSnapshot update)
    {
        StatusText = update.Phase switch
        {
            DualAudioProgressPhase.Validating => "确认规格",
            DualAudioProgressPhase.DownloadingSourceA => "下载来源 A",
            DualAudioProgressPhase.DownloadingSourceB => "下载来源 B",
            DualAudioProgressPhase.Muxing => "封装中",
            DualAudioProgressPhase.Completed => "已完成",
            DualAudioProgressPhase.Failed => "失败",
            DualAudioProgressPhase.Cancelled => "已取消",
            _ => update.Message
        };
    }

    private void Source_SelectionChanged(object? sender, EventArgs e) => UpdateRecommendation();

    private void UpdateRecommendation()
    {
        if (SourceA is null || SourceB is null)
        {
            RecommendationReason = "请先完成分集配对";
            MainVideoSource = DualAudioSource.A;
            NotifySizeChanged();
            return;
        }
        if (SourceA.Episode.IsMuxedStream || SourceB.Episode.IsMuxedStream)
        {
            RecommendationReason = "旧式音视频合流分段暂不支持多音轨拆分";
            MainVideoSource = DualAudioSource.A;
            NotifySizeChanged();
            return;
        }
        var sourceA = SourceA.BuildSelection().Video;
        var sourceB = SourceB.BuildSelection().Video;
        if (sourceA is null || sourceB is null)
        {
            RecommendationReason = "双方都需要选择视频规格";
            NotifySizeChanged();
            return;
        }
        var recommendation = DualAudioRecommendationPolicy.Recommend(sourceA, sourceB, _sourceARule.PreferredEncoding);
        MainVideoSource = MainVideoModeText switch
        {
            "来源 A" => DualAudioSource.A,
            "来源 B" => DualAudioSource.B,
            _ => recommendation.Source
        };
        RecommendationReason = MainVideoModeText == "推荐"
            ? recommendation.Reason
            : $"已固定使用{MainVideoModeText}主视频";
        NotifySizeChanged();
    }

    private void NotifySizeChanged()
    {
        OnPropertyChanged(nameof(EstimatedSizeBytes));
        OnPropertyChanged(nameof(EstimatedSizeText));
    }
}
