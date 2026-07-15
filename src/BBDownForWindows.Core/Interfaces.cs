namespace BBDownForWindows.Core;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken);
    Task TerminateAllAsync();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task<IReadOnlyList<HistoryRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(IReadOnlyList<HistoryRecord> records, CancellationToken cancellationToken = default);
    Task AddAsync(HistoryRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(int index, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IToolLocator
{
    ToolPaths Locate(AppSettings settings);
    Task<string> GetVersionAsync(string executable, CancellationToken cancellationToken = default);
}

public interface IAccountStatusService
{
    Task<AccountStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AccountChannelStatus> GetStatusAsync(AccountChannel channel, CancellationToken cancellationToken = default);
}

public interface ITaskManager
{
    TaskSnapshot? ActiveTask { get; }
    event EventHandler<TaskSnapshot>? TaskChanged;
    event EventHandler<string>? LogAppended;
    Task<TaskSnapshot> RunExclusiveAsync(TaskKind kind, bool persistLog, string logLabel, Func<TaskExecutionContext, CancellationToken, Task> operation, CancellationToken cancellationToken = default);
    Task CancelActiveAsync();
    Task CleanupAsync();
    string ReadSavedLog(string path);
}

public interface IBBDownService
{
    Task<VideoInfo> GetVideoInfoAsync(string url, string pages, TaskExecutionContext context, CancellationToken cancellationToken);
    Task DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
    Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken);
    Task<string> GetTitleAsync(string url, CancellationToken cancellationToken);
}

public interface IDualAudioService
{
    Task DownloadAndMuxAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
    Task RemuxExistingAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
}
