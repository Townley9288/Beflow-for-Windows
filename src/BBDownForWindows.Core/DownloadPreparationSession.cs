using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BBDownForWindows.Core;

internal sealed record DownloadPreparationReply(int Protocol, int VideoIndex, int AudioIndex, string RelativeOutputPath, string Aria2Arguments);

/// <summary>Exchanges the live catalog and final selection with the same BBDown process that downloads it.</summary>
internal static class DownloadPreparationSession
{
    public static async Task<ProcessResult> RunAsync(
        Func<string, CancellationToken, Task<ProcessResult>> startProcess,
        Func<DownloadEpisodeInfo, DownloadPreparationReply> prepare,
        CancellationToken cancellationToken)
    {
        var name = $"Beflow-download-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exchange = ExchangeAsync();
        Task<ProcessResult>? process = null;
        try
        {
            process = startProcess(name, lifetime.Token);
            if (await Task.WhenAny(process, exchange) == process && !exchange.IsCompleted)
            {
                var early = await process;
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(BBDownService.BuildProcessFailureMessage("下载准备失败，未收到分集规格", early));
            }
            await exchange;
            return await process;
        }
        finally
        {
            // End only this episode's process if selection fails or the caller cancels.
            lifetime.Cancel();
            pipe.Dispose();
            try { await exchange; } catch { /* The original failure is propagated above. */ }
            if (process is not null)
                try { await process; } catch { /* Observe cleanup without replacing the original failure. */ }
        }

        async Task ExchangeAsync()
        {
            await pipe.WaitForConnectionAsync(lifetime.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(lifetime.Token)
                ?? throw new InvalidOperationException("下载准备连接提前关闭");
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (root.GetProperty("Protocol").GetInt32() != 1) throw new InvalidOperationException("BBDown 下载准备协议版本不匹配");
            var parser = new StreamingDownloadParser(DownloadParseMode.Current, null);
            foreach (var item in root.GetProperty("Output").GetString()!.Split('\n')) parser.Consume(item + "\n");
            parser.Complete();
            var episode = parser.Episodes.Single();
            if (episode.Page.Number != root.GetProperty("Page").GetInt32()) throw new InvalidOperationException("下载准备返回的分集信息不一致");
            lifetime.Token.ThrowIfCancellationRequested();
            var reply = prepare(episode);
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), lifetime.Token);
        }
    }
}
