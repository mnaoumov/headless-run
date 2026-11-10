using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

internal static class Native
{
    private const string Kernel32Library = "kernel32.dll";
    internal const int HandleListAttributeCount = 1;
    internal const int JobListAttributeCount = 1;
    internal const int StandardInputHandle = -10;
    internal const int StandardOutputHandle = -11;
    internal const int StandardErrorHandle = -12;
    internal const uint JobObjectLimitKillOnJobClose = 0x2000;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const nuint ProcessThreadAttributeHandleList = 0x20002;
    internal const nuint ProcessThreadAttributeJobList = 0x2000D;
    internal const uint StartUsingStandardHandles = 0x100;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x400;
    internal const uint IdlePriorityClass = 0x40;
    internal const uint BelowNormalPriorityClass = 0x4000;
    internal const uint NormalPriorityClass = 0x20;
    internal const uint AboveNormalPriorityClass = 0x8000;
    internal const uint HighPriorityClass = 0x80;
    internal const uint RealtimePriorityClass = 0x100;
    internal const uint WaitTimeout = 0x102;
    internal const uint WaitObjectSignaled = 0;
    internal const uint InfiniteWait = uint.MaxValue;
    internal const uint DuplicateSameAccess = 2;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileAppendData = 4;
    internal const uint FileShareRead = 1;
    internal const uint FileShareWrite = 2;
    internal const uint FileShareDelete = 4;
    internal const uint OpenExisting = 3;
    internal const uint OpenAlways = 4;
    internal const uint CreateAlways = 2;
    internal const uint FileAttributeNormal = 0x80;
    private const string Advapi32Library = "advapi32.dll";
    private const string User32Library = "user32.dll";
    internal const uint WaitObjectSecondSignaled = 1;
    internal const uint Synchronize = 0x00100000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevationClass = 20;
    internal const int UserObjectFlagsIndex = 1;
    internal const uint WindowStationVisible = 1;
    internal const int ErrorCancelled = 1223;

    [StructLayout(LayoutKind.Sequential)]
    internal struct UserObjectFlags
    {
        public int Inherit;
        public int Reserved;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public int Inherit;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public nint ReservedBytes;
        public nint Input;
        public nint Output;
        public nint Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority;
        public uint Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory;
        public nuint JobMemory;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [DllImport(Kernel32Library)]

    internal static extern nint GetCommandLineW();
    [DllImport(Kernel32Library, SetLastError = true)]
    internal static extern nint GetStdHandle(int kind);
    [DllImport(Kernel32Library)]
    internal static extern nint GetCurrentProcess();
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport(Kernel32Library, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
    [DllImport(Kernel32Library, EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static extern nint CreateJobObject(nint security, nint name);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(nint job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(nint job, uint code);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport(Kernel32Library)]
    internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport(Kernel32Library, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport(Kernel32Library, SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport(Kernel32Library, SetLastError = true)]
    internal static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
    [DllImport(Kernel32Library, SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetStdHandle(int kind, nint handle);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
    [DllImport(Advapi32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport(Advapi32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(nint token, int kind, out uint value, uint size, out uint returnedSize);
    [DllImport(User32Library, SetLastError = true)]
    internal static extern nint GetProcessWindowStation();
    [DllImport(User32Library, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(nint handle, int kind, out UserObjectFlags flags, uint size, out uint returnedSize);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(nint file, byte[] buffer, uint length, out uint written, nint overlapped);
}
