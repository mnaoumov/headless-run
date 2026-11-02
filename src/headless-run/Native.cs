using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public int Size; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll")] internal static extern nint GetCommandLineW();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)] internal static extern nint CreateJobObject(nint security, nint name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(nint job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(nint file, byte[] buffer, uint length, out uint written, nint overlapped);
}
