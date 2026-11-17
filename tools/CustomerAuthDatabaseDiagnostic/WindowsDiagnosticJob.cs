using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

// A suspended worker is assigned before it can execute or create an uncontained child.
internal sealed class WindowsDiagnosticJob : IDisposable
{
    private IntPtr job;
    private IntPtr process;
    private readonly AnonymousPipeServerStream output = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream input = new(PipeDirection.Out, HandleInheritability.Inheritable);
    internal StreamReader Output { get; }
    internal int ProcessId { get; private set; }
    internal bool Exited => process != IntPtr.Zero && WaitForSingleObject(process, 0) == 0;
    internal uint ActiveProcesses
    {
        get
        {
            Check(QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero));
            return accounting.ActiveProcesses;
        }
    }
    internal int ExitCode
    {
        get { Check(GetExitCodeProcess(process, out var code)); return unchecked((int)code); }
    }

    internal WindowsDiagnosticJob()
    {
        Output = new StreamReader(output);
        job = CreateJobObjectW(IntPtr.Zero, null);
        Check(job != IntPtr.Zero);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
        try { Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())); }
        catch { Dispose(); throw; }
    }

    internal void Start(string executable, string arguments, string directory, Dictionary<string, string> environment)
    {
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100,
            Input = input.ClientSafePipeHandle.DangerousGetHandle(),
            Output = output.ClientSafePipeHandle.DangerousGetHandle(),
            Error = output.ClientSafePipeHandle.DangerousGetHandle()
        };
        var block = string.Join('\0', environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key + "=" + x.Value)) + "\0\0";
        var pointer = Marshal.StringToHGlobalUni(block);
        ProcessInfo child = default;
        try
        {
            Check(CreateProcessW(executable, new StringBuilder("\"" + executable + "\" " + arguments),
                IntPtr.Zero, IntPtr.Zero, true, 0x4 | 0x400 | 0x08000000, pointer, directory, ref startup, out child));
            process = child.Process;
            ProcessId = child.Id;
            Check(AssignProcessToJobObject(job, process));
            Check(ResumeThread(child.Thread) != uint.MaxValue);
        }
        catch
        {
            if (process != IntPtr.Zero) TerminateProcess(process, 125);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            output.DisposeLocalCopyOfClientHandle();
            input.DisposeLocalCopyOfClientHandle();
            input.Dispose(); // Noninteractive worker receives EOF.
        }
    }

    internal void Stop() { if (job != IntPtr.Zero) Check(TerminateJobObject(job, 124)); }

    internal static bool CurrentProcessIsContained() =>
        OperatingSystem.IsWindows() && IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var contained) && contained;

    public void Dispose()
    {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; } // Also kills orphan descendants on normal exit.
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
        Output.Dispose();
        output.Dispose();
        input.Dispose();
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public short Show, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int type, out Accounting accounting, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
}
