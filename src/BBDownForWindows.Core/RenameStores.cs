using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class RenameSettingsStore(ApplicationPaths paths) : IRenameSettingsStore
{
    private static readonly JsonSerializerOptions Options = SettingsStore.CreateOptions();
    private readonly SemaphoreSlim _gate = JsonFileGates.For(paths.RenameSettingsFile);

    public async Task<RenameSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(RenameSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = settings.Clone();
            snapshot.EnsureDefaults();
            await AtomicJson.WriteAsync(paths.RenameSettingsFile, snapshot, Options, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<RenameSettings> UpdateAsync(Func<RenameSettings, RenameSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = update(await LoadCoreAsync(cancellationToken)) ?? throw new InvalidOperationException("重命名设置更新不能返回空值");
            settings.EnsureDefaults();
            await AtomicJson.WriteAsync(paths.RenameSettingsFile, settings, Options, cancellationToken);
            return settings.Clone();
        }
        finally { _gate.Release(); }
    }

    private async Task<RenameSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.RenameSettingsFile))
        {
            var defaults = new RenameSettings();
            defaults.EnsureDefaults();
            return defaults;
        }
        try
        {
            await using var stream = File.OpenRead(paths.RenameSettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<RenameSettings>(stream, Options, cancellationToken) ?? new RenameSettings();
            settings.EnsureDefaults();
            return settings;
        }
        catch (JsonException)
        {
            var defaults = new RenameSettings();
            defaults.EnsureDefaults();
            return defaults;
        }
    }
}

public sealed class RenameHistoryStore(ApplicationPaths paths) : IRenameHistoryStore
{
    private static readonly JsonSerializerOptions Options = SettingsStore.CreateOptions();
    private readonly SemaphoreSlim _gate = JsonFileGates.For(paths.RenameHistoryFile);
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<RenameHistoryRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(RenameHistoryRecord record, CancellationToken cancellationToken = default)
    {
        await MutateAsync(records =>
        {
            if (record.Id == Guid.Empty) record.Id = Guid.NewGuid();
            records.Insert(0, record);
            return true;
        }, cancellationToken);
    }

    public async Task MarkUndoneAsync(Guid id, DateTimeOffset undoneAt, CancellationToken cancellationToken = default)
    {
        await MutateAsync(records =>
        {
            var record = records.FirstOrDefault(item => item.Id == id);
            if (record is null || record.UndoneAt is not null) return false;
            record.UndoneAt = undoneAt;
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(records => records.RemoveAll(record => record.Id == id) > 0, cancellationToken);

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await AtomicJson.WriteAsync(paths.RenameHistoryFile, Array.Empty<RenameHistoryRecord>(), Options, cancellationToken); }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task MutateAsync(Func<List<RenameHistoryRecord>, bool> update, CancellationToken cancellationToken)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await LoadCoreAsync(cancellationToken);
            changed = update(records);
            if (changed) await AtomicJson.WriteAsync(paths.RenameHistoryFile, records, Options, cancellationToken);
        }
        finally { _gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<RenameHistoryRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.RenameHistoryFile)) return [];
        try
        {
            await using var stream = File.OpenRead(paths.RenameHistoryFile);
            return await JsonSerializer.DeserializeAsync<List<RenameHistoryRecord>>(stream, Options, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
    }
}
