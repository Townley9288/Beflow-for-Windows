using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BBDownForWindows.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private AppSettings _settings = new();
    private string _toolStatus = "尚未检测";
    private string _message = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetCommand = new RelayCommand(Reset);
        DetectToolsCommand = new AsyncRelayCommand(DetectToolsAsync);
        CleanupCommand = new AsyncRelayCommand(CleanupAsync);
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
    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand DetectToolsCommand { get; }
    public IAsyncRelayCommand CleanupCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _services.Settings.LoadAsync();
        await DetectToolsAsync();
    }

    public async Task SaveAsync()
    {
        Settings.SchemaVersion = 1;
        await _services.Settings.SaveAsync(Settings);
        Message = "设置已保存";
    }

    private void Reset()
    {
        Settings = new AppSettings();
        Message = "已恢复默认值（尚未保存）";
    }

    private async Task DetectToolsAsync()
    {
        var tools = _services.ToolLocator.Locate(Settings);
        var settingsChanged = false;
        if (string.IsNullOrWhiteSpace(Settings.Aria2cPath) && !string.IsNullOrWhiteSpace(tools.Aria2c))
        {
            Settings.Aria2cPath = tools.Aria2c;
            settingsChanged = true;
        }
        if (string.IsNullOrWhiteSpace(Settings.MkvmergePath) && !string.IsNullOrWhiteSpace(tools.Mkvmerge))
        {
            Settings.MkvmergePath = tools.Mkvmerge;
            settingsChanged = true;
        }
        if (settingsChanged) Settings = Settings.Clone();

        var versions = await Task.WhenAll(
            _services.ToolLocator.GetVersionAsync(tools.BBDown),
            _services.ToolLocator.GetVersionAsync(tools.Aria2c),
            _services.ToolLocator.GetVersionAsync(tools.Ffmpeg),
            _services.ToolLocator.GetVersionAsync(tools.Mkvmerge));
        ToolStatus = $"BBDown: {versions[0]}\naria2c: {versions[1]}\nFFmpeg: {versions[2]}\nmkvmerge: {versions[3]}";
    }

    private async Task CleanupAsync()
    {
        await _services.TaskManager.CleanupAsync();
        Message = "本会话启动的进程已清理；下载文件和 .aria2 文件均已保留。";
    }
}
