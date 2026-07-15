using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    public sealed record OptionItem(string Value, string Label);

    private readonly AppServices _services;
    private AppSettings _settings = new();
    private string _toolStatus = "尚未检测";
    private string _message = string.Empty;
    private string _loginMessage = string.Empty;
    private string _lastAccountCheckText = "尚未检测";
    private InfoBarSeverity _loginMessageSeverity = InfoBarSeverity.Informational;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetCommand = new RelayCommand(Reset);
        DetectToolsCommand = new AsyncRelayCommand(DetectToolsAsync);
        CleanupCommand = new AsyncRelayCommand(CleanupAsync);
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAccountsAsync);
        LoginWebCommand = new AsyncRelayCommand(() => LoginAsync(AccountChannel.Web), CanStartLogin);
        LoginTvCommand = new AsyncRelayCommand(() => LoginAsync(AccountChannel.Tv), CanStartLogin);
        Console = services.TaskConsole;
        WebAccount = new AccountChannelViewModel(AccountChannel.Web);
        TvAccount = new AccountChannelViewModel(AccountChannel.Tv);
        Console.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Console.IsBusy))
            {
                LoginWebCommand.NotifyCanExecuteChanged();
                LoginTvCommand.NotifyCanExecuteChanged();
            }
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
    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public TaskConsoleViewModel Console { get; }
    public AccountChannelViewModel WebAccount { get; }
    public AccountChannelViewModel TvAccount { get; }
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }
    public string LastAccountCheckText { get => _lastAccountCheckText; private set => SetProperty(ref _lastAccountCheckText, value); }
    public string LoginMessage
    {
        get => _loginMessage;
        private set
        {
            if (SetProperty(ref _loginMessage, value))
            {
                OnPropertyChanged(nameof(HasLoginMessage));
                OnPropertyChanged(nameof(LoginMessageVisibility));
            }
        }
    }
    public bool HasLoginMessage => !string.IsNullOrWhiteSpace(LoginMessage);
    public Visibility LoginMessageVisibility => HasLoginMessage ? Visibility.Visible : Visibility.Collapsed;
    public InfoBarSeverity LoginMessageSeverity { get => _loginMessageSeverity; private set => SetProperty(ref _loginMessageSeverity, value); }
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
                OnPropertyChanged(nameof(MessageVisibility));
            }
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public Visibility MessageVisibility => HasMessage ? Visibility.Visible : Visibility.Collapsed;
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand DetectToolsCommand { get; }
    public IAsyncRelayCommand CleanupCommand { get; }
    public IAsyncRelayCommand RefreshAccountsCommand { get; }
    public IAsyncRelayCommand LoginWebCommand { get; }
    public IAsyncRelayCommand LoginTvCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _services.Settings.LoadAsync();
        await Task.WhenAll(DetectToolsAsync(), RefreshAccountsAsync());
    }

    public async Task RefreshAccountsAsync()
    {
        WebAccount.SetChecking();
        TvAccount.SetChecking();
        var snapshot = await _services.AccountStatus.GetStatusAsync();
        WebAccount.Apply(snapshot.Web);
        TvAccount.Apply(snapshot.Tv);
        LastAccountCheckText = $"最近检测：{snapshot.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    public async Task RefreshAccountAsync(AccountChannel channel)
    {
        var account = channel == AccountChannel.Web ? WebAccount : TvAccount;
        account.SetChecking();
        var status = await _services.AccountStatus.GetStatusAsync(channel);
        account.Apply(status);
        LastAccountCheckText = $"最近检测：{status.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
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

    private async Task LoginAsync(AccountChannel channel)
    {
        LoginMessage = string.Empty;
        var tv = channel == AccountChannel.Tv;
        var credentialPath = tv ? _services.Paths.TvCredentialFile : _services.Paths.WebCredentialFile;
        var credentialTimestamp = File.Exists(credentialPath) ? File.GetLastWriteTimeUtc(credentialPath) : DateTime.MinValue;
        var snapshot = await _services.TaskManager.RunExclusiveAsync(
            tv ? TaskKind.LoginTv : TaskKind.LoginWeb,
            false,
            tv ? "login_tv" : "login_web",
            (context, token) => _services.BBDown.LoginAsync(tv, context, token));

        if (snapshot.State == TaskState.Completed)
        {
            await RefreshAccountAsync(channel);
            var account = channel == AccountChannel.Web ? WebAccount : TvAccount;
            var credentialUpdated = File.Exists(credentialPath) && File.GetLastWriteTimeUtc(credentialPath) > credentialTimestamp;
            LoginMessageSeverity = credentialUpdated && account.IsLoggedIn ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            LoginMessage = credentialUpdated
                ? account.IsLoggedIn ? $"{account.ChannelTitle}登录成功" : $"{account.ChannelTitle}账号数据已更新，但状态尚未验证成功"
                : $"{account.ChannelTitle}账号数据没有更新，二维码可能已过期或尚未在手机端确认";
        }
        else
        {
            LoginMessageSeverity = snapshot.State == TaskState.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
            LoginMessage = snapshot.State == TaskState.Cancelled ? "登录已取消" : $"登录失败：{snapshot.Error}";
        }
    }

    private bool CanStartLogin() => !Console.IsBusy;
}
