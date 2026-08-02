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
    private RenameSettings _renameSettings = new();
    private string _toolStatus = "尚未检测";
    private string _message = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private string _loginMessage = string.Empty;
    private string _lastAccountCheckText = "尚未检测";
    private InfoBarSeverity _loginMessageSeverity = InfoBarSeverity.Informational;
    private bool _active;
    private bool _initialized;
    private readonly SemaphoreSlim _toolDetectionGate = new(1, 1);
    private readonly SemaphoreSlim _accountRefreshGate = new(1, 1);
    private CancellationTokenSource? _activationCancellation;

    internal sealed record ToolDetectionResult(ToolPaths Tools, IReadOnlyList<string> Versions);

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        SaveDownloadCommand = new AsyncRelayCommand(SaveDownloadSettingsAsync);
        ResetDownloadCommand = new RelayCommand(ResetDownloadSettings);
        SaveRenameCommand = new AsyncRelayCommand(SaveRenameSettingsAsync);
        SaveAriaCommand = new AsyncRelayCommand(SaveAriaSettingsAsync);
        SaveMkvCommand = new AsyncRelayCommand(SaveMkvSettingsAsync);
        SaveUpdateCommand = new AsyncRelayCommand(SaveUpdateSettingsAsync);
        DetectToolsCommand = new AsyncRelayCommand(DetectToolsCommandAsync);
        CleanupCommand = new AsyncRelayCommand(CleanupAsync);
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAccountsAsync);
        LoginWebCommand = new AsyncRelayCommand(() => LoginAsync(AccountChannel.Web), CanStartLogin);
        LoginTvCommand = new AsyncRelayCommand(() => LoginAsync(AccountChannel.Tv), CanStartLogin);
        ValidateTmdbCommand = new AsyncRelayCommand(ValidateTmdbAsync);
        Console = services.TaskConsole;
        WebAccount = new AccountChannelViewModel(AccountChannel.Web);
        TvAccount = new AccountChannelViewModel(AccountChannel.Tv);
    }

    public IReadOnlyList<OptionItem> QualityOptions { get; } =
    [
        new("杜比视界", "杜比视界"), new("HDR 真彩", "HDR 真彩（大视界）"),
        new("4K·SDR增强", "4K·SDR增强（大视界）"), new("4K 超高清", "4K 超高清（大视界）"),
        new("1080P 高码率", "1080P 高码率"), new("1080P 高清", "1080P 高清"),
        new("智能修复", "智能修复"), new("720P 准高清", "720P 准高清"), new("480P 标清", "480P 标清"), new("360P 流畅", "360P 流畅")
    ];
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
    public RenameSettings RenameSettings { get => _renameSettings; private set => SetProperty(ref _renameSettings, value); }
    public TaskConsoleViewModel Console { get; }
    public AccountChannelViewModel WebAccount { get; }
    public AccountChannelViewModel TvAccount { get; }
    public bool IsActive => _active;
    public bool IsInitialized => _initialized;
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
    public InfoBarSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }
    public IAsyncRelayCommand SaveDownloadCommand { get; }
    public IRelayCommand ResetDownloadCommand { get; }
    public IAsyncRelayCommand SaveRenameCommand { get; }
    public IAsyncRelayCommand SaveAriaCommand { get; }
    public IAsyncRelayCommand SaveMkvCommand { get; }
    public IAsyncRelayCommand SaveUpdateCommand { get; }
    public IAsyncRelayCommand DetectToolsCommand { get; }
    public IAsyncRelayCommand CleanupCommand { get; }
    public IAsyncRelayCommand RefreshAccountsCommand { get; }
    public IAsyncRelayCommand LoginWebCommand { get; }
    public IAsyncRelayCommand LoginTvCommand { get; }
    public IAsyncRelayCommand ValidateTmdbCommand { get; }

    public void SetWorkDirectory(string value) => UpdateSettings(settings => settings.WorkDirectory = value);
    public void SetAria2cPath(string value) => UpdateSettings(settings => settings.Aria2cPath = value);
    public void SetMkvmergePath(string value) => UpdateSettings(settings => settings.MkvmergePath = value);

    private void UpdateSettings(Action<AppSettings> update)
    {
        var snapshot = Settings.Clone();
        update(snapshot);
        Settings = snapshot;
    }

    public void Activate()
    {
        if (_active) return;
        _activationCancellation?.Dispose();
        _activationCancellation = new CancellationTokenSource();
        _active = true;
        Console.PropertyChanged += Console_PropertyChanged;
    }

    public void Deactivate()
    {
        var activationCancellation = _activationCancellation;
        _activationCancellation = null;
        activationCancellation?.Cancel();
        activationCancellation?.Dispose();
        _initialized = false;
        if (!_active) return;
        _active = false;
        Console.PropertyChanged -= Console_PropertyChanged;
    }

    public async Task InitializeAsync()
    {
        var cancellationToken = GetCurrentOperationToken();
        _initialized = false;

        try
        {
            var settingsTask = _services.Settings.LoadAsync(cancellationToken);
            var renameSettingsTask = _services.RenameSettings.LoadAsync(cancellationToken);
            await Task.WhenAll(settingsTask, renameSettingsTask);
            cancellationToken.ThrowIfCancellationRequested();

            Settings = await settingsTask;
            RenameSettings = await renameSettingsTask;
            Settings.ThemeMode = _services.Theme.CurrentMode;
            _initialized = true;

            // Let the first settings frame render before probing executables or contacting account APIs.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (CanApply(cancellationToken)) _ = RunBackgroundInitializationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation away from the page cancels initialization silently.
            _initialized = false;
        }
        catch (Exception exception)
        {
            _initialized = false;
            if (CanApply(cancellationToken))
                SetMessage($"设置页初始化失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task RunBackgroundInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(DetectToolsAsync(cancellationToken), RefreshAccountsCoreAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (CanApply(cancellationToken))
                SetMessage($"设置页后台初始化失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    public async Task RefreshAccountsAsync()
    {
        var cancellationToken = GetCurrentOperationToken();
        try
        {
            await RefreshAccountsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAccountsCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _accountRefreshGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanApply(cancellationToken)) return;
                WebAccount.SetChecking();
                TvAccount.SetChecking();
                var snapshot = await Task.Run(
                    () => _services.AccountStatus.GetStatusAsync(cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanApply(cancellationToken)) return;
                WebAccount.Apply(snapshot.Web);
                TvAccount.Apply(snapshot.Tv);
                LastAccountCheckText = $"最近检测：{snapshot.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            }
            finally
            {
                _accountRefreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!CanApply(cancellationToken)) return;
            LastAccountCheckText = "最近检测：失败";
            SetMessage($"账号状态检测失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    public Task RefreshAccountAsync(AccountChannel channel) =>
        RefreshAccountAsync(channel, GetCurrentOperationToken());

    private async Task RefreshAccountAsync(AccountChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await _accountRefreshGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanApply(cancellationToken)) return;
                var account = channel == AccountChannel.Web ? WebAccount : TvAccount;
                account.SetChecking();
                var status = await Task.Run(
                    () => _services.AccountStatus.GetStatusAsync(channel, cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanApply(cancellationToken)) return;
                account.Apply(status);
                LastAccountCheckText = $"最近检测：{status.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            }
            finally
            {
                _accountRefreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task SaveDownloadSettingsAsync()
    {
        var edited = Settings.Clone();
        await UpdateStoredSettingsAsync(settings => CopyDownloadSettings(edited, settings));
        SetMessage("默认下载设置已保存", InfoBarSeverity.Success);
    }

    public async Task SaveInputSettingsAsync()
    {
        var monitorClipboard = Settings.MonitorClipboard;
        var monitorDragLinks = Settings.MonitorDragLinks;
        await UpdateStoredSettingsAsync(settings =>
        {
            settings.MonitorClipboard = monitorClipboard;
            settings.MonitorDragLinks = monitorDragLinks;
        });
        SetMessage("便捷输入设置已保存", InfoBarSeverity.Success);
    }

    private void ResetDownloadSettings()
    {
        var defaults = new AppSettings();
        UpdateSettings(settings => CopyDownloadSettings(defaults, settings));
        SetMessage("默认下载设置已恢复（尚未保存）", InfoBarSeverity.Warning);
    }

    private async Task SaveRenameSettingsAsync()
    {
        var edited = RenameSettings.Clone();
        RenameSettings = await _services.RenameSettings.UpdateAsync(current =>
        {
            var snapshot = current.Clone();
            CopyTmdbSettings(edited, snapshot);
            return snapshot;
        });
        SetMessage("影视重命名设置已保存", InfoBarSeverity.Success);
    }

    private async Task SaveAriaSettingsAsync()
    {
        var edited = Settings.Clone();
        await UpdateStoredSettingsAsync(settings => CopyAriaSettings(edited, settings));
        SetMessage("aria2c 设置已保存", InfoBarSeverity.Success);
    }

    private async Task SaveMkvSettingsAsync()
    {
        var path = Settings.MkvmergePath;
        await UpdateStoredSettingsAsync(settings => settings.MkvmergePath = path);
        SetMessage("MKVToolNix 设置已保存", InfoBarSeverity.Success);
    }

    private async Task SaveUpdateSettingsAsync()
    {
        var checkOnStartup = Settings.CheckUpdatesOnStartup;
        await UpdateStoredSettingsAsync(settings => settings.CheckUpdatesOnStartup = checkOnStartup);
        SetMessage("软件更新设置已保存", InfoBarSeverity.Success);
    }

    private async Task UpdateStoredSettingsAsync(Action<AppSettings> update)
    {
        await _services.Settings.UpdateAsync(current =>
        {
            var snapshot = current.Clone();
            snapshot.SchemaVersion = 4;
            update(snapshot);
            snapshot.ThemeMode = current.ThemeMode;
            return snapshot;
        });
    }

    private static void CopyDownloadSettings(AppSettings source, AppSettings target)
    {
        target.Quality = source.VideoQualityRule;
        target.VideoQualityRule = source.VideoQualityRule;
        target.IncludeHdrDolbyInAutoSelection = false;
        target.Encoding = source.Encoding;
        target.DownloadMode = source.DownloadMode;
        target.AudioCodec = source.AudioCodec;
        target.AudioBitratePriority = source.AudioBitratePriority;
        target.WorkDirectory = source.WorkDirectory;
        target.Danmaku = source.Danmaku;
        target.Subtitle = source.Subtitle;
        target.Cover = source.Cover;
        target.MultiThread = source.MultiThread;
        target.SaveTaskLogs = source.SaveTaskLogs;
    }

    private static void CopyAriaSettings(AppSettings source, AppSettings target)
    {
        target.UseAria2c = source.UseAria2c;
        target.Aria2AutoTune = source.Aria2AutoTune;
        target.Aria2cPath = source.Aria2cPath;
        target.Aria2MaxConnection = source.Aria2MaxConnection;
        target.Aria2Split = source.Aria2Split;
        target.Aria2MaxConcurrentDownloads = source.Aria2MaxConcurrentDownloads;
        target.Aria2MinSplitSize = source.Aria2MinSplitSize;
    }

    private async Task DetectToolsCommandAsync()
    {
        var cancellationToken = GetCurrentOperationToken();
        try
        {
            await DetectToolsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DetectToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _toolDetectionGate.WaitAsync(cancellationToken);
            try
            {
                await DetectToolsCoreAsync(cancellationToken);
            }
            finally
            {
                _toolDetectionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (CanApply(cancellationToken)) ToolStatus = $"工具检测失败：{exception.Message}";
        }
    }

    private async Task DetectToolsCoreAsync(CancellationToken cancellationToken)
    {
        var settingsSnapshot = Settings.Clone();
        var result = await ProbeToolsAsync(_services.ToolLocator, settingsSnapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanApply(cancellationToken)) return;

        var tools = result.Tools;
        var currentSettings = Settings.Clone();
        var settingsChanged = false;
        if (string.IsNullOrWhiteSpace(settingsSnapshot.Aria2cPath)
            && string.IsNullOrWhiteSpace(currentSettings.Aria2cPath)
            && !string.IsNullOrWhiteSpace(tools.Aria2c))
        {
            currentSettings.Aria2cPath = tools.Aria2c;
            settingsChanged = true;
        }
        if (string.IsNullOrWhiteSpace(settingsSnapshot.MkvmergePath)
            && string.IsNullOrWhiteSpace(currentSettings.MkvmergePath)
            && !string.IsNullOrWhiteSpace(tools.Mkvmerge))
        {
            currentSettings.MkvmergePath = tools.Mkvmerge;
            settingsChanged = true;
        }
        if (settingsChanged) Settings = currentSettings;
        ToolStatus = $"BBDown: {result.Versions[0]}\naria2c: {result.Versions[1]}\nFFmpeg: {result.Versions[2]}\nffprobe: {result.Versions[3]}\nmkvmerge: {result.Versions[4]}";
    }

    internal static async Task<ToolDetectionResult> ProbeToolsAsync(
        IToolLocator toolLocator,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolLocator);
        ArgumentNullException.ThrowIfNull(settings);
        var settingsSnapshot = settings.Clone();

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tools = toolLocator.Locate(settingsSnapshot);
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await Task.WhenAll(
                toolLocator.GetVersionAsync(tools.BBDown, cancellationToken),
                toolLocator.GetVersionAsync(tools.Aria2c, cancellationToken),
                toolLocator.GetVersionAsync(tools.Ffmpeg, cancellationToken),
                toolLocator.GetVersionAsync(tools.Ffprobe, cancellationToken),
                toolLocator.GetVersionAsync(tools.Mkvmerge, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return new ToolDetectionResult(tools, versions);
        }, cancellationToken);
    }

    private CancellationToken GetCurrentOperationToken() => _activationCancellation?.Token ?? CancellationToken.None;

    private bool CanApply(CancellationToken cancellationToken)
    {
        if (!_active || cancellationToken.IsCancellationRequested) return false;
        return _activationCancellation is { } activation && cancellationToken == activation.Token;
    }

    private async Task CleanupAsync()
    {
        await _services.TaskManager.CleanupAsync();
        SetMessage("本会话启动的进程已清理；下载文件和 .aria2 文件均已保留。", InfoBarSeverity.Success);
    }

    private async Task LoginAsync(AccountChannel channel)
    {
        var activationToken = GetCurrentOperationToken();
        if (!CanApply(activationToken)) return;
        LoginMessage = string.Empty;
        var tv = channel == AccountChannel.Tv;
        var credentialPath = tv ? _services.Paths.TvCredentialFile : _services.Paths.WebCredentialFile;
        var credentialTimestamp = File.Exists(credentialPath) ? File.GetLastWriteTimeUtc(credentialPath) : DateTime.MinValue;
        TaskSnapshot snapshot;
        try
        {
            snapshot = await _services.TaskManager.RunExclusiveAsync(
                tv ? TaskKind.LoginTv : TaskKind.LoginWeb,
                false,
                tv ? "login_tv" : "login_web",
                (context, token) => _services.BBDown.LoginAsync(tv, context, token),
                activationToken);
        }
        catch (OperationCanceledException) when (activationToken.IsCancellationRequested)
        {
            return;
        }

        if (!CanApply(activationToken)) return;
        if (snapshot.State == TaskState.Completed)
        {
            await RefreshAccountAsync(channel, activationToken);
            if (!CanApply(activationToken)) return;
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

    private async Task ValidateTmdbAsync()
    {
        try
        {
            var edited = RenameSettings.Clone();
            await _services.Tmdb.ValidateApiKeyAsync(edited);
            RenameSettings = await _services.RenameSettings.UpdateAsync(current =>
            {
                var snapshot = current.Clone();
                CopyTmdbSettings(edited, snapshot);
                return snapshot;
            });
            SetMessage("TMDB API Key 验证成功", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            SetMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    public void DismissMessage() => Message = string.Empty;

    private static void CopyTmdbSettings(RenameSettings source, RenameSettings target)
    {
        target.TmdbApiKey = source.TmdbApiKey;
        target.ProxyUrl = source.ProxyUrl;
        target.RequestTimeoutSeconds = source.RequestTimeoutSeconds;
    }

    private void SetMessage(string message, InfoBarSeverity severity)
    {
        MessageSeverity = severity;
        Message = message;
    }

    private void Console_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Console.IsBusy)) return;
        LoginWebCommand.NotifyCanExecuteChanged();
        LoginTvCommand.NotifyCanExecuteChanged();
    }
}
