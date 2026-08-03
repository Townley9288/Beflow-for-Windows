using BBDownForWindows.App;
using BBDownForWindows.App.Pages;
using BBDownForWindows.App.ViewModels;
using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class SettingsInitializationTests
{
    [Fact(Timeout = 5_000)]
    public async Task ProbeToolsRunsLocatorAndVersionChecksOffTheCallingThread()
    {
        var locator = new RecordingToolLocator();
        var callingThread = 0;
        var completion = new TaskCompletionSource<SettingsViewModel.ToolDetectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            callingThread = Environment.CurrentManagedThreadId;
            try
            {
                completion.SetResult(SettingsViewModel.ProbeToolsAsync(locator, new AppSettings()).GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };

        caller.Start();
        var result = await completion.Task;
        Assert.True(caller.Join(TimeSpan.FromSeconds(1)));

        Assert.NotEqual(callingThread, locator.LocateThread);
        Assert.NotEmpty(locator.VersionThreads);
        Assert.All(locator.VersionThreads, thread => Assert.NotEqual(callingThread, thread));
        Assert.Equal("BBDown 1", result.Versions[0]);
        Assert.Equal("mkvmerge 1", result.Versions[^1]);
    }

    [Fact]
    public async Task ProbeToolsHonorsCancellationBeforeStartingWork()
    {
        var locator = new RecordingToolLocator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SettingsViewModel.ProbeToolsAsync(locator, new AppSettings(), cancellation.Token));

        Assert.Equal(0, locator.LocateCalls);
    }

    [Fact(Timeout = 5_000)]
    public async Task ProbeToolsCancelsRunningVersionChecks()
    {
        var locator = new BlockingVersionToolLocator();
        using var cancellation = new CancellationTokenSource();
        var probe = SettingsViewModel.ProbeToolsAsync(locator, new AppSettings(), cancellation.Token);
        await locator.VersionCheckStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Fact]
    public void DeactivateCancelsThePageOperationToken()
    {
        var root = Directory.CreateTempSubdirectory();
        AppServices? services = null;
        try
        {
            var appDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            services = new AppServices(new ApplicationPaths(appDirectory.FullName, Path.Combine(root.FullName, "local")));
            var viewModel = new SettingsViewModel(services);
            viewModel.Activate();
            var source = Assert.IsType<CancellationTokenSource>(
                typeof(SettingsViewModel)
                    .GetField("_activationCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(viewModel));
            var token = source.Token;

            viewModel.Deactivate();

            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            services?.HttpClient.Dispose();
            services?.UpdateHttpClient.Dispose();
            root.Delete(true);
        }
    }

    [Fact]
    public void QrCancellationRequiresTheSameRunningLoginTask()
    {
        var loginTaskId = Guid.NewGuid();
        var matching = new TaskSnapshot
        {
            Id = loginTaskId,
            Kind = TaskKind.LoginWeb,
            State = TaskState.Running
        };

        Assert.True(SettingsPage.CanCancelQrTask(matching, loginTaskId));
        Assert.False(SettingsPage.CanCancelQrTask(matching, Guid.NewGuid()));
        Assert.False(SettingsPage.CanCancelQrTask(new TaskSnapshot
        {
            Id = loginTaskId,
            Kind = TaskKind.LoginTv,
            State = TaskState.Completed
        }, loginTaskId));
        Assert.False(SettingsPage.CanCancelQrTask(new TaskSnapshot
        {
            Id = loginTaskId,
            Kind = TaskKind.DownloadParse,
            State = TaskState.Running
        }, loginTaskId));
        Assert.False(SettingsPage.CanCancelQrTask(null, loginTaskId));
    }

    private sealed class RecordingToolLocator : IToolLocator
    {
        private int _locateCalls;
        public int LocateCalls => _locateCalls;
        public int LocateThread { get; private set; }
        public List<int> VersionThreads { get; } = [];

        public ToolPaths Locate(AppSettings settings)
        {
            Interlocked.Increment(ref _locateCalls);
            LocateThread = Environment.CurrentManagedThreadId;
            return new ToolPaths
            {
                BBDown = "BBDown.exe",
                Aria2c = "aria2c.exe",
                Ffmpeg = "ffmpeg.exe",
                Ffprobe = "ffprobe.exe",
                Mkvmerge = "mkvmerge.exe"
            };
        }

        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (VersionThreads) VersionThreads.Add(Environment.CurrentManagedThreadId);
            return Task.FromResult($"{Path.GetFileNameWithoutExtension(executable)} 1");
        }
    }

    private sealed class BlockingVersionToolLocator : IToolLocator
    {
        public TaskCompletionSource<bool> VersionCheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolPaths Locate(AppSettings settings) => new()
        {
            BBDown = "BBDown.exe",
            Aria2c = "aria2c.exe",
            Ffmpeg = "ffmpeg.exe",
            Ffprobe = "ffprobe.exe",
            Mkvmerge = "mkvmerge.exe"
        };

        public async Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default)
        {
            VersionCheckStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return executable;
        }
    }
}
