using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class BBDownRuntimeTests
{
    [Fact]
    public void StagesBBDownBesideCredentialsAndRefreshesChangedBinary()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            paths.EnsureCreated();
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(paths.ToolsDirectory, "BBDown"));
            var source = Path.Combine(sourceDirectory.FullName, "BBDown.exe");
            File.WriteAllText(source, "first");
            File.WriteAllText(paths.WebCredentialFile, "credential");

            var manager = new BBDownRuntimeManager(paths);
            var staged = manager.PrepareExecutable(source);

            Assert.Equal(paths.RuntimeBBDownExecutable, staged);
            Assert.Equal(Path.GetDirectoryName(paths.WebCredentialFile), Path.GetDirectoryName(staged));
            Assert.Equal("first", File.ReadAllText(staged));
            Assert.Equal("credential", File.ReadAllText(paths.WebCredentialFile));

            File.WriteAllText(source, "second-version");
            staged = manager.PrepareExecutable(source);

            Assert.Equal("second-version", File.ReadAllText(staged));
            Assert.Equal("credential", File.ReadAllText(paths.WebCredentialFile));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task BBDownServiceRunsManagedRuntimeExecutable()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            paths.EnsureCreated();
            var source = Path.Combine(app.FullName, "BBDown.exe");
            File.WriteAllText(source, "binary");
            File.WriteAllText(paths.WebCredentialFile, "credential");
            var runner = new RecordingProcessRunner();
            var service = new BBDownService(paths, runner, new FixedToolLocator(source), new FixedSettingsStore());

            var title = await service.GetTitleAsync("https://example.test/video", CancellationToken.None);

            Assert.Equal("测试标题", title);
            Assert.NotNull(runner.LastRequest);
            Assert.Equal(paths.RuntimeBBDownExecutable, runner.LastRequest!.FileName);
            Assert.Equal(paths.RuntimeDirectory, runner.LastRequest.WorkingDirectory);
            Assert.True(File.Exists(paths.RuntimeBBDownExecutable));
            Assert.Equal("credential", File.ReadAllText(paths.WebCredentialFile));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task DownloadReturnsTitleAlreadyParsedByBBDown()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            var paths = new ApplicationPaths(app.FullName, Path.Combine(root.FullName, "local"));
            paths.EnsureCreated();
            var source = Path.Combine(app.FullName, "BBDown.exe");
            File.WriteAllText(source, "binary");
            var runner = new RecordingProcessRunner();
            var service = new BBDownService(paths, runner, new FixedToolLocator(source), new FixedSettingsStore());
            var manager = new TaskManager(paths, runner);
            var title = string.Empty;

            var snapshot = await manager.RunExclusiveAsync(TaskKind.Download, false, "download", async (context, token) =>
            {
                title = await service.DownloadAsync(new DownloadRequest { Url = "https://example.test/video" }, context, token);
            });

            Assert.Equal(TaskState.Completed, snapshot.State);
            Assert.Equal("测试标题", title);
        }
        finally { root.Delete(true); }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public ProcessRunRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            LastRequest = request;
            const string output = "[2026-07-15 00:00:00.000] - 视频标题: 测试标题";
            onOutput?.Invoke(output);
            return Task.FromResult(new ProcessResult(0, output, false));
        }

        public Task TerminateAllAsync() => Task.CompletedTask;
    }

    private sealed class FixedToolLocator(string bbDown) : IToolLocator
    {
        public ToolPaths Locate(AppSettings settings) => new() { BBDown = bbDown };
        public Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default) =>
            Task.FromResult("test");
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
