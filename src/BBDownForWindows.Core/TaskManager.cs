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

    public TaskManager(ApplicationPaths paths, IProcessRunner processRunner)
    {
        _paths = paths;
        _processRunner = processRunner;
        _paths.EnsureCreated();
        CleanupOldLogs();
    }

    public TaskSnapshot? ActiveTask { get { lock (_sync) return _activeTask; } }
    public event EventHandler<TaskSnapshot>? TaskChanged;
    public event EventHandler<string>? LogAppended;

    public async Task<TaskSnapshot> RunExclusiveAsync(TaskKind kind, bool persistLog, string logLabel, Func<TaskExecutionContext, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (!await _exclusive.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("已有任务正在运行，请先等待完成或取消当前任务。");

        var snapshot = new TaskSnapshot { Kind = kind, State = TaskState.Running, StartedAt = DateTimeOffset.Now };
        StreamWriter? writer = null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (persistLog)
            {
                snapshot.LogPath = CreateLogPath(snapshot.Id, logLabel);
                writer = new StreamWriter(new FileStream(snapshot.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync("Beflow for Windows 任务日志");
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

            await operation(context, linked.Token);
            snapshot.State = linked.IsCancellationRequested ? TaskState.Cancelled : TaskState.Completed;
        }
        catch (OperationCanceledException)
        {
            snapshot.State = TaskState.Cancelled;
            LogAppended?.Invoke(this, "\n任务已取消\n");
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
            snapshot.FinishedAt = DateTimeOffset.Now;
            writer?.Dispose();
            linked.Dispose();
            lock (_sync) _activeCancellation = null;
            RaiseTaskChanged(snapshot);
            _exclusive.Release();
            CleanupOldLogs();
        }
        return snapshot;
    }

    public async Task CancelActiveAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) cancellation = _activeCancellation;
        cancellation?.Cancel();
        await _processRunner.TerminateAllAsync();
    }

    public async Task CleanupAsync() => await CancelActiveAsync();

    public string ReadSavedLog(string path)
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
        return File.ReadAllText(candidate);
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
        var files = new DirectoryInfo(_paths.LogsDirectory).EnumerateFiles("*.log").OrderByDescending(file => file.LastWriteTime).ToList();
        foreach (var file in files.Where(file => file.LastWriteTime < cutoff).Concat(files.Skip(MaximumLogFiles)).Distinct())
        {
            try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void RaiseTaskChanged(TaskSnapshot snapshot) => TaskChanged?.Invoke(this, snapshot);
}
