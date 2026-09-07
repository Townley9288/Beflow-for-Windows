using System.Collections.Concurrent;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class DownloadQueueTests
{
    [Fact]
    public async Task MixedQueueIsFifoSnapshotsAreIndependentAndParsingCanOverlap()
    {
        using var fixture = new Fixture();
        var first = Job(fixture.Root); var second = Job(fixture.Root, dual: true);
        var id1 = await fixture.Queue.EnqueueAsync(first);
        await fixture.Executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        first.Download!.Options.WorkDirectory = "changed"; first.Download.Episodes.Clear();
        using (fixture.Work.EnterForeground(TaskKind.DownloadParse))
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Work.EnterForeground(TaskKind.LoginWeb));
            var id2 = await fixture.Queue.EnqueueAsync(second);
            Assert.Equal(fixture.Root, fixture.Queue.Snapshot.Items.Single(i => i.Id == id1).Download!.Options.WorkDirectory);
            fixture.Executor.Complete.Release();
            Assert.Equal(id2, await fixture.Executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            fixture.Executor.Complete.Release();
            await Until(() => fixture.Queue.Snapshot.Items.All(i => i.State == DownloadQueueState.Completed));
            Assert.Equal([id1, id2], fixture.Executor.Order);
        }
        Assert.Equal(1, fixture.Executor.MaximumActive);
    }

    [Fact]
    public async Task PausedAndRestartedQueuesRequireExplicitResumeEvenAfterEnqueue()
    {
        using var f = new Fixture();
        await f.Queue.PauseAsync();
        var id = await f.Queue.EnqueueAsync(Job(f.Root));
        Assert.Empty(f.Executor.Order);
        await f.Queue.ResumeAsync(); await f.Executor.Started.Reader.ReadAsync();
        await f.Queue.PauseAsync();
        Assert.Equal(DownloadQueueState.Paused, f.Queue.Snapshot.Items.Single().State);
        await f.Queue.EnqueueAsync(Job(f.Root));
        Assert.Single(f.Executor.Order);
        var restarted = new DownloadQueueService(f.Store, f.Executor, new TaskManager(f.Paths, new ProcessRunner()), f.Work);
        await restarted.InitializeAsync(); Assert.True(restarted.Snapshot.Paused);
        await restarted.ResumeAsync(); Assert.Equal(id, await f.Executor.Started.Reader.ReadAsync());
        await restarted.PauseAsync();
    }

    [Fact]
    public async Task EditingSkipsJobAndSavesSamePositionAndId()
    {
        using var f = new Fixture(); await f.Queue.PauseAsync();
        var id1 = await f.Queue.EnqueueAsync(Job(f.Root)); var id2 = await f.Queue.EnqueueAsync(Job(f.Root));
        var edited = await f.Queue.BeginEditAsync(id1); edited.Download!.Options.Quality = "edited";
        await f.Queue.ResumeAsync(); Assert.Equal(id2, await f.Executor.Started.Reader.ReadAsync());
        Assert.NotEqual("edited", f.Queue.Snapshot.Items[0].Download!.Options.Quality);
        await f.Queue.SaveEditAsync(id1, edited);
        Assert.Equal(id1, f.Queue.Snapshot.Items[0].Id);
        Assert.Equal("edited", f.Queue.Snapshot.Items[0].Download!.Options.Quality);
        f.Executor.Complete.Release(); Assert.Equal(id1, await f.Executor.Started.Reader.ReadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Queue.BeginEditAsync(id1));
        await f.Queue.PauseAsync();
    }

    [Fact]
    public async Task CancelCurrentContinuesNextAndDoesNotCancelForeground()
    {
        using var f = new Fixture();
        await f.Queue.EnqueueAsync(Job(f.Root)); await f.Executor.Started.Reader.ReadAsync();
        using var parse = f.Work.EnterForeground(TaskKind.DualAudioParse);
        var next = await f.Queue.EnqueueAsync(Job(f.Root));
        await f.Queue.CancelCurrentAsync();
        Assert.Equal(next, await f.Executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(DownloadQueueState.Cancelled, f.Queue.Snapshot.Items[0].State);
        Assert.False(f.Queue.Snapshot.Paused); Assert.True(f.Work.HasRunningOperations);
        await f.Queue.PauseAsync(); Assert.True(f.Work.HasRunningOperations);
    }

    [Fact]
    public async Task StoreFailurePreventsUnpersistedTaskFromStarting()
    {
        using var f = new Fixture(); await f.Queue.InitializeAsync();
        f.Store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.Queue.EnqueueAsync(Job(f.Root)));
        Assert.Empty(f.Executor.Order); Assert.Empty(f.Queue.Snapshot.Items); Assert.NotEmpty(f.Queue.Error);
    }

    [Fact]
    public async Task InvalidQueueIsPreservedAndHistoryIsUpserted()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(f.Paths.DownloadQueueFile, "{broken");
        await Assert.ThrowsAnyAsync<Exception>(() => new DownloadQueueStore(f.Paths).LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(f.Paths.DownloadQueueFile));
        var history = new HistoryStore(f.Paths); var id = Guid.NewGuid();
        await history.AddAsync(new HistoryRecord { QueueTaskId = id, Title = "first" });
        await history.AddAsync(new HistoryRecord { QueueTaskId = id, Title = "second" });
        var saved = Assert.Single(await history.LoadAsync()); Assert.Equal("second", saved.Title);
    }

    [Fact]
    public async Task SharedParseQuotaCapsAllSourcesAndCancelledWaiterDoesNotConsumeSlot()
    {
        using var f = new Fixture(); var settings = new SettingsStore(f.Paths);
        await settings.SaveAsync(new AppSettings { ParseConcurrency = 4 });
        var quota = new ParseConcurrencyLimiter(settings);
        var leases = new List<IDisposable>();
        for (var i = 0; i < 4; i++) leases.Add(await quota.EnterAsync(CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); var waiter = quota.EnterAsync(cancelled.Token);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var fifth = quota.EnterAsync(CancellationToken.None); Assert.False(fifth.IsCompleted);
        leases[0].Dispose(); using var granted = await fifth.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var lease in leases) lease.Dispose();
    }

    [Fact]
    public async Task SavedRunningAndEditingJobsRestorePausedWithoutLosingConfiguration()
    {
        using var f = new Fixture();
        var running = Job(f.Root); running.State = DownloadQueueState.Running; running.StartedAt = DateTimeOffset.Now;
        var editing = Job(f.Root); editing.State = DownloadQueueState.Editing;
        f.Store.Document = new DownloadQueueDocument { Items = [running, editing] };
        await f.Queue.InitializeAsync();
        Assert.True(f.Queue.Snapshot.Paused);
        Assert.Equal(DownloadQueueState.Paused, f.Queue.Snapshot.Items[0].State);
        Assert.Equal(DownloadQueueState.Waiting, f.Queue.Snapshot.Items[1].State);
        Assert.Empty(f.Executor.Order);
    }

    private static DownloadQueueItem Job(string directory, bool dual = false)
    {
        var episode = new DownloadEpisodeInfo { Page = new(1, "123", "One", "10"), State = DownloadEpisodeParseState.Ready,
            VideoStreams = [new(0, "1080P 高清", "1920x1080", 1920, 1080, "AVC", "25", "500 kbps", 500, "1 MB")],
            AudioStreams = [new(0, "AAC", "128 kbps", 128, "1 MB")] };
        var catalog = new DownloadCatalog { SourceUrl = "https://www.bilibili.com/video/av1", Title = "test", Episodes = [episode], AllPages = [episode.Page] };
        var selection = new EpisodeStreamSelection { PageNumber = 1, PageTitle = "One", Video = new("1080P 高清", "1920x1080", "AVC", 500), Audio = new("AAC", 128) };
        var options = new DownloadRequest { Url = catalog.SourceUrl, WorkDirectory = directory };
        return dual ? new DownloadQueueItem { Kind = DownloadQueueKind.DualAudio,
            DualCatalog = new DualAudioCatalog { SourceA = catalog, SourceB = catalog },
            DualAudio = new DualAudioBatchRequest { WorkDirectory = directory, SourceAUrl = options.Url, SourceBUrl = options.Url, Options = options,
                Pairs = [new() { PairNumber = 1, SourceAPageNumber = 1, SourceBPageNumber = 1, SourceA = selection, SourceB = selection }] } }
            : new DownloadQueueItem { Catalog = catalog, Download = new DownloadBatchRequest { Options = options, Episodes = [selection] } };
    }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private sealed class MemoryStore : IDownloadQueueStore
    {
        public DownloadQueueDocument Document = new(); public bool Fail;
        public Task<DownloadQueueDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(QueueSnapshot.Copy(Document));
        public Task SaveAsync(DownloadQueueDocument document, CancellationToken cancellationToken = default)
        { if (Fail) throw new IOException("disk full"); Document = QueueSnapshot.Copy(document); return Task.CompletedTask; }
    }
    private sealed class Executor : IDownloadQueueExecutor
    {
        public readonly System.Threading.Channels.Channel<Guid> Started = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        public readonly SemaphoreSlim Complete = new(0); public readonly List<Guid> Order = [];
        public int MaximumActive; private int active;
        public async Task ExecuteAsync(DownloadQueueItem item, Func<QueueCheckpoint, Task> saveCheckpoint, IProgress<QueueProgress> progress, TaskExecutionContext context, CancellationToken cancellationToken)
        {
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref active));
            try
            {
                Order.Add(item.Id); await Started.Writer.WriteAsync(item.Id, cancellationToken);
                await Complete.WaitAsync(cancellationToken);
                await saveCheckpoint(new QueueCheckpoint { Units = [new() { Number = 1, Completed = true }] });
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "beflow-queue-tests", Guid.NewGuid().ToString("N"));
        public ApplicationPaths Paths; public MemoryStore Store = new(); public Executor Executor = new();
        public WorkCoordinator Work = new(); public DownloadQueueService Queue;
        public Fixture() { Paths = new ApplicationPaths(Root, Root); Paths.EnsureCreated(); Queue = new(Store, Executor, new TaskManager(Paths, new ProcessRunner()), Work); }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
