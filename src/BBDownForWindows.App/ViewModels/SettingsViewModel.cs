using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BBDownForWindows.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
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
    public IReadOnlyList<string> AudioCodecOptions { get; } = ["auto", "E-AC-3", "M4A", "FLAC", "AC-3", "DTS"];
    public IReadOnlyList<string> AudioBitrateOptions { get; } = ["highest", "lowest"];
    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
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
        var bbdown = await _services.ToolLocator.GetVersionAsync(tools.BBDown);
        var aria = await _services.ToolLocator.GetVersionAsync(tools.Aria2c);
        var ffmpeg = await _services.ToolLocator.GetVersionAsync(tools.Ffmpeg);
        var mkv = await _services.ToolLocator.GetVersionAsync(tools.Mkvmerge);
        ToolStatus = $"BBDown: {bbdown}\naria2c: {aria}\nFFmpeg: {ffmpeg}\nmkvmerge: {mkv}";
        if (string.IsNullOrWhiteSpace(Settings.Aria2cPath)) Settings.Aria2cPath = tools.Aria2c;
        if (string.IsNullOrWhiteSpace(Settings.MkvmergePath)) Settings.MkvmergePath = tools.Mkvmerge;
    }

    private async Task CleanupAsync()
    {
        await _services.TaskManager.CleanupAsync();
        Message = "本会话启动的进程已清理；下载文件和 .aria2 文件均已保留。";
    }
}
