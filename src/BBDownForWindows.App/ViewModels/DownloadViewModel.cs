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

    public DownloadViewModel(AppServices services)
    {
        _services = services;
        Console = services.TaskConsole;
        GetInfoCommand = new AsyncRelayCommand(GetInfoAsync, CanStart);
        DownloadCommand = new AsyncRelayCommand(() => StartDownloadAsync(false), CanStart);
        SeasonCommand = new AsyncRelayCommand(() => StartDownloadAsync(true), CanStart);
        LoginWebCommand = new AsyncRelayCommand(() => LoginAsync(false), CanRunWithoutUrl);
        LoginTvCommand = new AsyncRelayCommand(() => LoginAsync(true), CanRunWithoutUrl);
        Console.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Console.IsBusy)) NotifyCommands();
        };
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
    public IReadOnlyList<string> UposHostOptions { get; } = ["upos-sz-mirrorcos.bilivideo.com", "upos-sz-mirrorcoso1.bilivideo.com", "upos-sz-mirrorali.bilivideo.com", "upos-sz-mirroralib.bilivideo.com", "upos-sz-mirrorhw.bilivideo.com"];

    public TaskConsoleViewModel Console { get; }
    public string Url { get => _url; set { if (SetProperty(ref _url, value)) NotifyCommands(); } }
    public string Pages { get => _pages; set => SetProperty(ref _pages, value); }
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public string DownloadModeText { get => _downloadMode; set => SetProperty(ref _downloadMode, value); }
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
    public IAsyncRelayCommand LoginWebCommand { get; }
    public IAsyncRelayCommand LoginTvCommand { get; }

    public async Task InitializeAsync(HistoryRecord? restore = null)
    {
        if (restore?.Download is { } saved)
        {
            Apply(saved);
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
            context.AppendLog($"\n标题: {info.Title}\n分P数: {info.Pages.Count}\n视频流: {info.VideoStreams.Count}\n音频流: {info.AudioStreams.Count}\n");
        });
    }

    private async Task StartDownloadAsync(bool season)
    {
        var request = await BuildRequestAsync(season);
        var kind = season ? TaskKind.SeasonDownload : TaskKind.Download;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(kind, SaveTaskLogs, season ? "season_download" : "download", (context, token) => _services.BBDown.DownloadAsync(request, context, token));
        if (snapshot.State == TaskState.Completed)
            await _services.History.AddAsync(new HistoryRecord { TaskType = kind, Url = request.Url, Timestamp = DateTimeOffset.Now, LogPath = snapshot.LogPath, Download = request });
    }

    private Task LoginAsync(bool tv) => _services.TaskManager.RunExclusiveAsync(tv ? TaskKind.LoginTv : TaskKind.LoginWeb, false, tv ? "login_tv" : "login_web", (context, token) => _services.BBDown.LoginAsync(tv, context, token));

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
            SaveTaskLogs = SaveTaskLogs, ApiMode = settings.ApiMode
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
    private bool CanRunWithoutUrl() => !Console.IsBusy;
    private void NotifyCommands()
    {
        GetInfoCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); SeasonCommand.NotifyCanExecuteChanged();
        LoginWebCommand.NotifyCanExecuteChanged(); LoginTvCommand.NotifyCanExecuteChanged();
    }
}
