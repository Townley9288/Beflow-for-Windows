namespace BBDownForWindows.Core;

/// <summary>Reads the original WEB catalog and attaches a separate, optional TV catalog.</summary>
public sealed class AutomaticDownloadSession(IAccountStatusService accounts, IBBDownService parser,
    Func<AccountChannel, DateTime>? credentialUpdatedAt = null)
{
    private readonly SemaphoreSlim validationGate = new(1, 1);
    private readonly Dictionary<AccountChannel, (AccountChannelStatus Status, DateTime Stamp)> validated = [];

    private async Task<AccountChannelStatus> GetStatusAsync(AccountChannel channel, CancellationToken token)
    {
        await validationGate.WaitAsync(token);
        try
        {
            var stamp = credentialUpdatedAt?.Invoke(channel) ?? default;
            if (credentialUpdatedAt is not null && validated.TryGetValue(channel, out var cached)
                && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.Status.CheckedAt < TimeSpan.FromSeconds(30))
                return cached.Status;
            var status = await accounts.GetStatusAsync(channel, token);
            if (status.State == AccountLoginState.LoggedIn) validated[channel] = (status, stamp);
            else validated.Remove(channel);
            return status;
        }
        finally { validationGate.Release(); }
    }

    public async Task<DownloadCatalog> ParseAsync(DownloadParseRequest request, IProgress<DownloadParseProgress>? progress,
        TaskExecutionContext context, CancellationToken cancellationToken)
    {
        var web = await GetStatusAsync(AccountChannel.Web, cancellationToken);
        if (web.State is not (AccountLoginState.LoggedIn or AccountLoginState.NotConfigured))
            throw new InvalidOperationException($"无法使用 WEB 会话：{web.Message}。请确认 WEB 登录后手动重试。");
        context.AppendLog(web.State == AccountLoginState.NotConfigured
            ? "未配置 WEB 登录数据，沿用匿名 WEB 解析公开视频。\n" : "正在读取原有 WEB 画质。\n");
        var webProgress = new DirectProgress<DownloadParseProgress>(p =>
        {
            if (p.Episode is { } episode) DownloadSessionPolicy.Remember(episode, "WEB");
            progress?.Report(p);
        });
        var catalog = await parser.ParseDownloadAsync(request with { ApiMode = "WEB" }, webProgress, context, cancellationToken);
        catalog.AutomaticSession = true;
        foreach (var episode in catalog.Episodes) DownloadSessionPolicy.Remember(episode, "WEB");
        if (cancellationToken.IsCancellationRequested) return catalog;
        var ready = catalog.Episodes.Where(e => e.State == DownloadEpisodeParseState.Ready).ToList();
        if (ready.Count == 0) return catalog;
        void Warn(DownloadEpisodeInfo episode, string message)
        {
            episode.SourceWarning = $"TV 读取失败：{message}；已保留 WEB 画质，可重新解析重试。";
            context.AppendLog($"P{episode.Page.Number} {episode.SourceWarning}\n");
        }
        try
        {
            var tv = await GetStatusAsync(AccountChannel.Tv, cancellationToken);
            if (tv.State == AccountLoginState.NotConfigured) return catalog;
            if (tv.State != AccountLoginState.LoggedIn)
            {
                foreach (var episode in ready) Warn(episode, tv.Message);
                return catalog;
            }
            context.AppendLog("正在读取独立的 TV 画质，默认仍使用 WEB。\n");
            var result = await parser.ParseDownloadAsync(request with
            {
                Url = catalog.ResolvedUrl, ApiMode = "TV", Pages = string.Join(',', ready.Select(e => e.Page.Number)), Rule = null
            }, null, context.WithPrefix("TV画质"), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return catalog;
            foreach (var original in ready)
            {
                var matches = result.Episodes.Where(e => e.Page.Number == original.Page.Number).ToList();
                if (matches.Count != 1) { Warn(original, "没有返回唯一的对应分P"); continue; }
                var current = matches[0];
                if (current.State != DownloadEpisodeParseState.Ready) { Warn(original, current.Error); continue; }
                if (current.Page.Cid != original.Page.Cid) { Warn(original, "分集身份已变化，请重新解析"); continue; }
                original.Sources.Add(new("TV", current.VideoStreams, current.AudioStreams, current.IsMuxedStream));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return catalog; }
        catch (Exception exception)
        {
            foreach (var episode in ready) Warn(episode, exception.Message);
        }
        finally
        {
            var completed = 0;
            foreach (var episode in ready)
                progress?.Report(new(++completed, ready.Count, episode.Page.Number, episode.Page.Title, episode,
                    string.IsNullOrWhiteSpace(episode.SourceWarning) ? $"P{episode.Page.Number} WEB／TV 画质读取完成" : episode.SourceWarning));
        }
        return catalog;
    }

    // Both catalogs were read at parse time. Applying a rule never silently changes sessions.
    public Task PrepareAsync(DownloadCatalog catalog, StreamSelectionRule rule, DownloadMode mode,
        TaskExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
