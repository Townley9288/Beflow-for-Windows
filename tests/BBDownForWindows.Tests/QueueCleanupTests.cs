using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class QueueCleanupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletedMuxResumesSourceCleanupWithoutDownloadingAgain(bool keepSources)
    {
        using var f = new Fixture(keepSources);
        // The previous process saved the published MKV then stopped before source cleanup.
        var result = await f.Run();
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(0, f.Runner.Calls);
        Assert.True(File.Exists(f.Final));
        Assert.Equal(keepSources, File.Exists(f.SourceA));
        Assert.Equal(keepSources, File.Exists(f.SourceB));
        Assert.Equal(!keepSources, f.Item.Checkpoint.Units[0].SourceA.SourcesCleaned);
    }

    [Fact]
    public async Task DeletedSourceBeforeCheckpointSaveDoesNotBlockRemainingCleanup()
    {
        using var f = new Fixture(false);
        // Deletion can precede the durable SourcesCleaned flag when the process is interrupted.
        File.Delete(f.SourceA);
        var result = await f.Run();
        Assert.Equal(TaskState.Completed, result.State);
        Assert.False(File.Exists(f.SourceB));
        Assert.True(f.Item.Checkpoint.Units[0].SourceA.SourcesCleaned);
        Assert.True(f.Item.Checkpoint.Units[0].SourceB.SourcesCleaned);
        Assert.True(File.Exists(f.Final));
    }

    [Fact]
    public async Task ChangedFinalOutputPreventsDeletingSourcesOnResume()
    {
        using var f = new Fixture(false);
        await File.WriteAllTextAsync(f.Final, "changed output");
        var result = await f.Run();
        Assert.Equal(TaskState.Failed, result.State);
        Assert.True(File.Exists(f.SourceA));
        Assert.True(File.Exists(f.SourceB));
        Assert.Equal(0, f.Runner.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beflow-queue-cleanup-");
        public readonly NoProcessRunner Runner = new();
        public string Final { get; }
        public string SourceA { get; }
        public string SourceB { get; }
        public DownloadQueueItem Item { get; }
        private readonly DownloadQueueExecutor executor;
        private readonly TaskManager manager;
        public Fixture(bool keep)
        {
            var paths = new ApplicationPaths(root.FullName, root.FullName);
            paths.EnsureCreated();
            Final = Path.Combine(root.FullName, "final.mkv");
            SourceA = Path.Combine(root.FullName, "source-a.mp4");
            SourceB = Path.Combine(root.FullName, "source-b.m4a");
            foreach (var path in new[] { Final, SourceA, SourceB }) File.WriteAllText(path, Path.GetFileName(path));
            Item = new DownloadQueueItem
            {
                Kind = DownloadQueueKind.DualAudio,
                DualAudio = new DualAudioBatchRequest { KeepSourceFiles = keep, Pairs = [new() { PairNumber = 1 }] },
                Checkpoint = new QueueCheckpoint { WorkDirectory = root.FullName, Units = [new QueueUnitCheckpoint
                {
                    Number = 1, Completed = true, FinalPath = Final, Files = [QueueFileStamp.Read(Final)],
                    SourceA = new() { Completed = true, Files = [QueueFileStamp.Read(SourceA)] },
                    SourceB = new() { Completed = true, Files = [QueueFileStamp.Read(SourceB)] }
                }] }
            };
            var tools = new ToolLocator(paths, () => []);
            executor = new DownloadQueueExecutor(new QueuedMediaDownloader(paths, Runner, tools,
                new ParseConcurrencyLimiter(new SettingsStore(paths))), Runner, tools, new DownloadNamingService(), new HistoryStore(paths));
            manager = new TaskManager(paths, Runner);
        }
        public Task<TaskSnapshot> Run() => manager.RunExclusiveAsync(TaskKind.DualAudioMux, false, "cleanup", (context, token) =>
            executor.ExecuteAsync(Item, _ => Task.CompletedTask, new Progress<QueueProgress>(), context, token));
        public void Dispose() => root.Delete(true);
    }

    private sealed class NoProcessRunner : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? output, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Completed media must not be downloaded or muxed again"); }
        public Task TerminateAllAsync() => Task.CompletedTask;
    }
}
