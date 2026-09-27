using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App.ViewModels;

public sealed class SpaceVideoViewModel(BilibiliSpaceVideo video) : ObservableObject
{
    private bool selected;
    private bool enqueued;
    private string error = "";
    public BilibiliSpaceVideo Video { get; private set; } = video;
    public string Title => Video.Title;
    public string Bvid => Video.Bvid;
    public string CoverUrl => Video.CoverUrl;
    public string DurationText => Video.DurationSeconds >= 3600
        ? TimeSpan.FromSeconds(Video.DurationSeconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(Video.DurationSeconds).ToString(@"m\:ss");
    public string PublishedText => Video.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");
    public bool IsSelected { get => selected; set { if (SetProperty(ref selected, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool IsEnqueued { get => enqueued; set { if (SetProperty(ref enqueued, value)) { OnPropertyChanged(nameof(CanSelect)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); Changed?.Invoke(this, EventArgs.Empty); } } }
    public bool CanSelect => !IsEnqueued;
    public string Error { get => error; set { if (SetProperty(ref error, value)) { OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); } } }
    public string Status => IsEnqueued ? "已加入队列" : Error;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public DownloadCatalog? Catalog { get; private set; }
    public ObservableCollection<DownloadEpisodeViewModel> Rows { get; } = [];
    public event EventHandler? Changed;

    public void UpdateVideo(BilibiliSpaceVideo value)
    {
        Video = value;
        foreach (var name in new[] { nameof(Title), nameof(CoverUrl), nameof(DurationText), nameof(PublishedText) }) OnPropertyChanged(name);
    }

    public void MergeCatalog(DownloadCatalog catalog, StreamSelectionRule rule, DownloadMode mode)
    {
        Catalog = catalog;
        foreach (var page in catalog.AllPages.OrderBy(p => p.Number))
        {
            var old = Rows.FirstOrDefault(r => r.PageNumber == page.Number);
            if (old?.IsReady == true) continue;
            var episode = catalog.Episodes.FirstOrDefault(e => e.Page.Number == page.Number)
                ?? new DownloadEpisodeInfo { Page = page, State = DownloadEpisodeParseState.Pending, Error = "尚未解析，可重试" };
            if (old is not null) { old.SelectionChanged -= RowChanged; Rows.Remove(old); }
            var row = new DownloadEpisodeViewModel(episode);
            row.ApplyRule(rule, mode);
            row.IsSelected = row.IsReady && SelectionComplete(row, mode);
            row.SelectionChanged += RowChanged;
            var index = Rows.TakeWhile(r => r.PageNumber < row.PageNumber).Count();
            Rows.Insert(index, row);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RowChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
    public static bool SelectionComplete(DownloadEpisodeViewModel row, DownloadMode mode)
    {
        if (!row.IsReady) return false;
        var selection = row.BuildSelection();
        return row.Episode.IsMuxedStream ? selection.Video is not null : mode switch
        {
            DownloadMode.AudioOnly => selection.Audio is not null,
            DownloadMode.VideoOnly => selection.Video is not null,
            _ => selection.Video is not null && selection.Audio is not null
        };
    }
}

public sealed class SpaceCollectionViewModel(BilibiliSpaceCollection collection)
{
    public BilibiliSpaceCollection Collection { get; } = collection;
    public string Title => Collection.Title;
    public string CoverUrl => Collection.CoverUrl;
    public string Detail => $"{(Collection.Kind == BilibiliSpaceCollectionKind.Season ? "合集" : "系列列表")} · {Collection.Count} 个视频";
}

public sealed partial class PersonalSpaceViewModel : ObservableObject
{
    private readonly IBilibiliSpaceService space;
    private readonly BilibiliSpaceBatchService batch;
    private readonly ISettingsStore settings;
    private readonly ITaskManager tasks;
    private readonly DownloadQueueService queue;
    private readonly Dictionary<string, SpaceVideoViewModel> videos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BilibiliSpaceDirectory<BilibiliSpaceVideo>> directories = new();
    private BilibiliSpaceDirectory<BilibiliSpaceCollection> collections = NewCollections();
    private BilibiliSpaceCollection? currentCollection;
    private CancellationTokenSource? cancellation;
    private bool initialized;
    private bool busy;
    private bool selectionView;
    private string input = "";
    private string message = "粘贴个人主页链接直接读取，或输入 UP 主名称搜索后选择账号。";
    private string search = "";
    private string sort = "最新发布";
    private int category;
    private int tab;
    private int page = 1;
    private string workDirectory = "";
    private string quality = "4K 超高清";
    private string encoding = "AVC";
    private string audio = "auto";
    private string bitrate = "最高码率";
    private string mode = "视频+音频";
    public PersonalSpaceViewModel(AppServices services) : this(services.BilibiliSpace, services.BBDown, services.Settings, services.TaskManager, services.DownloadQueue) { }
    public PersonalSpaceViewModel(IBilibiliSpaceService space, IBBDownService downloader, ISettingsStore settings, ITaskManager tasks, DownloadQueueService queue)
    {
        this.space = space; batch = new(downloader); this.settings = settings; this.tasks = tasks; this.queue = queue;
        ReadCommand = new AsyncRelayCommand(SubmitInputAsync, () => IsIdle);
        RefreshCommand = new AsyncRelayCommand(() => ReadAsync(true), () => IsIdle && !searchOpen);
        ContinueCommand = new AsyncRelayCommand(() => RunAsync(LoadCurrentAsync), () => IsIdle && Profile is not null && !DirectoryComplete);
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
        OpenCollectionCommand = new AsyncRelayCommand<SpaceCollectionViewModel>(OpenCollectionAsync, _ => IsIdle);
        BackToCollectionsCommand = new RelayCommand(() => { currentCollection = null; ResetFilter(); }, () => IsIdle);
        SelectPageCommand = new RelayCommand(() => SetSelection(VisibleVideos), () => IsIdle && !ShowingCollections);
        SelectFilteredCommand = new RelayCommand(() => SetSelection(FilteredVideos()), () => IsIdle && !ShowingCollections && DirectoryComplete);
        ClearSelectionCommand = new RelayCommand(() => { foreach (var v in videos.Values) v.IsSelected = false; }, () => IsIdle);
        NextPageCommand = new RelayCommand(() => { page++; RefreshView(); }, () => IsIdle && page < TotalPages);
        PreviousPageCommand = new RelayCommand(() => { page--; RefreshView(); }, () => IsIdle && page > 1);
        ParseCommand = new AsyncRelayCommand(ParseSelectedAsync, () => IsIdle && videos.Values.Any(v => v.IsSelected && !v.IsEnqueued));
        BrowseCommand = new RelayCommand(() => { selectionView = false; NotifyView(); }, () => IsIdle);
        ShowSelectionCommand = new RelayCommand(() => { selectionView = true; RefreshSelection(); NotifyView(); }, () => IsIdle && videos.Values.Any(v => v.IsSelected));
        ApplyRuleCommand = new RelayCommand(ApplyRule, () => IsIdle);
        EnqueueCommand = new AsyncRelayCommand(EnqueueAsync, () => IsIdle && SelectedVideos.Any(v => !v.IsEnqueued && ReadyRows(v).Any()));
        InitializeUserSearch();
    }
    public BilibiliSpaceProfile? Profile { get; private set; }
    public string ProfileName => Profile?.Name ?? "个人主页";
    public string ProfileDetail => Profile is null ? "" : $"UID {Profile.Uid} · {Profile.Description}";
    public string AvatarUrl => Profile?.AvatarUrl ?? "";
    public string Input
    {
        get => input;
        set
        {
            if (!SetProperty(ref input, value)) return;
            ResetUserSearch();
            OnPropertyChanged(nameof(InputActionText));
        }
    }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); } } }
    public bool IsIdle => !IsBusy;
    public string SearchText { get => search; set { if (SetProperty(ref search, value)) { page = 1; RefreshView(); } } }
    public string SortOrder { get => sort; set { if (SetProperty(ref sort, value)) { page = 1; RefreshView(); } } }
    public int CategoryId { get => category; set { if (SetProperty(ref category, value)) { page = 1; RefreshView(); } } }
    public string WorkDirectory { get => workDirectory; set => SetProperty(ref workDirectory, value); }
    public string QualityRule { get => quality; set => SetProperty(ref quality, value); }
    public string Encoding { get => encoding; set => SetProperty(ref encoding, value); }
    public string AudioCodec { get => audio; set => SetProperty(ref audio, value); }
    public string AudioBitrate { get => bitrate; set => SetProperty(ref bitrate, value); }
    public string DownloadModeText { get => mode; set { if (SetProperty(ref mode, value)) { foreach (var v in videos.Values.Where(v => !v.IsEnqueued)) foreach (var row in v.Rows) row.SetDownloadMode(CurrentMode); RefreshSelection(); } } }
    public IReadOnlyList<string> QualityOptions { get; } = ["杜比视界", "HDR 真彩", "4K 超高清", "4K·SDR增强", "智能修复", "1080P 高码率", "1080P 高清", "720P 准高清", "480P 标清", "360P 流畅"];
    public IReadOnlyList<string> EncodingOptions { get; } = ["AVC", "HEVC", "AV1"];
    public IReadOnlyList<string> AudioOptions { get; } = ["auto", "E-AC-3", "M4A", "FLAC", "AC-3", "DTS"];
    public IReadOnlyList<string> BitrateOptions { get; } = ["最高码率", "最低码率"];
    public IReadOnlyList<string> ModeOptions { get; } = ["视频+音频", "仅视频", "仅音频"];
    public IReadOnlyList<string> SortOptions { get; } = ["目录顺序", "最新发布", "最早发布"];
    public ObservableCollection<BilibiliSpaceCategory> Categories { get; } = [new(0, "全部分类", 0)];
    public ObservableCollection<SpaceVideoViewModel> VisibleVideos { get; } = [];
    public ObservableCollection<SpaceCollectionViewModel> VisibleCollections { get; } = [];
    public ObservableCollection<SpaceVideoViewModel> SelectedVideos { get; } = [];
    public bool ShowingCollections => tab == 1 && currentCollection is null;
    public bool DirectoryComplete => Profile is not null && (ShowingCollections ? collections.Complete : CurrentDirectory.Complete);
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((ShowingCollections ? FilteredCollections().Count() : FilteredVideos().Count()) / 30d));
    public string PageText => $"第 {page} / {TotalPages} 页 · 每页 30 条";
    public string DirectoryTitle => currentCollection?.Title ?? (tab == 0 ? "投稿视频" : "合集和列表");
    public string DirectoryStatus => Profile is null ? "" : ShowingCollections
        ? $"已读取 {collections.Items.Count} / {collections.Total} 个合集和列表{(collections.Complete ? "" : " · 未读取完整") }"
        : $"已读取 {CurrentDirectory.Items.Count} / {CurrentDirectory.Total} 个视频{(CurrentDirectory.Complete ? "" : " · 未读取完整") }";
    public string SelectionSummary => $"已选 {videos.Values.Count(v => v.IsSelected)} 个视频 · 本次已入队 {videos.Values.Count(v => v.IsEnqueued)} 个";
    public string ReadySummary
    {
        get
        {
            var pending = SelectedVideos.Where(v => !v.IsEnqueued).ToList();
            var ready = pending.Sum(v => ReadyRows(v).Count());
            var unavailable = pending.Sum(v => v.Catalog is null ? 1 : v.Rows.Count(r => !r.IsReady || (r.IsSelected && !SpaceVideoViewModel.SelectionComplete(r, CurrentMode))));
            return $"可入队 {pending.Count(v => ReadyRows(v).Any())} 个视频 / {ready} 个分P · 未就绪 {unavailable} 项（不入队）";
        }
    }
    public Visibility BrowseVisibility => selectionView || searchOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectionVisibility => selectionView && !searchOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoVisibility => ShowingCollections ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CollectionVisibility => ShowingCollections ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CategoryVisibility => tab == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackVisibility => currentCollection is null ? Visibility.Collapsed : Visibility.Visible;
    public IAsyncRelayCommand ReadCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ContinueCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand<SpaceCollectionViewModel> OpenCollectionCommand { get; }
    public IRelayCommand BackToCollectionsCommand { get; }
    public IRelayCommand SelectPageCommand { get; }
    public IRelayCommand SelectFilteredCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IAsyncRelayCommand ParseCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    public IRelayCommand ShowSelectionCommand { get; }
    public IRelayCommand ApplyRuleCommand { get; }
    public IAsyncRelayCommand EnqueueCommand { get; }
    private string DirectoryKey => currentCollection is null ? "uploads" : $"{currentCollection.Kind}:{currentCollection.Id}";
    private BilibiliSpaceDirectory<BilibiliSpaceVideo> CurrentDirectory
    {
        get
        {
            if (!directories.TryGetValue(DirectoryKey, out var result)) directories[DirectoryKey] = result = new(v => v.Bvid);
            return result;
        }
    }
    private static BilibiliSpaceDirectory<BilibiliSpaceCollection> NewCollections() => new(c => $"{c.Kind}:{c.Id}");
    private StreamSelectionRule Rule => new(QualityRule, Encoding, AudioCodec, AudioBitrate == "最低码率" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest);
    private DownloadMode CurrentMode => DownloadModeText switch { "仅视频" => DownloadMode.VideoOnly, "仅音频" => DownloadMode.AudioOnly, _ => DownloadMode.VideoAndAudio };

    public async Task InitializeAsync()
    {
        if (initialized) return;
        var config = await settings.LoadAsync();
        WorkDirectory = config.WorkDirectory; QualityRule = config.VideoQualityRule; Encoding = config.Encoding;
        AudioCodec = config.AudioCodec; AudioBitrate = config.AudioBitratePriority == AudioBitratePriority.Lowest ? "最低码率" : "最高码率";
        DownloadModeText = config.LegacyAudioOnly;
        initialized = true;
    }
    public async Task ReceiveInputAsync(string value, bool read)
    {
        if (IsBusy) { Message = "当前操作尚未完成，请取消或等待后再读取主页。"; return; }
        if (!BilibiliInputParser.TryGetSpaceUid(value, out var uid)) { Message = "请输入有效的 B 站个人主页链接。"; return; }
        Input = $"https://space.bilibili.com/{uid}";
        if (read) await ReadAsync(false);
    }
    public async Task SwitchTabAsync(int index)
    {
        if (IsBusy || index == tab) return;
        tab = index; currentCollection = null; ResetFilter();
        if (Profile is not null && !DirectoryComplete) await RunAsync(LoadCurrentAsync);
    }
    private async Task ReadAsync(bool refresh)
    {
        if (!BilibiliInputParser.TryGetSpaceUid(Input, out var uid)) { Message = "请输入有效的 B 站个人主页链接。"; return; }
        await RunAsync(async token =>
        {
            var profile = await space.GetProfileAsync(uid, token);
            if (Profile?.Uid != uid)
            {
                foreach (var v in videos.Values) v.Changed -= VideoChanged;
                videos.Clear(); directories.Clear(); collections = NewCollections(); currentCollection = null; selectionView = false;
            }
            Profile = profile;
            OnPropertyChanged(nameof(ProfileName)); OnPropertyChanged(nameof(ProfileDetail)); OnPropertyChanged(nameof(AvatarUrl));
            if (refresh)
            {
                if (ShowingCollections) collections = NewCollections(); else directories.Remove(DirectoryKey);
            }
            ResetFilter();
            await LoadCurrentAsync(token);
        });
    }
    private async Task LoadCurrentAsync(CancellationToken token)
    {
        if (Profile is null) return;
        if (ShowingCollections)
        {
            while (!collections.Complete)
            {
                collections.Append(await space.GetCollectionsAsync(Profile.Uid, collections.NextPage, token));
                RefreshView(); Message = DirectoryStatus;
            }
        }
        else
        {
            var directory = CurrentDirectory;
            while (!directory.Complete)
            {
                var result = currentCollection is null
                    ? await space.GetUploadsAsync(Profile.Uid, directory.NextPage, token)
                    : await space.GetCollectionVideosAsync(currentCollection, directory.NextPage, token);
                directory.Append(result);
                foreach (var video in result.Items)
                {
                    if (videos.TryGetValue(video.Bvid, out var cached)) { cached.UpdateVideo(video); continue; }
                    var row = new SpaceVideoViewModel(video); row.Changed += VideoChanged; videos.Add(video.Bvid, row);
                }
                if (tab == 0)
                {
                    Categories.Clear(); Categories.Add(new(0, "全部分类", directory.Total));
                    foreach (var item in directory.Categories) Categories.Add(item);
                    OnPropertyChanged(nameof(CategoryId));
                }
                RefreshView(); Message = DirectoryStatus;
            }
        }
        Message = DirectoryStatus + " · 读取完成";
    }
    private async Task OpenCollectionAsync(SpaceCollectionViewModel? item)
    {
        if (item is null) return;
        currentCollection = item.Collection; ResetFilter();
        if (!DirectoryComplete) await RunAsync(LoadCurrentAsync);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        using var source = new CancellationTokenSource(); cancellation = source;
        try
        {
            var result = await tasks.RunExclusiveAsync(TaskKind.DownloadParse, false, "space", (_, token) => action(token), source.Token);
            if (result.State == TaskState.Failed) Message = result.Error + " 已保留结果，可手动重试。";
            if (result.State == TaskState.Cancelled) Message = "已取消，保留已读取和已解析的结果，可继续。";
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { cancellation = null; IsBusy = false; RefreshView(); RefreshSelection(); }
    }
    private async Task ParseSelectedAsync()
    {
        if (IsBusy) return;
        selectionView = true; RefreshSelection(); NotifyView();
        IsBusy = true;
        using var source = new CancellationTokenSource(); cancellation = source;
        try
        {
            var config = await settings.LoadAsync(source.Token);
            var selected = SelectedVideos.Where(v => !v.IsEnqueued).ToList();
            var existing = selected.Where(v => v.Catalog is not null).ToDictionary(v => v.Bvid, v => v.Catalog!);
            var result = await tasks.RunExclusiveAsync(TaskKind.DownloadParse, config.SaveTaskLogs, "space-parse", async (context, token) =>
            {
                Message = $"正在解析 {selected.Count} 个视频的全部分P…";
                await foreach (var parsed in batch.ParseAsync(selected.Select(v => v.Video), existing, config.ApiMode, context, token))
                {
                    var video = videos[parsed.Video.Bvid]; video.Error = parsed.Error;
                    if (parsed.Catalog is not null) video.MergeCatalog(parsed.Catalog, Rule, CurrentMode);
                    Message = $"已处理：{video.Title} · {ReadySummary}";
                }
            }, source.Token);
            Message = result.State switch
            {
                TaskState.Failed => result.Error,
                TaskState.Cancelled => "解析已取消，已保留规格；点击继续解析／重试可处理剩余分P。",
                _ => "解析完成。请检查实际规格和替代原因，然后将就绪项加入队列。"
            };
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { cancellation = null; IsBusy = false; RefreshSelection(); }
    }
    private IEnumerable<DownloadEpisodeViewModel> ReadyRows(SpaceVideoViewModel video) => video.Rows.Where(r => r.IsSelected && SpaceVideoViewModel.SelectionComplete(r, CurrentMode));
    private async Task EnqueueAsync()
    {
        if (IsBusy || Profile is null) return;
        IsBusy = true;
        try
        {
            var config = await settings.LoadAsync();
            var selected = SelectedVideos.Where(v => !v.IsEnqueued && v.Catalog is not null && ReadyRows(v).Any()).ToList();
            var items = selected.Select(v => BilibiliSpaceBatchService.BuildQueueItem(Profile, v.Catalog!,
                ReadyRows(v).Select(r => r.BuildSelection()).ToList(), config, WorkDirectory, Rule, CurrentMode)).ToList();
            await queue.EnqueueManyAsync(items);
            foreach (var video in selected) video.IsEnqueued = true;
            Message = $"已加入 {items.Count} 个独立任务。输出根目录：{BilibiliSpaceBatchService.OutputRoot(WorkDirectory, Profile)}";
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; RefreshSelection(); }
    }
    private void ApplyRule()
    {
        foreach (var video in SelectedVideos.Where(v => !v.IsEnqueued)) foreach (var row in video.Rows) row.ApplyRule(Rule, CurrentMode);
        RefreshSelection();
    }
    private void SetSelection(IEnumerable<SpaceVideoViewModel> items) { foreach (var v in items.ToList().Where(v => !v.IsEnqueued)) v.IsSelected = true; }
    private void VideoChanged(object? sender, EventArgs e) => RefreshSelection();
    private IEnumerable<SpaceVideoViewModel> FilteredVideos()
    {
        var entries = CurrentDirectory.Items.Where(v => (tab != 0 || category == 0 || v.CategoryId == category)
            && (string.IsNullOrWhiteSpace(search) || v.Title.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        IEnumerable<BilibiliSpaceVideo> ordered = sort switch { "最新发布" => entries.OrderByDescending(v => v.PublishedAt), "最早发布" => entries.OrderBy(v => v.PublishedAt), _ => entries };
        return ordered.Where(v => videos.ContainsKey(v.Bvid)).Select(v => videos[v.Bvid]);
    }
    private IEnumerable<BilibiliSpaceCollection> FilteredCollections() => collections.Items.Where(c => string.IsNullOrWhiteSpace(search) || c.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
    private void ResetFilter()
    {
        search = ""; category = 0; sort = tab == 0 ? "最新发布" : "目录顺序"; page = 1;
        OnPropertyChanged(nameof(SearchText)); OnPropertyChanged(nameof(CategoryId)); OnPropertyChanged(nameof(SortOrder)); RefreshView();
    }
    private void RefreshView()
    {
        page = Math.Clamp(page, 1, TotalPages);
        VisibleVideos.Clear(); VisibleCollections.Clear();
        if (ShowingCollections) foreach (var c in FilteredCollections().Skip((page - 1) * 30).Take(30)) VisibleCollections.Add(new(c));
        else foreach (var v in FilteredVideos().Skip((page - 1) * 30).Take(30)) VisibleVideos.Add(v);
        NotifyView(); NotifyCommands();
    }
    private void RefreshSelection()
    {
        var selected = videos.Values.Where(v => v.IsSelected).ToList();
        if (!SelectedVideos.SequenceEqual(selected)) { SelectedVideos.Clear(); foreach (var v in selected) SelectedVideos.Add(v); }
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(ReadySummary)); NotifyCommands();
    }
    private void NotifyView()
    {
        foreach (var name in new[] { nameof(BrowseVisibility), nameof(SelectionVisibility), nameof(VideoVisibility), nameof(CollectionVisibility), nameof(CategoryVisibility), nameof(BackVisibility), nameof(DirectoryTitle), nameof(DirectoryStatus), nameof(PageText), nameof(DirectoryComplete), nameof(TotalPages) }) OnPropertyChanged(name);
    }
    private void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { ReadCommand, RefreshCommand, ContinueCommand, CancelCommand, OpenCollectionCommand, BackToCollectionsCommand, SelectPageCommand, SelectFilteredCommand, ClearSelectionCommand, NextPageCommand, PreviousPageCommand, ParseCommand, BrowseCommand, ShowSelectionCommand, ApplyRuleCommand, EnqueueCommand }) command.NotifyCanExecuteChanged();
        NotifyUserSearchCommands();
    }
}
