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
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    event EventHandler? Changed;
    Task<IReadOnlyList<HistoryRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(HistoryRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateTitlesAsync(IReadOnlyDictionary<Guid, string> titles, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IUpdateStateStore
{
    Task<UpdateState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UpdateState state, CancellationToken cancellationToken = default);
}

public interface IRenameSettingsStore
{
    Task<RenameSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RenameSettings settings, CancellationToken cancellationToken = default);
    Task<RenameSettings> UpdateAsync(Func<RenameSettings, RenameSettings> update, CancellationToken cancellationToken = default);
}

public interface IRenameHistoryStore
{
    event EventHandler? Changed;
    Task<IReadOnlyList<RenameHistoryRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(RenameHistoryRecord record, CancellationToken cancellationToken = default);
    Task MarkUndoneAsync(Guid id, DateTimeOffset undoneAt, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
    Task<string> DownloadAndVerifyAsync(UpdateAsset asset, string destinationDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
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

public interface ITmdbService
{
    Task ValidateApiKeyAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<TmdbTitleDetail> GetDetailAsync(TmdbSearchResult result, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<int, string>> GetEpisodeNamesAsync(int tmdbId, int season, CancellationToken cancellationToken = default);
}

public interface IRenameService
{
    Task<IReadOnlyList<RenameFileEntry>> ScanAsync(string directoryPath, IReadOnlyCollection<string>? preferredFiles = null, CancellationToken cancellationToken = default);
    Task<RenamePreview> BuildPreviewAsync(RenamePreviewRequest request, TaskExecutionContext context, CancellationToken cancellationToken = default);
    Task<RenameExecutionResult> ExecuteAsync(RenamePreview preview, TaskExecutionContext context, CancellationToken cancellationToken = default);
    Task<RenameExecutionResult> UndoAsync(Guid historyId, TaskExecutionContext context, CancellationToken cancellationToken = default);
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
    Task<DownloadResult> DownloadAsync(DownloadRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
    Task LoginAsync(bool tv, TaskExecutionContext context, CancellationToken cancellationToken);
    Task<string> GetTitleAsync(string url, CancellationToken cancellationToken);
}

public interface IDualAudioService
{
    Task<string> DownloadAndMuxAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
    Task RemuxExistingAsync(DualAudioRequest request, TaskExecutionContext context, CancellationToken cancellationToken);
}
