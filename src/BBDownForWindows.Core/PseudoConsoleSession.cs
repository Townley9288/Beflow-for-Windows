using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BBDownForWindows.Core;

internal sealed class PseudoConsoleSession : IDisposable
{
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly int _columns;
    private readonly int _rows;
    private readonly SafeFileHandle _inputReadSide;
    private readonly SafeFileHandle _outputWriteSide;
    private IntPtr _pseudoConsole;
    private bool _disposed;

    private PseudoConsoleSession(WindowsChildProcess process, SafeFileHandle input, SafeFileHandle output, IntPtr pseudoConsole,
        SafeFileHandle inputReadSide, SafeFileHandle outputWriteSide, int columns, int rows)
    {
        Process = process;
        _input = new FileStream(input, FileAccess.Write, 4096, false);
        _output = new FileStream(output, FileAccess.Read, 4096, false);
        _pseudoConsole = pseudoConsole;
        _inputReadSide = inputReadSide;
        _outputWriteSide = outputWriteSide;
        _columns = columns;
        _rows = rows;
    }

    public WindowsChildProcess Process { get; }

    public static PseudoConsoleSession Start(ProcessRunRequest request)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        WindowsChildProcess? process = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建伪终端管道");

            var size = new Coord((short)Math.Clamp(request.PseudoConsoleColumns, 40, short.MaxValue),
                (short)Math.Clamp(request.PseudoConsoleRows, 10, short.MaxValue));
            var result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (result < 0) Marshal.ThrowExceptionForHR(result);

            process = WindowsChildProcess.Start(request, pseudoConsole);
            var session = new PseudoConsoleSession(process, inputWrite, outputRead, pseudoConsole,
                inputRead, outputWrite, size.X, size.Y);
            process = null;
            pseudoConsole = IntPtr.Zero;
            inputRead = null;
            inputWrite = null;
            outputRead = null;
            outputWrite = null;
            return session;
        }
        finally
        {
            process?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
        }
    }

    public void WriteInput(string input)
    {
        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r", StringComparison.Ordinal);
        var data = Encoding.UTF8.GetBytes(normalized);
        _input.Write(data);
        _input.Flush();
    }

    public void ReadOutput(Action<string> consume)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var processor = new PseudoConsoleOutputProcessor(_columns, _rows);
        ProcessOutputBuffer? rawOutput = new();
        var buffer = new byte[4096];
        var characters = new char[8192];
        while (true)
        {
            var count = _output.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            decoder.Convert(buffer, 0, count, characters, 0, characters.Length, false,
                out _, out var charactersUsed, out _);
            if (charactersUsed == 0) continue;
            var decoded = new string(characters, 0, charactersUsed);
            rawOutput?.Append(decoded);
            foreach (var item in processor.Feed(decoded))
            {
                consume(item);
                rawOutput = null;
            }
        }
        decoder.Convert([], 0, 0, characters, 0, characters.Length, true,
            out _, out var remainingCharacters, out _);
        if (remainingCharacters > 0)
            foreach (var item in processor.Feed(new string(characters, 0, remainingCharacters)))
            {
                consume(item);
                rawOutput = null;
            }
        foreach (var item in processor.Complete())
        {
            consume(item);
            rawOutput = null;
        }
        if (rawOutput is not null)
        {
            var plain = Regex.Replace(rawOutput.ToString(), "\\x1B(?:\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\[[0-?]*[ -/]*[@-~])", string.Empty);
            if (!string.IsNullOrWhiteSpace(plain)) consume(plain);
        }
    }

    public void ClosePseudoConsole()
    {
        if (_pseudoConsole == IntPtr.Zero) return;
        ClosePseudoConsole(_pseudoConsole);
        _pseudoConsole = IntPtr.Zero;
        _inputReadSide.Dispose();
        _outputWriteSide.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClosePseudoConsole();
        _input.Dispose();
        _output.Dispose();
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
}
