using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed class RenameFileItemViewModel : ObservableObject
{
    private bool _isSelected;
    public RenameFileItemViewModel(RenameFileEntry entry)
    {
        SourcePath = entry.SourcePath;
        DetectedEpisode = entry.DetectedEpisode;
        _isSelected = entry.IsSelected;
    }
    public string SourcePath { get; }
    public string Name => Path.GetFileName(SourcePath);
    public int? DetectedEpisode { get; }
    public string EpisodeText => DetectedEpisode is null ? "未识别" : $"E{DetectedEpisode:00}";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public RenameFileEntry ToModel() => new() { SourcePath = SourcePath, DetectedEpisode = DetectedEpisode, IsSelected = IsSelected };
}

public sealed class RenameViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<RenameTemplate> _allTemplates = [];
    private CancellationTokenSource? _tmdbCancellation;
    private string _directoryPath = string.Empty;
    private RenameMediaType _mediaType = RenameMediaType.Series;
    private string _chineseTitle = string.Empty;
    private string _englishTitle = string.Empty;
    private string _year = string.Empty;
    private int _season = 1;
    private string _filenameSuffix = string.Empty;
    private bool _useCustomEpisodes;
    private int _startEpisode = 1;
    private RenameTemplate? _selectedTemplate;
    private string _templatePattern = string.Empty;
    private string _tmdbQuery = string.Empty;
    private TmdbSearchResult? _selectedTmdbResult;
    private RenamePreview? _preview;
    private RenameHistoryRecord? _selectedHistory;
    private string _historySearch = string.Empty;
    private string _message = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private int? _tmdbId;
    private bool _active;
    private bool _suppressTemplatePersistence;
    private string _activeSeriesTemplateId = RenameTemplate.BuiltInSeriesId;
    private string _activeMovieTemplateId = RenameTemplate.BuiltInMovieId;
    private readonly List<RenameHistoryRecord> _filteredHistory = [];
    private int _historyPageNumber = 1;
    private const int HistoryPageSize = 8;
    private bool _isPreviewing;
    private CancellationTokenSource? _historyLoadCancellation;
    private int _historyLoadGeneration;
    private CancellationTokenSource? _templatePersistenceCancellation;
    private int _templatePersistenceGeneration;
    private bool _showingExecutionResult;
    private int _executionOperationCount;

    public RenameViewModel(AppServices services)
    {
        _services = services;
        try { _dispatcher = DispatcherQueue.GetForCurrentThread(); }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Headless App-layer tests do not initialize the WinUI dispatcher.
        }
        Console = services.TaskConsole;
        Files.CollectionChanged += (_, _) =>
        {
            ClearPreview();
            OnPropertyChanged(nameof(CanPreview));
        };
    }

    public TaskConsoleViewModel Console { get; }
    public ObservableCollection<RenameFileItemViewModel> Files { get; } = [];
    public ObservableCollection<RenamePreviewItem> PreviewItems { get; } = [];
    public ObservableCollection<RenameTemplate> Templates { get; } = [];
    public ObservableCollection<TmdbSearchResult> TmdbResults { get; } = [];
    public ObservableCollection<RenameHistoryRecord> HistoryRecords { get; } = [];

    public string DirectoryPath
    {
        get => _directoryPath;
        private set
        {
            if (SetProperty(ref _directoryPath, value)) OnPropertyChanged(nameof(CanReloadDirectory));
        }
    }
    public RenameMediaType MediaType
    {
        get => _mediaType;
        private set
        {
            if (!SetProperty(ref _mediaType, value)) return;
            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(CanEditCustomEpisodes));
            OnPropertyChanged(nameof(EpisodeSettingsSummary));
            OnPropertyChanged(nameof(TmdbInfoText));
            ApplyTemplatesForMediaType(GetActiveTemplateId(value));
        }
    }
    public bool IsSeries => MediaType == RenameMediaType.Series;
    public string ChineseTitle => _chineseTitle;
    public string EnglishTitle => _englishTitle;
    public string Year => _year;
    public string TmdbMediaTypeText => MediaType == RenameMediaType.Series ? "剧集" : "电影";
    public string TmdbInfoText => HasTmdbMatch
        ? $"已匹配：{ChineseTitle}{(string.IsNullOrWhiteSpace(EnglishTitle) ? string.Empty : $" / {EnglishTitle}")} · {Year} · {TmdbMediaTypeText}"
        : string.Empty;
    public bool HasTmdbMatch => _tmdbId is not null;
    public Visibility TmdbMatchVisibility => HasTmdbMatch ? Visibility.Visible : Visibility.Collapsed;
    public int Season
    {
        get => _season;
        set
        {
            if (!SetProperty(ref _season, Math.Clamp(value, 0, 99))) return;
            OnPropertyChanged(nameof(SeasonText));
            ClearPreview();
        }
    }
    public string SeasonText
    {
        get => Season.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, out var parsed)) Season = parsed;
        }
    }
    public string FilenameSuffix { get => _filenameSuffix; set { if (SetProperty(ref _filenameSuffix, value)) ClearPreview(); } }
    public bool UseCustomEpisodes
    {
        get => _useCustomEpisodes;
        set
        {
            if (!SetProperty(ref _useCustomEpisodes, value)) return;
            OnPropertyChanged(nameof(CanEditCustomEpisodes));
            OnPropertyChanged(nameof(EpisodeSettingsSummary));
            ClearPreview();
        }
    }
    public bool CanEditCustomEpisodes => IsSeries && UseCustomEpisodes;
    public string EpisodeSettingsSummary => UseCustomEpisodes ? "高级设置 · 自定义集数" : "高级设置 · 自动识别集数";
    public int StartEpisode
    {
        get => _startEpisode;
        set
        {
            if (!SetProperty(ref _startEpisode, Math.Max(1, value))) return;
            OnPropertyChanged(nameof(StartEpisodeText));
            OnPropertyChanged(nameof(EpisodeSettingsSummary));
            ClearPreview();
        }
    }
    public string StartEpisodeText
    {
        get => StartEpisode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, out var parsed)) StartEpisode = Math.Min(999, parsed);
        }
    }

    public void NormalizeNumericInputs()
    {
        OnPropertyChanged(nameof(SeasonText));
        OnPropertyChanged(nameof(StartEpisodeText));
    }
    public RenameTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetProperty(ref _selectedTemplate, value) || value is null) return;
            TemplatePattern = value.Pattern;
            ClearPreview();
            if (_suppressTemplatePersistence) return;
            SetActiveTemplateId(value.MediaType, value.Id);
            QueueActiveTemplatePersistence(value);
        }
    }
    public string TemplatePattern
    {
        get => _templatePattern;
        private set
        {
            if (!SetProperty(ref _templatePattern, value)) return;
            OnPropertyChanged(nameof(TemplateExample));
            ClearPreview();
        }
    }
    public string TemplateExample => RenameTemplatePresentation.BuildExample(TemplatePattern);
    public string TmdbQuery { get => _tmdbQuery; set => SetProperty(ref _tmdbQuery, value); }
    public TmdbSearchResult? SelectedTmdbResult { get => _selectedTmdbResult; set => SetProperty(ref _selectedTmdbResult, value); }
    public RenameHistoryRecord? SelectedHistory
    {
        get => _selectedHistory;
        set { if (SetProperty(ref _selectedHistory, value)) OnPropertyChanged(nameof(CanUndoSelectedHistory)); }
    }
    public bool CanUndoSelectedHistory => SelectedHistory is { UndoneAt: null } && !Console.IsBusy;
    public string HistorySearch
    {
        get => _historySearch;
        set
        {
            if (!SetProperty(ref _historySearch, value)) return;
            _historyPageNumber = 1;
            _ = LoadHistoryAsync();
        }
    }
    public int HistoryPageNumber
    {
        get => _historyPageNumber;
        private set
        {
            if (!SetProperty(ref _historyPageNumber, value)) return;
            OnPropertyChanged(nameof(HistoryPageText));
            OnPropertyChanged(nameof(CanPreviousHistoryPage));
            OnPropertyChanged(nameof(CanNextHistoryPage));
        }
    }
    public int HistoryTotalPages => Math.Max(1, (int)Math.Ceiling(_filteredHistory.Count / (double)HistoryPageSize));
    public string HistoryPageText => $"第 {HistoryPageNumber} / {HistoryTotalPages} 页";
    public bool CanPreviousHistoryPage => HistoryPageNumber > 1;
    public bool CanNextHistoryPage => HistoryPageNumber < HistoryTotalPages;
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
    public InfoBarSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }
    public bool IsPreviewing
    {
        get => _isPreviewing;
        private set
        {
            if (SetProperty(ref _isPreviewing, value)) OnPropertyChanged(nameof(CanPreview));
        }
    }
    public bool CanPreview => HasTmdbMatch && Files.Any(file => file.IsSelected) && !Console.IsBusy && !IsPreviewing;
    public bool CanReloadDirectory => !string.IsNullOrWhiteSpace(DirectoryPath) && !Console.IsBusy;
    public bool CanExecutePreview => _preview?.CanExecute == true && !Console.IsBusy;
    public string PreviewTitle => _showingExecutionResult ? "重命名结果" : "重命名预览";
    public string PreviewSummary => _preview is null
        ? _showingExecutionResult
            ? $"已完成 {PreviewItems.Count} 个视频 · {_executionOperationCount} 项文件名变更"
            : "尚未生成预览"
        : _preview.CanExecute ? $"{_preview.Items.Count} 个视频，可执行 {_preview.Operations.Count} 项文件名变更" : $"预览包含 {_preview.Errors.Count} 个冲突";

    public void Activate()
    {
        if (_active) return;
        _active = true;
        Console.PropertyChanged += Console_PropertyChanged;
        _services.RenameHistory.Changed += RenameHistory_Changed;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        Console.PropertyChanged -= Console_PropertyChanged;
        _services.RenameHistory.Changed -= RenameHistory_Changed;
        _tmdbCancellation?.Cancel();
        _historyLoadCancellation?.Cancel();
    }

    public async Task InitializeAsync(RenameNavigationContext? context)
    {
        await RefreshTemplatesAsync();
        await LoadHistoryAsync();
        if (context is not null && Directory.Exists(context.DirectoryPath))
        {
            ResetWorkspaceForNewDownload();
            await LoadDirectoryAsync(context.DirectoryPath, context.PreferredFiles, context.SuggestedTitle);
        }
    }

    public async Task LoadDirectoryAsync(string directory, IReadOnlyCollection<string>? preferredFiles = null, string? suggestedTitle = null)
    {
        ClearTmdbSelection();
        DirectoryPath = Path.GetFullPath(directory);
        Files.Clear();
        foreach (var file in await _services.Rename.ScanAsync(DirectoryPath, preferredFiles))
        {
            var item = new RenameFileItemViewModel(file);
            item.PropertyChanged += File_PropertyChanged;
            Files.Add(item);
        }
        var extracted = RenameService.ExtractTitleFromFolder(Path.GetFileName(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(suggestedTitle) && !ContainsChinese(extracted) && ContainsChinese(suggestedTitle)) extracted = suggestedTitle.Trim();
        TmdbQuery = extracted;
        ClearPreview();
        MessageSeverity = Files.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Message = Files.Count == 0 ? "所选文件夹中没有受支持的视频文件" : $"已加载 {Files.Count} 个视频文件，请搜索并应用正确的 TMDB 条目";
        OnPropertyChanged(nameof(CanPreview));
    }

    public async Task SearchTmdbAsync()
    {
        _tmdbCancellation?.Cancel();
        _tmdbCancellation = new CancellationTokenSource();
        TmdbResults.Clear();
        try
        {
            var query = TmdbQuery.Trim();
            if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("请输入 TMDB 搜索关键词");
            foreach (var result in await _services.Tmdb.SearchAsync(query, _tmdbCancellation.Token)) TmdbResults.Add(result);
            MessageSeverity = TmdbResults.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            Message = TmdbResults.Count == 0 ? "TMDB 没有找到匹配结果，请修改关键词后重试" : $"找到 {TmdbResults.Count} 个 TMDB 结果，请选择正确条目";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            MessageSeverity = InfoBarSeverity.Warning;
            Message = $"{exception.Message}；请检查 TMDB Key、代理或网络设置";
        }
    }

    public async Task ApplyTmdbResultAsync(TmdbSearchResult result)
    {
        try
        {
            var detail = await _services.Tmdb.GetDetailAsync(result);
            MediaType = detail.MediaType;
            _tmdbId = detail.Id;
            _chineseTitle = detail.ChineseTitle;
            _englishTitle = detail.EnglishTitle;
            _year = detail.Year;
            OnPropertyChanged(nameof(ChineseTitle));
            OnPropertyChanged(nameof(EnglishTitle));
            OnPropertyChanged(nameof(Year));
            OnPropertyChanged(nameof(TmdbMediaTypeText));
            OnPropertyChanged(nameof(TmdbInfoText));
            OnPropertyChanged(nameof(HasTmdbMatch));
            OnPropertyChanged(nameof(TmdbMatchVisibility));
            OnPropertyChanged(nameof(CanPreview));
            TmdbResults.Clear();
            Message = string.Empty;
            ClearPreview();
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            MessageSeverity = InfoBarSeverity.Warning;
            Message = $"{exception.Message}；请检查 TMDB Key、代理或网络设置";
        }
    }

    public async Task PreviewAsync()
    {
        if (!CanPreview || IsPreviewing) return;
        if (!HasTmdbMatch)
        {
            MessageSeverity = InfoBarSeverity.Warning;
            Message = "请先搜索并应用正确的 TMDB 条目";
            return;
        }
        IsPreviewing = true;
        try
        {
            RenamePreview? generated = null;
            TaskSnapshot snapshot;
            try
            {
                snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.RenamePreview, false, "rename_preview", async (taskContext, token) =>
                {
                    IReadOnlyDictionary<int, string> episodeNames = new Dictionary<int, string>();
                    if (MediaType == RenameMediaType.Series && _tmdbId is int tmdbId)
                    {
                        try { episodeNames = await _services.Tmdb.GetEpisodeNamesAsync(tmdbId, Season, token); }
                        catch (InvalidOperationException) { }
                    }
                    var request = new RenamePreviewRequest
                    {
                        DirectoryPath = DirectoryPath,
                        MediaType = MediaType,
                        ChineseTitle = ChineseTitle,
                        EnglishTitle = EnglishTitle,
                        Year = Year,
                        Season = Season,
                        TemplateName = SelectedTemplate?.Name ?? "自定义模板",
                        TemplatePattern = TemplatePattern,
                        FilenameSuffix = FilenameSuffix,
                        UseCustomEpisodes = UseCustomEpisodes,
                        StartEpisode = StartEpisode,
                        Files = Files.Select(file => file.ToModel()).ToList(),
                        EpisodeNames = episodeNames
                    };
                    generated = await _services.Rename.BuildPreviewAsync(request, taskContext, token);
                });
            }
            catch (InvalidOperationException exception)
            {
                MessageSeverity = InfoBarSeverity.Warning;
                Message = exception.Message;
                return;
            }
            if (snapshot.State != TaskState.Completed || generated is null)
            {
                MessageSeverity = snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
                Message = snapshot.State == TaskState.Cancelled ? "预览已取消" : $"预览失败：{snapshot.Error}";
                return;
            }
            _preview = generated;
            PreviewItems.Clear();
            foreach (var item in generated.Items) PreviewItems.Add(item);
            OnPropertyChanged(nameof(CanExecutePreview));
            OnPropertyChanged(nameof(PreviewSummary));
            MessageSeverity = generated.CanExecute ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            Message = generated.CanExecute ? "预览已生成，请确认后执行" : string.Join("；", generated.Errors.Take(4));
        }
        finally { IsPreviewing = false; }
    }

    public async Task ExecuteAsync()
    {
        if (_preview is null) return;
        var completedItems = _preview.Items.ToList();
        RenameExecutionResult? result = null;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.RenameExecute, true, "rename", async (context, token) =>
        {
            result = await _services.Rename.ExecuteAsync(_preview, context, token);
        });
        if (snapshot.State != TaskState.Completed || result is null)
        {
            MessageSeverity = snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
            Message = snapshot.State == TaskState.Cancelled ? "重命名已取消并回滚" : $"重命名失败：{snapshot.Error}";
            return;
        }
        await LoadDirectoryAsync(result.DirectoryPath);
        ShowExecutionResult(completedItems, result.OperationCount);
        MessageSeverity = InfoBarSeverity.Success;
        Message = $"已完成 {result.OperationCount} 项文件名变更";
        await LoadHistoryAsync();
    }

    public async Task UndoSelectedHistoryAsync()
    {
        if (SelectedHistory is not { UndoneAt: null } record) return;
        RenameExecutionResult? result = null;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.RenameUndo, true, "rename_undo", async (context, token) =>
        {
            result = await _services.Rename.UndoAsync(record.Id, context, token);
        });
        if (snapshot.State != TaskState.Completed || result is null)
        {
            MessageSeverity = InfoBarSeverity.Error;
            Message = snapshot.State == TaskState.Cancelled ? "撤销已取消并回滚" : $"撤销失败：{snapshot.Error}";
            return;
        }
        MessageSeverity = InfoBarSeverity.Success;
        Message = $"已撤销 {result.OperationCount} 项文件名变更";
        if (Directory.Exists(result.DirectoryPath)) await LoadDirectoryAsync(result.DirectoryPath);
        await LoadHistoryAsync();
    }

    public async Task LoadHistoryAsync()
    {
        var generation = Interlocked.Increment(ref _historyLoadGeneration);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _historyLoadCancellation, cancellation);
        previous?.Cancel();
        var query = HistorySearch.Trim();
        try
        {
            var records = await _services.RenameHistory.LoadAsync(cancellation.Token);
            if (generation != Volatile.Read(ref _historyLoadGeneration) || cancellation.IsCancellationRequested) return;
            var filtered = string.IsNullOrWhiteSpace(query) ? records : records.Where(record =>
                record.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                record.DirectoryPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                record.TemplateName.Contains(query, StringComparison.OrdinalIgnoreCase));
            _filteredHistory.Clear();
            _filteredHistory.AddRange(filtered);
            HistoryPageNumber = Math.Min(HistoryPageNumber, HistoryTotalPages);
            ApplyHistoryPage();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (generation != Volatile.Read(ref _historyLoadGeneration)) return;
            MessageSeverity = InfoBarSeverity.Warning;
            Message = $"重命名历史加载失败：{exception.Message}";
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _historyLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    public void PreviousHistoryPage()
    {
        if (!CanPreviousHistoryPage) return;
        HistoryPageNumber--;
        ApplyHistoryPage();
    }

    public void NextHistoryPage()
    {
        if (!CanNextHistoryPage) return;
        HistoryPageNumber++;
        ApplyHistoryPage();
    }

    public async Task DeleteSelectedHistoryAsync()
    {
        if (SelectedHistory is null) return;
        await _services.RenameHistory.DeleteAsync(SelectedHistory.Id);
        await LoadHistoryAsync();
    }

    public async Task ClearHistoryAsync()
    {
        await _services.RenameHistory.ClearAsync();
        await LoadHistoryAsync();
    }

    private async Task RefreshTemplatesAsync()
    {
        var settings = await _services.RenameSettings.LoadAsync();
        _allTemplates.Clear();
        _allTemplates.AddRange(settings.Templates);
        _activeSeriesTemplateId = settings.ActiveSeriesTemplateId;
        _activeMovieTemplateId = settings.ActiveMovieTemplateId;
        ApplyTemplatesForMediaType(GetActiveTemplateId(MediaType));
    }

    private void ApplyTemplatesForMediaType(string? selectedId)
    {
        _suppressTemplatePersistence = true;
        Templates.Clear();
        foreach (var template in _allTemplates
                     .Where(template => template.MediaType == MediaType)
                     .OrderByDescending(template => template.BuiltIn)
                     .ThenBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase))
            Templates.Add(template);
        var builtInId = MediaType == RenameMediaType.Series ? RenameTemplate.BuiltInSeriesId : RenameTemplate.BuiltInMovieId;
        SelectedTemplate = Templates.FirstOrDefault(template => template.Id == selectedId)
            ?? Templates.FirstOrDefault(template => template.Id == builtInId)
            ?? Templates.FirstOrDefault();
        _suppressTemplatePersistence = false;
    }

    private void ApplyHistoryPage()
    {
        HistoryRecords.Clear();
        foreach (var record in _filteredHistory.Skip((HistoryPageNumber - 1) * HistoryPageSize).Take(HistoryPageSize)) HistoryRecords.Add(record);
        SelectedHistory = HistoryRecords.FirstOrDefault();
        OnPropertyChanged(nameof(HistoryTotalPages));
        OnPropertyChanged(nameof(HistoryPageText));
        OnPropertyChanged(nameof(CanPreviousHistoryPage));
        OnPropertyChanged(nameof(CanNextHistoryPage));
    }

    private void QueueActiveTemplatePersistence(RenameTemplate template)
    {
        var generation = Interlocked.Increment(ref _templatePersistenceGeneration);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _templatePersistenceCancellation, cancellation);
        previous?.Cancel();
        _ = PersistActiveTemplateAsync(template, generation, cancellation);
    }

    private async Task PersistActiveTemplateAsync(RenameTemplate template, int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await _services.RenameSettings.UpdateAsync(current =>
            {
                if (template.MediaType == RenameMediaType.Series) current.ActiveSeriesTemplateId = template.Id;
                else current.ActiveMovieTemplateId = template.Id;
                return current;
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (generation == Volatile.Read(ref _templatePersistenceGeneration))
            {
                MessageSeverity = InfoBarSeverity.Warning;
                Message = $"模板选择暂时无法保存：{exception.Message}";
            }
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _templatePersistenceCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private string GetActiveTemplateId(RenameMediaType mediaType) => mediaType == RenameMediaType.Series
        ? _activeSeriesTemplateId
        : _activeMovieTemplateId;

    private void SetActiveTemplateId(RenameMediaType mediaType, string id)
    {
        if (mediaType == RenameMediaType.Series) _activeSeriesTemplateId = id;
        else _activeMovieTemplateId = id;
    }

    private void ClearTmdbSelection()
    {
        _tmdbId = null;
        TmdbResults.Clear();
        SelectedTmdbResult = null;
        _chineseTitle = string.Empty;
        _englishTitle = string.Empty;
        _year = string.Empty;
        OnPropertyChanged(nameof(ChineseTitle));
        OnPropertyChanged(nameof(EnglishTitle));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(TmdbMediaTypeText));
        OnPropertyChanged(nameof(TmdbInfoText));
        OnPropertyChanged(nameof(HasTmdbMatch));
        OnPropertyChanged(nameof(TmdbMatchVisibility));
        OnPropertyChanged(nameof(CanPreview));
        ClearPreview();
    }

    private void ClearPreview()
    {
        _preview = null;
        _showingExecutionResult = false;
        _executionOperationCount = 0;
        PreviewItems.Clear();
        OnPropertyChanged(nameof(CanExecutePreview));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewSummary));
    }

    private void ShowExecutionResult(IEnumerable<RenamePreviewItem> items, int operationCount)
    {
        _preview = null;
        PreviewItems.Clear();
        foreach (var item in items) PreviewItems.Add(item);
        _showingExecutionResult = true;
        _executionOperationCount = operationCount;
        OnPropertyChanged(nameof(CanExecutePreview));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewSummary));
    }

    private void ResetWorkspaceForNewDownload()
    {
        MediaType = RenameMediaType.Series;
        _season = 1;
        _filenameSuffix = string.Empty;
        _useCustomEpisodes = false;
        _startEpisode = 1;
        OnPropertyChanged(nameof(Season));
        OnPropertyChanged(nameof(SeasonText));
        OnPropertyChanged(nameof(FilenameSuffix));
        OnPropertyChanged(nameof(UseCustomEpisodes));
        OnPropertyChanged(nameof(CanEditCustomEpisodes));
        OnPropertyChanged(nameof(StartEpisode));
        OnPropertyChanged(nameof(StartEpisodeText));
        OnPropertyChanged(nameof(EpisodeSettingsSummary));
    }

    private void File_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RenameFileItemViewModel.IsSelected)) return;
        ClearPreview();
        OnPropertyChanged(nameof(CanPreview));
    }

    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Console.IsBusy)) return;
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanExecutePreview));
        OnPropertyChanged(nameof(CanUndoSelectedHistory));
        OnPropertyChanged(nameof(CanReloadDirectory));
    }

    private void RenameHistory_Changed(object? sender, EventArgs e)
    {
        if (!_active) return;
        if (_dispatcher is null) _ = LoadHistoryAsync();
        else _dispatcher.TryEnqueue(async () => await LoadHistoryAsync());
    }

    private static bool ContainsChinese(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

}
