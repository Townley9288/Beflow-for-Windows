using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed class DualAudioViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private bool _initialized;
    private bool _active;
    private string _sourceModeText = "两个独立链接";
    private string _sourceAUrl = string.Empty;
    private string _sourceBUrl = string.Empty;
    private string _sourceAQuality = "4K 超清";
    private string _sourceBQuality = "4K 超清";
    private string _sourceAEncoding = "AVC";
    private string _sourceBEncoding = "AVC";
    private string _sourceAAudio = "auto";
    private string _sourceBAudio = "auto";
    private string _audioBitrate = "highest";
    private string _mainVideoMode = "推荐";
    private string _sourceALabel = "国语";
    private string _sourceBLabel = "粤语";
    private string _sourceALanguage = "zh";
    private string _sourceBLanguage = "yue";
    private string _defaultAudio = "来源 A";
    private double _sourceBDelay;
    private bool _keepSourceFiles = true;
    private string _workDirectory = string.Empty;
    private string _existingTaskDirectory = string.Empty;
    private string _mkvmergePath = string.Empty;
    private string _sourceATitle = string.Empty;
    private string _sourceBTitle = string.Empty;
    private string _sourceAParseStatus = "尚未解析";
    private string _sourceBParseStatus = "尚未解析";
    private string _message = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private double _overallProgress;
    private double _currentProgress;
    private bool _currentProgressIndeterminate;
    private string _progressDetail = string.Empty;
    private string _progressTitle = "当前任务";
    private DualAudioBatchResult? _lastResult;
    private string _resultMessage = string.Empty;
    private DualAudioCatalog? _catalog;
    private string _apiMode = "WEB";
    private bool _isParsing;
    private int _parseGeneration;
    private bool _suppressSourceInvalidation;
    private bool _existingTaskCanRemux;
    private int _existingInspectionGeneration;
    private bool _loadingManifestSettings;
    private bool _trackingManifestOverrides;
    private bool _manifestAudioMetadataModified;
    private bool _manifestDelayModified;

    public DualAudioViewModel(AppServices services)
    {
        _services = services;
        Console = services.TaskConsole;
        ParseCurrentCommand = new AsyncRelayCommand(() => ParseAsync(DownloadParseMode.Current), CanParse);
        ParseAllCommand = new AsyncRelayCommand(() => ParseAsync(DownloadParseMode.All), CanParse);
        RetrySourceACommand = new AsyncRelayCommand(() => RetrySourceAsync(DualAudioSource.A), () => CanRetrySource(DualAudioSource.A));
        RetrySourceBCommand = new AsyncRelayCommand(() => RetrySourceAsync(DualAudioSource.B), () => CanRetrySource(DualAudioSource.B));
        ApplyAllCommand = new RelayCommand(ApplyAll, () => Pairs.Count > 0 && !Console.IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => Pairs.Count > 0 && !Console.IsBusy);
        InvertSelectionCommand = new RelayCommand(InvertSelection, () => Pairs.Count > 0 && !Console.IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        RemuxCommand = new AsyncRelayCommand(RemuxAsync, CanRemux);
    }

    public IReadOnlyList<string> SourceModes { get; } = ["两个独立链接", "同一链接奇偶分P"];
    public IReadOnlyList<OptionItem> QualityOptions { get; } =
    [
        new("杜比视界", "杜比视界"), new("HDR 真彩", "HDR 真彩"), new("4K 超清", "4K 超清"),
        new("1080P 高码率", "1080P 高码率"), new("1080P 高清", "1080P 高清"),
        new("720P 高清", "720P 高清"), new("480P 清晰", "480P 清晰"), new("360P 流畅", "360P 流畅")
    ];
    public IReadOnlyList<string> EncodingOptions { get; } = ["HEVC", "AVC", "AV1"];
    public IReadOnlyList<OptionItem> AudioOptions { get; } =
    [
        new("auto", "自动"), new("E-AC-3", "E-AC-3"), new("M4A", "M4A"), new("FLAC", "FLAC"),
        new("AC-3", "AC-3"), new("DTS", "DTS")
    ];
    public IReadOnlyList<OptionItem> AudioBitrateOptions { get; } = [new("highest", "最高码率"), new("lowest", "最低码率")];
    public IReadOnlyList<string> MainVideoModeOptions { get; } = ["推荐", "来源 A", "来源 B"];
    public IReadOnlyList<string> DefaultAudioOptions { get; } = ["来源 A", "来源 B"];
    public ObservableCollection<DualAudioPairViewModel> Pairs { get; } = [];
    public TaskConsoleViewModel Console { get; }

    public string SourceModeText { get => _sourceModeText; set { if (SetProperty(ref _sourceModeText, value)) { if (!_suppressSourceInvalidation) InvalidateParsedCatalog(); OnPropertyChanged(nameof(SourceBUrlVisibility)); OnPropertyChanged(nameof(SourceARetryVisibility)); OnPropertyChanged(nameof(SourceBRetryVisibility)); NotifyCommands(); } } }
    public string SourceAUrl { get => _sourceAUrl; set { if (SetProperty(ref _sourceAUrl, value)) { if (!_suppressSourceInvalidation) InvalidateParsedCatalog(); NotifyCommands(); } } }
    public string SourceBUrl { get => _sourceBUrl; set { if (SetProperty(ref _sourceBUrl, value)) { if (!_suppressSourceInvalidation) InvalidateParsedCatalog(); NotifyCommands(); } } }
    public Visibility SourceBUrlVisibility => SourceModeText == "同一链接奇偶分P" ? Visibility.Collapsed : Visibility.Visible;
    public string SourceAQuality { get => _sourceAQuality; set => SetProperty(ref _sourceAQuality, value); }
    public string SourceBQuality { get => _sourceBQuality; set => SetProperty(ref _sourceBQuality, value); }
    public string SourceAEncoding { get => _sourceAEncoding; set => SetProperty(ref _sourceAEncoding, value); }
    public string SourceBEncoding { get => _sourceBEncoding; set => SetProperty(ref _sourceBEncoding, value); }
    public string SourceAAudio { get => _sourceAAudio; set => SetProperty(ref _sourceAAudio, value); }
    public string SourceBAudio { get => _sourceBAudio; set => SetProperty(ref _sourceBAudio, value); }
    public string AudioBitrate { get => _audioBitrate; set => SetProperty(ref _audioBitrate, value); }
    public string MainVideoMode { get => _mainVideoMode; set => SetProperty(ref _mainVideoMode, value); }
    public string SourceALabel { get => _sourceALabel; set { if (SetProperty(ref _sourceALabel, value)) MarkManifestAudioMetadataModified(); } }
    public string SourceBLabel { get => _sourceBLabel; set { if (SetProperty(ref _sourceBLabel, value)) MarkManifestAudioMetadataModified(); } }
    public string SourceALanguage { get => _sourceALanguage; set { if (SetProperty(ref _sourceALanguage, value)) MarkManifestAudioMetadataModified(); } }
    public string SourceBLanguage { get => _sourceBLanguage; set { if (SetProperty(ref _sourceBLanguage, value)) MarkManifestAudioMetadataModified(); } }
    public string DefaultAudio { get => _defaultAudio; set { if (SetProperty(ref _defaultAudio, value)) MarkManifestAudioMetadataModified(); } }
    public double SourceBDelay { get => _sourceBDelay; set { if (SetProperty(ref _sourceBDelay, Math.Clamp(value, -10000, 10000))) MarkManifestDelayModified(); } }
    public bool KeepSourceFiles { get => _keepSourceFiles; set => SetProperty(ref _keepSourceFiles, value); }
    public string WorkDirectory { get => _workDirectory; set => SetProperty(ref _workDirectory, value); }
    public string ExistingTaskDirectory { get => _existingTaskDirectory; set { if (SetProperty(ref _existingTaskDirectory, value)) { _existingTaskCanRemux = false; _trackingManifestOverrides = false; _manifestAudioMetadataModified = false; _manifestDelayModified = false; NotifyCommands(); _ = InspectExistingTaskAsync(value, showMessage: false); } } }
    public string MkvmergePath { get => _mkvmergePath; set => SetProperty(ref _mkvmergePath, value); }
    public string SourceATitle { get => _sourceATitle; private set { if (SetProperty(ref _sourceATitle, value)) OnPropertyChanged(nameof(SourceAInfoText)); } }
    public string SourceBTitle { get => _sourceBTitle; private set { if (SetProperty(ref _sourceBTitle, value)) OnPropertyChanged(nameof(SourceBInfoText)); } }
    public string SourceAParseStatus { get => _sourceAParseStatus; private set => SetProperty(ref _sourceAParseStatus, value); }
    public string SourceBParseStatus { get => _sourceBParseStatus; private set => SetProperty(ref _sourceBParseStatus, value); }
    public string SourceAInfoText => string.IsNullOrWhiteSpace(SourceATitle) ? "来源 A" : SourceATitle;
    public string SourceBInfoText => string.IsNullOrWhiteSpace(SourceBTitle) ? "来源 B" : SourceBTitle;
    public Visibility SourceARetryVisibility => SourceModeText == "两个独立链接" && !string.IsNullOrWhiteSpace(_catalog?.SourceAError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceBRetryVisibility => SourceModeText == "两个独立链接" && !string.IsNullOrWhiteSpace(_catalog?.SourceBError) ? Visibility.Visible : Visibility.Collapsed;
    public bool HasCatalog => _catalog is not null && Pairs.Count > 0;
    public Visibility CatalogVisibility => HasCatalog ? Visibility.Visible : Visibility.Collapsed;
    public bool IsParsing { get => _isParsing; private set { if (SetProperty(ref _isParsing, value)) OnPropertyChanged(nameof(SourceCardsVisibility)); } }
    public Visibility SourceCardsVisibility => IsParsing || HasCatalog ? Visibility.Visible : Visibility.Collapsed;
    public string Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public InfoBarSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }
    public double OverallProgress { get => _overallProgress; private set => SetProperty(ref _overallProgress, value); }
    public double CurrentProgress { get => _currentProgress; private set => SetProperty(ref _currentProgress, value); }
    public bool CurrentProgressIndeterminate { get => _currentProgressIndeterminate; private set => SetProperty(ref _currentProgressIndeterminate, value); }
    public string ProgressDetail { get => _progressDetail; private set => SetProperty(ref _progressDetail, value); }
    public string ProgressTitle { get => _progressTitle; private set => SetProperty(ref _progressTitle, value); }
    public Visibility ProgressVisibility => Console.IsBusy || OverallProgress > 0 ? Visibility.Visible : Visibility.Collapsed;
    public DualAudioBatchResult? LastResult { get => _lastResult; private set { if (SetProperty(ref _lastResult, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => LastResult is not null;
    public string ResultMessage { get => _resultMessage; private set => SetProperty(ref _resultMessage, value); }
    public string SelectionSummary => $"已选 {Pairs.Count(item => item.IsSelected && item.IsDownloadable)} / {Pairs.Count(item => item.IsDownloadable)} 对 · 预计 {MediaEstimateFormatter.FormatBytes(Pairs.Where(item => item.IsSelected).Sum(item => item.EstimatedSizeBytes))}";

    public IAsyncRelayCommand ParseCurrentCommand { get; }
    public IAsyncRelayCommand ParseAllCommand { get; }
    public IAsyncRelayCommand RetrySourceACommand { get; }
    public IAsyncRelayCommand RetrySourceBCommand { get; }
    public IRelayCommand ApplyAllCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand InvertSelectionCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand RemuxCommand { get; }
    public Func<Task<bool>>? ConfirmMkvmergeAvailableAsync { get; set; }
    public void DismissMessage() => Message = string.Empty;
    public async Task PrepareExistingRemuxAsync(string taskDirectory)
    {
        ExistingTaskDirectory = taskDirectory;
        await InspectExistingTaskAsync(taskDirectory, showMessage: true);
    }

    public void Activate()
    {
        if (_active) return;
        _active = true;
        Console.PropertyChanged += Console_PropertyChanged;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        Console.PropertyChanged -= Console_PropertyChanged;
    }

    public async Task InitializeAsync(HistoryRecord? restore = null)
    {
        if (!_initialized)
        {
            var settings = await _services.Settings.LoadAsync();
            WorkDirectory = settings.WorkDirectory;
            MkvmergePath = settings.MkvmergePath;
            SourceAQuality = SourceBQuality = settings.VideoQualityRule;
            SourceAEncoding = SourceBEncoding = settings.Encoding;
            SourceAAudio = SourceBAudio = settings.AudioCodec;
            AudioBitrate = settings.AudioBitratePriority == AudioBitratePriority.Lowest ? "lowest" : "highest";
            _apiMode = settings.ApiMode;
            _initialized = true;
        }
        if (restore?.DualAudioBatch is not null)
        {
            InvalidateParsedCatalog();
            _suppressSourceInvalidation = true;
            try { ApplyBatch(restore.DualAudioBatch.Request); }
            finally { _suppressSourceInvalidation = false; }
            await ParseAsync(DownloadParseMode.All, restore.DualAudioBatch);
        }
        else if (restore?.DualAudio is not null)
        {
            InvalidateParsedCatalog();
            _suppressSourceInvalidation = true;
            try { ApplyLegacy(restore.DualAudio); }
            finally { _suppressSourceInvalidation = false; }
            ShowMessage("旧版多音轨配置已加载，请重新解析来源 A/B 后确认配对。", InfoBarSeverity.Informational);
        }
    }

    private async Task ParseAsync(DownloadParseMode mode, DualAudioBatchHistory? restore = null)
    {
        var parseGeneration = ++_parseGeneration;
        var sourceMode = CurrentSourceMode;
        var sourceAUrl = SourceAUrl.Trim();
        var sourceBUrl = sourceMode == DualAudioSourceMode.Interleaved ? sourceAUrl : SourceBUrl.Trim();
        ClearCatalogRows();
        LastResult = null;
        Message = string.Empty;
        SourceAParseStatus = "正在解析…";
        SourceBParseStatus = SourceModeText == "同一链接奇偶分P" ? "等待奇偶配对…" : "正在解析…";
        DualAudioCatalog? catalog = null;
        var progressA = new Progress<DownloadParseProgress>(update => { if (parseGeneration == _parseGeneration) SourceAParseStatus = FormatParseStatus(update); });
        var progressB = new Progress<DownloadParseProgress>(update => { if (parseGeneration == _parseGeneration) SourceBParseStatus = FormatParseStatus(update); });
        TaskSnapshot snapshot;
        IsParsing = true;
        try
        {
            snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioParse, false, "dual_audio_parse", async (context, token) =>
            {
                catalog = await _services.DualAudio.ParseAsync(new DualAudioParseRequest(
                    sourceMode,
                    sourceAUrl,
                    sourceBUrl,
                    mode, _apiMode), progressA, progressB, context, token);
            });
        }
        finally { IsParsing = false; }
        if (parseGeneration != _parseGeneration || !CatalogInputMatches(sourceMode, sourceAUrl, sourceBUrl)) return;
        if (catalog is null)
        {
            ShowMessage(string.IsNullOrWhiteSpace(snapshot.Error) ? "解析未完成" : snapshot.Error, snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            return;
        }
        LoadCatalog(catalog);
        if (restore is not null) ApplyRestoredSelections(restore);
        var errors = new[] { catalog.SourceAError, catalog.SourceBError }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
        if (snapshot.State == TaskState.Cancelled) ShowMessage("解析已取消，已保留双方已经完成的解析结果。", InfoBarSeverity.Warning);
        else if (errors.Count > 0) ShowMessage($"部分来源解析失败：{string.Join("；", errors)}", InfoBarSeverity.Warning);
        else ShowMessage($"解析完成，共生成 {Pairs.Count(item => item.IsDownloadable)} 对分集。", InfoBarSeverity.Success);
    }

    private void LoadCatalog(DualAudioCatalog catalog)
    {
        _catalog = catalog;
        SourceATitle = catalog.SourceA?.Title ?? string.Empty;
        SourceBTitle = catalog.SourceB?.Title ?? string.Empty;
        SourceAParseStatus = catalog.SourceA is null ? catalog.SourceAError : $"已解析 {catalog.SourceA.Episodes.Count} 集";
        SourceBParseStatus = catalog.SourceB is null ? catalog.SourceBError : $"已解析 {catalog.SourceB.Episodes.Count} 集";
        var sourceBChoices = (catalog.SourceB?.Episodes ?? [])
            .OrderBy(item => item.Page.Number)
            .Select(item => new DualAudioPairViewModel.EpisodeChoice(item.Page.Number, $"P{item.Page.Number} · {item.Page.Title}", item))
            .ToList();
        Pairs.Clear();
        foreach (var pair in catalog.Pairs)
        {
            var row = new DualAudioPairViewModel(pair, sourceBChoices, SourceARule(), SourceBRule(), checked((int)SourceBDelay));
            row.SourceBReassignRequested += Row_SourceBReassignRequested;
            row.PropertyChanged += Row_PropertyChanged;
            Pairs.Add(row);
        }
        OnPropertyChanged(nameof(SourceARetryVisibility));
        OnPropertyChanged(nameof(SourceBRetryVisibility));
        NotifyRowsChanged();
    }

    private async Task RetrySourceAsync(DualAudioSource source)
    {
        if (_catalog is null || SourceModeText != "两个独立链接") return;
        var existingCatalog = _catalog;
        var parseGeneration = _parseGeneration;
        var sourceAUrl = SourceAUrl.Trim();
        var sourceBUrl = SourceBUrl.Trim();
        LastResult = null;
        Message = string.Empty;
        if (source == DualAudioSource.A) SourceAParseStatus = "正在重新解析…";
        else SourceBParseStatus = "正在重新解析…";

        DualAudioCatalog? retried = null;
        var progress = new Progress<DownloadParseProgress>(update =>
        {
            if (source == DualAudioSource.A) SourceAParseStatus = FormatParseStatus(update);
            else SourceBParseStatus = FormatParseStatus(update);
        });
        TaskSnapshot snapshot;
        IsParsing = true;
        try
        {
            snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioParse, false, "dual_audio_retry", async (context, token) =>
            {
                retried = await _services.DualAudio.ParseAsync(new DualAudioParseRequest(
                    DualAudioSourceMode.Separate,
                    sourceAUrl,
                    sourceBUrl,
                    existingCatalog.ParseMode,
                    _apiMode,
                    source),
                    source == DualAudioSource.A ? progress : null,
                    source == DualAudioSource.B ? progress : null,
                    context,
                    token);
            });
        }
        finally { IsParsing = false; }
        if (parseGeneration != _parseGeneration || !CatalogInputMatches(DualAudioSourceMode.Separate, sourceAUrl, sourceBUrl)) return;
        if (retried is null)
        {
            ShowMessage(string.IsNullOrWhiteSpace(snapshot.Error) ? $"来源 {source} 重新解析未完成" : snapshot.Error,
                snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            return;
        }

        var merged = new DualAudioCatalog
        {
            SourceMode = existingCatalog.SourceMode,
            ParseMode = existingCatalog.ParseMode,
            SourceAUrl = sourceAUrl,
            SourceBUrl = sourceBUrl,
            SourceA = source == DualAudioSource.A ? retried.SourceA : existingCatalog.SourceA,
            SourceB = source == DualAudioSource.B ? retried.SourceB : existingCatalog.SourceB,
            SourceAError = source == DualAudioSource.A ? retried.SourceAError : existingCatalog.SourceAError,
            SourceBError = source == DualAudioSource.B ? retried.SourceBError : existingCatalog.SourceBError
        };
        merged.Pairs.AddRange(DualAudioService.BuildPairs(merged.SourceA, merged.SourceB));
        LoadCatalog(merged);
        var error = source == DualAudioSource.A ? merged.SourceAError : merged.SourceBError;
        if (snapshot.State == TaskState.Cancelled) ShowMessage($"来源 {source} 的重试已取消，原有结果仍保留。", InfoBarSeverity.Warning);
        else if (!string.IsNullOrWhiteSpace(error)) ShowMessage($"来源 {source} 仍无法解析：{error}", InfoBarSeverity.Warning);
        else ShowMessage($"来源 {source} 已重新解析并更新配对。", InfoBarSeverity.Success);
    }

    private void Row_SourceBReassignRequested(object? sender, int pageNumber)
    {
        if (sender is not DualAudioPairViewModel row || _catalog?.SourceB is null) return;
        var target = _catalog.SourceB.Episodes.FirstOrDefault(item => item.Page.Number == pageNumber);
        if (target is null) return;
        var previous = row.SourceBInfo;
        var existing = Pairs.FirstOrDefault(item => !ReferenceEquals(item, row) && item.SourceBInfo?.Page.Number == pageNumber);
        existing?.AssignSourceB(previous, SourceBRule());
        row.AssignSourceB(target, SourceBRule());
        NotifyRowsChanged();
    }

    private void ApplyAll()
    {
        foreach (var row in Pairs) row.ApplyRules(SourceARule(), SourceBRule(), MainVideoMode, checked((int)SourceBDelay));
        NotifyRowsChanged();
    }

    private async Task StartAsync()
    {
        if (ConfirmMkvmergeAvailableAsync is not null && !await ConfirmMkvmergeAvailableAsync()) return;
        Message = string.Empty;
        LastResult = null;
        OverallProgress = 0;
        CurrentProgress = 0;
        var request = await BuildBatchRequestAsync();
        DualAudioBatchResult? result = null;
        var progress = new Progress<DualAudioProgressSnapshot>(UpdateProgress);
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioMux, request.Options.SaveTaskLogs, "dual_audio_mux", async (context, token) =>
        {
            result = await _services.DualAudio.DownloadAndMuxAsync(request, progress, context, token);
            if (!result.Cancelled && result.Succeeded == 0) throw new InvalidOperationException("所有配对均下载或封装失败");
        });
        if (result is null)
        {
            ShowMessage(snapshot.Error, InfoBarSeverity.Error);
            return;
        }
        LastResult = result;
        foreach (var pairResult in result.Pairs)
            Pairs.FirstOrDefault(item => item.PairNumber == pairResult.PairNumber)?.ApplyResult(pairResult);
        ResultMessage = $"成功 {result.Succeeded} 对，失败 {result.Failed} 对。输出：{result.OutputDirectory}";
        await _services.History.AddAsync(new HistoryRecord
        {
            TaskType = TaskKind.DualAudioMux,
            Title = result.Title,
            Url = request.SourceAUrl,
            SecondaryUrl = request.SourceBUrl,
            Timestamp = DateTimeOffset.Now,
            LogPath = snapshot.LogPath,
            OutputDirectory = result.OutputDirectory,
            OutputFiles = result.OutputFiles,
            DualAudioBatch = new DualAudioBatchHistory { Request = request, Pairs = result.Pairs, ManifestPath = result.ManifestPath }
        });
        ShowMessage(snapshot.State switch
        {
            TaskState.Cancelled => "任务已取消，已保留完成结果和中间文件。",
            TaskState.Failed => snapshot.Error,
            _ when result.Failed > 0 => ResultMessage,
            _ => "多音轨封装完成。"
        }, snapshot.State == TaskState.Failed ? InfoBarSeverity.Error : result.Failed > 0 || snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        NotifyRowsChanged();
    }

    private async Task RemuxAsync()
    {
        if (ConfirmMkvmergeAvailableAsync is not null && !await ConfirmMkvmergeAvailableAsync()) return;
        var request = BuildLegacyRemuxRequest();
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioRemux, request.SaveTaskLogs, "dual_audio_remux",
            (context, token) => _services.DualAudio.RemuxExistingAsync(request, context, token));
        if (snapshot.State == TaskState.Completed)
        {
            await _services.History.AddAsync(new HistoryRecord
            {
                TaskType = TaskKind.DualAudioRemux,
                Title = Path.GetFileName(request.ExistingTaskDirectory),
                Url = request.ExistingTaskDirectory,
                Timestamp = DateTimeOffset.Now,
                LogPath = snapshot.LogPath,
                DualAudio = request
            });
            ShowMessage("重新封装完成。", InfoBarSeverity.Success);
        }
        else ShowMessage(snapshot.Error, snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
    }

    private async Task<DualAudioBatchRequest> BuildBatchRequestAsync()
    {
        var settings = await _services.Settings.LoadAsync();
        return new DualAudioBatchRequest
        {
            SourceMode = SourceModeText == "同一链接奇偶分P" ? DualAudioSourceMode.Interleaved : DualAudioSourceMode.Separate,
            SourceAUrl = SourceAUrl.Trim(),
            SourceBUrl = SourceModeText == "同一链接奇偶分P" ? SourceAUrl.Trim() : SourceBUrl.Trim(),
            SourceATitle = SourceATitle,
            SourceBTitle = SourceBTitle,
            ApiMode = settings.ApiMode,
            Options = BuildDownloadOptions(settings),
            Pairs = Pairs.Where(item => item.IsDownloadable).Select(item => item.BuildSelection(checked((int)SourceBDelay))).ToList(),
            SourceALabel = SourceALabel,
            SourceBLabel = SourceBLabel,
            SourceALanguage = SourceALanguage,
            SourceBLanguage = SourceBLanguage,
            DefaultAudioSource = DefaultAudio == "来源 B" ? DualAudioSource.B : DualAudioSource.A,
            SourceBDelayMs = checked((int)SourceBDelay),
            WorkDirectory = WorkDirectory,
            MkvmergePath = MkvmergePath,
            KeepSourceFiles = KeepSourceFiles
        };
    }

    private DownloadRequest BuildDownloadOptions(AppSettings settings) => new()
    {
        Url = SourceAUrl.Trim(),
        Quality = SourceAQuality,
        Encoding = SourceAEncoding,
        AudioCodec = SourceAAudio,
        AudioBitratePriority = AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest,
        WorkDirectory = WorkDirectory,
        MultiThread = settings.MultiThread,
        UposHost = settings.UposHost,
        UseAria2c = settings.UseAria2c,
        Aria2AutoTune = settings.Aria2AutoTune,
        Aria2cPath = settings.Aria2cPath,
        Aria2MaxConnection = settings.Aria2MaxConnection,
        Aria2Split = settings.Aria2Split,
        Aria2MaxConcurrentDownloads = settings.Aria2MaxConcurrentDownloads,
        Aria2MinSplitSize = settings.Aria2MinSplitSize,
        SaveTaskLogs = settings.SaveTaskLogs,
        ApiMode = settings.ApiMode
    };

    private DualAudioRequest BuildLegacyRemuxRequest() => new()
    {
        SourceMode = SourceModeText == "同一链接奇偶分P" ? DualAudioSourceMode.Interleaved : DualAudioSourceMode.Separate,
        PrimaryLabel = SourceALabel,
        SecondaryLabel = SourceBLabel,
        PrimaryLanguage = SourceALanguage,
        SecondaryLanguage = SourceBLanguage,
        SecondaryIsDefault = DefaultAudio == "来源 B",
        SecondaryAudioDelayMs = checked((int)SourceBDelay),
        ExistingTaskDirectory = ExistingTaskDirectory,
        MkvmergePath = MkvmergePath,
        SaveTaskLogs = true,
        OverrideManifestAudioMetadata = _trackingManifestOverrides && _manifestAudioMetadataModified,
        OverrideManifestDelay = _trackingManifestOverrides && _manifestDelayModified
    };

    private void ApplyBatch(DualAudioBatchRequest request)
    {
        SourceModeText = request.SourceMode == DualAudioSourceMode.Interleaved ? "同一链接奇偶分P" : "两个独立链接";
        SourceAUrl = request.SourceAUrl;
        SourceBUrl = request.SourceBUrl;
        SourceAQuality = request.Pairs.FirstOrDefault()?.SourceA.Video?.Quality ?? request.Options.Quality;
        SourceBQuality = request.Pairs.FirstOrDefault()?.SourceB.Video?.Quality ?? request.Options.Quality;
        SourceAEncoding = request.Pairs.FirstOrDefault()?.SourceA.Video?.Codec ?? request.Options.Encoding;
        SourceBEncoding = request.Pairs.FirstOrDefault()?.SourceB.Video?.Codec ?? request.Options.Encoding;
        SourceAAudio = request.Pairs.FirstOrDefault()?.SourceA.Audio?.Codec ?? request.Options.AudioCodec;
        SourceBAudio = request.Pairs.FirstOrDefault()?.SourceB.Audio?.Codec ?? request.Options.AudioCodec;
        AudioBitrate = request.Options.AudioBitratePriority == AudioBitratePriority.Lowest ? "lowest" : "highest";
        SourceALabel = request.SourceALabel;
        SourceBLabel = request.SourceBLabel;
        SourceALanguage = request.SourceALanguage;
        SourceBLanguage = request.SourceBLanguage;
        DefaultAudio = request.DefaultAudioSource == DualAudioSource.B ? "来源 B" : "来源 A";
        SourceBDelay = request.SourceBDelayMs;
        WorkDirectory = request.WorkDirectory;
        MkvmergePath = request.MkvmergePath;
        KeepSourceFiles = request.KeepSourceFiles;
    }

    private void ApplyLegacy(DualAudioRequest request)
    {
        SourceModeText = request.SourceMode == DualAudioSourceMode.Interleaved ? "同一链接奇偶分P" : "两个独立链接";
        SourceAUrl = request.PrimaryUrl;
        SourceBUrl = request.SecondaryUrl;
        SourceAQuality = SourceBQuality = StreamSelectionPolicy.NormalizeQualityRule(request.Quality);
        SourceAEncoding = SourceBEncoding = request.Encoding;
        SourceAAudio = request.PrimaryAudioCodec;
        SourceBAudio = request.SecondaryAudioCodec;
        SourceALabel = request.PrimaryLabel;
        SourceBLabel = request.SecondaryLabel;
        SourceALanguage = request.PrimaryLanguage;
        SourceBLanguage = request.SecondaryLanguage;
        DefaultAudio = request.SecondaryIsDefault ? "来源 B" : "来源 A";
        SourceBDelay = request.SecondaryAudioDelayMs;
        WorkDirectory = request.WorkDirectory;
        ExistingTaskDirectory = request.ExistingTaskDirectory;
        MkvmergePath = request.MkvmergePath;
    }

    private void ApplyRestoredSelections(DualAudioBatchHistory history)
    {
        var restoredPairs = DualAudioService.BuildRestoredPairs(_catalog?.SourceA, _catalog?.SourceB, history.Request.Pairs);
        foreach (var row in Pairs)
        {
            row.AssignSourceB(null, SourceBRule());
            row.IsSelected = false;
        }
        foreach (var restoredPair in restoredPairs)
        {
            DualAudioPairViewModel? row = restoredPair.SourceA is not null
                ? Pairs.FirstOrDefault(item => item.SourceAInfo?.Page.Number == restoredPair.SourceA.Page.Number)
                : Pairs.FirstOrDefault(item => item.SourceAInfo is null && item.SourceBInfo is null);
            row?.AssignSourceB(restoredPair.SourceB, SourceBRule());
        }
        foreach (var saved in history.Request.Pairs)
        {
            var row = Pairs.FirstOrDefault(item => item.SourceAInfo?.Page.Number == saved.SourceAPageNumber);
            if (row is null) continue;
            row.MainVideoModeText = saved.MainVideoMode switch
            {
                DualAudioMainVideoMode.SourceA => "来源 A",
                DualAudioMainVideoMode.SourceB => "来源 B",
                _ => "推荐"
            };
            row.UseCustomDelay = saved.SourceBDelayOverrideMs.HasValue;
            row.DelayValue = saved.SourceBDelayOverrideMs ?? history.Request.SourceBDelayMs;
            row.SourceA?.ApplyRestored(saved.SourceA, DownloadMode.VideoAndAudio);
            row.SourceB?.ApplyRestored(saved.SourceB, DownloadMode.VideoAndAudio);
            row.IsSelected = saved.IsSelected;
        }
        NotifyRowsChanged();
    }

    private StreamSelectionRule SourceARule() => new(SourceAQuality, SourceAEncoding, SourceAAudio,
        AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest);
    private StreamSelectionRule SourceBRule() => new(SourceBQuality, SourceBEncoding, SourceBAudio,
        AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest);

    private void UpdateProgress(DualAudioProgressSnapshot update)
    {
        OverallProgress = update.OverallPercent;
        CurrentProgress = update.CurrentPercent ?? 0;
        CurrentProgressIndeterminate = update.CurrentPercent is null;
        ProgressDetail = $"{update.CompletedPairs}/{update.TotalPairs} 对 · {update.Message}";
        ProgressTitle = update.CurrentPair > 0 ? $"第 {update.CurrentPair} 对 · {update.Message}" : update.Message;
        Pairs.FirstOrDefault(item => item.PairNumber == update.CurrentPair)?.SetRuntimeStatus(update);
        OnPropertyChanged(nameof(ProgressVisibility));
    }

    private static string FormatParseStatus(DownloadParseProgress update) => update.Total > 0
        ? $"已解析 {update.Completed}/{update.Total} · {update.CurrentTitle}"
        : update.Message;

    private void SetAllSelected(bool selected)
    {
        foreach (var row in Pairs.Where(item => item.IsDownloadable)) row.IsSelected = selected;
        NotifyRowsChanged();
    }

    private void InvertSelection()
    {
        foreach (var row in Pairs.Where(item => item.IsDownloadable)) row.IsSelected = !row.IsSelected;
        NotifyRowsChanged();
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DualAudioPairViewModel.IsSelected) or nameof(DualAudioPairViewModel.EstimatedSizeText) or nameof(DualAudioPairViewModel.EstimatedSizeBytes))
            NotifyRowsChanged();
    }

    private void NotifyRowsChanged()
    {
        OnPropertyChanged(nameof(HasCatalog));
        OnPropertyChanged(nameof(CatalogVisibility));
        OnPropertyChanged(nameof(SourceCardsVisibility));
        OnPropertyChanged(nameof(SelectionSummary));
        NotifyCommands();
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageSeverity = severity;
        Message = string.IsNullOrWhiteSpace(message) ? "操作未完成" : message;
    }

    private DualAudioSourceMode CurrentSourceMode => SourceModeText == "同一链接奇偶分P"
        ? DualAudioSourceMode.Interleaved
        : DualAudioSourceMode.Separate;

    private void InvalidateParsedCatalog()
    {
        _parseGeneration++;
        ClearCatalogRows();
        SourceATitle = string.Empty;
        SourceBTitle = string.Empty;
        SourceAParseStatus = "尚未解析";
        SourceBParseStatus = "尚未解析";
        LastResult = null;
        OverallProgress = 0;
        CurrentProgress = 0;
        ProgressDetail = string.Empty;
        NotifyRowsChanged();
    }

    private void ClearCatalogRows()
    {
        foreach (var row in Pairs)
        {
            row.SourceBReassignRequested -= Row_SourceBReassignRequested;
            row.PropertyChanged -= Row_PropertyChanged;
        }
        Pairs.Clear();
        _catalog = null;
        OnPropertyChanged(nameof(SourceARetryVisibility));
        OnPropertyChanged(nameof(SourceBRetryVisibility));
        NotifyRowsChanged();
    }

    private bool CatalogMatchesCurrentInput()
    {
        if (_catalog is null) return false;
        var sourceA = SourceAUrl.Trim();
        var sourceB = CurrentSourceMode == DualAudioSourceMode.Interleaved ? sourceA : SourceBUrl.Trim();
        return _catalog.SourceMode == CurrentSourceMode
               && UrlsMatch(_catalog.SourceAUrl, sourceA)
               && UrlsMatch(_catalog.SourceBUrl, sourceB);
    }

    private bool CatalogInputMatches(DualAudioSourceMode sourceMode, string sourceAUrl, string sourceBUrl)
    {
        var currentA = SourceAUrl.Trim();
        var currentB = CurrentSourceMode == DualAudioSourceMode.Interleaved ? currentA : SourceBUrl.Trim();
        return CurrentSourceMode == sourceMode && UrlsMatch(currentA, sourceAUrl) && UrlsMatch(currentB, sourceBUrl);
    }

    private static bool UrlsMatch(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task InspectExistingTaskAsync(string taskDirectory, bool showMessage)
    {
        var inspectionGeneration = ++_existingInspectionGeneration;
        if (string.IsNullOrWhiteSpace(taskDirectory))
        {
            _existingTaskCanRemux = false;
            NotifyCommands();
            return;
        }

        var preparation = await _services.DualAudio.InspectExistingAsync(taskDirectory);
        if (inspectionGeneration != _existingInspectionGeneration || !UrlsMatch(taskDirectory, ExistingTaskDirectory)) return;
        _existingTaskCanRemux = preparation.CanRemux;
        _trackingManifestOverrides = preparation.CanRemux && preparation.IsManifest;
        _manifestAudioMetadataModified = false;
        _manifestDelayModified = false;
        if (_trackingManifestOverrides)
        {
            _loadingManifestSettings = true;
            try
            {
                SourceALabel = preparation.SourceALabel;
                SourceBLabel = preparation.SourceBLabel;
                SourceALanguage = preparation.SourceALanguage;
                SourceBLanguage = preparation.SourceBLanguage;
                DefaultAudio = preparation.DefaultAudioSource == DualAudioSource.B ? "来源 B" : "来源 A";
                SourceBDelay = preparation.SourceBDelayMs;
            }
            finally { _loadingManifestSettings = false; }
            _manifestAudioMetadataModified = false;
            _manifestDelayModified = false;
        }
        NotifyCommands();
        if (!showMessage) return;
        if (!preparation.CanRemux)
            ShowMessage(preparation.Error, InfoBarSeverity.Warning);
        else if (preparation.IsManifest)
            ShowMessage(preparation.HasPerPairDelays
                ? "已载入新任务清单；默认保留原音轨信息和每集延迟，修改对应设置后才会统一覆盖。"
                : "已载入新任务清单；默认保留原音轨信息和延迟，修改后才会覆盖。", InfoBarSeverity.Informational);
        else
            ShowMessage("已载入旧版任务目录，可调整音轨信息和延迟后重新封装。", InfoBarSeverity.Informational);
    }

    private void MarkManifestAudioMetadataModified()
    {
        if (_trackingManifestOverrides && !_loadingManifestSettings) _manifestAudioMetadataModified = true;
    }

    private void MarkManifestDelayModified()
    {
        if (_trackingManifestOverrides && !_loadingManifestSettings) _manifestDelayModified = true;
    }

    private bool CanParse() => !Console.IsBusy && !string.IsNullOrWhiteSpace(SourceAUrl) && (SourceModeText == "同一链接奇偶分P" || !string.IsNullOrWhiteSpace(SourceBUrl));
    private bool CanRetrySource(DualAudioSource source) => !Console.IsBusy
        && SourceModeText == "两个独立链接"
        && _catalog is not null
        && !string.IsNullOrWhiteSpace(source == DualAudioSource.A ? _catalog.SourceAError : _catalog.SourceBError);
    private bool CanStart() => !Console.IsBusy && CatalogMatchesCurrentInput() && Pairs.Any(item => item.IsSelected && item.IsDownloadable);
    private bool CanRemux() => !Console.IsBusy && _existingTaskCanRemux;
    private void NotifyCommands()
    {
        ParseCurrentCommand.NotifyCanExecuteChanged();
        ParseAllCommand.NotifyCanExecuteChanged();
        RetrySourceACommand.NotifyCanExecuteChanged();
        RetrySourceBCommand.NotifyCanExecuteChanged();
        ApplyAllCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        RemuxCommand.NotifyCanExecuteChanged();
    }

    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Console.IsBusy)) return;
        OnPropertyChanged(nameof(ProgressVisibility));
        NotifyCommands();
    }
}
