using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BBDownForWindows.Core;

internal sealed class WindowsChildProcess : IDisposable
{
    internal const int PseudoConsoleAttribute = 0x00020016;
    private const int JobListAttribute = 0x0002000D;
    private const int HandleListAttribute = 0x00020002;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly object _lifetime = new();
    private bool _stopped;

    private WindowsChildProcess(SafeFileHandle job, SafeProcessHandle process)
    {
        _job = job;
        _process = process;
    }

    public static WindowsChildProcess Start(ProcessRunRequest request, nint pseudoConsole = 0,
        SafeFileHandle? input = null, SafeFileHandle? output = null, SafeFileHandle? error = null)
    {
        var job = CreateJobObjectW(0, null);
        SafeProcessHandle? process = null;
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建工具进程作业");
            var information = new JobExtendedLimits { Basic = new JobBasicLimits { Flags = 0x00002000 } };
            if (!SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<JobExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置工具进程作业");

            using var attributes = new ProcessAttributes();
            attributes.AddHandles(JobListAttribute, [job.DangerousGetHandle()]);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles },
                Attributes = attributes.Pointer
            };
            if (pseudoConsole != 0)
            {
                attributes.Add(PseudoConsoleAttribute, pseudoConsole, IntPtr.Size);
            }
            else
            {
                startup.Startup.Input = input!.DangerousGetHandle();
                startup.Startup.Output = output!.DangerousGetHandle();
                startup.Startup.Error = error!.DangerousGetHandle();
                attributes.AddHandles(HandleListAttribute, [startup.Startup.Input, startup.Startup.Output, startup.Startup.Error]);
            }

            var executable = Path.GetFullPath(request.FileName);
            var commandLine = new StringBuilder(string.Join(' ', new[] { Quote(executable) }.Concat(request.Arguments.Select(Quote))));
            var flags = ExtendedStartupInfoPresent | (pseudoConsole == 0 ? CreateNoWindow : 0);
            if (!CreateProcessW(executable, commandLine, 0, 0, pseudoConsole == 0, flags, 0, request.WorkingDirectory, ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法启动 {executable}");

            using var thread = new SafeFileHandle(child.Thread, ownsHandle: true);
            process = new SafeProcessHandle(child.Process, ownsHandle: true);
            return new WindowsChildProcess(job, process);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            throw;
        }
    }

    public async Task<int> WaitForExitAsync()
    {
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        signal.SafeWaitHandle = new SafeWaitHandle(_process.DangerousGetHandle(), ownsHandle: false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try { await completion.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
        if (!GetExitCodeProcess(_process, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)exitCode);
    }

    public void StopTree() => CloseJob(terminate: true);

    public void CloseJob() => CloseJob(terminate: false);

    private void CloseJob(bool terminate)
    {
        lock (_lifetime)
        {
            if (_stopped) return;
            _stopped = true;
            if (terminate) TerminateJobObject(_job, 1);
            _job.Dispose();
        }
    }

    public void Dispose()
    {
        StopTree();
        _process.Dispose();
    }

    internal static (SafeFileHandle Read, SafeFileHandle Write) CreateRedirectedPipe(bool inheritRead)
    {
        var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建进程管道");
        if (SetHandleInformation(inheritRead ? write : read, 1, 0)) return (read, write);
        var error = Marshal.GetLastWin32Error();
        read.Dispose();
        write.Dispose();
        throw new Win32Exception(error, "无法限制进程管道继承");
    }

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

    private sealed class ProcessAttributes : IDisposable
    {
        private readonly List<nint> _values = [];
        public nint Pointer { get; }

        public ProcessAttributes()
        {
            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Pointer = Marshal.AllocHGlobal(size);
            if (InitializeProcThreadAttributeList(Pointer, 2, 0, ref size)) return;
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(Pointer);
            throw new Win32Exception(error, "无法初始化进程属性列表");
        }

        public void AddHandles(int attribute, nint[] handles)
        {
            var size = handles.Length * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            _values.Add(buffer);
            Marshal.Copy(handles, 0, buffer, handles.Length);
            Add(attribute, buffer, size);
        }

        public void Add(int attribute, nint value, int size)
        {
            if (!UpdateProcThreadAttribute(Pointer, 0, attribute, value, size, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置工具进程属性");
        }

        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            foreach (var value in _values) Marshal.FreeHGlobal(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint Flags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public nint ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimits information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint creationFlags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value,
        nint size, nint previousValue, nint returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
}
