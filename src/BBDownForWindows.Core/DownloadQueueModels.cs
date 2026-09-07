using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBDownForWindows.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadQueueKind { Download, DualAudio }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadQueueState { Waiting, Editing, Running, Pausing, Paused, Completed, PartialFailure, Failed, Cancelled }

public sealed class DownloadQueueDocument
{
    public int SchemaVersion { get; set; } = 1;
    public bool Paused { get; set; }
    public List<DownloadQueueItem> Items { get; set; } = [];
}

public sealed class DownloadQueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public DownloadQueueKind Kind { get; set; }
    public DownloadQueueState State { get; set; } = DownloadQueueState.Waiting;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DownloadBatchRequest? Download { get; set; }
    public DualAudioBatchRequest? DualAudio { get; set; }
    public DownloadCatalog? Catalog { get; set; }
    public DualAudioCatalog? DualCatalog { get; set; }
    public QueueCheckpoint Checkpoint { get; set; } = new();
    public string Error { get; set; } = string.Empty;
    public List<string> LogPaths { get; set; } = [];
    [JsonIgnore] public string Title => Download?.Title ?? DualAudio?.SourceATitle ?? string.Empty;
    [JsonIgnore] public string Url => Download?.Options.Url ?? DualAudio?.SourceAUrl ?? string.Empty;
    [JsonIgnore] public string OutputDirectory => Checkpoint.OutputDirectory.Length > 0 ? Checkpoint.OutputDirectory : Download?.Options.WorkDirectory ?? DualAudio?.WorkDirectory ?? string.Empty;
    [JsonIgnore] public int Total => Download?.Episodes.Count ?? DualAudio?.Pairs.Count(p => p.IsSelected) ?? 0;
    [JsonIgnore] public int Succeeded => Checkpoint.Units.Count(u => u.Completed);
    [JsonIgnore] public int Failed => Checkpoint.Units.Count(u => u.Error.Length > 0);
    [JsonIgnore] public bool IsTerminal => State is DownloadQueueState.Completed or DownloadQueueState.PartialFailure or DownloadQueueState.Failed or DownloadQueueState.Cancelled;
    [JsonIgnore] public bool CanEdit => StartedAt is null && State == DownloadQueueState.Waiting;
}

public sealed class QueueCheckpoint
{
    public string WorkDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public List<QueueUnitCheckpoint> Units { get; set; } = [];
}

public sealed class QueueUnitCheckpoint
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public string Error { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public string MuxPath { get; set; } = string.Empty;
    public bool MuxCompleted { get; set; }
    public List<QueueFileStamp> Files { get; set; } = [];
    public List<QueuePublication> Publications { get; set; } = [];
    public QueueSourceCheckpoint SourceA { get; set; } = new();
    public QueueSourceCheckpoint SourceB { get; set; } = new();
}

public sealed class QueueSourceCheckpoint
{
    public string Directory { get; set; } = string.Empty;
    public string OutputStem { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public bool SourcesCleaned { get; set; }
    public List<QueueFileStamp> Files { get; set; } = [];
}

public sealed record QueuePublication(QueueFileStamp Source, string Destination);

public sealed record QueueFileStamp(string Path, long Length, long LastWriteTicks, string Sha256)
{
    public static QueueFileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new IOException($"文件缺失或为空：{path}");
        using var stream = File.OpenRead(path);
        return new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
    }
    public void Verify()
    {
        if (Read(Path) != this) throw new IOException($"文件已被修改，不能复用：{Path}");
    }
}

public sealed record QueueProgress(Guid Id, int Current, string Phase, double? Percent, string Speed, string Eta);

public interface IDownloadQueueStore
{
    Task<DownloadQueueDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadQueueDocument document, CancellationToken cancellationToken = default);
}

public interface IDownloadQueueExecutor
{
    Task ExecuteAsync(DownloadQueueItem item, Func<QueueCheckpoint, Task> saveCheckpoint,
        IProgress<QueueProgress> progress, TaskExecutionContext context, CancellationToken cancellationToken);
}

public static class QueueSnapshot
{
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SettingsStore.CreateOptions()), SettingsStore.CreateOptions())!;
}

public sealed class DownloadQueueStore(ApplicationPaths paths) : IDownloadQueueStore
{
    private readonly SemaphoreSlim gate = JsonFileGates.For(paths.DownloadQueueFile);
    public async Task<DownloadQueueDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.DownloadQueueFile)) return new();
            await using var stream = File.OpenRead(paths.DownloadQueueFile);
            var document = await JsonSerializer.DeserializeAsync<DownloadQueueDocument>(stream, SettingsStore.CreateOptions(), cancellationToken)
                ?? throw new InvalidDataException("下载队列文件为空");
            if (document.SchemaVersion != 1 || document.Items is null || document.Items.Select(i => i.Id).Distinct().Count() != document.Items.Count)
                throw new InvalidDataException("下载队列版本或任务数据无效，请保留原文件并检查");
            foreach (var item in document.Items) DownloadQueueService.ValidateItem(item);
            return document;
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(DownloadQueueDocument document, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await AtomicJson.WriteAsync(paths.DownloadQueueFile, document, SettingsStore.CreateOptions(), cancellationToken); }
        finally { gate.Release(); }
    }
}
