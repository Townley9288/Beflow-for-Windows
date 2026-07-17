using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BBDownForWindows.Core;

internal sealed class PseudoConsoleSession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly int _columns;
    private readonly int _rows;
    private readonly SafeFileHandle _inputReadSide;
    private readonly SafeFileHandle _outputWriteSide;
    private readonly IntPtr _attributeList;
    private readonly IntPtr _nativeProcessHandle;
    private readonly IntPtr _nativeThreadHandle;
    private IntPtr _pseudoConsole;
    private bool _disposed;

    private PseudoConsoleSession(Process process, SafeFileHandle input, SafeFileHandle output, IntPtr pseudoConsole,
        SafeFileHandle inputReadSide, SafeFileHandle outputWriteSide, IntPtr attributeList,
        IntPtr nativeProcessHandle, IntPtr nativeThreadHandle, int columns, int rows)
    {
        Process = process;
        _input = new FileStream(input, FileAccess.Write, 4096, false);
        _output = new FileStream(output, FileAccess.Read, 4096, false);
        _pseudoConsole = pseudoConsole;
        _inputReadSide = inputReadSide;
        _outputWriteSide = outputWriteSide;
        _attributeList = attributeList;
        _nativeProcessHandle = nativeProcessHandle;
        _nativeThreadHandle = nativeThreadHandle;
        _columns = columns;
        _rows = rows;
    }

    public Process Process { get; }

    public static PseudoConsoleSession Start(ProcessRunRequest request)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建伪终端管道");

            var size = new Coord((short)Math.Clamp(request.PseudoConsoleColumns, 40, short.MaxValue),
                (short)Math.Clamp(request.PseudoConsoleRows, 10, short.MaxValue));
            var result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (result < 0) Marshal.ThrowExceptionForHR(result);

            nint attributeSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化进程属性列表");
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法附加伪终端");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = IntPtr.Zero,
                    StdOutput = IntPtr.Zero,
                    StdError = IntPtr.Zero
                },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder(BuildCommandLine(request.FileName, request.Arguments));
            var securityAttributeSize = Marshal.SizeOf<SecurityAttributes>();
            var processAttributes = new SecurityAttributes { Length = securityAttributeSize };
            var threadAttributes = new SecurityAttributes { Length = securityAttributeSize };
            if (!CreateProcessW(null, commandLine, ref processAttributes, ref threadAttributes, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, request.WorkingDirectory,
                    ref startup, out var processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法启动 {request.FileName}");

            var process = Process.GetProcessById((int)processInformation.ProcessId);
            process.EnableRaisingEvents = true;
            var session = new PseudoConsoleSession(process, inputWrite, outputRead, pseudoConsole,
                inputRead, outputWrite, attributeList, processInformation.ProcessHandle,
                processInformation.ThreadHandle, request.PseudoConsoleColumns, request.PseudoConsoleRows);
            pseudoConsole = IntPtr.Zero;
            attributeList = IntPtr.Zero;
            inputRead = null;
            inputWrite = null;
            outputRead = null;
            outputWrite = null;
            return session;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
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
        var rawOutput = new StringBuilder();
        var emitted = 0;
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
            rawOutput.Append(decoded);
            foreach (var item in processor.Feed(decoded))
            {
                consume(item);
                emitted++;
            }
        }
        decoder.Convert([], 0, 0, characters, 0, characters.Length, true,
            out _, out var remainingCharacters, out _);
        if (remainingCharacters > 0)
            foreach (var item in processor.Feed(new string(characters, 0, remainingCharacters)))
            {
                consume(item);
                emitted++;
            }
        foreach (var item in processor.Complete())
        {
            consume(item);
            emitted++;
        }
        if (emitted == 0 && rawOutput.Length > 0)
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
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
        }
        if (_nativeThreadHandle != IntPtr.Zero) CloseHandle(_nativeThreadHandle);
        if (_nativeProcessHandle != IntPtr.Zero) CloseHandle(_nativeProcessHandle);
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine,
        ref SecurityAttributes processAttributes, ref SecurityAttributes threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
