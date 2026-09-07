namespace BBDownForWindows.Core;

/// <summary>Foreground parsing may overlap the queue; file mutations and login remain exclusive.</summary>
public sealed class WorkCoordinator
{
    private readonly object gate = new();
    private bool foreground;
    private bool exclusive;
    private bool download;
    public event EventHandler? Changed;
    public bool HasRunningOperations { get { lock (gate) return foreground || download; } }
    public bool DownloadRunning { get { lock (gate) return download; } }
    public IDisposable EnterForeground(TaskKind kind)
    {
        var parsing = kind is TaskKind.DownloadParse or TaskKind.DualAudioParse or TaskKind.Info;
        lock (gate)
        {
            if (foreground || (!parsing && download)) throw new InvalidOperationException("请先暂停下载队列并等待当前操作结束。");
            foreground = true;
            exclusive = !parsing;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new ActionLease(() => { lock (gate) { foreground = exclusive = false; } Changed?.Invoke(this, EventArgs.Empty); });
    }
    public IDisposable? TryEnterDownload()
    {
        lock (gate)
        {
            if (exclusive || download) return null;
            download = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new ActionLease(() => { lock (gate) download = false; Changed?.Invoke(this, EventArgs.Empty); });
    }
}

internal sealed class ActionLease(Action release) : IDisposable
{
    private Action? action = release;
    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
}

public sealed class ParseConcurrencyLimiter(ISettingsStore settings)
{
    private readonly object gate = new();
    private int active;
    private TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var limit = (await settings.LoadAsync(cancellationToken)).ParseConcurrency;
        ParseConcurrencyPolicy.Validate(limit);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (active < limit)
                {
                    active++;
                    return new ActionLease(() => { lock (gate) { active--; var signal = changed; changed = NewSignal(); signal.TrySetResult(); } });
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }
}
