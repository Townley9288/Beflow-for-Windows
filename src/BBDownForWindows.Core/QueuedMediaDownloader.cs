using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BBDownForWindows.Core;

/// <summary>Queue protocol 2 holds the downloader at durable stage boundaries.</summary>
public sealed class QueuedMediaDownloader(ApplicationPaths paths, IProcessRunner runner, IToolLocator locator,
    ParseConcurrencyLimiter limiter)
{
    private readonly BBDownRuntimeManager runtime = new(paths);
    public async Task DownloadAsync(DownloadRequest options, EpisodeStreamSelection desired, string expectedCid,
        QueueSourceCheckpoint checkpoint, Func<Task> save, IProgress<ExactDownloadProgress> progress,
        TaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (checkpoint.Completed)
        {
            foreach (var file in checkpoint.Files) file.Verify();
            return;
        }
        if (string.IsNullOrWhiteSpace(checkpoint.Directory)) throw new InvalidOperationException("队列下载工作目录未保存");
        Directory.CreateDirectory(checkpoint.Directory);
        var tools = locator.Locate(new AppSettings { Aria2cPath = options.Aria2cPath });
        var executable = runtime.PrepareExecutable(tools.BBDown);
        var outputStem = Path.Combine(checkpoint.Directory, "output");
        if (checkpoint.OutputStem.Length > 0 && checkpoint.OutputStem != outputStem) throw new InvalidDataException("队列临时输出路径已变化");
        checkpoint.OutputStem = outputStem;
        // Only these known, private outputs can belong to an interrupted mux attempt.
        foreach (var extension in new[] { ".mp4", ".m4a" })
        {
            var partialMux = outputStem + extension;
            if (File.Exists(partialMux))
            {
                if (checkpoint.Stage != "muxing") throw new IOException($"存在未确认来源的输出：{partialMux}");
                File.Delete(partialMux);
            }
        }
        await save();
        var request = QueueSnapshot.Copy(options);
        request.Pages = desired.PageNumber.ToString(); request.Season = false;
        request.WorkDirectory = checkpoint.Directory; request.MultiFilePattern = string.Empty;
        var args = BBDownCommandBuilder.BuildExactDownloadArguments(request, tools);
        args.Add("--beflow-queue");
        var name = "Beflow-queue-" + Guid.NewGuid().ToString("N");
        args.AddRange(["--beflow-pipe", name]);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IDisposable? permit = await limiter.EnterAsync(cancellationToken);
        Aria2ProgressParser? aria = null;
        BBDownInternalProgressParser? internalProgress = null;
        var outputGate = new object();
        Task<ProcessResult>? process = null;
        var exchange = ExchangeAsync();
        try
        {
            process = runner.RunAsync(new ProcessRunRequest(executable, args, checkpoint.Directory, UsePseudoConsole: !request.UseAria2c), line =>
            {
                lock (outputGate)
                {
                    if (aria is not null && internalProgress is not null)
                    {
                        var parsed = request.UseAria2c ? aria.TryConsume(line, out var p) : internalProgress.TryConsume(line, out p);
                        if (parsed) progress.Report(new(DownloadProgressPhase.Downloading, p.Percent, p.Speed, p.Eta, "正在下载"));
                    }
                }
                context.AppendLog(BBDownService.SanitizeDiagnosticOutput(line));
            }, lifetime.Token);
            if (await Task.WhenAny(process, exchange) == process && !exchange.IsCompleted)
            {
                var early = await process;
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException(BBDownService.BuildProcessFailureMessage("队列下载进程提前结束", early));
            }
            await exchange;
            var result = await process;
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !checkpoint.Completed) throw new IOException(BBDownService.BuildProcessFailureMessage("队列下载未完成", result));
        }
        finally
        {
            permit?.Dispose(); lifetime.Cancel(); pipe.Dispose();
            try { await exchange; } catch { }
            if (process is not null) try { await process; } catch { }
        }

        async Task ExchangeAsync()
        {
            await pipe.WaitForConnectionAsync(lifetime.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var first = await ReadAsync();
            using (first)
            {
                var root = first.RootElement;
                var parser = new StreamingDownloadParser(DownloadParseMode.Current, null);
                foreach (var line in root.GetProperty("Output").GetString()!.Split('\n')) parser.Consume(line + "\n");
                parser.Complete();
                var current = parser.Episodes.Single();
                if (current.Page.Number != desired.PageNumber || string.IsNullOrWhiteSpace(expectedCid) || current.Page.Cid != expectedCid)
                    throw new InvalidOperationException("分集身份已变化，请重新解析后选择");
                if (current.IsMuxedStream != desired.IsMuxedStream) throw new InvalidOperationException("所选音视频规格已变化");
                var strict = QueueSnapshot.Copy(desired);
                if (strict.Video is not null) strict.Video = strict.Video with { IsManual = true };
                if (strict.Audio is not null) strict.Audio = strict.Audio with { IsManual = true };
                var choice = StreamSelectionPolicy.Resolve(current, strict, request);
                lock (outputGate)
                {
                    var mode = current.IsMuxedStream ? DownloadMode.VideoOnly : request.DownloadMode;
                    aria = new(choice.Video?.EstimatedSizeBytes ?? 0, choice.Audio?.EstimatedSizeBytes ?? 0, mode);
                    internalProgress = new(choice.Video?.EstimatedSizeBytes ?? 0, choice.Audio?.EstimatedSizeBytes ?? 0, mode);
                }
                var ariaArgs = string.Empty;
                if (request.UseAria2c)
                {
                    Aria2TuningPolicy.Apply(request, Math.Max(choice.Video?.EstimatedSizeBytes ?? 0, choice.Audio?.EstimatedSizeBytes ?? 0));
                    var tuned = BBDownCommandBuilder.BuildExactDownloadArguments(request, tools);
                    ariaArgs = tuned[tuned.IndexOf("--aria2c-args") + 1] + " --continue=true --always-resume=true --allow-overwrite=false --auto-save-interval=1";
                }
                checkpoint.Stage = "downloading";
                await save();
                permit?.Dispose(); permit = null;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new DownloadPreparationReply(2, choice.Video?.Index ?? -1, choice.Audio?.Index ?? -1, outputStem, ariaArgs)).AsMemory(), lifetime.Token);
            }
            while (true)
            {
                using var message = await ReadAsync();
                var root = message.RootElement;
                var stage = root.GetProperty("Stage").GetString()!;
                if (stage is not ("video" or "audio" or "muxing" or "muxed")) throw new InvalidDataException("未知下载阶段");
                checkpoint.Stage = stage;
                if (stage == "muxing") progress.Report(new(DownloadProgressPhase.Muxing, null, string.Empty, string.Empty, "正在封装"));
                if (stage == "muxed")
                {
                    var output = Path.GetFullPath(root.GetProperty("Path").GetString()!, checkpoint.Directory);
                    if (output != outputStem + ".mp4" && output != outputStem + ".m4a") throw new InvalidDataException("下载输出路径不属于本任务");
                    checkpoint.Files = [QueueFileStamp.Read(output)];
                    // Include produced subtitles and covers, but never internal source fragments.
                    checkpoint.Files.AddRange(Directory.EnumerateFiles(checkpoint.Directory, "output.*")
                        .Where(p => p != output && Path.GetExtension(p) is ".srt" or ".ass" or ".jpg" or ".png" or ".xml")
                        .Select(QueueFileStamp.Read));
                    checkpoint.Completed = true;
                }
                await save();
                await writer.WriteLineAsync("{\"Protocol\":2,\"Ack\":true}".AsMemory(), lifetime.Token);
                if (stage == "muxed") break;
            }
            async Task<JsonDocument> ReadAsync()
            {
                var line = await reader.ReadLineAsync(lifetime.Token) ?? throw new IOException("队列阶段连接已关闭");
                var message = JsonDocument.Parse(line);
                if (message.RootElement.GetProperty("Protocol").GetInt32() != 2) { message.Dispose(); throw new InvalidDataException("队列下载协议版本不匹配"); }
                return message;
            }
        }
    }
}
