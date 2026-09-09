namespace BBDownForWindows.Core;

public sealed class DownloadQueueService
{
    private readonly IDownloadQueueStore store;
    private readonly IDownloadQueueExecutor executor;
    private readonly ITaskManager tasks;
    private readonly WorkCoordinator work;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DownloadQueueDocument document = new();
    private DownloadQueueDocument snapshot = new();
    private Task? pump;
    private Task? initialization;
    private CancellationTokenSource? activeCancellation;
    private Guid? activeId;
    private bool cancelCurrent;
    private bool stopping;
    private bool faulted;
    public string Error { get; private set; } = string.Empty;
    public event EventHandler? Changed;
    public event EventHandler<QueueProgress>? ProgressChanged;
    public DownloadQueueDocument Snapshot => QueueSnapshot.Copy(Volatile.Read(ref snapshot));
    public DownloadQueueService(IDownloadQueueStore store, IDownloadQueueExecutor executor, ITaskManager tasks, WorkCoordinator work)
    {
        this.store = store; this.executor = executor; this.tasks = tasks; this.work = work;
        work.Changed += (_, _) => Kick();
    }

    public Task InitializeAsync()
    {
        lock (this) return initialization ??= InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            document = await store.LoadAsync();
            // Only unfinished work needs an explicit resume after restarting.
            document.Paused = document.Items.Any(i => !i.IsTerminal);
            foreach (var item in document.Items)
            {
                if (item.State == DownloadQueueState.Editing) item.State = DownloadQueueState.Waiting;
                if (item.State is DownloadQueueState.Running or DownloadQueueState.Pausing) item.State = DownloadQueueState.Paused;
            }
            await SaveLockedAsync();
        }
        catch (Exception exception) { Fault(exception); throw; }
        finally { gate.Release(); }
    }

    public static void ValidateItem(DownloadQueueItem item)
    {
        if (item.Id == Guid.Empty || item.Checkpoint is null || !Enum.IsDefined(item.State)) throw new InvalidDataException("队列任务数据无效");
        if (item.Kind == DownloadQueueKind.Download)
        {
            if (item.Download is null || item.DualAudio is not null || item.Download.Episodes.Count == 0) throw new InvalidDataException("下载队列没有已选分集");
            if (string.IsNullOrWhiteSpace(item.Download.Options.Url) || string.IsNullOrWhiteSpace(item.Download.Options.WorkDirectory)) throw new InvalidDataException("链接和下载目录不能为空");
            if (item.Download.Episodes.Select(e => e.PageNumber).Distinct().Count() != item.Download.Episodes.Count) throw new InvalidDataException("队列中有重复分集");
        }
        else if (item.Kind == DownloadQueueKind.DualAudio)
        {
            if (item.DualAudio is null || item.Download is not null || !item.DualAudio.Pairs.Any(p => p.IsSelected)) throw new InvalidDataException("多音轨队列没有已选配对");
            if (string.IsNullOrWhiteSpace(item.DualAudio.WorkDirectory)) throw new InvalidDataException("输出目录不能为空");
        }
        else throw new InvalidDataException("未知队列任务类型");
    }

    public async Task<Guid> EnqueueAsync(DownloadQueueItem item)
    {
        var copy = QueueSnapshot.Copy(item);
        copy.Id = Guid.NewGuid(); copy.State = DownloadQueueState.Waiting;
        copy.AddedAt = DateTimeOffset.Now; copy.StartedAt = copy.FinishedAt = null;
        copy.Checkpoint = new(); copy.LogPaths = []; copy.Error = string.Empty;
        ValidateItem(copy);
        await ChangeAsync(doc => doc.Items.Add(copy));
        return copy.Id;
    }
    public async Task<DownloadQueueItem> BeginEditAsync(Guid id)
    {
        DownloadQueueItem? result = null;
        await ChangeAsync(doc =>
        {
            var item = Find(doc, id);
            if (!item.CanEdit) throw new InvalidOperationException("只能编辑尚未开始的等待任务");
            item.State = DownloadQueueState.Editing;
            result = QueueSnapshot.Copy(item);
        });
        return result!;
    }
    public Task CancelEditAsync(Guid id) => ChangeAsync(doc =>
    {
        var item = Find(doc, id);
        if (item.State != DownloadQueueState.Editing) throw new InvalidOperationException("任务不在编辑状态");
        item.State = DownloadQueueState.Waiting;
    });
    public Task SaveEditAsync(Guid id, DownloadQueueItem edited) => ChangeAsync(doc =>
    {
        var old = Find(doc, id);
        if (old.State != DownloadQueueState.Editing || old.StartedAt is not null) throw new InvalidOperationException("任务已不能编辑");
        var copy = QueueSnapshot.Copy(edited);
        copy.Id = old.Id; copy.ParentId = old.ParentId; copy.AddedAt = old.AddedAt;
        copy.StartedAt = copy.FinishedAt = null; copy.State = DownloadQueueState.Waiting;
        copy.Checkpoint = new(); copy.LogPaths = []; copy.Error = string.Empty;
        ValidateItem(copy);
        doc.Items[doc.Items.IndexOf(old)] = copy;
    });
    public Task MoveAsync(Guid id, int direction) => ChangeAsync(doc =>
    {
        var item = Find(doc, id);
        if (!item.CanEdit) throw new InvalidOperationException("只能调整等待任务顺序");
        var waiting = doc.Items.Where(i => i.CanEdit).ToList();
        var index = waiting.IndexOf(item); var other = index + Math.Sign(direction);
        if (other < 0 || other >= waiting.Count) return;
        var a = doc.Items.IndexOf(item); var b = doc.Items.IndexOf(waiting[other]);
        (doc.Items[a], doc.Items[b]) = (doc.Items[b], doc.Items[a]);
    });
    public Task RemoveAsync(Guid id) => ChangeAsync(doc =>
    {
        var item = Find(doc, id);
        if (item.State is DownloadQueueState.Running or DownloadQueueState.Pausing or DownloadQueueState.Editing) throw new InvalidOperationException("请先停止任务或结束编辑");
        doc.Items.Remove(item);
    });
    public Task ClearCompletedAsync() => ChangeAsync(doc => doc.Items.RemoveAll(i => i.State == DownloadQueueState.Completed));

    public async Task PauseAsync()
    {
        await ChangeAsync(doc =>
        {
            doc.Paused = true;
            if (activeId is { } id) Find(doc, id).State = DownloadQueueState.Pausing;
        }, () => activeCancellation?.Cancel());
        Task? running; lock (this) running = pump;
        if (running is not null) await running;
    }
    public Task ResumeAsync() => ChangeAsync(doc =>
    {
        if (stopping) throw new InvalidOperationException("软件正在退出");
        doc.Paused = false;
        foreach (var item in doc.Items.Where(i => i.State == DownloadQueueState.Paused)) item.State = DownloadQueueState.Waiting;
    });
    public Task CancelCurrentAsync() => ChangeAsync(doc =>
    {
        if (activeId is null) return;
        Find(doc, activeId.Value).State = DownloadQueueState.Pausing;
    }, () => { cancelCurrent = true; activeCancellation?.Cancel(); });
    public async Task ShutdownAsync()
    {
        stopping = true;
        await PauseAsync();
    }
    public Task RetryFailedAsync(Guid id)
    {
        var item = CreateRetry(id);
        return EnqueueAsync(item);
    }
    public DownloadQueueItem CreateRetry(Guid id)
    {
        var item = Find(Snapshot, id);
        if (!item.IsTerminal) throw new InvalidOperationException("请等待任务结束后重试");
        var failed = item.Checkpoint.Units.Where(u => u.Error.Length > 0).Select(u => u.Number).ToHashSet();
        if (failed.Count == 0 && item.State == DownloadQueueState.Failed) failed = item.Download?.Episodes.Select(e => e.PageNumber).ToHashSet() ?? item.DualAudio!.Pairs.Select(p => p.PairNumber).ToHashSet();
        if (failed.Count == 0) throw new InvalidOperationException("没有失败集");
        var copy = QueueSnapshot.Copy(item); copy.ParentId = item.Id;
        if (copy.Download is not null) copy.Download.Episodes.RemoveAll(e => !failed.Contains(e.PageNumber));
        if (copy.DualAudio is not null) copy.DualAudio.Pairs.RemoveAll(p => !failed.Contains(p.PairNumber));
        copy.Checkpoint = new(); copy.StartedAt = null; copy.LogPaths = []; copy.State = DownloadQueueState.Waiting;
        return copy;
    }

    private async Task ChangeAsync(Action<DownloadQueueDocument> action, Action? afterSave = null)
    {
        await InitializeAsync();
        await gate.WaitAsync();
        try
        {
            if (faulted) throw new IOException(Error);
            var next = QueueSnapshot.Copy(document);
            action(next);
            try { await store.SaveAsync(next); }
            catch (Exception exception) { Fault(exception); throw; }
            document = next; afterSave?.Invoke(); Publish();
        }
        finally { gate.Release(); }
        Kick();
    }
    private void Kick()
    {
        lock (this)
        {
            if (initialization?.IsCompletedSuccessfully != true || faulted || stopping || pump is { IsCompleted: false }) return;
            pump = Task.Run(PumpAsync);
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                IDisposable? activity = null;
                DownloadQueueItem? job = null;
                await gate.WaitAsync();
                try
                {
                    if (document.Paused || faulted || stopping) return;
                    var next = document.Items.FirstOrDefault(i => i.State == DownloadQueueState.Waiting);
                    if (next is null || (activity = work.TryEnterDownload()) is null) return;
                    next.State = DownloadQueueState.Running; next.StartedAt ??= DateTimeOffset.Now;
                    activeId = next.Id; cancelCurrent = false;
                    activeCancellation = new CancellationTokenSource();
                    await SaveLockedAsync(); job = QueueSnapshot.Copy(next);
                }
                finally
                {
                    gate.Release();
                    if (job is null) { activeCancellation?.Dispose(); activeCancellation = null; activeId = null; activity?.Dispose(); }
                }
                try
                {
                    var result = await tasks.RunExclusiveAsync(job.Kind == DownloadQueueKind.Download ? TaskKind.DownloadBatch : TaskKind.DualAudioMux,
                        job.Download?.Options.SaveTaskLogs ?? job.DualAudio!.Options.SaveTaskLogs, "queue_" + job.Id.ToString("N"), async (context, token) =>
                        {
                            var log = tasks.ActiveTask?.LogPath;
                            if (!string.IsNullOrWhiteSpace(log)) { await SaveLogAsync(job.Id, log); job.LogPaths.Add(log); }
                            try
                            {
                                await executor.ExecuteAsync(job, checkpoint => SaveCheckpointAsync(job.Id, checkpoint),
                                    new DirectProgress<QueueProgress>(p => ProgressChanged?.Invoke(this, p)), context, token);
                            }
                            catch (QueuePersistenceException exception) { Fault(exception); throw; }
                        }, activeCancellation!.Token);
                    await gate.WaitAsync();
                    try
                    {
                        var item = Find(document, job.Id);
                        if (cancelCurrent) item.State = DownloadQueueState.Cancelled;
                        else if (document.Paused || result.State == TaskState.Cancelled) item.State = DownloadQueueState.Paused;
                        else if (result.State == TaskState.Failed) { item.State = DownloadQueueState.Failed; item.Error = result.Error; }
                        else item.State = item.Failed == 0 ? DownloadQueueState.Completed : item.Succeeded == 0 ? DownloadQueueState.Failed : DownloadQueueState.PartialFailure;
                        if (item.IsTerminal) item.FinishedAt = DateTimeOffset.Now;
                        await SaveLockedAsync();
                    }
                    finally { gate.Release(); }
                }
                finally
                {
                    activeCancellation?.Dispose(); activeCancellation = null; activeId = null;
                    activity!.Dispose();
                }
            }
        }
        catch (Exception exception) { Fault(exception); }
        finally
        {
            lock (this) pump = null;
            // A foreground exclusive operation may have ended while this pump was exiting.
            if (!faulted && !stopping && !Snapshot.Paused && Snapshot.Items.Any(i => i.State == DownloadQueueState.Waiting) && !work.HasRunningOperations) Kick();
        }
    }
    private async Task SaveCheckpointAsync(Guid id, QueueCheckpoint checkpoint)
    {
        await gate.WaitAsync();
        try { Find(document, id).Checkpoint = QueueSnapshot.Copy(checkpoint); await SaveLockedAsync(); }
        finally { gate.Release(); }
    }
    private async Task SaveLogAsync(Guid id, string path)
    {
        await gate.WaitAsync();
        try { Find(document, id).LogPaths.Add(path); await SaveLockedAsync(); }
        finally { gate.Release(); }
    }
    private async Task SaveLockedAsync()
    {
        try { await store.SaveAsync(document); Publish(); }
        catch (Exception exception) { Fault(exception); throw; }
    }
    private void Publish() { Volatile.Write(ref snapshot, QueueSnapshot.Copy(document)); Changed?.Invoke(this, EventArgs.Empty); }
    private void Fault(Exception exception)
    {
        faulted = true; Error = $"队列已停止：{exception.Message}"; activeCancellation?.Cancel(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private static DownloadQueueItem Find(DownloadQueueDocument doc, Guid id) => doc.Items.SingleOrDefault(i => i.Id == id) ?? throw new InvalidOperationException("队列任务不存在");
    private sealed class DirectProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
