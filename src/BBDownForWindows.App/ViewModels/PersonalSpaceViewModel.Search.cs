using System.Collections.ObjectModel;
using BBDownForWindows.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace BBDownForWindows.App.ViewModels;

public sealed class SpaceUserViewModel(BilibiliSpaceUser user)
{
    public BilibiliSpaceUser User { get; } = user;
    public string Name => User.Name;
    public string AvatarUrl => User.AvatarUrl;
    public string Description => User.Description;
    public string Detail => $"UID {User.Uid} · {User.Fans:N0} 粉丝 · {User.Videos:N0} 个投稿";
    public string Verification => User.Verification;
    public bool HasVerification => !string.IsNullOrWhiteSpace(Verification);
}

public sealed partial class PersonalSpaceViewModel
{
    private bool searchOpen;
    private string userKeyword = "";
    private int userPage;
    private int userTotal;
    private int userPages;
    private bool userSearchFailed;

    public ObservableCollection<SpaceUserViewModel> UserResults { get; } = [];
    public string InputActionText => string.IsNullOrWhiteSpace(Input) || LooksLikeLink(Input) ? "读取主页" : "搜索 UP 主";
    public Visibility UserSearchVisibility => searchOpen ? Visibility.Visible : Visibility.Collapsed;
    public string UserSearchSummary => userPage == 0
        ? $"搜索“{userKeyword}”"
        : $"“{userKeyword}” · 已显示 {UserResults.Count} / {userTotal} 个 UP 主，请选择要读取的主页。";
    public Visibility RetryUserSearchVisibility => userSearchFailed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MoreUsersVisibility => userPage > 0 && userPage < userPages && !userSearchFailed ? Visibility.Visible : Visibility.Collapsed;
    public IAsyncRelayCommand MoreUsersCommand { get; private set; } = null!;
    public IAsyncRelayCommand RetryUserSearchCommand { get; private set; } = null!;
    public IAsyncRelayCommand<SpaceUserViewModel> OpenUserCommand { get; private set; } = null!;
    public IRelayCommand CloseUserSearchCommand { get; private set; } = null!;

    private void InitializeUserSearch()
    {
        MoreUsersCommand = new AsyncRelayCommand(() => SearchUsersAsync(userPage + 1), () => IsIdle && searchOpen && userPage < userPages);
        RetryUserSearchCommand = new AsyncRelayCommand(() => SearchUsersAsync(userPage + 1), () => IsIdle && searchOpen && userSearchFailed);
        OpenUserCommand = new AsyncRelayCommand<SpaceUserViewModel>(OpenUserAsync, user => IsIdle && user is not null && UserResults.Contains(user));
        CloseUserSearchCommand = new RelayCommand(() =>
        {
            if (Profile is not null) Input = $"https://space.bilibili.com/{Profile.Uid}";
            ResetUserSearch();
            Message = Profile is null ? "输入 UP 主名称搜索，或粘贴个人主页链接。" : DirectoryStatus;
        }, () => IsIdle && searchOpen);
    }

    private static bool LooksLikeLink(string value) => value.Contains("://", StringComparison.Ordinal)
        || value.TrimStart().StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || value.Contains("bilibili.com/", StringComparison.OrdinalIgnoreCase);

    private async Task SubmitInputAsync()
    {
        if (IsBusy) return;
        if (BilibiliInputParser.TryGetSpaceUid(Input, out _) || LooksLikeLink(Input))
        {
            ResetUserSearch();
            await ReadAsync(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(Input)) { Message = "请输入 UP 主名称或个人主页链接。"; return; }
        ResetUserSearch();
        userKeyword = Input.Trim();
        searchOpen = true;
        NotifyUserSearch();
        await SearchUsersAsync(1);
    }

    private async Task SearchUsersAsync(int requestedPage)
    {
        if (IsBusy) return;
        IsBusy = true;
        userSearchFailed = false;
        Message = $"正在搜索“{userKeyword}”…";
        NotifyUserSearch();
        using var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            var result = await tasks.RunExclusiveAsync(TaskKind.DownloadParse, false, "space-user-search", async (_, token) =>
            {
                var resultPage = await space.SearchUsersAsync(userKeyword, requestedPage, token);
                token.ThrowIfCancellationRequested();
                foreach (var user in resultPage.Items)
                    if (!UserResults.Any(existing => existing.User.Uid == user.Uid)) UserResults.Add(new(user));
                userPage = resultPage.PageNumber;
                userPages = resultPage.TotalPages;
                userTotal = resultPage.Total;
            }, source.Token);
            userSearchFailed = result.State != TaskState.Completed;
            Message = result.State switch
            {
                TaskState.Failed => $"{result.Error} 点击“重试搜索”可重试。",
                TaskState.Cancelled => "搜索已取消，已显示的候选账号仍保留；可重试搜索。",
                _ when UserResults.Count == 0 => $"没有找到与“{userKeyword}”匹配的 UP 主，请换个名称搜索。",
                _ => "请选择目标 UP 主；选中后读取其投稿、合集和列表。"
            };
        }
        catch (Exception ex) { userSearchFailed = true; Message = $"{ex.Message} 点击“重试搜索”可重试。"; }
        finally { cancellation = null; IsBusy = false; NotifyUserSearch(); }
    }

    private async Task OpenUserAsync(SpaceUserViewModel? user)
    {
        if (IsBusy || user is null || !UserResults.Contains(user)) return;
        Input = $"https://space.bilibili.com/{user.User.Uid}";
        await ReadAsync(false);
    }

    private void ResetUserSearch()
    {
        searchOpen = false;
        userKeyword = "";
        userPage = userPages = userTotal = 0;
        userSearchFailed = false;
        UserResults.Clear();
        NotifyUserSearch();
    }

    private void NotifyUserSearch()
    {
        foreach (var name in new[] { nameof(UserSearchVisibility), nameof(UserSearchSummary), nameof(RetryUserSearchVisibility), nameof(MoreUsersVisibility), nameof(BrowseVisibility), nameof(SelectionVisibility) }) OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged();
        NotifyUserSearchCommands();
    }

    private void NotifyUserSearchCommands()
    {
        MoreUsersCommand?.NotifyCanExecuteChanged(); RetryUserSearchCommand?.NotifyCanExecuteChanged();
        OpenUserCommand?.NotifyCanExecuteChanged(); CloseUserSearchCommand?.NotifyCanExecuteChanged();
    }
}
