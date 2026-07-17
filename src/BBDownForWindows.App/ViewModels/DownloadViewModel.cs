using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BBDownForWindows.App.ViewModels;

public sealed class DownloadViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private string _url = string.Empty;
    private string _pages = string.Empty;
    private string _quality = "4K";
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
    private string _resolvedTitle = string.Empty;
    private string _resolvedTitleUrl = string.Empty;
    private bool _active;
    private DownloadResult? _lastDownloadResult;

    public DownloadViewModel(AppServices services)
    {
        _services = services;
        Console = services.TaskConsole;
        GetInfoCommand = new AsyncRelayCommand(GetInfoAsync, CanStart);
        DownloadCommand = new AsyncRelayCommand(() => StartDownloadAsync(false), CanStart);
        SeasonCommand = new AsyncRelayCommand(() => StartDownloadAsync(true), CanStart);
    }

    public IReadOnlyList<string> QualityOptions { get; } = ["杜比视界", "HDR 真彩", "4K", "1080P 高码率", "1080P", "720P", "480P", "360P"];
    public IReadOnlyList<string> EncodingOptions { get; } = ["HEVC", "AVC", "AV1"];
    public IReadOnlyList<string> DownloadModeOptions { get; } = ["视频+音频", "仅视频", "仅音频"];
    public IReadOnlyList<OptionItem> AudioCodecOptions { get; } =
    [
        new("auto", "自动"),
        new("E-AC-3", "E-AC-3"),
        new("M4A", "M4A"),
        new("FLAC", "FLAC"),
        new("AC-3", "AC-3"),
        new("DTS", "DTS")
    ];
    public IReadOnlyList<OptionItem> AudioBitrateOptions { get; } =
    [new("highest", "最高码率"), new("lowest", "最低码率")];
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
    public string Url
    {
        get => _url;
        set
        {
            if (!SetProperty(ref _url, value)) return;
            if (!NormalizeUrl(value).Equals(_resolvedTitleUrl, StringComparison.Ordinal))
            {
                _resolvedTitle = string.Empty;
                _resolvedTitleUrl = string.Empty;
            }
            NotifyCommands();
        }
    }
    public string Pages { get => _pages; set => SetProperty(ref _pages, value); }
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public string DownloadModeText
    {
        get => _downloadMode;
        set
        {
            if (SetProperty(ref _downloadMode, value)) OnPropertyChanged(nameof(CanRenameDownload));
        }
    }
    public string AudioCodec { get => _audioCodec; set => SetProperty(ref _audioCodec, value); }
    public string AudioBitrate { get => _audioBitrate; set => SetProperty(ref _audioBitrate, value); }
    public string WorkDirectory { get => _workDirectory; set => SetProperty(ref _workDirectory, value); }
    public string UposHost { get => _uposHost; set => SetProperty(ref _uposHost, value); }
    public bool Danmaku { get => _danmaku; set => SetProperty(ref _danmaku, value); }
    public bool Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }
    public bool Cover { get => _cover; set => SetProperty(ref _cover, value); }
    public bool MultiThread { get => _multiThread; set => SetProperty(ref _multiThread, value); }
    public bool UseAria2c { get => _useAria2c; set => SetProperty(ref _useAria2c, value); }
    public bool SaveTaskLogs { get => _saveTaskLogs; set => SetProperty(ref _saveTaskLogs, value); }
    public IAsyncRelayCommand GetInfoCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand SeasonCommand { get; }
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
    public Microsoft.UI.Xaml.Visibility DownloadResultVisibility => HasDownloadResult ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool CanRenameDownload => LastDownloadResult?.HasVideo == true ||
        (LastDownloadResult is { OutputDirectory.Length: > 0 } && DownloadModeText != "仅音频");
    public string DownloadResultMessage => LastDownloadResult is null
        ? string.Empty
        : LastDownloadResult.HasVideo
            ? $"已保存到 {LastDownloadResult.OutputDirectory}，可以直接进入重命名。"
            : $"已保存到 {LastDownloadResult.OutputDirectory}，暂未识别到本次生成的视频，可进入重命名页重新扫描。";

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
        if (restore?.Download is { } saved)
        {
            Apply(saved);
            RememberTitle(restore.Url, restore.Title);
            if (!string.IsNullOrWhiteSpace(restore.OutputDirectory))
                LastDownloadResult = new DownloadResult(restore.Title, restore.OutputDirectory, restore.OutputFiles, restore.OutputFiles.Any(IsVideoFile));
            return;
        }
        var settings = await _services.Settings.LoadAsync();
        Apply(settings);
    }

    private async Task GetInfoAsync()
    {
        await _services.TaskManager.RunExclusiveAsync(TaskKind.Info, false, "info", async (context, token) =>
        {
            var info = await _services.BBDown.GetVideoInfoAsync(Url.Trim(), string.IsNullOrWhiteSpace(Pages) ? "1" : Pages, context, token);
            RememberTitle(Url, info.Title);
            context.AppendLog($"\n标题: {info.Title}\n分P数: {info.Pages.Count}\n视频流: {info.VideoStreams.Count}\n音频流: {info.AudioStreams.Count}\n");
        });
    }

    private async Task StartDownloadAsync(bool season)
    {
        LastDownloadResult = null;
        var request = await BuildRequestAsync(season);
        var kind = season ? TaskKind.SeasonDownload : TaskKind.Download;
        DownloadResult? result = null;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(kind, SaveTaskLogs, season ? "season_download" : "download", async (context, token) =>
        {
            result = await _services.BBDown.DownloadAsync(request, context, token);
        });
        if (snapshot.State == TaskState.Completed)
        {
            result ??= new DownloadResult(GetRememberedTitle(request.Url), request.WorkDirectory, [], false);
            LastDownloadResult = result;
            var title = !string.IsNullOrWhiteSpace(result.Title) ? result.Title : GetRememberedTitle(request.Url);
            RememberTitle(request.Url, title);
            await _services.History.AddAsync(new HistoryRecord
            {
                TaskType = kind,
                Title = title,
                Url = request.Url,
                Timestamp = DateTimeOffset.Now,
                LogPath = snapshot.LogPath,
                OutputDirectory = result.OutputDirectory,
                OutputFiles = result.OutputFiles.ToList(),
                Download = request
            });
        }
    }

    private string GetRememberedTitle(string url)
    {
        var normalized = NormalizeUrl(url);
        return normalized.Equals(_resolvedTitleUrl, StringComparison.Ordinal) ? _resolvedTitle : string.Empty;
    }

    private void RememberTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _resolvedTitleUrl = NormalizeUrl(url);
        _resolvedTitle = title.Trim();
    }

    private static string NormalizeUrl(string value) => value.Trim();

    private async Task<DownloadRequest> BuildRequestAsync(bool season)
    {
        var settings = await _services.Settings.LoadAsync();
        return new DownloadRequest
        {
            Url = Url.Trim(), Pages = Pages.Trim(), Season = season, Quality = Quality, Encoding = Encoding,
            DownloadMode = DownloadModeText switch { "仅视频" => DownloadMode.VideoOnly, "仅音频" => DownloadMode.AudioOnly, _ => DownloadMode.VideoAndAudio },
            AudioCodec = AudioCodec, AudioBitratePriority = AudioBitrate == "lowest" ? AudioBitratePriority.Lowest : AudioBitratePriority.Highest,
            Danmaku = Danmaku, Subtitle = Subtitle, Cover = Cover, WorkDirectory = WorkDirectory, MultiThread = MultiThread,
            UposHost = UposHost, UseAria2c = UseAria2c, Aria2cPath = settings.Aria2cPath,
            Aria2MaxConnection = settings.Aria2MaxConnection, Aria2Split = settings.Aria2Split,
            Aria2MaxConcurrentDownloads = settings.Aria2MaxConcurrentDownloads, Aria2MinSplitSize = settings.Aria2MinSplitSize,
            SaveTaskLogs = SaveTaskLogs, ApiMode = settings.ApiMode,
            OrganizeInTitleDirectory = true, TitleHint = GetRememberedTitle(Url)
        };
    }

    private void Apply(AppSettings settings)
    {
        Quality = settings.Quality; Encoding = settings.Encoding; DownloadModeText = settings.LegacyAudioOnly;
        AudioCodec = settings.AudioCodec; AudioBitrate = settings.LegacyAudioBitratePriority; WorkDirectory = settings.WorkDirectory;
        Danmaku = settings.Danmaku; Subtitle = settings.Subtitle; Cover = settings.Cover; MultiThread = settings.MultiThread;
        UposHost = settings.UposHost; UseAria2c = settings.UseAria2c; SaveTaskLogs = settings.SaveTaskLogs;
    }

    private void Apply(DownloadRequest request)
    {
        Url = request.Url; Pages = request.Pages; Quality = request.Quality; Encoding = request.Encoding;
        DownloadModeText = request.DownloadMode switch { DownloadMode.VideoOnly => "仅视频", DownloadMode.AudioOnly => "仅音频", _ => "视频+音频" };
        AudioCodec = request.AudioCodec; AudioBitrate = request.AudioBitratePriority == AudioBitratePriority.Lowest ? "lowest" : "highest";
        WorkDirectory = request.WorkDirectory; Danmaku = request.Danmaku; Subtitle = request.Subtitle; Cover = request.Cover;
        MultiThread = request.MultiThread; UposHost = request.UposHost; UseAria2c = request.UseAria2c; SaveTaskLogs = request.SaveTaskLogs;
    }

    private bool CanStart() => !Console.IsBusy && !string.IsNullOrWhiteSpace(Url);
    private void NotifyCommands()
    {
        GetInfoCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); SeasonCommand.NotifyCanExecuteChanged();
    }

    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Console.IsBusy)) NotifyCommands();
    }

    private static bool IsVideoFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }
}
