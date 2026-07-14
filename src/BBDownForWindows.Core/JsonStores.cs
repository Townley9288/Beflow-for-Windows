using System.Text.Json;

namespace BBDownForWindows.Core;

public sealed class SettingsStore(ApplicationPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
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

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        AtomicJson.WriteAsync(paths.SettingsFile, settings, Options, cancellationToken);

    internal static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class HistoryStore(ApplicationPaths paths) : IHistoryStore
{
    private static readonly JsonSerializerOptions Options = SettingsStore.CreateOptions();

    public async Task<IReadOnlyList<HistoryRecord>> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task AddAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        var history = (await LoadAsync(cancellationToken)).ToList();
        history.Insert(0, record);
        await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
    }

    public Task SaveAllAsync(IReadOnlyList<HistoryRecord> records, CancellationToken cancellationToken = default) =>
        AtomicJson.WriteAsync(paths.HistoryFile, records, Options, cancellationToken);

    public async Task DeleteAsync(int index, CancellationToken cancellationToken = default)
    {
        var history = (await LoadAsync(cancellationToken)).ToList();
        if (index >= 0 && index < history.Count)
        {
            history.RemoveAt(index);
            await AtomicJson.WriteAsync(paths.HistoryFile, history, Options, cancellationToken);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        AtomicJson.WriteAsync(paths.HistoryFile, Array.Empty<HistoryRecord>(), Options, cancellationToken);
}

internal static class AtomicJson
{
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, true);
    }
}
