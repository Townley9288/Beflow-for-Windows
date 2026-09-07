using System.Security.Cryptography;
using System.Text.Json;
using BBDownForWindows.Core;

// Each phase runs in a separate process against an explicitly supplied, isolated data directory.
if (args.Length < 5) throw new ArgumentException("phase application-root run-root source-a mkvmerge [source-b] [credential-file]");
var phase = args[0];
var runRoot = Path.GetFullPath(args[2]);
var paths = new ApplicationPaths(args[1], runRoot);
if (paths.Portable) throw new InvalidOperationException("Use a tool-only application directory without portable.flag");
paths.EnsureCreated();
var runner = new ProcessRunner();
var settings = new SettingsStore(paths);
await settings.SaveAsync(new AppSettings { ParseConcurrency = 4, MkvmergePath = args[4] });
var tools = new ToolLocator(paths);
var work = new WorkCoordinator();
var history = new HistoryStore(paths);
var manager = new TaskManager(paths, runner);
var executor = new DownloadQueueExecutor(new QueuedMediaDownloader(paths, runner, tools, new ParseConcurrencyLimiter(settings)),
    runner, tools, new DownloadNamingService(), history);
var queue = new DownloadQueueService(new DownloadQueueStore(paths), executor, manager, work);
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var credential = args.Length > 6 ? Path.GetFullPath(args[6]) : null;
byte[]? credentialHash = null;
if (credential is not null)
{
    credentialHash = SHA256.HashData(File.ReadAllBytes(credential));
    File.Copy(credential, paths.WebCredentialFile, false);
}
try
{
    await queue.InitializeAsync();
    if (phase == "prepare")
    {
        if (queue.Snapshot.Items.Count > 0) throw new InvalidOperationException("Run directory already has a queue");
        await queue.PauseAsync();
        var service = new BBDownService(paths, runner, tools, settings);
        var sourceB = args.Length > 5 ? args[5] : args[3];
        var a = await Parse(args[3]);
        var b = sourceB == args[3] ? a : await Parse(sourceB);
        var selectionA = Select(a);
        var selectionB = Select(b);
        foreach (var mode in new[] { "single", "multi", "aria" })
        {
            var options = Options(args[3], mode, mode);
            await queue.EnqueueAsync(new DownloadQueueItem
            {
                Catalog = a, Download = new DownloadBatchRequest { Options = options, Title = a.Title,
                    TotalPages = a.AllPages.Count, Episodes = [selectionA] }
            });
        }
        foreach (var keep in new[] { true, false })
        {
            var output = keep ? "dual-keep" : "dual-clean";
            await queue.EnqueueAsync(new DownloadQueueItem
            {
                Kind = DownloadQueueKind.DualAudio,
                DualCatalog = new DualAudioCatalog { SourceA = a, SourceB = b, SourceAUrl = args[3], SourceBUrl = sourceB,
                    Pairs = [new DualAudioEpisodePair { PairNumber = 1,
                        SourceA = a.Episodes.Single(e => e.Page.Number == selectionA.PageNumber),
                        SourceB = b.Episodes.Single(e => e.Page.Number == selectionB.PageNumber) }] },
                DualAudio = new DualAudioBatchRequest
                {
                    SourceAUrl = args[3], SourceBUrl = sourceB, SourceATitle = a.Title, SourceBTitle = b.Title,
                    Options = Options(args[3], output, "multi"), WorkDirectory = Path.Combine(runRoot, output),
                    MkvmergePath = args[4], KeepSourceFiles = keep,
                    SourceALabel = "验收来源 A", SourceBLabel = "验收来源 B", SourceALanguage = "zh", SourceBLanguage = "zh",
                    Pairs = [new DualAudioPairSelection { PairNumber = 1, SourceAPageNumber = selectionA.PageNumber,
                        SourceBPageNumber = selectionB.PageNumber, SourceAPageTitle = selectionA.PageTitle,
                        SourceBPageTitle = selectionB.PageTitle, SourceA = selectionA, SourceB = selectionB,
                        MainVideoMode = DualAudioMainVideoMode.SourceA, MainVideoSource = DualAudioSource.A }]
                }
            });
        }
        await Report(new { Phase = phase, a.Title, SourceA = args[3], SourceB = sourceB, SelectionA = selectionA,
            SelectionB = selectionB, Tasks = queue.Snapshot.Items.Count, Paused = queue.Snapshot.Paused });

        async Task<DownloadCatalog> Parse(string url)
        {
            DownloadCatalog? catalog = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var task = await manager.RunExclusiveAsync(TaskKind.DownloadParse, true, "integration-parse", async (context, token) =>
                catalog = await service.ParseDownloadAsync(new DownloadParseRequest(url, DownloadParseMode.Current), null, context, token), timeout.Token);
            if (task.State != TaskState.Completed || catalog is null) throw new IOException(task.Error);
            Console.WriteLine($"PARSED {catalog.Title} ({catalog.Episodes.Count} episodes)");
            return catalog;
        }
    }
    else if (phase == "pause")
    {
        if (!queue.Snapshot.Paused) throw new InvalidOperationException("Restart auto-resumed the queue");
        await queue.ResumeAsync();
        await Until(() => Directory.EnumerateFiles(runRoot, "*.part-*", SearchOption.AllDirectories)
            .Any(p => new FileInfo(p).Length > 256 * 1024), TimeSpan.FromMinutes(2));
        await queue.ShutdownAsync();
        var first = queue.Snapshot.Items.First();
        if (first.State != DownloadQueueState.Paused) throw new IOException($"Pause state is {first.State}: {first.Error}");
        var partials = Directory.EnumerateFiles(runRoot, "*.part-*", SearchOption.AllDirectories)
            .Select(p => new { Path = p, Bytes = new FileInfo(p).Length }).ToArray();
        if (partials.Length == 0) throw new IOException("Pause did not retain a partial transfer");
        await Report(new { Phase = phase, State = first.State, Partials = partials, QueuePaused = queue.Snapshot.Paused });
    }
    else if (phase == "complete")
    {
        if (!queue.Snapshot.Paused) throw new InvalidOperationException("Restart auto-resumed the queue");
        await queue.ResumeAsync();
        await Until(() => queue.Snapshot.Items.All(i => i.IsTerminal), TimeSpan.FromMinutes(12));
        await queue.ShutdownAsync();
        var items = queue.Snapshot.Items;
        if (items.Any(i => i.State != DownloadQueueState.Completed))
            throw new IOException(JsonSerializer.Serialize(items.Select(i => new { i.Title, i.State, i.Error, Errors = i.Checkpoint.Units.Select(u => u.Error) }), json));
        var files = new List<object>();
        foreach (var item in items)
        {
            foreach (var file in item.Checkpoint.Units.SelectMany(u => u.Files))
            {
                file.Verify();
                var probe = await runner.RunAsync(new ProcessRunRequest(tools.Locate(new()).Ffprobe,
                    ["-v", "error", "-show_streams", "-show_format", "-of", "json", file.Path], runRoot), null, CancellationToken.None);
                if (probe.ExitCode != 0) throw new IOException(probe.Output);
                using var media = JsonDocument.Parse(probe.Output);
                var streams = media.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = streams.Count(s => s.GetProperty("codec_type").GetString() == "video");
                var audio = streams.Count(s => s.GetProperty("codec_type").GetString() == "audio");
                if (video != 1 || audio != (item.Kind == DownloadQueueKind.DualAudio ? 2 : 1))
                    throw new IOException($"Unexpected streams: video={video}, audio={audio}");
                files.Add(new { file.Path, file.Length, file.Sha256, VideoStreams = video, AudioStreams = audio });
            }
            if (item.DualAudio is { } dual)
                foreach (var source in item.Checkpoint.Units.SelectMany(u => new[] { u.SourceA, u.SourceB }))
                    foreach (var file in source.Files)
                        if (File.Exists(file.Path) != dual.KeepSourceFiles) throw new IOException("KeepSourceFiles contract failed");
        }
        var parts = Directory.EnumerateFiles(runRoot, "*.part-*", SearchOption.AllDirectories).ToArray();
        if (parts.Length != 0) throw new IOException("Completed queue retained parts: " + string.Join(", ", parts));
        if ((await history.LoadAsync()).Count != items.Count) throw new IOException("History was duplicated or lost after restart");
        await Report(new { Phase = phase, Completed = items.Count, Files = files, RemainingParts = parts.Length, HistoryRecords = items.Count });
    }
    else throw new ArgumentException("Unknown phase");
}
finally
{
    try { await queue.ShutdownAsync(); }
    finally
    {
        await runner.TerminateAllAsync();
        if (credential is not null)
        {
            File.Delete(paths.WebCredentialFile);
            if (!SHA256.HashData(File.ReadAllBytes(credential)).SequenceEqual(credentialHash!))
                throw new IOException("Original credential changed during isolated verification");
        }
    }
}

