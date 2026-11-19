using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class TaskExecutionContext
{
    private readonly Action<string> _append;
    internal TaskExecutionContext(Action<string> append) => _append = append;
    public void AppendLog(string text)
    {
        if (!string.IsNullOrEmpty(text)) _append(text);
    }

    public TaskExecutionContext WithPrefix(string prefix) => new(text =>
    {
        if (string.IsNullOrEmpty(text)) return;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index == lines.Length - 1 && lines[index].Length == 0) continue;
            _append($"[{prefix}] {lines[index]}{(index < lines.Length - 1 ? "\n" : string.Empty)}");
        }
    });
}

public sealed class TaskManager : ITaskManager
{
    private const int RetentionDays = 30;
    private const int MaximumLogFiles = 100;
    private readonly ApplicationPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private TaskSnapshot? _activeTask;
    private readonly WorkCoordinator? _work;

    public TaskManager(ApplicationPaths paths, IProcessRunner processRunner, WorkCoordinator? work = null)
    {
        _paths = paths;
        _processRunner = processRunner;
        _work = work;
        _paths.EnsureCreated();
        CleanupOldLogs();
    }

    public TaskSnapshot? ActiveTask { get { lock (_sync) return _activeTask; } }
    public event EventHandler<TaskSnapshot>? TaskChanged;
    public event EventHandler<string>? LogAppended;

    public async Task<TaskSnapshot> RunExclusiveAsync(TaskKind kind, bool persistLog, string logLabel, Func<TaskExecutionContext, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (!await _exclusive.WaitAsync(0))
            throw new InvalidOperationException("已有任务正在运行，请先等待完成或取消当前任务。");

        var snapshot = new TaskSnapshot { Kind = kind, State = TaskState.Running, StartedAt = DateTimeOffset.Now };
        StreamWriter? writer = null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            using var activity = _work?.EnterForeground(kind);
            if (persistLog)
            {
                snapshot.LogPath = CreateLogPath(snapshot.Id, logLabel);
                writer = new StreamWriter(new FileStream(snapshot.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync("Beflow 任务日志");
                await writer.WriteLineAsync($"任务ID: {snapshot.Id}");
                await writer.WriteLineAsync($"开始时间: {snapshot.StartedAt:O}");
                await writer.WriteLineAsync();
            }
            lock (_sync)
            {
                _activeTask = snapshot;
                _activeCancellation = linked;
            }
            RaiseTaskChanged(snapshot);

            var context = new TaskExecutionContext(text =>
            {
                LogAppended?.Invoke(this, text);
                if (writer is not null)
                {
                    lock (writer) writer.Write(text);
                }
            });

            linked.Token.ThrowIfCancellationRequested();
            await operation(context, linked.Token);
            snapshot.State = linked.IsCancellationRequested ? TaskState.Cancelled : TaskState.Completed;
        }
        catch (OperationCanceledException)
        {
            snapshot.State = TaskState.Cancelled;
        }
        catch (Exception exception)
        {
            snapshot.State = TaskState.Failed;
            snapshot.Error = exception.Message;
            var text = $"\n错误: {exception.Message}\n";
            LogAppended?.Invoke(this, text);
            if (writer is not null) await writer.WriteAsync(text);
        }
        finally
        {
            try
            {
                if (snapshot.State == TaskState.Cancelled)
                {
                    const string message = "\n任务已取消\n";
                    if (writer is not null) await writer.WriteAsync(message);
                    LogAppended?.Invoke(this, message);
                }
            }
            finally
            {
                snapshot.FinishedAt = DateTimeOffset.Now;
                lock (_sync)
                {
                    _activeCancellation = null;
                    linked.Dispose();
                }
                try { writer?.Dispose(); }
                finally { _exclusive.Release(); }
                RaiseTaskChanged(snapshot);
                CleanupOldLogs();
            }
        }
        return snapshot;
    }

    public async Task CancelActiveAsync()
    {
        lock (_sync) _activeCancellation?.Cancel();
        await _processRunner.TerminateAllAsync();
    }

    public async Task CleanupAsync() => await CancelActiveAsync();

    public string ReadSavedLog(string path)
    {
        return File.ReadAllText(ResolveSavedLogPath(path));
    }

    public async Task<IReadOnlyList<string>> ReadSavedLogLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        await using var stream = new FileStream(
            ResolveSavedLogPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            lines.Add(line);
        return lines;
    }

    private string ResolveSavedLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("该历史记录没有保存日志");
        var root = Path.GetFullPath(_paths.LogsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var migratedCandidate = Path.Combine(_paths.LogsDirectory, Path.GetFileName(candidate));
            if (File.Exists(migratedCandidate)) candidate = migratedCandidate;
        }
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !candidate.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("日志路径无效");
        return candidate;
    }

    private string CreateLogPath(Guid id, string label)
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        var safe = string.Concat(label.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(_paths.LogsDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safe}_{id:N}.log");
    }

    private void CleanupOldLogs()
    {
        if (!Directory.Exists(_paths.LogsDirectory)) return;
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        try
        {
            var files = new DirectoryInfo(_paths.LogsDirectory).EnumerateFiles("*.log").Where(file => !file.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTime).ToList();
            var expired = files.Where(file => file.LastWriteTime < cutoff).Concat(files.Skip(MaximumLogFiles)).Distinct().ToList();
            if (expired.Count == 0) return;

            var options = SettingsStore.CreateOptions();
            var history = ReadRecords<List<HistoryRecord>>(_paths.HistoryFile, options);
            var queue = ReadRecords<DownloadQueueDocument>(_paths.DownloadQueueFile, options);
            if (history?.Any(record => record is null) == true
                || queue is not null && (queue.Items is null || queue.Items.Any(item => item?.LogPaths is null)))
                return;
            var referenced = (history ?? []).Select(record => record.LogPath)
                .Concat((queue?.Items ?? []).SelectMany(item => item.LogPaths))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in expired.Where(file => !referenced.Contains(file.Name)))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Keep logs whenever their reference files could not be read.
        }
    }

    private static T? ReadRecords<T>(string path, JsonSerializerOptions options) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, options) ?? throw new JsonException("日志引用文件为空");
        }
        catch (FileNotFoundException) { return null; }
    }

    private void RaiseTaskChanged(TaskSnapshot snapshot) => TaskChanged?.Invoke(this, snapshot);
}
