using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadEpisodeViewModel : ObservableObject
{
    private const string MuxedAudioValue = "\u001fmuxed-audio";

    public sealed class Choice(string value, string label, string apiMode = "", bool isSelectable = true) : ObservableObject
    {
        private string _label = label;
        public string Value { get; } = value;
        public string ApiMode { get; } = apiMode;
        public bool IsSelectable { get; } = isSelectable;
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
    private StreamSelectionRule _rule = new("4K 超高清", "auto", "auto", AudioBitratePriority.Highest);

    public DownloadEpisodeViewModel(DownloadEpisodeInfo episode)
    {
        // Rows may share a catalog (for example a reused multi-audio source). Keep their active session independent.
        Episode = new DownloadEpisodeInfo
        {
            Page = episode.Page, VideoStreams = episode.VideoStreams, AudioStreams = episode.AudioStreams,
            IsMuxedStream = episode.IsMuxedStream, ApiMode = episode.ApiMode, SessionReason = episode.SessionReason,
            SourceWarning = episode.SourceWarning, Sources = episode.Sources, State = episode.State, Error = episode.Error
        };
        _statusText = episode.State == DownloadEpisodeParseState.Ready ? ReadyText : episode.Error;
        AudioOptions = episode.IsMuxedStream
            ? [new Choice(MuxedAudioValue, "内封音频（合流）")]
            : episode.AudioStreams.Select(item => new Choice(AudioKey(item), $"{item.Codec} · {item.Bitrate}")).ToList();
        if (episode.IsMuxedStream) _selectedAudio = MuxedAudioValue;
        InitializeQualityOptions();
    }

    public event EventHandler? SelectionChanged;
    public DownloadEpisodeInfo Episode { get; }
    public int PageNumber => Episode.Page.Number;
    public string PageNumberText => $"P{PageNumber}";
    public string Title => Episode.Page.Title;
    public bool IsReady => Episode.State == DownloadEpisodeParseState.Ready;
    public ObservableCollection<Choice> QualityOptions { get; } = [];
    public IReadOnlyList<Choice> AudioOptions { get; private set; }
    private string ReadyText => string.IsNullOrWhiteSpace(Episode.ApiMode) ? "就绪" : $"就绪 · {Episode.ApiMode}";
    public ObservableCollection<Choice> EncodingOptions { get; } = [];

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
            var choice = QualityOptions.FirstOrDefault(c => c.Value == value);
            if (choice is { IsSelectable: false }) return;
            if (!SetProperty(ref _selectedQuality, value)) return;
            var changedSource = choice is not null && choice.ApiMode != Episode.ApiMode
                && Episode.Sources.Any(s => s.ApiMode == choice.ApiMode);
            if (changedSource)
            {
                DownloadSessionPolicy.Apply(Episode, Episode.Sources.Single(s => s.ApiMode == choice!.ApiMode));
                _unavailableRestoredVideo = null;
                _unavailableRestoredAudio = null;
                _restoredBaseFallback = string.Empty;
                FallbackText = string.Empty;
                RebuildAudioOptions();
                try
                {
                    var decision = StreamSelectionPolicy.Select(Episode, _rule with
                    {
                        QualityRule = Episode.VideoStreams.First(v => QualityKey(v, Episode.ApiMode) == value).Quality,
                        PreferredEncoding = "auto"
                    }, _downloadMode);
                    _selectedAudio = decision.Audio is null ? Episode.IsMuxedStream ? MuxedAudioValue : string.Empty : AudioKey(decision.Audio);
                    FallbackText = decision.FallbackReason;
                    _restoredBaseFallback = decision.FallbackReason;
                    StatusText = ReadyText;
                }
                catch (InvalidOperationException exception)
                {
                    _selectedAudio = string.Empty;
                    StatusText = exception.Message;
                    IsSelected = false;
                }
                _audioManual = false;
                OnPropertyChanged(nameof(SelectedAudio));
                OnPropertyChanged(nameof(VideoSelectionEnabled));
                OnPropertyChanged(nameof(AudioSelectionEnabled));
            }
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
    public string FallbackText { get => _fallbackText; private set { if (SetProperty(ref _fallbackText, value)) { OnPropertyChanged(nameof(HasFallback)); OnPropertyChanged(nameof(NoticeText)); OnPropertyChanged(nameof(HasNotice)); } } }
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackText);
    public string NoticeText => string.Join("；", new[] { Episode.SourceWarning, FallbackText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeText);
    public bool QualitySelectionEnabled => IsReady && (VideoSelectionEnabled || Episode.Sources.Count > 1);
    public bool VideoSelectionEnabled => IsReady && (Episode.IsMuxedStream || _downloadMode != DownloadMode.AudioOnly);
    public bool AudioSelectionEnabled => IsReady && !Episode.IsMuxedStream && _downloadMode != DownloadMode.VideoOnly;

    public long EstimatedSizeBytes
    {
        get
        {
            var video = FindSelectedVideo();
            var audio = FindSelectedAudio();
            if (Episode.IsMuxedStream) return video?.EstimatedSizeBytes ?? 0;
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
        OnPropertyChanged(nameof(QualitySelectionEnabled));
        NotifyEstimatedSizeChanged();
    }

    public void ApplyRule(StreamSelectionRule rule, DownloadMode mode)
    {
        _rule = rule;
        SetDownloadMode(mode);
        if (!IsReady) return;
        var previousApi = Episode.ApiMode;
        DownloadSessionPolicy.Select(Episode, rule, mode);
        if (previousApi != Episode.ApiMode) RefreshSourceChoices();
        else RefreshAvailableSources();
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
                SelectedQuality = QualityKey(decision.Video, Episode.ApiMode);
                SelectedEncoding = decision.Video.Codec;
            }
            else if (mode == DownloadMode.AudioOnly && Episode.VideoStreams.FirstOrDefault() is { } first)
                SelectedQuality = QualityKey(first, Episode.ApiMode);
            if (decision.Audio is not null) SelectedAudio = AudioKey(decision.Audio);
            else if (Episode.IsMuxedStream) SelectedAudio = MuxedAudioValue;
            _videoManual = false;
            _audioManual = false;
            _restoredBaseFallback = string.Join("；", new[] { Episode.SessionReason, decision.FallbackReason }.Where(s => !string.IsNullOrWhiteSpace(s)));
            FallbackText = _restoredBaseFallback;
            StatusText = ReadyText;
        }
        finally { _updating = false; }
        NotifyEstimatedSizeChanged();
    }

    public void ApplyRestored(EpisodeStreamSelection selection, DownloadMode mode)
    {
        SetDownloadMode(mode);
        if (!string.IsNullOrWhiteSpace(selection.ApiMode) && Episode.ApiMode != selection.ApiMode && Episode.Sources.Count > 0)
        {
            var original = DownloadSessionPolicy.ForSelection(Episode, selection.ApiMode);
            Episode.ApiMode = original.ApiMode; Episode.VideoStreams = original.VideoStreams;
            Episode.AudioStreams = original.AudioStreams; Episode.IsMuxedStream = original.IsMuxedStream;
            RefreshSourceChoices();
        }
        _updating = true;
        try
        {
            _unavailableRestoredVideo = null;
            _unavailableRestoredAudio = null;
            if (Episode.IsMuxedStream) SelectedAudio = MuxedAudioValue;
            if (selection.Video is not null)
            {
                var match = Episode.VideoStreams
                    .Where(item => StreamSelectionPolicy.AreEquivalentQualities(item.Quality, selection.Video.Quality)
                                   && item.Resolution.Equals(selection.Video.Resolution, StringComparison.OrdinalIgnoreCase)
                                   && item.Codec.Equals(selection.Video.Codec, StringComparison.OrdinalIgnoreCase)
                                   && (!selection.Video.IsManual || item.BitrateKbps == selection.Video.BitrateKbps))
                    .OrderBy(item => Math.Abs(item.BitrateKbps - selection.Video.BitrateKbps))
                    .FirstOrDefault();
                if (match is not null)
                {
                    SelectedQuality = QualityKey(match, Episode.ApiMode);
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
            if (StatusText != "历史手动规格已失效") StatusText = ReadyText;
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
            ApiMode = Episode.ApiMode,
            PageNumber = PageNumber,
            PageTitle = Title,
            Video = _downloadMode == DownloadMode.AudioOnly && !Episode.IsMuxedStream
                ? null
                : _unavailableRestoredVideo ?? (video is null ? null : new VideoStreamSelection(video.Quality, video.Resolution, video.Codec, video.BitrateKbps, _videoManual)),
            Audio = _downloadMode == DownloadMode.VideoOnly || Episode.IsMuxedStream
                ? null
                : _unavailableRestoredAudio ?? (audio is null ? null : new AudioStreamSelection(audio.Codec, audio.BitrateKbps, _audioManual)),
            IsMuxedStream = Episode.IsMuxedStream,
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
        foreach (var codec in Episode.VideoStreams.Where(item => QualityKey(item, Episode.ApiMode).Equals(SelectedQuality, StringComparison.OrdinalIgnoreCase)).Select(item => item.Codec).Distinct(StringComparer.OrdinalIgnoreCase))
            EncodingOptions.Add(new Choice(codec, codec));
        var selected = EncodingOptions.FirstOrDefault(item => item.Value.Equals(previous, StringComparison.OrdinalIgnoreCase))?.Value
                       ?? EncodingOptions.FirstOrDefault()?.Value
                       ?? string.Empty;
        SetProperty(ref _selectedEncoding, selected, nameof(SelectedEncoding));
        UpdateQualityOptionLabels();
    }

    private void InitializeQualityOptions()
    {
        var sources = QualitySources();
        var grouped = sources.Any(s => s.ApiMode == "TV");
        foreach (var source in sources)
        {
            if (grouped) QualityOptions.Add(new Choice($"\u001fheader-{source.ApiMode}", source.ApiMode == "TV" ? "TV 画质" : "WEB 画质", source.ApiMode, false));
            foreach (var item in source.VideoStreams.GroupBy(v => QualityKey(v, source.ApiMode), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                QualityOptions.Add(new Choice(QualityKey(item, source.ApiMode), string.Empty, source.ApiMode));
        }
        UpdateQualityOptionLabels();
    }

    private List<DownloadEpisodeSource> QualitySources() => Episode.Sources.Count == 0
        ? [new(Episode.ApiMode, Episode.VideoStreams, Episode.AudioStreams, Episode.IsMuxedStream)]
        : Episode.Sources.OrderBy(s => s.ApiMode == "WEB" ? 0 : 1).ToList();

    public void RefreshAvailableSources(DownloadEpisodeInfo? latest = null)
    {
        if (latest is not null && latest.Page.Cid == Episode.Page.Cid)
        {
            Episode.SourceWarning = latest.SourceWarning;
            if (!ReferenceEquals(Episode.Sources, latest.Sources))
            {
                Episode.Sources.Clear();
                Episode.Sources.AddRange(latest.Sources);
            }
        }
        var sources = QualitySources();
        var grouped = sources.Any(source => source.ApiMode == "TV");
        var desired = new List<Choice>();
        foreach (var source in sources)
        {
            if (grouped) desired.Add(new($"\u001fheader-{source.ApiMode}", source.ApiMode == "TV" ? "TV 画质" : "WEB 画质", source.ApiMode, false));
            foreach (var stream in source.VideoStreams.DistinctBy(stream => QualityKey(stream, source.ApiMode)))
                desired.Add(new(QualityKey(stream, source.ApiMode), string.Empty, source.ApiMode));
        }
        // Keep the selected WEB item in the collection while TV options arrive.
        // Clearing a bound ComboBox resets its selection and dependent encoding list.
        var values = desired.Select(choice => choice.Value).ToHashSet();
        for (var index = QualityOptions.Count - 1; index >= 0; index--)
            if (!values.Contains(QualityOptions[index].Value)) QualityOptions.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var existing = QualityOptions.FirstOrDefault(choice => choice.Value == desired[index].Value);
            if (existing is null) QualityOptions.Insert(index, desired[index]);
            else if (QualityOptions.IndexOf(existing) != index) QualityOptions.Move(QualityOptions.IndexOf(existing), index);
        }
        UpdateQualityOptionLabels();
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(QualitySelectionEnabled));
    }

    private void UpdateQualityOptionLabels()
    {
        foreach (var choice in QualityOptions)
        {
            if (!choice.IsSelectable) continue;
            var source = QualitySources().First(s => s.ApiMode == choice.ApiMode);
            var candidates = source.VideoStreams.Where(item => QualityKey(item, source.ApiMode).Equals(choice.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            var stream = candidates.FirstOrDefault(item => item.Codec.Equals(_selectedEncoding, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
            choice.Label = stream is null
                ? choice.Value
                : $"{(choice.ApiMode == "TV" ? "TV · " : "")}{StreamSelectionPolicy.NormalizeQualityRule(stream.Quality)} · {FormatResolution(stream.Resolution)} · {stream.Bitrate}";
        }
    }

    private void RefreshSourceChoices()
    {
        _updating = true;
        try
        {
            QualityOptions.Clear(); EncodingOptions.Clear();
            RebuildAudioOptions();
            InitializeQualityOptions();
            RebuildEncodingOptions();
            OnPropertyChanged(nameof(VideoSelectionEnabled));
            OnPropertyChanged(nameof(AudioSelectionEnabled));
            OnPropertyChanged(nameof(QualitySelectionEnabled));
        }
        finally { _updating = false; }
    }

    private VideoStreamInfo? FindSelectedVideo() => Episode.VideoStreams.FirstOrDefault(item =>
        QualityKey(item, Episode.ApiMode).Equals(SelectedQuality, StringComparison.OrdinalIgnoreCase)
        && item.Codec.Equals(SelectedEncoding, StringComparison.OrdinalIgnoreCase));

    private AudioStreamInfo? FindSelectedAudio() => Episode.AudioStreams.FirstOrDefault(item => AudioKey(item).Equals(SelectedAudio, StringComparison.OrdinalIgnoreCase));
    private void RebuildAudioOptions()
    {
        AudioOptions = Episode.IsMuxedStream ? [new Choice(MuxedAudioValue, "内封音频（合流）")]
            : Episode.AudioStreams.Select(item => new Choice(AudioKey(item), $"{item.Codec} · {item.Bitrate}")).ToList();
        OnPropertyChanged(nameof(AudioOptions));
    }
    private static string QualityKey(VideoStreamInfo stream, string apiMode) => $"{(apiMode == "TV" ? "TV\u001e" : "")}{StreamSelectionPolicy.NormalizeQualityRule(stream.Quality)}\u001f{stream.Resolution}";
    private static string AudioKey(AudioStreamInfo stream) => $"{stream.Index}\u001f{stream.Codec}\u001f{stream.BitrateKbps}";
    private static string FormatResolution(string resolution) => resolution.Replace("x", "×", StringComparison.OrdinalIgnoreCase);
    private void RefreshRestoredStreamStatus()
    {
        var missing = new List<string>();
        if (_unavailableRestoredVideo is not null) missing.Add("历史中的手动视频规格已不可用");
        if (_unavailableRestoredAudio is not null) missing.Add("历史中的手动音频规格已不可用");
        if (missing.Count == 0)
        {
            if (StatusText == "历史手动规格已失效") StatusText = ReadyText;
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