DownloadRequest Options(string url, string output, string mode) => new()
{
    Url = url, WorkDirectory = Path.Combine(runRoot, output), MultiThread = mode == "multi", UseAria2c = mode == "aria",
    Aria2AutoTune = false, Aria2MaxConnection = 4, Aria2Split = 4, Aria2MinSplitSize = 1, SaveTaskLogs = true
};
EpisodeStreamSelection Select(DownloadCatalog catalog)
{
    var episode = catalog.Episodes.First(e => e.State == DownloadEpisodeParseState.Ready);
    if (episode.IsMuxedStream) throw new InvalidOperationException("This validation requires DASH video and audio streams");
    var video = episode.VideoStreams.OrderBy(v => (long)v.Width * v.Height).ThenBy(v => v.BitrateKbps).First();
    var audio = episode.AudioStreams.OrderBy(a => a.BitrateKbps).First();
    return new EpisodeStreamSelection { PageNumber = episode.Page.Number, PageTitle = episode.Page.Title,
        Video = new(video.Quality, video.Resolution, video.Codec, video.BitrateKbps, true),
        Audio = new(audio.Codec, audio.BitrateKbps, true) };
}
async Task Until(Func<bool> done, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!done())
    {
        if (queue.Error.Length > 0) throw new IOException(queue.Error);
        if (queue.Snapshot.Items.Any(i => i.State is DownloadQueueState.Failed or DownloadQueueState.PartialFailure))
            throw new IOException(JsonSerializer.Serialize(queue.Snapshot.Items.Select(i => new { i.State, i.Error, Errors = i.Checkpoint.Units.Select(u => u.Error) }), json));
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Queue integration timed out");
        await Task.Delay(20);
    }
}
async Task Report(object result)
{
    var content = JsonSerializer.Serialize(result, json);
    await File.WriteAllTextAsync(Path.Combine(runRoot, phase + "-results.json"), content);
    Console.WriteLine(content);
}
