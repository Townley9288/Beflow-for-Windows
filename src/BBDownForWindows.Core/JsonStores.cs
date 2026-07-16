using System.Collections.Concurrent;
using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class SettingsStore(ApplicationPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly SemaphoreSlim _gate = JsonFileGates.For(paths.SettingsFile);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJson.WriteAsync(paths.SettingsFile, settings, Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = update(await LoadCoreAsync(cancellationToken)) ?? throw new InvalidOperationException("设置更新不能返回空值");
            await AtomicJson.WriteAsync(paths.SettingsFile, settings, Options, cancellationToken);
            return settings.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.SettingsFile)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    internal static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class HistoryStore(ApplicationPaths paths) : IHistoryStore
{
    private static readonly JsonSerializerOptions Options = SettingsStore.CreateOptions();
    private readonly SemaphoreSlim _gate = JsonFileGates.For(paths.HistoryFile);
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<HistoryRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await LoadCoreAsync(cancellationToken);
            if (EnsureIds(history)) await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
            return history;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await LoadCoreAsync(cancellationToken);
            EnsureIds(history);
            if (record.Id == Guid.Empty) record.Id = Guid.NewGuid();
            history.Insert(0, record);
            await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await LoadCoreAsync(cancellationToken);
            EnsureIds(history);
            changed = history.RemoveAll(record => record.Id == id) > 0;
            if (changed) await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateTitlesAsync(IReadOnlyDictionary<Guid, string> titles, CancellationToken cancellationToken = default)
    {
        if (titles.Count == 0) return;
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await LoadCoreAsync(cancellationToken);
            EnsureIds(history);
            foreach (var record in history)
            {
                if (!titles.TryGetValue(record.Id, out var title) || string.IsNullOrWhiteSpace(title) || record.Title == title) continue;
                record.Title = title;
                changed = true;
            }
            if (changed) await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AtomicJson.WriteAsync(paths.HistoryFile, Array.Empty<HistoryRecord>(), Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<HistoryRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.HistoryFile)) return [];
        try
        {
            await using var stream = File.OpenRead(paths.HistoryFile);
            return await JsonSerializer.DeserializeAsync<List<HistoryRecord>>(stream, Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool EnsureIds(IEnumerable<HistoryRecord> records)
    {
        var changed = false;
        foreach (var record in records)
        {
            if (record.Id != Guid.Empty) continue;
            record.Id = Guid.NewGuid();
            changed = true;
        }
        return changed;
    }
}

public sealed class UpdateStateStore(ApplicationPaths paths) : IUpdateStateStore
{
    private static readonly JsonSerializerOptions Options = SettingsStore.CreateOptions();

    public async Task<UpdateState> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.UpdateStateFile)) return new UpdateState();
        try
        {
            await using var stream = File.OpenRead(paths.UpdateStateFile);
            return await JsonSerializer.DeserializeAsync<UpdateState>(stream, Options, cancellationToken) ?? new UpdateState();
        }
        catch (JsonException)
        {
            return new UpdateState();
        }
    }

    public Task SaveAsync(UpdateState state, CancellationToken cancellationToken = default) =>
        AtomicJson.WriteAsync(paths.UpdateStateFile, state, Options, cancellationToken);
}

internal static class AtomicJson
{
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class JsonFileGates
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static SemaphoreSlim For(string path) => Gates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
}
