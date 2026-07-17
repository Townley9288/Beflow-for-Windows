using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BBDownForWindows.Core;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly ConcurrentDictionary<int, Process> _ownedProcesses = new();

    public async Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || !File.Exists(request.FileName))
            throw new FileNotFoundException("找不到外部工具", request.FileName);

        Directory.CreateDirectory(request.WorkingDirectory);
        if (request.UsePseudoConsole)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("伪终端进程仅支持 Windows");
            return await RunPseudoConsoleAsync(request, onOutput, cancellationToken);
        }
        return await RunRedirectedAsync(request, onOutput, cancellationToken);
    }

    private async Task<ProcessResult> RunRedirectedAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput is not null
            },
            EnableRaisingEvents = true
        };
        foreach (var argument in request.Arguments) process.StartInfo.ArgumentList.Add(argument);

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {request.FileName}");
        _ownedProcesses[process.Id] = process;

        using var registration = cancellationToken.Register(() => TryKill(process));
        var output = new StringBuilder();
        var outputLock = new object();
        void Consume(string line)
        {
            lock (outputLock) output.Append(line);
            onOutput?.Invoke(line);
        }

        try
        {
            if (request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), CancellationToken.None);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            var stdout = ReadLinesAsync(process.StandardOutput.BaseStream, Consume);
            var stderr = ReadLinesAsync(process.StandardError.BaseStream, Consume);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
            return new ProcessResult(process.ExitCode, output.ToString(), cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _ownedProcesses.TryRemove(process.Id, out _);
        }
    }

    private async Task<ProcessResult> RunPseudoConsoleAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var session = PseudoConsoleSession.Start(request);
        var process = session.Process;
        _ownedProcesses[process.Id] = process;
        using var registration = cancellationToken.Register(() => TryKill(process));
        var output = new StringBuilder();
        var outputLock = new object();
        void Consume(string text)
        {
            lock (outputLock) output.Append(text);
            onOutput?.Invoke(text);
        }

        try
        {
            var readTask = Task.Factory.StartNew(
                () => session.ReadOutput(Consume),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (request.StandardInput is not null) await Task.Run(() => session.WriteInput(request.StandardInput));
            await process.WaitForExitAsync();
            session.ClosePseudoConsole();
            await readTask;
            return new ProcessResult(process.ExitCode, output.ToString(), cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _ownedProcesses.TryRemove(process.Id, out _);
        }
    }

    public Task TerminateAllAsync()
    {
        foreach (var process in _ownedProcesses.Values.ToArray()) TryKill(process);
        return Task.CompletedTask;
    }

    private static async Task ReadLinesAsync(Stream stream, Action<string> consume)
    {
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                pending.Write(buffer, start, index - start + 1);
                consume(OutputDecoder.Decode(pending.ToArray()));
                pending.SetLength(0);
                start = index + 1;
            }
            if (start < count) pending.Write(buffer, start, count - start);
        }
        if (pending.Length > 0) consume(OutputDecoder.Decode(pending.ToArray()));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public static class OutputDecoder
{
    static OutputDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] data)
    {
        foreach (var name in new[] { "utf-8", "GB18030", "gbk" })
        {
            try
            {
                var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return encoding.GetString(data);
            }
            catch (DecoderFallbackException) { }
        }
        return Encoding.UTF8.GetString(data);
    }
}
