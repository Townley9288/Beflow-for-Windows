using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BBDownForWindows.Core;

public sealed class ProcessRunner : IProcessRunner
{
    private const int CompleteOutputLimit = 4 * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, Action> _ownedProcesses = new();

    public async Task<ProcessResult> RunAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || !File.Exists(request.FileName))
            throw new FileNotFoundException("找不到外部工具", request.FileName);

        Directory.CreateDirectory(request.WorkingDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
            return request.UsePseudoConsole
                ? await RunPseudoConsoleAsync(request, onOutput, cancellationToken)
                : await RunWindowsRedirectedAsync(request, onOutput, cancellationToken);
        if (request.UsePseudoConsole) throw new PlatformNotSupportedException("伪终端进程仅支持 Windows");
        return await RunRedirectedAsync(request, onOutput, cancellationToken);
    }

    private async Task<ProcessResult> RunRedirectedAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {request.FileName}");
        return await CaptureAsync(() => TryKill(process), async (consume, overflow) =>
        {
            var stdout = ReadLinesAsync(process.StandardOutput.BaseStream, consume, overflow);
            var stderr = ReadLinesAsync(process.StandardError.BaseStream, consume, overflow);
            var stdin = WriteInputAsync(process.StandardInput.BaseStream, request.StandardInput);
            await Task.WhenAll(stdout, stderr, stdin, process.WaitForExitAsync());
            return process.ExitCode;
        }, request.RequireCompleteOutput, onOutput, cancellationToken);
    }

    private async Task<ProcessResult> RunWindowsRedirectedAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var stdin = WindowsChildProcess.CreateRedirectedPipe(inheritRead: true);
        using var childInput = stdin.Read;
        using var parentInput = stdin.Write;
        var stdout = WindowsChildProcess.CreateRedirectedPipe(inheritRead: false);
        using var childOutput = stdout.Write;
        using var parentOutput = stdout.Read;
        var stderr = WindowsChildProcess.CreateRedirectedPipe(inheritRead: false);
        using var childError = stderr.Write;
        using var parentError = stderr.Read;
        using var input = new FileStream(parentInput, FileAccess.Write);
        using var output = new FileStream(parentOutput, FileAccess.Read);
        using var error = new FileStream(parentError, FileAccess.Read);
        using var process = WindowsChildProcess.Start(request, input: childInput, output: childOutput, error: childError);
        childInput.Dispose();
        childOutput.Dispose();
        childError.Dispose();

        return await CaptureAsync(process.StopTree, async (consume, overflow) =>
        {
            var readOutput = Task.Run(() => ReadLinesAsync(output, consume, overflow));
            var readError = Task.Run(() => ReadLinesAsync(error, consume, overflow));
            var writeInput = Task.Run(() => WriteInputAsync(input, request.StandardInput));
            var exitCode = await process.WaitForExitAsync();
            process.CloseJob();
            await Task.WhenAll(readOutput, readError, writeInput);
            return exitCode;
        }, request.RequireCompleteOutput, onOutput, cancellationToken);
    }

    private async Task<ProcessResult> RunPseudoConsoleAsync(ProcessRunRequest request, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        using var session = PseudoConsoleSession.Start(request);
        var process = session.Process;
        return await CaptureAsync(process.StopTree, async (consume, overflow) =>
        {
            var read = Task.Factory.StartNew(() => session.ReadOutput(consume), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var write = request.StandardInput is { } input ? Task.Run(() =>
            {
                try { session.WriteInput(input); }
                catch (IOException) { }
            }) : Task.CompletedTask;
            var exitCode = await process.WaitForExitAsync();
            process.CloseJob();
            session.ClosePseudoConsole();
            await Task.WhenAll(read, write);
            return exitCode;
        }, request.RequireCompleteOutput, onOutput, cancellationToken);
    }

    private async Task<ProcessResult> CaptureAsync(Action stop, Func<Action<string>, Action?, Task<int>> run,
        bool requireCompleteOutput, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _ownedProcesses[id] = stop;
        using var registration = cancellationToken.Register(stop);
        var complete = requireCompleteOutput ? new StringBuilder() : null;
        var tail = requireCompleteOutput ? null : new ProcessOutputBuffer();
        var outputLock = new object();
        Exception? observerError = null;
        void Consume(string text)
        {
            lock (outputLock)
            {
                if (observerError is not null) return;
                if (complete is not null)
                {
                    if (text.Length > CompleteOutputLimit - complete.Length)
                    {
                        MarkOverflow();
                        return;
                    }
                    complete.Append(text);
                }
                else tail!.Append(text);
                try { onOutput?.Invoke(text); }
                catch (Exception exception)
                {
                    observerError = exception;
                    stop();
                }
            }
        }

        void MarkOverflow()
        {
            lock (outputLock)
            {
                observerError ??= new InvalidDataException("工具的完整输出超出 4 MiB 捕获上限。");
                stop();
            }
        }

        try
        {
            var code = await run(Consume, requireCompleteOutput ? MarkOverflow : null);
            if (observerError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(observerError);
            return new ProcessResult(code, complete?.ToString() ?? tail!.ToString(), cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _ownedProcesses.TryRemove(id, out _);
            stop();
        }
    }

    public Task TerminateAllAsync()
    {
        foreach (var stop in _ownedProcesses.Values) stop();
        return Task.CompletedTask;
    }

    private static async Task WriteInputAsync(Stream stream, string? input)
    {
        using (stream)
        {
            if (input is null) return;
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(input));
                await stream.FlushAsync();
            }
            catch (IOException)
            {
                // A process may close its input before the write. Report its exit code and diagnostics.
            }
        }
    }

    internal static async Task ReadLinesAsync(Stream stream, Action<string> consume, Action? onOversizedLine = null)
    {
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        var previousWasCarriageReturn = false;
        // Allow up to four UTF-8 bytes per character when capturing structured output.
        var lineLimit = onOversizedLine is null ? ProcessOutputBuffer.Capacity : 4 * CompleteOutputLimit;
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    if (value == (byte)'\n')
                    {
                        start = index + 1;
                        continue;
                    }
                }
                if (value != (byte)'\r' && value != (byte)'\n') continue;
                Append(buffer, start, index - start);
                consume(OutputDecoder.Decode(pending.ToArray()) + "\n");
                pending.SetLength(0);
                start = index + 1;
                previousWasCarriageReturn = value == (byte)'\r';
            }
            if (start < count) Append(buffer, start, count - start);
        }
        if (pending.Length > 0) consume(OutputDecoder.Decode(pending.ToArray()));

        void Append(byte[] bytes, int offset, int count)
        {
            var remaining = lineLimit - (int)pending.Length;
            if (count > remaining) onOversizedLine?.Invoke();
            pending.Write(bytes, offset, Math.Min(count, remaining));
        }
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
