using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BBDownForWindows.Core;

// aria2 wrapper used only by this isolated test; queue pipe replies replace CLI extra args.
if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
{
    var executable = Environment.GetEnvironmentVariable("BEFLOW_SPACE_TEST_ARIA") ?? throw new InvalidOperationException("Missing test aria2 path.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    start.ArgumentList.Add("--max-overall-download-limit=128K");
    using var child = Process.Start(start)!;
    await child.WaitForExitAsync();
    Environment.ExitCode = child.ExitCode;
    return;
}

if (args.Length < 3) throw new ArgumentException("application-root isolated-run-root credential-file [list-only]");
var runRoot = Path.GetFullPath(args[1]);
if (Directory.Exists(runRoot)) throw new IOException("Choose a new, isolated run directory.");
var paths = new ApplicationPaths(args[0], runRoot);
if (paths.Portable) throw new InvalidOperationException("The tool application directory must not contain portable.flag.");
paths.EnsureCreated();
var credential = Path.GetFullPath(args[2]);
var originalHash = SHA256.HashData(File.ReadAllBytes(credential));
File.Copy(credential, paths.WebCredentialFile, false);
var report = new List<object>();
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var runner = new ProcessRunner();
DownloadQueueService? queue = null;
try
{
    using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    var service = new BilibiliSpaceService(http, paths);
    var profile = await service.GetProfileAsync("538596213");
    var watch = Stopwatch.StartNew();
    var uploads = new BilibiliSpaceDirectory<BilibiliSpaceVideo>(v => v.Bvid);
    while (!uploads.Complete) uploads.Append(await service.GetUploadsAsync(profile.Uid, uploads.NextPage));
    report.Add(new { Stage = "uploads", profile.Name, profile.Uid, Count = uploads.Items.Count, uploads.Total, Milliseconds = watch.ElapsedMilliseconds });
    Console.WriteLine($"UPLOADS: {uploads.Items.Count}/{uploads.Total} ({watch.ElapsedMilliseconds} ms)");
    var collections = new BilibiliSpaceDirectory<BilibiliSpaceCollection>(c => $"{c.Kind}:{c.Id}");
    while (!collections.Complete) collections.Append(await service.GetCollectionsAsync(profile.Uid, collections.NextPage));
    foreach (var collection in collections.Items)
    {
        var members = new BilibiliSpaceDirectory<BilibiliSpaceVideo>(v => v.Bvid);
        while (!members.Complete) members.Append(await service.GetCollectionVideosAsync(collection, members.NextPage));
        report.Add(new { Stage = "collection", collection.Id, collection.Title, Count = members.Items.Count, members.Total });
        Console.WriteLine($"COLLECTION: {collection.Title} {members.Items.Count}/{members.Total}");
    }
    var series = new BilibiliSpaceCollection("340933", "23630128", BilibiliSpaceCollectionKind.Series, "系列接口验收", "", "", 0);
    var seriesItems = new BilibiliSpaceDirectory<BilibiliSpaceVideo>(v => v.Bvid);
    while (!seriesItems.Complete) seriesItems.Append(await service.GetCollectionVideosAsync(series, seriesItems.NextPage));
    report.Add(new { Stage = "series", Count = seriesItems.Items.Count, seriesItems.Total });
    if (args.Length > 3 && args[3] == "list-only") return;

    var selected = uploads.Items.Where(v => v.DurationSeconds is > 5 and <= 45).Take(2).ToList();
    if (selected.Count != 2) throw new InvalidOperationException("Need two short videos for bounded download validation.");
    var settings = new SettingsStore(paths);
    var config = new AppSettings { WorkDirectory = Path.Combine(runRoot, "downloads"), VideoQualityRule = "360P 流畅",
        UseAria2c = true, Aria2AutoTune = false, Aria2MaxConnection = 1, Aria2Split = 1, ParseConcurrency = 4 };
    await settings.SaveAsync(config);
    var tools = new ToolLocator(paths);
    Environment.SetEnvironmentVariable("BEFLOW_SPACE_TEST_ARIA", tools.Locate(config).Aria2c);
    config.Aria2cPath = Path.Combine(AppContext.BaseDirectory, "SpaceIntegrationHarness.exe");
    var work = new WorkCoordinator();
    var limiter = new ParseConcurrencyLimiter(settings);
    var manager = new TaskManager(paths, runner, work);
    var downloader = new BBDownService(paths, runner, tools, settings, new BilibiliMetadataService(http), new DownloadNamingService(), limiter);
    var batches = new BilibiliSpaceBatchService(downloader);
    var rule = new StreamSelectionRule(config.VideoQualityRule, "AVC", "auto", AudioBitratePriority.Highest);
    var jobs = new List<DownloadQueueItem>();
    var parse = await manager.RunExclusiveAsync(TaskKind.DownloadParse, true, "space-acceptance", async (context, token) =>
    {
        await foreach (var result in batches.ParseAsync(selected, new Dictionary<string, DownloadCatalog>(), "WEB", context, token))
        {
            if (result.Catalog is null || result.Error.Length > 0) throw new IOException(result.Error);
            var choices = result.Catalog.Episodes.Select(e =>
            {
                if (e.State != DownloadEpisodeParseState.Ready) throw new IOException(e.Error);
                var choice = StreamSelectionPolicy.Select(e, rule, DownloadMode.VideoAndAudio);
                return new EpisodeStreamSelection { PageNumber = e.Page.Number, PageTitle = e.Page.Title,
                    Video = new(choice.Video!.Quality, choice.Video.Resolution, choice.Video.Codec, choice.Video.BitrateKbps),
                    Audio = new(choice.Audio!.Codec, choice.Audio.BitrateKbps), FallbackReason = choice.FallbackReason };
            }).ToList();
            jobs.Add(BilibiliSpaceBatchService.BuildQueueItem(profile, result.Catalog, choices, config, config.WorkDirectory, rule, DownloadMode.VideoAndAudio));
            report.Add(new { Stage = "parsed", result.Video.Bvid, result.Video.Title, Pages = choices.Count });
            Console.WriteLine($"PARSED {result.Video.Bvid}: {choices.Count} pages");
        }
    });
    if (parse.State != TaskState.Completed) throw new IOException(parse.Error);
    // Throttle only this isolated acceptance run so a short video can be paused reliably.
    var throttle = new ProcessRunner();
    var queueManager = new TaskManager(paths, throttle);
    queue = new(new DownloadQueueStore(paths), new DownloadQueueExecutor(new QueuedMediaDownloader(paths, throttle, tools, limiter), throttle, tools, new DownloadNamingService(), new HistoryStore(paths)), queueManager, work);
    await queue.InitializeAsync(); await queue.PauseAsync(); await queue.EnqueueManyAsync(jobs); await queue.ResumeAsync();
    await Until(() => Directory.Exists(config.WorkDirectory) && Directory.EnumerateFiles(config.WorkDirectory, "*.aria2", SearchOption.AllDirectories).Any(), queue, TimeSpan.FromMinutes(2));
    await Task.Delay(1800); await queue.PauseAsync();
    if (queue.Snapshot.Items[0].State != DownloadQueueState.Paused) throw new IOException("First download did not pause.");
    report.Add(new { Stage = "paused", Controls = Directory.GetFiles(config.WorkDirectory, "*.aria2", SearchOption.AllDirectories).Length });
    Console.WriteLine("PAUSED with aria2 control file");
    await queue.ResumeAsync();
    await Until(() => queue.Snapshot.Items.Any(i => i.State == DownloadQueueState.Running), queue, TimeSpan.FromSeconds(15));
    var whileDownloading = await manager.RunExclusiveAsync(TaskKind.DownloadParse, false, "space-concurrent", async (context, token) =>
    {
        await service.GetCollectionsAsync(profile.Uid, 1, token);
        await foreach (var result in batches.ParseAsync(selected.Take(1), new Dictionary<string, DownloadCatalog>(), "WEB", context, token))
            if (result.Catalog is null || result.Error.Length > 0) throw new IOException(result.Error);
    });
    if (whileDownloading.State != TaskState.Completed) throw new IOException(whileDownloading.Error);
    report.Add(new { Stage = "concurrent-directory-and-parse", Completed = true });
    await Until(() => queue.Snapshot.Items.All(i => i.IsTerminal), queue, TimeSpan.FromMinutes(8));
    var finished = queue.Snapshot.Items;
    if (finished.Any(i => i.State != DownloadQueueState.Completed)) throw new IOException(string.Join("; ", finished.SelectMany(i => i.Checkpoint.Units).Select(u => u.Error)));
    foreach (var job in finished)
    foreach (var file in job.Checkpoint.Units.SelectMany(u => u.Files).Where(f => DownloadFileKinds.IsVideoFile(f.Path)))
    {
        file.Verify();
        var probe = await runner.RunAsync(new ProcessRunRequest(tools.Locate(config).Ffprobe, ["-v", "error", "-show_streams", "-of", "json", file.Path], runRoot), null, CancellationToken.None);
        if (probe.ExitCode != 0) throw new IOException(probe.Output);
        using var media = JsonDocument.Parse(probe.Output);
        var streams = media.RootElement.GetProperty("streams").EnumerateArray().ToList();
        if (streams.Count(s => s.GetProperty("codec_type").GetString() == "video") != 1 || streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") != 1) throw new IOException("Expected one video and one audio track.");
        if (!file.Path.StartsWith(BilibiliSpaceBatchService.OutputRoot(config.WorkDirectory, profile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Output escaped the UP directory.");
        report.Add(new { Stage = "completed", file.Path, file.Length, file.Sha256, Video = 1, Audio = 1 });
    }
    Console.WriteLine("SPACE_ACCEPTANCE_PASSED");
}
finally
{
    if (queue is not null) await queue.ShutdownAsync();
    await runner.TerminateAllAsync();
    File.Delete(paths.WebCredentialFile);
    if (!originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(credential)))) throw new IOException("Original credential changed.");
    await File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(report, json));
}

static async Task Until(Func<bool> predicate, DownloadQueueService queue, TimeSpan timeout)
{
    using var cancel = new CancellationTokenSource(timeout);
    while (!predicate())
    {
        if (queue.Error.Length > 0) throw new IOException(queue.Error);
        if (queue.Snapshot.Items.Any(i => i.State is DownloadQueueState.Failed or DownloadQueueState.PartialFailure)) throw new IOException(string.Join("; ", queue.Snapshot.Items.SelectMany(i => i.Checkpoint.Units).Select(u => u.Error)));
        await Task.Delay(100, cancel.Token);
    }
}
