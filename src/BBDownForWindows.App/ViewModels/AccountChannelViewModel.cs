using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace BBDownForWindows.App.ViewModels;

public sealed class AccountChannelViewModel : ObservableObject
{
    private string _displayName = "尚未检测";
    private string _userIdText = "UID：—";
    private string _detailText = "等级与会员信息将在登录后显示";
    private string _expiryText = "有效期：—";
    private string _avatarUrl = string.Empty;
    private string _statusTitle = "尚未检测";
    private string _statusMessage = "进入设置页后自动检测";
    private string _loginButtonText;
    private InfoBarSeverity _severity = InfoBarSeverity.Informational;
    private bool _isLoggedIn;

    public AccountChannelViewModel(AccountChannel channel)
    {
        Channel = channel;
        ChannelTitle = channel == AccountChannel.Web ? "WEB 账号" : "TV 账号";
        ChannelDescription = channel == AccountChannel.Web ? "用于 WEB 接口和网页会员内容" : "用于 TV 接口和对应会员内容";
        _loginButtonText = channel == AccountChannel.Web ? "网页登录" : "TV 登录";
    }

    public AccountChannel Channel { get; }
    public string ChannelTitle { get; }
    public string ChannelDescription { get; }
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }
    public string UserIdText { get => _userIdText; private set => SetProperty(ref _userIdText, value); }
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    public string ExpiryText { get => _expiryText; private set => SetProperty(ref _expiryText, value); }
    public string AvatarUrl { get => _avatarUrl; private set => SetProperty(ref _avatarUrl, value); }
    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LoginButtonText { get => _loginButtonText; private set => SetProperty(ref _loginButtonText, value); }
    public InfoBarSeverity Severity { get => _severity; private set => SetProperty(ref _severity, value); }
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }

    public void SetChecking()
    {
        StatusTitle = "正在检测";
        StatusMessage = "正在连接 B 站账号接口…";
        Severity = InfoBarSeverity.Informational;
    }

    public void Apply(AccountChannelStatus status)
    {
        IsLoggedIn = status.State == AccountLoginState.LoggedIn;
        StatusTitle = status.State switch
        {
            AccountLoginState.LoggedIn => "已登录",
            AccountLoginState.Expired => "登录已失效",
            AccountLoginState.Unavailable => "暂时无法验证",
            _ => "未登录"
        };
        StatusMessage = status.Message;
        Severity = status.State switch
        {
            AccountLoginState.LoggedIn => InfoBarSeverity.Success,
            AccountLoginState.Expired => InfoBarSeverity.Warning,
            AccountLoginState.Unavailable => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
        LoginButtonText = IsLoggedIn ? "重新登录" : Channel == AccountChannel.Web ? "网页登录" : "TV 登录";
        ExpiryText = status.CredentialUpdatedAt is null
            ? "有效期：—"
            : status.CredentialExpiresAt is { } expiresAt
                ? $"有效期至：{expiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "有效期：未知，以实时验证为准";

        if (status.Profile is { } profile)
        {
            DisplayName = profile.DisplayName;
            UserIdText = $"UID：{profile.UserId}";
            DetailText = $"Lv{profile.Level} · {profile.VipLabel}";
            AvatarUrl = profile.AvatarUrl;
        }
        else
        {
            DisplayName = status.CredentialUpdatedAt is null ? "尚未登录" : "本地账号数据已存在";
            UserIdText = "UID：—";
            DetailText = "验证成功后显示等级与会员信息";
            AvatarUrl = string.Empty;
        }
    }
}
