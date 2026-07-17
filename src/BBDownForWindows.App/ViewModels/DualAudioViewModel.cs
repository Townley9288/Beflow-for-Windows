using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BBDownForWindows.App.ViewModels;

public sealed class DualAudioViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private string _sourceMode = "两个独立链接";
    private string _primaryUrl = string.Empty;
    private string _secondaryUrl = string.Empty;
    private string _pages = "ALL";
    private string _quality = "4K";
    private string _encoding = "AVC";
    private string _primaryAudioCodec = "auto";
    private string _secondaryAudioCodec = "auto";
    private string _primaryLabel = "国语";
    private string _secondaryLabel = "粤语";
    private string _primaryLanguage = "zh";
    private string _secondaryLanguage = "zh";
    private string _defaultAudio = "主版本音轨";
    private double _delay;
    private string _workDirectory = string.Empty;
    private string _existingTaskDirectory = string.Empty;
    private string _mkvmergePath = string.Empty;
    private string _resolvedPrimaryTitle = string.Empty;
    private string _resolvedPrimaryUrl = string.Empty;
    private bool _active;

    public DualAudioViewModel(AppServices services)
    {
        _services = services;
        Console = services.TaskConsole;
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        InfoCommand = new AsyncRelayCommand(GetInfoAsync, CanStart);
        RemuxCommand = new AsyncRelayCommand(RemuxAsync, CanRemux);
    }

    public IReadOnlyList<string> SourceModes { get; } = ["两个独立链接", "同一链接奇偶分P"];
    public IReadOnlyList<string> QualityOptions { get; } = ["杜比视界", "HDR 真彩", "4K", "1080P 高码率", "1080P", "720P", "480P", "360P"];
    public IReadOnlyList<string> EncodingOptions { get; } = ["HEVC", "AVC", "AV1"];
    public IReadOnlyList<OptionItem> AudioCodecOptions { get; } =
    [
        new("auto", "自动"),
        new("E-AC-3", "E-AC-3"),
        new("M4A", "M4A"),
        new("FLAC", "FLAC"),
        new("AC-3", "AC-3"),
        new("DTS", "DTS")
    ];
    public IReadOnlyList<string> DefaultAudioOptions { get; } = ["主版本音轨", "副版本音轨"];
    public TaskConsoleViewModel Console { get; }
    public string SourceModeText { get => _sourceMode; set { if (SetProperty(ref _sourceMode, value)) NotifyCommands(); } }
    public string PrimaryUrl
    {
        get => _primaryUrl;
        set
        {
            if (!SetProperty(ref _primaryUrl, value)) return;
            if (!NormalizeUrl(value).Equals(_resolvedPrimaryUrl, StringComparison.Ordinal))
            {
                _resolvedPrimaryTitle = string.Empty;
                _resolvedPrimaryUrl = string.Empty;
            }
            NotifyCommands();
        }
    }
    public string SecondaryUrl { get => _secondaryUrl; set { if (SetProperty(ref _secondaryUrl, value)) NotifyCommands(); } }
    public string Pages { get => _pages; set => SetProperty(ref _pages, value); }
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public string PrimaryAudioCodec { get => _primaryAudioCodec; set => SetProperty(ref _primaryAudioCodec, value); }
    public string SecondaryAudioCodec { get => _secondaryAudioCodec; set => SetProperty(ref _secondaryAudioCodec, value); }
    public string PrimaryLabel { get => _primaryLabel; set => SetProperty(ref _primaryLabel, value); }
    public string SecondaryLabel { get => _secondaryLabel; set => SetProperty(ref _secondaryLabel, value); }
    public string PrimaryLanguage { get => _primaryLanguage; set => SetProperty(ref _primaryLanguage, value); }
    public string SecondaryLanguage { get => _secondaryLanguage; set => SetProperty(ref _secondaryLanguage, value); }
    public string DefaultAudio { get => _defaultAudio; set => SetProperty(ref _defaultAudio, value); }
    public double SecondaryAudioDelay { get => _delay; set => SetProperty(ref _delay, value); }
    public string WorkDirectory { get => _workDirectory; set => SetProperty(ref _workDirectory, value); }
    public string ExistingTaskDirectory { get => _existingTaskDirectory; set { if (SetProperty(ref _existingTaskDirectory, value)) NotifyCommands(); } }
    public string MkvmergePath { get => _mkvmergePath; set => SetProperty(ref _mkvmergePath, value); }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand InfoCommand { get; }
    public IAsyncRelayCommand RemuxCommand { get; }
    public Func<Task<bool>>? ConfirmMkvmergeAvailableAsync { get; set; }

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
        var settings = await _services.Settings.LoadAsync();
        WorkDirectory = settings.WorkDirectory;
        MkvmergePath = settings.MkvmergePath;
        Quality = QualityOptions.Contains(settings.Quality) ? settings.Quality : "4K";
        Encoding = EncodingOptions.Contains(settings.Encoding) ? settings.Encoding : "AVC";
        var defaultAudioCodec = AudioCodecOptions.FirstOrDefault(option => option.Value.Equals(settings.AudioCodec, StringComparison.OrdinalIgnoreCase))?.Value ?? "auto";
        PrimaryAudioCodec = defaultAudioCodec;
        SecondaryAudioCodec = defaultAudioCodec;
        if (restore?.DualAudio is { } request)
        {
            Apply(request);
            RememberPrimaryTitle(restore.Url, restore.Title);
        }
    }

    private async Task StartAsync()
    {
        if (ConfirmMkvmergeAvailableAsync is not null && !await ConfirmMkvmergeAvailableAsync()) return;
        var request = await BuildAsync();
        var completedTitle = string.Empty;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioMux, request.SaveTaskLogs, "dual_audio_mux", async (context, token) =>
        {
            completedTitle = await _services.DualAudio.DownloadAndMuxAsync(request, context, token);
        });
        if (snapshot.State == TaskState.Completed)
        {
            var title = !string.IsNullOrWhiteSpace(completedTitle) ? completedTitle : GetRememberedPrimaryTitle(request.PrimaryUrl);
            await _services.History.AddAsync(new HistoryRecord { TaskType = TaskKind.DualAudioMux, Title = title, Url = request.PrimaryUrl, SecondaryUrl = request.SecondaryUrl, Timestamp = DateTimeOffset.Now, LogPath = snapshot.LogPath, DualAudio = request });
        }
    }

    private async Task GetInfoAsync()
    {
        await _services.TaskManager.RunExclusiveAsync(TaskKind.Info, false, "dual_audio_info", async (context, token) =>
        {
            context.AppendLog("===== 主版本规格 =====\n");
            var primary = await _services.BBDown.GetVideoInfoAsync(PrimaryUrl, Pages, context, token);
            RememberPrimaryTitle(PrimaryUrl, primary.Title);
            context.AppendLog($"标题: {primary.Title} / 分P: {primary.Pages.Count}\n");
            if (SourceModeText != "同一链接奇偶分P")
            {
                context.AppendLog("\n===== 副音轨版本规格 =====\n");
                var secondary = await _services.BBDown.GetVideoInfoAsync(SecondaryUrl, Pages, context, token);
                context.AppendLog($"标题: {secondary.Title} / 分P: {secondary.Pages.Count}\n");
            }
        });
    }

    private async Task RemuxAsync()
    {
        if (ConfirmMkvmergeAvailableAsync is not null && !await ConfirmMkvmergeAvailableAsync()) return;
        var request = await BuildAsync();
        var snapshot = await _services.TaskManager.RunExclusiveAsync(TaskKind.DualAudioRemux, request.SaveTaskLogs, "dual_audio_remux", (context, token) => _services.DualAudio.RemuxExistingAsync(request, context, token));
        if (snapshot.State == TaskState.Completed)
            await _services.History.AddAsync(new HistoryRecord { TaskType = TaskKind.DualAudioRemux, Title = Path.GetFileName(request.ExistingTaskDirectory), Url = request.ExistingTaskDirectory, Timestamp = DateTimeOffset.Now, LogPath = snapshot.LogPath, DualAudio = request });
    }

    private async Task<DualAudioRequest> BuildAsync()
    {
        var settings = await _services.Settings.LoadAsync();
        return new DualAudioRequest
        {
            SourceMode = SourceModeText == "同一链接奇偶分P" ? DualAudioSourceMode.Interleaved : DualAudioSourceMode.Separate,
            PrimaryUrl = PrimaryUrl.Trim(), SecondaryUrl = SecondaryUrl.Trim(), Pages = Pages.Trim(), Quality = Quality, Encoding = Encoding,
            PrimaryAudioCodec = PrimaryAudioCodec, SecondaryAudioCodec = SecondaryAudioCodec,
            PrimaryLabel = PrimaryLabel, SecondaryLabel = SecondaryLabel, PrimaryLanguage = PrimaryLanguage, SecondaryLanguage = SecondaryLanguage,
            SecondaryIsDefault = DefaultAudio == "副版本音轨", SecondaryAudioDelayMs = checked((int)SecondaryAudioDelay),
            WorkDirectory = WorkDirectory, ExistingTaskDirectory = ExistingTaskDirectory, MkvmergePath = MkvmergePath,
            AudioBitratePriority = settings.AudioBitratePriority, MultiThread = settings.MultiThread, UposHost = settings.UposHost,
            UseAria2c = settings.UseAria2c, Aria2AutoTune = settings.Aria2AutoTune, Aria2cPath = settings.Aria2cPath, Aria2MaxConnection = settings.Aria2MaxConnection,
            Aria2Split = settings.Aria2Split, Aria2MaxConcurrentDownloads = settings.Aria2MaxConcurrentDownloads,
            Aria2MinSplitSize = settings.Aria2MinSplitSize, SaveTaskLogs = settings.SaveTaskLogs
        };
    }

    private string GetRememberedPrimaryTitle(string url)
    {
        var normalized = NormalizeUrl(url);
        return normalized.Equals(_resolvedPrimaryUrl, StringComparison.Ordinal) ? _resolvedPrimaryTitle : string.Empty;
    }

    private void RememberPrimaryTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _resolvedPrimaryUrl = NormalizeUrl(url);
        _resolvedPrimaryTitle = title.Trim();
    }

    private static string NormalizeUrl(string value) => value.Trim();

    private void Apply(DualAudioRequest request)
    {
        SourceModeText = request.SourceMode == DualAudioSourceMode.Interleaved ? "同一链接奇偶分P" : "两个独立链接";
        PrimaryUrl = request.PrimaryUrl; SecondaryUrl = request.SecondaryUrl; Pages = request.Pages; Quality = request.Quality; Encoding = request.Encoding;
        PrimaryAudioCodec = request.PrimaryAudioCodec; SecondaryAudioCodec = request.SecondaryAudioCodec;
        PrimaryLabel = request.PrimaryLabel; SecondaryLabel = request.SecondaryLabel; PrimaryLanguage = request.PrimaryLanguage;
        SecondaryLanguage = request.SecondaryLanguage; DefaultAudio = request.SecondaryIsDefault ? "副版本音轨" : "主版本音轨";
        SecondaryAudioDelay = request.SecondaryAudioDelayMs; WorkDirectory = request.WorkDirectory;
        ExistingTaskDirectory = request.ExistingTaskDirectory; MkvmergePath = request.MkvmergePath;
    }

    private bool CanStart() => !Console.IsBusy && !string.IsNullOrWhiteSpace(PrimaryUrl) && (SourceModeText == "同一链接奇偶分P" || !string.IsNullOrWhiteSpace(SecondaryUrl));
    private bool CanRemux() => !Console.IsBusy && !string.IsNullOrWhiteSpace(ExistingTaskDirectory);
    private void NotifyCommands() { StartCommand.NotifyCanExecuteChanged(); InfoCommand.NotifyCanExecuteChanged(); RemuxCommand.NotifyCanExecuteChanged(); }
    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == nameof(Console.IsBusy)) NotifyCommands(); }
}
