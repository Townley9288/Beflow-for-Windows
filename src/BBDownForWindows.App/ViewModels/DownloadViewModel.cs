using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private readonly HashSet<int> _failedPages = [];
    private string _url = string.Empty;
    private string _searchText = string.Empty;
    private string _qualityRule = "4K 超清";
    private string _encoding = "AVC";
    private string _downloadMode = "视频+音频";
    private string _audioCodec = "auto";
    private string _workDirectory = string.Empty;
    private string _uposHost = string.Empty;
    private bool _danmaku;
    private bool _subtitle;
    private bool _cover;
    private bool _multiThread = true;
    private bool _useAria2c = true;
    private string _audioBitrate = "highest";
    private bool _saveTaskLogs = true;
    private bool _active;
    private bool _initialized;
    private DownloadCatalog? _catalog;
    private DownloadResult? _lastDownloadResult;
    private List<EpisodeStreamSelection>? _pendingRestore;
    private string _message = string.Empty;
    private string _messageLogPath = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private double _overallProgress;
    private double _currentProgress;
    private bool _currentProgressIndeterminate;
    private string _progressTitle = string.Empty;
    private string _progressDetail = string.Empty;
    private bool _showProgress;
    private bool _continueAvailable;
    private DownloadNamingSettings _downloadNaming = new();
    private DownloadNamingProfile? _restoredNamingProfile;
    private DownloadNamingProfileKind _restoredNamingProfileKind;
    private bool _loadingRestore;
    private int _parseGeneration;

    public DownloadViewModel(AppServices services)
    {
        _services = services;
        Console = services.TaskConsole;
        ParseCurrentCommand = new AsyncRelayCommand(() => ParseAsync(DownloadParseMode.Current), CanParse);
        ParseAllCommand = new AsyncRelayCommand(() => ParseAsync(DownloadParseMode.All), CanParse);
        ContinueParseCommand = new AsyncRelayCommand(ContinueParseAsync, CanContinueParse);
        ApplyRuleCommand = new RelayCommand(ApplyRuleToAll, () => Rows.Count > 0 && !Console.IsBusy);
        SelectAllCommand = new RelayCommand(SelectAll, () => Rows.Count > 0 && !Console.IsBusy);
        InvertSelectionCommand = new RelayCommand(InvertSelection, () => Rows.Count > 0 && !Console.IsBusy);
        DownloadSelectedCommand = new AsyncRelayCommand(StartDownloadAsync, CanDownload);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => _failedPages.Count > 0 && !Console.IsBusy);
    }

    public IReadOnlyList<OptionItem> QualityRuleOptions { get; } =
    [
        new("杜比视界", "杜比视界"),
        new("HDR 真彩", "HDR 真彩"),
        new("4K 超清", "4K 超清"),
        new("1080P 高码率", "1080P 高码率"),
        new("1080P 高清", "1080P 高清"),
        new("720P 高清", "720P 高清"),
        new("480P 清晰", "480P 清晰"),
        new("360P 流畅", "360P 流畅")
    ];
    public IReadOnlyList<string> EncodingOptions { get; } = ["HEVC", "AVC", "AV1"];
    public IReadOnlyList<string> DownloadModeOptions { get; } = ["视频+音频", "仅视频", "仅音频"];
    public IReadOnlyList<OptionItem> AudioCodecOptions { get; } =
    [
        new("auto", "自动选择"), new("E-AC-3", "E-AC-3"), new("M4A", "M4A"),
        new("FLAC", "FLAC"), new("AC-3", "AC-3"), new("DTS", "DTS")
    ];
    public IReadOnlyList<OptionItem> AudioBitrateOptions { get; } = [new("highest", "最高码率"), new("lowest", "最低码率")];
    public IReadOnlyList<OptionItem> UposHostOptions { get; } =
    [
        new("", "使用默认 CDN 节点"),
        new("upos-sz-mirrorcos.bilivideo.com", "upos-sz-mirrorcos.bilivideo.com"),
        new("upos-sz-mirrorcoso1.bilivideo.com", "upos-sz-mirrorcoso1.bilivideo.com"),
        new("upos-sz-mirrorali.bilivideo.com", "upos-sz-mirrorali.bilivideo.com"),
        new("upos-sz-mirroralib.bilivideo.com", "upos-sz-mirroralib.bilivideo.com"),
        new("upos-sz-mirrorhw.bilivideo.com", "upos-sz-mirrorhw.bilivideo.com")
    ];

    public TaskConsoleViewModel Console { get; }
    public ObservableCollection<DownloadEpisodeViewModel> Rows { get; } = [];
    public ObservableCollection<DownloadEpisodeViewModel> VisibleRows { get; } = [];

    public string Url
    {
        get => _url;
        set
        {
            if (!SetProperty(ref _url, value)) return;
            if (!_loadingRestore)
            {
                _restoredNamingProfile = null;
                OnPropertyChanged(nameof(ActiveNamingProfileText));
                OnPropertyChanged(nameof(NamingRuleSummary));
                InvalidateParsedState();
            }
            DismissMessage();
            NotifyCommands();
        }
    }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ApplyFilter(); } }
    public string QualityRule { get => _qualityRule; set => SetProperty(ref _qualityRule, value); }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public string AudioCodec { get => _audioCodec; set => SetProperty(ref _audioCodec, value); }
    public string AudioBitrate { get => _audioBitrate; set => SetProperty(ref _audioBitrate, value); }
    public string DownloadModeText
    {
        get => _downloadMode;
        set
        {
            if (!SetProperty(ref _downloadMode, value)) return;
            foreach (var row in Rows) row.SetDownloadMode(CurrentDownloadMode);
            OnSelectionChanged();
            OnPropertyChanged(nameof(CanRenameDownload));
        }
    }
    public string WorkDirectory
    {
        get => _workDirectory;
        set
        {
            if (SetProperty(ref _workDirectory, value))
            {
                OnPropertyChanged(nameof(DownloadDirectorySummary));
                OnPropertyChanged(nameof(NamingRuleSummary));
            }
        }
    }
    public string DownloadDirectorySummary => string.IsNullOrWhiteSpace(WorkDirectory) ? "保存位置：尚未设置下载目录" : $"保存到：{WorkDirectory}";
    public string UposHost { get => _uposHost; set => SetProperty(ref _uposHost, value); }
    public bool Danmaku { get => _danmaku; set => SetProperty(ref _danmaku, value); }
    public bool Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }
    public bool Cover { get => _cover; set => SetProperty(ref _cover, value); }
    public bool MultiThread { get => _multiThread; set => SetProperty(ref _multiThread, value); }
    public bool UseAria2c { get => _useAria2c; set => SetProperty(ref _useAria2c, value); }
    public bool SaveTaskLogs { get => _saveTaskLogs; set => SetProperty(ref _saveTaskLogs, value); }

    public DownloadCatalog? Catalog
    {
        get => _catalog;
        private set
        {
            if (!SetProperty(ref _catalog, value)) return;
            OnPropertyChanged(nameof(HasCatalog));
            OnPropertyChanged(nameof(CatalogVisibility));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(CoverUrl));
            OnPropertyChanged(nameof(OwnerName));
            OnPropertyChanged(nameof(OwnerAvatarUrl));
            OnPropertyChanged(nameof(EpisodeCountText));
            OnPropertyChanged(nameof(ActiveNamingProfileText));
            OnPropertyChanged(nameof(NamingRuleSummary));
        }
    }
    public bool HasCatalog => Catalog is not null;
    public Visibility CatalogVisibility => HasCatalog ? Visibility.Visible : Visibility.Collapsed;
    public string DisplayTitle => Catalog?.Metadata?.Title ?? Catalog?.Title ?? string.Empty;
    public string CoverUrl => Catalog?.Metadata?.CoverUrl ?? string.Empty;
    public string OwnerName => string.IsNullOrWhiteSpace(Catalog?.Metadata?.OwnerName) ? "UP 主信息暂不可用" : Catalog!.Metadata!.OwnerName;
    public string OwnerAvatarUrl => Catalog?.Metadata?.OwnerAvatarUrl ?? string.Empty;
    public string EpisodeCountText => Catalog is null ? string.Empty : $"已解析 {Rows.Count} 集";

    public string SelectionSummary
    {
        get
        {
            var selected = Rows.Where(row => row.IsSelected && row.IsReady).ToList();
            return $"已选择 {selected.Count}/{Rows.Count} 集 · 预计 {FormatBytes(selected.Sum(row => row.EstimatedSizeBytes))}";
        }
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (!SetProperty(ref _message, value)) return;
            OnPropertyChanged(nameof(HasMessage));
            OnPropertyChanged(nameof(MessageVisibility));
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public Visibility MessageVisibility => HasMessage ? Visibility.Visible : Visibility.Collapsed;
    public string MessageLogPath
    {
        get => _messageLogPath;
        private set
        {
            if (!SetProperty(ref _messageLogPath, value)) return;
            OnPropertyChanged(nameof(HasMessageLog));
            OnPropertyChanged(nameof(MessageLogVisibility));
        }
    }
    public bool HasMessageLog => !string.IsNullOrWhiteSpace(MessageLogPath);
    public Visibility MessageLogVisibility => HasMessageLog ? Visibility.Visible : Visibility.Collapsed;
    public InfoBarSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }

    public double OverallProgress { get => _overallProgress; private set => SetProperty(ref _overallProgress, value); }
    public double CurrentProgress { get => _currentProgress; private set => SetProperty(ref _currentProgress, value); }
    public bool CurrentProgressIndeterminate { get => _currentProgressIndeterminate; private set => SetProperty(ref _currentProgressIndeterminate, value); }
    public string ProgressTitle { get => _progressTitle; private set => SetProperty(ref _progressTitle, value); }
    public string ProgressDetail { get => _progressDetail; private set => SetProperty(ref _progressDetail, value); }
    public bool ShowProgress { get => _showProgress; private set { if (SetProperty(ref _showProgress, value)) OnPropertyChanged(nameof(ProgressVisibility)); } }
    public Visibility ProgressVisibility => ShowProgress ? Visibility.Visible : Visibility.Collapsed;

    public DownloadResult? LastDownloadResult
    {
        get => _lastDownloadResult;
        private set
        {
            if (!SetProperty(ref _lastDownloadResult, value)) return;
            OnPropertyChanged(nameof(HasDownloadResult));
            OnPropertyChanged(nameof(CanRenameDownload));
            OnPropertyChanged(nameof(DownloadResultMessage));
            OnPropertyChanged(nameof(DownloadResultVisibility));
        }
    }
    public bool HasDownloadResult => LastDownloadResult is not null;
    public Visibility DownloadResultVisibility => HasDownloadResult ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRenameDownload => LastDownloadResult?.HasVideo == true && !string.IsNullOrWhiteSpace(LastDownloadResult.RenameDirectory);
    public string DownloadResultMessage => LastDownloadResult switch
    {
        null => string.Empty,
        { HasVideo: false } result => $"已保存到 {result.OutputDirectory}，暂未识别到本次生成的视频。",
        { RenameDirectory.Length: > 0 } result => $"已保存到 {result.OutputDirectory}，可以直接进入重命名。",
        var result => $"已保存到 {result.OutputDirectory}；视频分布在多个子文件夹，请打开主文件夹查看。"
    };
    public DownloadNamingProfileKind ActiveNamingProfileKind => _restoredNamingProfile is not null
        ? _restoredNamingProfileKind
        : Catalog?.AllPages.Count > 1 ? DownloadNamingProfileKind.MultiEpisode : DownloadNamingProfileKind.SingleVideo;
    public string ActiveNamingProfileText => ActiveNamingProfileKind == DownloadNamingProfileKind.MultiEpisode ? "多集内容" : "单集视频";
    public string NamingRuleSummary
    {
        get
        {
            var profile = GetActiveNamingProfile();
            var preview = _services.DownloadNaming.Preview(profile, ActiveNamingProfileKind, WorkDirectory);
            return preview.IsValid ? preview.RelativePath : preview.Error;
        }
    }

    public IAsyncRelayCommand ParseCurrentCommand { get; }
    public IAsyncRelayCommand ParseAllCommand { get; }
    public IAsyncRelayCommand ContinueParseCommand { get; }
    public IRelayCommand ApplyRuleCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand InvertSelectionCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }

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

    public bool ApplyExternalInput(string? value)
    {
        if (!BilibiliInputParser.TryExtract(value, out var input)) return false;
        if (input.Equals(Url, StringComparison.OrdinalIgnoreCase)) return false;
        Url = input;
        return true;
    }

    public async Task InitializeAsync(HistoryRecord? restore = null)
    {
        var latestSettings = await _services.Settings.LoadAsync();
        _downloadNaming = latestSettings.DownloadNaming.Clone();
        if (!_initialized)
        {
            Apply(latestSettings);
            _initialized = true;
        }
        OnPropertyChanged(nameof(ActiveNamingProfileText));
        OnPropertyChanged(nameof(NamingRuleSummary));
        if (restore is null) return;
        ResetWorkspaceForRestore();
        if (restore.DownloadBatch is { } batch)
        {
            _restoredNamingProfile = (batch.NamingProfile ?? DownloadNamingProfile.Default()).Clone();
            _restoredNamingProfileKind = batch.NamingProfileKind;
            _loadingRestore = true;
            try
            {
                Apply(batch.Options);
                Url = restore.Url;
            }
            finally { _loadingRestore = false; }
            _pendingRestore = batch.Episodes.Select(item => new EpisodeStreamSelection
            {
                PageNumber = item.PageNumber,
                PageTitle = item.PageTitle,
                Video = item.Video,
                Audio = item.Audio,
                IsMuxedStream = item.IsMuxedStream,
                FallbackReason = item.FallbackReason,
                RelativeOutputPath = item.RelativeOutputPath
            }).ToList();
            SetMessage("批量下载配置已加载，请重新解析后确认实际可用规格。", InfoBarSeverity.Informational);
        }
        else if (restore.Download is { } legacy)
        {
            _loadingRestore = true;
            try { Apply(legacy); }
            finally { _loadingRestore = false; }
            SetMessage("旧版下载配置已加载，请先解析视频。", InfoBarSeverity.Informational);
        }
        if (!string.IsNullOrWhiteSpace(restore.OutputDirectory))
            LastDownloadResult = new DownloadResult(restore.Title, restore.OutputDirectory, restore.OutputFiles,
                restore.OutputFiles.Any(DownloadFileKinds.IsVideoFile), FindCommonVideoDirectory(restore.OutputFiles));
        OnPropertyChanged(nameof(ActiveNamingProfileText));
        OnPropertyChanged(nameof(NamingRuleSummary));
    }

    private void ResetWorkspaceForRestore()
    {
        _restoredNamingProfile = null;
        _restoredNamingProfileKind = DownloadNamingProfileKind.SingleVideo;
        foreach (var row in Rows) row.SelectionChanged -= Row_SelectionChanged;
        Rows.Clear();
        VisibleRows.Clear();
        Catalog = null;
        LastDownloadResult = null;
        SearchText = string.Empty;
        ShowProgress = false;
        _continueAvailable = false;
        _failedPages.Clear();
        _pendingRestore = null;
        NotifyCommands();
    }

    public void DismissMessage()
    {
        Message = string.Empty;
        MessageLogPath = string.Empty;
    }

    private async Task ParseAsync(DownloadParseMode mode, string pages = "", bool reset = true)
    {
        var parseGeneration = ++_parseGeneration;
        var parsedUrl = Url.Trim();
        if (reset)
        {
            ClearParsedState();
        }
        ShowProgress = true;
        OverallProgress = 0;
        CurrentProgress = 0;
        CurrentProgressIndeterminate = true;
        ProgressTitle = mode == DownloadParseMode.All ? "正在解析全部集" : "正在解析当前集";
        ProgressDetail = "正在启动 BBDown…";
        DownloadCatalog? catalog = null;
        var parseProgress = new Progress<DownloadParseProgress>(update =>
        {
            if (parseGeneration == _parseGeneration) OnParseProgress(update);
        });
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DownloadParse, SaveTaskLogs, "download_parse", async (context, token) =>
        {
            var settings = await _services.Settings.LoadAsync(token);
            catalog = await _services.BBDown.ParseDownloadAsync(new DownloadParseRequest(parsedUrl, mode, settings.ApiMode, pages), parseProgress, context, token);
        });

        if (parseGeneration != _parseGeneration || !UrlsMatch(parsedUrl, Url))
        {
            ShowProgress = false;
            NotifyCommands();
            return;
        }
        if (catalog is not null) ApplyCatalog(catalog, !reset);
        CurrentProgressIndeterminate = false;
        if (snapshot.State == TaskState.Failed)
        {
            SetMessage(string.IsNullOrWhiteSpace(snapshot.Error) ? "解析失败" : snapshot.Error, InfoBarSeverity.Error, snapshot.LogPath);
            ProgressTitle = "解析失败";
        }
        else if (snapshot.State == TaskState.Cancelled)
        {
            SetMessage(Rows.Count > 0 ? "解析已取消，已保留完成的结果。" : "解析已取消。", InfoBarSeverity.Warning);
            ProgressTitle = "解析已取消";
        }
        else
        {
            OverallProgress = 100;
            CurrentProgress = 100;
            ProgressTitle = "解析完成";
            ProgressDetail = $"成功解析 {Rows.Count(row => row.IsReady)}/{Rows.Count} 集";
        }
        ShowProgress = false;
        _continueAvailable = mode == DownloadParseMode.All && Catalog is not null && Catalog.AllPages.Any(page => Rows.All(row => row.PageNumber != page.Number || !row.IsReady));
        NotifyCommands();
    }

    private void OnParseProgress(DownloadParseProgress update)
    {
        ShowProgress = true;
        ProgressTitle = "正在解析视频规格";
        ProgressDetail = update.Message;
        OverallProgress = update.Total > 0 ? Math.Clamp(update.Completed * 100d / update.Total, 0, 100) : 0;
        if (update.Episode is not null) AddOrReplaceEpisode(update.Episode);
    }

    private void ApplyCatalog(DownloadCatalog catalog, bool merge)
    {
        if (merge && Catalog is { } existing)
        {
            Catalog = new DownloadCatalog
            {
                SourceUrl = existing.SourceUrl,
                Title = string.IsNullOrWhiteSpace(catalog.Title) ? existing.Title : catalog.Title,
                Metadata = catalog.Metadata ?? existing.Metadata,
                ParsedAt = catalog.ParsedAt,
                AllPages = existing.AllPages.Concat(catalog.AllPages).DistinctBy(item => item.Number).OrderBy(item => item.Number).ToList(),
                Episodes = existing.Episodes.Concat(catalog.Episodes).GroupBy(item => item.Page.Number).Select(group => group.Last()).OrderBy(item => item.Page.Number).ToList()
            };
        }
        else Catalog = catalog;
        foreach (var episode in catalog.Episodes)
        {
            AddOrReplaceEpisode(episode);
        }
        if (_pendingRestore is not null)
        {
            foreach (var row in Rows) row.IsSelected = false;
            foreach (var restored in _pendingRestore)
            {
                var row = Rows.FirstOrDefault(item => item.PageNumber == restored.PageNumber);
                row?.ApplyRestored(restored, CurrentDownloadMode);
            }
            _pendingRestore = null;
        }
        ApplyFilter();
        OnSelectionChanged();
    }

    private async Task ContinueParseAsync()
    {
        if (Catalog is null) return;
        var pages = Catalog.AllPages
            .Where(page => Rows.All(row => row.PageNumber != page.Number || !row.IsReady))
            .Select(page => page.Number)
            .Distinct()
            .Order()
            .ToList();
        if (pages.Count == 0) return;
        await ParseAsync(DownloadParseMode.All, string.Join(',', pages), reset: false);
    }

    private void AddOrReplaceEpisode(DownloadEpisodeInfo episode)
    {
        var existing = Rows.FirstOrDefault(row => row.PageNumber == episode.Page.Number);
        if (existing is not null)
        {
            if (existing.IsReady || !episode.State.Equals(DownloadEpisodeParseState.Ready)) return;
            existing.SelectionChanged -= Row_SelectionChanged;
            Rows.Remove(existing);
        }
        AddEpisode(episode);
        SortRows();
    }

    private void AddEpisode(DownloadEpisodeInfo episode)
    {
        var row = new DownloadEpisodeViewModel(episode);
        row.SelectionChanged += Row_SelectionChanged;
        if (row.IsReady)
        {
            row.ApplyRule(CurrentRule, CurrentDownloadMode);
            row.IsSelected = true;
        }
        Rows.Add(row);
        ApplyFilter();
        OnPropertyChanged(nameof(EpisodeCountText));
        OnSelectionChanged();
    }

    private void SortRows()
    {
        var ordered = Rows.OrderBy(row => row.PageNumber).ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Rows.IndexOf(ordered[target]);
            if (current != target) Rows.Move(current, target);
        }
        ApplyFilter();
    }

    private void ApplyRuleToAll()
    {
        foreach (var row in Rows.Where(item => item.IsReady)) row.ApplyRule(CurrentRule, CurrentDownloadMode);
        OnSelectionChanged();
    }

    private void SelectAll()
    {
        foreach (var row in VisibleRows.Where(item => item.IsReady)) row.IsSelected = true;
        OnSelectionChanged();
    }

    private void InvertSelection()
    {
        foreach (var row in VisibleRows.Where(item => item.IsReady)) row.IsSelected = !row.IsSelected;
        OnSelectionChanged();
    }

    private async Task StartDownloadAsync()
    {
        var selectedRows = Rows.Where(row => row.IsSelected && row.IsReady).OrderBy(row => row.PageNumber).ToList();
        if (selectedRows.Count == 0) return;
        LastDownloadResult = null;
        _failedPages.Clear();
        var options = await BuildRequestAsync();
        var namingProfile = GetActiveNamingProfile().Clone();
        var namingKind = ActiveNamingProfileKind;
        var downloadedAt = DateTimeOffset.Now;
        var batchRequest = new DownloadBatchRequest
        {
            Options = options,
            Title = Catalog?.Title ?? string.Empty,
            ParsedAt = Catalog?.ParsedAt ?? DateTimeOffset.Now,
            DownloadedAt = downloadedAt,
            TotalPages = Math.Max(Catalog?.AllPages.Count ?? selectedRows.Count, selectedRows.Count),
            NamingProfileKind = namingKind,
            NamingProfile = namingProfile,
            Metadata = Catalog?.Metadata,
            Episodes = selectedRows.Select(row => row.BuildSelection()).ToList()
        };
        DownloadBatchResult? result = null;
        ShowProgress = true;
        OverallProgress = 0;
        CurrentProgress = 0;
        CurrentProgressIndeterminate = !UseAria2c;
        ProgressTitle = "正在准备下载";
        ProgressDetail = $"共 {selectedRows.Count} 集";
        var downloadProgress = new Progress<DownloadProgressSnapshot>(OnDownloadProgress);
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DownloadBatch, SaveTaskLogs, "download_batch", async (context, token) =>
        {
            result = await _services.BBDown.DownloadBatchAsync(batchRequest, downloadProgress, context, token);
        });

        if (result is null)
        {
            ShowProgress = false;
            SetMessage(string.IsNullOrWhiteSpace(snapshot.Error) ? "下载任务未能启动" : snapshot.Error, InfoBarSeverity.Error);
            return;
        }
        foreach (var episode in result.Episodes)
        {
            Rows.FirstOrDefault(row => row.PageNumber == episode.PageNumber)?.ApplyResult(episode);
            if (episode.State == DownloadEpisodeResultState.Failed) _failedPages.Add(episode.PageNumber);
        }
        LastDownloadResult = new DownloadResult(result.Title, result.OutputDirectory, result.OutputFiles, result.HasVideo, result.RenameDirectory);
        await _services.History.AddAsync(new HistoryRecord
        {
            TaskType = TaskKind.DownloadBatch,
            Title = result.Title,
            Url = options.Url,
            Timestamp = DateTimeOffset.Now,
            LogPath = snapshot.LogPath,
            OutputDirectory = result.OutputDirectory,
            OutputFiles = result.OutputFiles,
            DownloadBatch = new DownloadBatchHistory
            {
                Options = options,
                ParsedAt = batchRequest.ParsedAt,
                DownloadedAt = downloadedAt,
                TotalPages = batchRequest.TotalPages,
                NamingProfileKind = namingKind,
                NamingProfile = namingProfile.Clone(),
                Episodes = result.Episodes
            }
        });
        var succeeded = result.Episodes.Count(item => item.State == DownloadEpisodeResultState.Completed);
        var failed = result.Episodes.Count(item => item.State == DownloadEpisodeResultState.Failed);
        ShowProgress = false;
        SetMessage(failed == 0 ? $"下载完成：成功 {succeeded} 集。" : $"批量任务完成：成功 {succeeded} 集，失败 {failed} 集。", failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        RetryFailedCommand.NotifyCanExecuteChanged();
        NotifyCommands();
    }

    private async Task RetryFailedAsync()
    {
        foreach (var row in Rows) row.IsSelected = _failedPages.Contains(row.PageNumber);
        await StartDownloadAsync();
    }

    private void OnDownloadProgress(DownloadProgressSnapshot update)
    {
        ShowProgress = true;
        OverallProgress = Math.Max(OverallProgress, update.OverallPercent);
        CurrentProgress = update.CurrentPercent ?? 0;
        CurrentProgressIndeterminate = update.CurrentPercent is null && update.Phase is DownloadProgressPhase.Downloading or DownloadProgressPhase.Muxing or DownloadProgressPhase.Validating;
        ProgressTitle = update.CurrentPage > 0 ? $"P{update.CurrentPage} {update.CurrentTitle}" : "批量下载";
        var transfer = string.Join(" · ", new[] { update.Message, string.IsNullOrWhiteSpace(update.Speed) ? string.Empty : $"速度 {update.Speed}", string.IsNullOrWhiteSpace(update.Eta) ? string.Empty : $"剩余 {update.Eta}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        ProgressDetail = $"已处理 {update.CompletedEpisodes}/{update.TotalEpisodes} 集{(string.IsNullOrWhiteSpace(transfer) ? string.Empty : $" · {transfer}")}";
        if (update.CurrentPage > 0) Rows.FirstOrDefault(row => row.PageNumber == update.CurrentPage)?.SetRuntimeStatus(update.Phase, update.Message);
    }

    private async Task<DownloadRequest> BuildRequestAsync()
    {
        var settings = await _services.Settings.LoadAsync();
        return new DownloadRequest
        {
            Url = Url.Trim(), Quality = QualityRule, Encoding = Encoding,
            DownloadMode = CurrentDownloadMode, AudioCodec = AudioCodec,
            AudioBitratePriority = AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest,
            Danmaku = Danmaku, Subtitle = Subtitle, Cover = Cover, WorkDirectory = WorkDirectory, MultiThread = MultiThread,
            UposHost = UposHost, UseAria2c = UseAria2c, Aria2cPath = settings.Aria2cPath,
            Aria2AutoTune = settings.Aria2AutoTune,
            Aria2MaxConnection = settings.Aria2MaxConnection, Aria2Split = settings.Aria2Split,
            Aria2MaxConcurrentDownloads = settings.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = settings.Aria2MinSplitSize,
            SaveTaskLogs = SaveTaskLogs, ApiMode = settings.ApiMode, OrganizeInTitleDirectory = false,
            TitleHint = Catalog?.Title ?? string.Empty
        };
    }

    private StreamSelectionRule CurrentRule => new(QualityRule, Encoding, AudioCodec,
        AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest);
    private DownloadMode CurrentDownloadMode => DownloadModeText switch { "仅视频" => DownloadMode.VideoOnly, "仅音频" => DownloadMode.AudioOnly, _ => DownloadMode.VideoAndAudio };

    private void Apply(AppSettings settings)
    {
        QualityRule = settings.VideoQualityRule;
        Encoding = settings.Encoding;
        DownloadModeText = settings.LegacyAudioOnly;
        AudioCodec = settings.AudioCodec;
        AudioBitrate = settings.LegacyAudioBitratePriority;
        WorkDirectory = settings.WorkDirectory;
        Danmaku = settings.Danmaku;
        Subtitle = settings.Subtitle;
        Cover = settings.Cover;
        MultiThread = settings.MultiThread;
        UposHost = settings.UposHost;
        UseAria2c = settings.UseAria2c;
        SaveTaskLogs = settings.SaveTaskLogs;
    }

    private void Apply(DownloadRequest request)
    {
        Url = request.Url;
        QualityRule = StreamSelectionPolicy.NormalizeQualityRule(request.Quality);
        Encoding = request.Encoding;
        DownloadModeText = request.DownloadMode switch { DownloadMode.VideoOnly => "仅视频", DownloadMode.AudioOnly => "仅音频", _ => "视频+音频" };
        AudioCodec = request.AudioCodec;
        AudioBitrate = request.AudioBitratePriority == AudioBitratePriority.Lowest ? "lowest" : "highest";
        WorkDirectory = request.WorkDirectory;
        Danmaku = request.Danmaku;
        Subtitle = request.Subtitle;
        Cover = request.Cover;
        MultiThread = request.MultiThread;
        UposHost = request.UposHost;
        UseAria2c = request.UseAria2c;
        SaveTaskLogs = request.SaveTaskLogs;
    }

    private DownloadNamingProfile GetActiveNamingProfile() =>
        (_restoredNamingProfile ?? _downloadNaming.GetProfile(ActiveNamingProfileKind) ?? DownloadNamingProfile.Default()).Clone();

    private static string FindCommonVideoDirectory(IEnumerable<string> files)
    {
        var directories = files.Where(DownloadFileKinds.IsVideoFile)
            .Select(Path.GetDirectoryName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return directories.Count == 1 ? directories[0]! : string.Empty;
    }

    private bool CanParse() => !Console.IsBusy && !string.IsNullOrWhiteSpace(Url);
    private bool CanContinueParse() => !Console.IsBusy && _continueAvailable && CatalogMatchesCurrentUrl() && Catalog!.AllPages.Any(page => Rows.All(row => row.PageNumber != page.Number || !row.IsReady));
    private bool CanDownload()
    {
        if (Console.IsBusy || !CatalogMatchesCurrentUrl()) return false;
        return Rows.Any(row =>
        {
            if (!row.IsSelected || !row.IsReady) return false;
            var selection = row.BuildSelection();
            if (row.Episode.IsMuxedStream) return selection.Video is not null;
            return CurrentDownloadMode switch
            {
                DownloadMode.VideoOnly => selection.Video is not null,
                DownloadMode.AudioOnly => selection.Audio is not null,
                _ => selection.Video is not null && selection.Audio is not null
            };
        });
    }

    private void ApplyFilter()
    {
        VisibleRows.Clear();
        var filter = SearchText.Trim();
        foreach (var row in Rows.Where(row => string.IsNullOrWhiteSpace(filter)
                                              || row.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                              || row.PageNumberText.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            VisibleRows.Add(row);
    }

    private void Row_SelectionChanged(object? sender, EventArgs e) => OnSelectionChanged();
    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }

    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Console.IsBusy)) NotifyCommands();
    }

    private void NotifyCommands()
    {
        ParseCurrentCommand.NotifyCanExecuteChanged();
        ParseAllCommand.NotifyCanExecuteChanged();
        ContinueParseCommand.NotifyCanExecuteChanged();
        ApplyRuleCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateParsedState()
    {
        _parseGeneration++;
        ClearParsedState();
        NotifyCommands();
    }

    private void ClearParsedState()
    {
        foreach (var row in Rows) row.SelectionChanged -= Row_SelectionChanged;
        Rows.Clear();
        VisibleRows.Clear();
        Catalog = null;
        LastDownloadResult = null;
        _failedPages.Clear();
        _continueAvailable = false;
        _pendingRestore = null;
        ShowProgress = false;
        OverallProgress = 0;
        CurrentProgress = 0;
    }

    private bool CatalogMatchesCurrentUrl() => Catalog is not null && UrlsMatch(Catalog.SourceUrl, Url);
    private static bool UrlsMatch(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void SetMessage(string value, InfoBarSeverity severity, string logPath = "")
    {
        MessageLogPath = logPath;
        MessageSeverity = severity;
        Message = value;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
