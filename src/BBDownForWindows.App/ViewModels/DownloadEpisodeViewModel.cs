using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadEpisodeViewModel : ObservableObject
{
    public sealed class Choice(string value, string label) : ObservableObject
    {
        private string _label = label;
        public string Value { get; } = value;
        public string Label { get => _label; set => SetProperty(ref _label, value); }
    }

    private bool _isSelected;
    private string _selectedQuality = string.Empty;
    private string _selectedEncoding = string.Empty;
    private string _selectedAudio = string.Empty;
    private string _statusText;
    private string _fallbackText = string.Empty;
    private bool _videoManual;
    private bool _audioManual;
    private bool _updating;
    private DownloadMode _downloadMode = DownloadMode.VideoAndAudio;
    private string _relativeOutputPath = string.Empty;
    private VideoStreamSelection? _unavailableRestoredVideo;
    private AudioStreamSelection? _unavailableRestoredAudio;
    private string _restoredBaseFallback = string.Empty;

    public DownloadEpisodeViewModel(DownloadEpisodeInfo episode)
    {
        Episode = episode;
        _statusText = episode.State == DownloadEpisodeParseState.Ready ? "就绪" : episode.Error;
        AudioOptions = episode.AudioStreams
            .Select(item => new Choice(AudioKey(item), $"{item.Codec} · {item.Bitrate}"))
            .ToList();
        InitializeQualityOptions();
    }

    public event EventHandler? SelectionChanged;
    public DownloadEpisodeInfo Episode { get; }
    public int PageNumber => Episode.Page.Number;
    public string PageNumberText => $"P{PageNumber}";
    public string Title => Episode.Page.Title;
    public bool IsReady => Episode.State == DownloadEpisodeParseState.Ready;
    public ObservableCollection<Choice> QualityOptions { get; } = [];
    public IReadOnlyList<Choice> AudioOptions { get; }
    public List<string> EncodingOptions { get; } = [];

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) RaiseSelectionChanged();
        }
    }

    public string SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (IsReady && string.IsNullOrWhiteSpace(value) && QualityOptions.Count > 0) return;
            if (!SetProperty(ref _selectedQuality, value)) return;
            RebuildEncodingOptions();
            if (!_updating)
            {
                _videoManual = true;
                _unavailableRestoredVideo = null;
                RefreshRestoredStreamStatus();
            }
            NotifyEstimatedSizeChanged();
            RaiseSelectionChanged();
        }
    }

    public string SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            if (IsReady && string.IsNullOrWhiteSpace(value) && EncodingOptions.Count > 0) return;
            if (!SetProperty(ref _selectedEncoding, value)) return;
            if (!_updating)
            {
                _videoManual = true;
                _unavailableRestoredVideo = null;
                RefreshRestoredStreamStatus();
            }
            UpdateQualityOptionLabels();
            NotifyEstimatedSizeChanged();
            RaiseSelectionChanged();
        }
    }

    public string SelectedAudio
    {
        get => _selectedAudio;
        set
        {
            if (IsReady && string.IsNullOrWhiteSpace(value) && AudioOptions.Count > 0) return;
            if (!SetProperty(ref _selectedAudio, value)) return;
            if (!_updating)
            {
                _audioManual = true;
                _unavailableRestoredAudio = null;
                RefreshRestoredStreamStatus();
            }
            NotifyEstimatedSizeChanged();
            RaiseSelectionChanged();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string FallbackText { get => _fallbackText; private set { if (SetProperty(ref _fallbackText, value)) OnPropertyChanged(nameof(HasFallback)); } }
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackText);
    public bool VideoSelectionEnabled => IsReady && _downloadMode != DownloadMode.AudioOnly;
    public bool AudioSelectionEnabled => IsReady && _downloadMode != DownloadMode.VideoOnly;

    public long EstimatedSizeBytes
    {
        get
        {
            var video = FindSelectedVideo();
            var audio = FindSelectedAudio();
            return (_downloadMode == DownloadMode.AudioOnly ? 0 : video?.EstimatedSizeBytes ?? 0)
                   + (_downloadMode == DownloadMode.VideoOnly ? 0 : audio?.EstimatedSizeBytes ?? 0);
        }
    }
    public string EstimatedSizeText => MediaEstimateFormatter.FormatBytes(EstimatedSizeBytes);
    public long SelectedVideoEstimatedSizeBytes => FindSelectedVideo()?.EstimatedSizeBytes ?? 0;
    public long SelectedAudioEstimatedSizeBytes => FindSelectedAudio()?.EstimatedSizeBytes ?? 0;
    public string SelectedQualityLabel => QualityOptions.FirstOrDefault(item => item.Value.Equals(SelectedQuality, StringComparison.OrdinalIgnoreCase))?.Label ?? string.Empty;
    public string SelectedAudioLabel => AudioOptions.FirstOrDefault(item => item.Value.Equals(SelectedAudio, StringComparison.OrdinalIgnoreCase))?.Label ?? string.Empty;
    public string SelectedSpecificationText => string.Join(" / ", new[] { SelectedQualityLabel, SelectedEncoding, SelectedAudioLabel }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public void SetDownloadMode(DownloadMode mode)
    {
        _downloadMode = mode;
        OnPropertyChanged(nameof(VideoSelectionEnabled));
        OnPropertyChanged(nameof(AudioSelectionEnabled));
        NotifyEstimatedSizeChanged();
    }

    public void ApplyRule(StreamSelectionRule rule, DownloadMode mode)
    {
        SetDownloadMode(mode);
        if (!IsReady) return;
        StreamSelectionDecision decision;
        try { decision = StreamSelectionPolicy.Select(Episode, rule, mode); }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            IsSelected = false;
            return;
        }
        _updating = true;
        try
        {
            _unavailableRestoredVideo = null;
            _unavailableRestoredAudio = null;
            if (decision.Video is not null)
            {
                SelectedQuality = QualityKey(decision.Video);
                SelectedEncoding = decision.Video.Codec;
            }
            if (decision.Audio is not null) SelectedAudio = AudioKey(decision.Audio);
            _videoManual = false;
            _audioManual = false;
            _restoredBaseFallback = decision.FallbackReason;
            FallbackText = decision.FallbackReason;
        }
        finally { _updating = false; }
        NotifyEstimatedSizeChanged();
    }

    public void ApplyRestored(EpisodeStreamSelection selection, DownloadMode mode)
    {
        SetDownloadMode(mode);
        _updating = true;
        try
        {
            _unavailableRestoredVideo = null;
            _unavailableRestoredAudio = null;
            if (selection.Video is not null)
            {
                var match = Episode.VideoStreams
                    .Where(item => item.Quality.Equals(selection.Video.Quality, StringComparison.OrdinalIgnoreCase)
                                   && item.Resolution.Equals(selection.Video.Resolution, StringComparison.OrdinalIgnoreCase)
                                   && item.Codec.Equals(selection.Video.Codec, StringComparison.OrdinalIgnoreCase)
                                   && (!selection.Video.IsManual || item.BitrateKbps == selection.Video.BitrateKbps))
                    .OrderBy(item => Math.Abs(item.BitrateKbps - selection.Video.BitrateKbps))
                    .FirstOrDefault();
                if (match is not null)
                {
                    SelectedQuality = QualityKey(match);
                    SelectedEncoding = match.Codec;
                    _videoManual = selection.Video.IsManual;
                }
                else if (selection.Video.IsManual)
                {
                    _unavailableRestoredVideo = selection.Video;
                    _videoManual = true;
                }
            }
            if (selection.Audio is not null)
            {
                var audio = Episode.AudioStreams
                    .Where(item => item.Codec.Equals(selection.Audio.Codec, StringComparison.OrdinalIgnoreCase)
                                   && (!selection.Audio.IsManual || item.BitrateKbps == selection.Audio.BitrateKbps))
                    .OrderBy(item => Math.Abs(item.BitrateKbps - selection.Audio.BitrateKbps))
                    .FirstOrDefault();
                if (audio is not null)
                {
                    SelectedAudio = AudioKey(audio);
                    _audioManual = selection.Audio.IsManual;
                }
                else if (selection.Audio.IsManual)
                {
                    _unavailableRestoredAudio = selection.Audio;
                    _audioManual = true;
                }
            }
            FallbackText = selection.FallbackReason;
            _restoredBaseFallback = selection.FallbackReason;
            _relativeOutputPath = selection.RelativeOutputPath;
            IsSelected = true;
            RefreshRestoredStreamStatus();
        }
        finally { _updating = false; }
        NotifyEstimatedSizeChanged();
    }

    public EpisodeStreamSelection BuildSelection()
    {
        var video = FindSelectedVideo();
        var audio = FindSelectedAudio();
        return new EpisodeStreamSelection
        {
            PageNumber = PageNumber,
            PageTitle = Title,
            Video = _downloadMode == DownloadMode.AudioOnly
                ? null
                : _unavailableRestoredVideo ?? (video is null ? null : new VideoStreamSelection(video.Quality, video.Resolution, video.Codec, video.BitrateKbps, _videoManual)),
            Audio = _downloadMode == DownloadMode.VideoOnly
                ? null
                : _unavailableRestoredAudio ?? (audio is null ? null : new AudioStreamSelection(audio.Codec, audio.BitrateKbps, _audioManual)),
            FallbackReason = FallbackText,
            RelativeOutputPath = _relativeOutputPath
        };
    }

    public void ApplyResult(DownloadEpisodeResult result)
    {
        StatusText = result.State switch
        {
            DownloadEpisodeResultState.Pending => "等待下载",
            DownloadEpisodeResultState.Validating => "确认规格",
            DownloadEpisodeResultState.Downloading => "下载中",
            DownloadEpisodeResultState.Muxing => "合并中",
            DownloadEpisodeResultState.Completed => "已完成",
            DownloadEpisodeResultState.Failed => $"失败：{result.Error}",
            DownloadEpisodeResultState.Cancelled => "已取消",
            _ => result.State.ToString()
        };
        _restoredBaseFallback = result.FallbackReason;
        FallbackText = result.FallbackReason;
    }

    public void SetRuntimeStatus(DownloadProgressPhase phase, string message)
    {
        StatusText = phase switch
        {
            DownloadProgressPhase.Validating => "确认规格",
            DownloadProgressPhase.Downloading => "下载中",
            DownloadProgressPhase.Muxing => "合并中",
            DownloadProgressPhase.Completed => "已完成",
            DownloadProgressPhase.Failed => "下载失败",
            DownloadProgressPhase.Cancelled => "已取消",
            _ => message
        };
    }

    private void RebuildEncodingOptions()
    {
        var previous = _selectedEncoding;
        EncodingOptions.Clear();
        foreach (var codec in Episode.VideoStreams.Where(item => QualityKey(item).Equals(SelectedQuality, StringComparison.OrdinalIgnoreCase)).Select(item => item.Codec).Distinct(StringComparer.OrdinalIgnoreCase))
            EncodingOptions.Add(codec);
        OnPropertyChanged(nameof(EncodingOptions));
        var selected = EncodingOptions.FirstOrDefault(item => item.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? EncodingOptions.FirstOrDefault() ?? string.Empty;
        SetProperty(ref _selectedEncoding, selected, nameof(SelectedEncoding));
        UpdateQualityOptionLabels();
    }

    private void InitializeQualityOptions()
    {
        foreach (var item in Episode.VideoStreams
            .GroupBy(QualityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
            QualityOptions.Add(new Choice(QualityKey(item), string.Empty));
        UpdateQualityOptionLabels();
    }

    private void UpdateQualityOptionLabels()
    {
        foreach (var choice in QualityOptions)
        {
            var candidates = Episode.VideoStreams.Where(item => QualityKey(item).Equals(choice.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            var stream = candidates.FirstOrDefault(item => item.Codec.Equals(_selectedEncoding, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
            choice.Label = stream is null
                ? choice.Value
                : $"{stream.Quality} · {FormatResolution(stream.Resolution)} · {stream.Bitrate}";
        }
    }

    private VideoStreamInfo? FindSelectedVideo() => Episode.VideoStreams.FirstOrDefault(item =>
        QualityKey(item).Equals(SelectedQuality, StringComparison.OrdinalIgnoreCase)
        && item.Codec.Equals(SelectedEncoding, StringComparison.OrdinalIgnoreCase));

    private AudioStreamInfo? FindSelectedAudio() => Episode.AudioStreams.FirstOrDefault(item => AudioKey(item).Equals(SelectedAudio, StringComparison.OrdinalIgnoreCase));
    private static string QualityKey(VideoStreamInfo stream) => $"{stream.Quality}\u001f{stream.Resolution}";
    private static string AudioKey(AudioStreamInfo stream) => $"{stream.Index}\u001f{stream.Codec}\u001f{stream.BitrateKbps}";
    private static string FormatResolution(string resolution) => resolution.Replace("x", "×", StringComparison.OrdinalIgnoreCase);
    private void RefreshRestoredStreamStatus()
    {
        var missing = new List<string>();
        if (_unavailableRestoredVideo is not null) missing.Add("历史中的手动视频规格已不可用");
        if (_unavailableRestoredAudio is not null) missing.Add("历史中的手动音频规格已不可用");
        if (missing.Count == 0)
        {
            if (StatusText == "历史手动规格已失效") StatusText = "就绪";
            FallbackText = _restoredBaseFallback;
            return;
        }
        StatusText = "历史手动规格已失效";
        var issue = string.Join("；", missing);
        FallbackText = string.IsNullOrWhiteSpace(_restoredBaseFallback) ? issue : $"{_restoredBaseFallback}；{issue}";
    }
    private void NotifyEstimatedSizeChanged()
    {
        OnPropertyChanged(nameof(EstimatedSizeBytes));
        OnPropertyChanged(nameof(EstimatedSizeText));
        OnPropertyChanged(nameof(SelectedVideoEstimatedSizeBytes));
        OnPropertyChanged(nameof(SelectedAudioEstimatedSizeBytes));
        OnPropertyChanged(nameof(SelectedQualityLabel));
        OnPropertyChanged(nameof(SelectedAudioLabel));
        OnPropertyChanged(nameof(SelectedSpecificationText));
    }
    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
