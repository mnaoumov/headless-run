using System.Globalization;
using System.Runtime.InteropServices;

namespace HeadlessRun;

// --tray, --tray=true, --tray=false and --tray=auto, the default.
internal enum TrayMode
{
    Auto,
    On,
    Off
}

// --tray=auto: the icon is shown when a person started the run on their desktop, and not under
// Task Scheduler, a service, session 0 or --detach, so scheduled callers need no flag to stay quiet.
internal static unsafe class TrayAuto
{
    // Test seam: the pid taken for the Task Scheduler service's process; 0 means none is running.
    internal const string SchedulerProcessVariable = "HEADLESS_RUN_TEST_SCHEDULER_PID";
    // Test seam: report the tray decision and its reason on stderr.
    internal const string ReportVariable = "HEADLESS_RUN_TEST_TRAY_REPORT";
    private const string SchedulerServiceName = "Schedule";
    private const string Kernel32Library = "kernel32.dll";
    private const string Advapi32Library = "advapi32.dll";
    private const uint ServiceManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int StatusProcessInformation = 0;
    private const uint SnapshotProcesses = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ExecutableNameCharacters = 260;
    // A parent chain longer than this is a loop of reused pids, not a real ancestry.
    private const int MaximumAncestors = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExecutableFile[ExecutableNameCharacters];
    }

    // Whether this launcher shows the icon. Called once, by the launcher that would host it.
    internal static bool Resolve(Options options)
    {
        var (show, reason) = options.TrayMode switch
        {
            TrayMode.On => (true, "--tray"),
            TrayMode.Off => (false, "--tray=false"),
            _ => Decide(options)
        };
        if (Environment.GetEnvironmentVariable(ReportVariable) == "1")
        {
            Program.Report(options, $"tray {(show ? "on" : "off")}: {reason}");
        }

        return show;
    }

    private static (bool Show, string Reason) Decide(Options options)
    {
        if (options.Detach)
        {
            return (false, "auto, --detach");
        }

        if (!ProcessIdToSessionId((uint) Environment.ProcessId, out var session) || session == 0)
        {
            return (false, "auto, session 0");
        }

        if (!Elevation.InteractiveWindowStation())
        {
            return (false, "auto, no interactive desktop");
        }

        if (UnderScheduler())
        {
            return (false, "auto, started by Task Scheduler");
        }

        return (true, "auto, interactive desktop");
    }

    // True when the Task Scheduler service's process is an ancestor, directly or through the
    // script its task runs. Each link must be older than its child, so a reused pid ends the walk.
    private static bool UnderScheduler()
    {
        var scheduler = SchedulerProcessId();
        if (scheduler == 0)
        {
            return false;
        }

        var parents = ParentProcessIds();
        var current = (uint) Environment.ProcessId;
        var created = CreationTime(current) ?? long.MaxValue;
        for (var depth = 0; depth < MaximumAncestors && parents.TryGetValue(current, out var parent); depth++)
        {
            if (parent == 0 || parent == current)
            {
                return false;
            }

            var parentCreated = CreationTime(parent);
            if (parent == scheduler)
            {
                // The service's process may refuse even a limited query; its pid cannot be reused while it runs.
                return parentCreated is null || parentCreated <= created;
            }

            if (parentCreated is null || parentCreated > created)
            {
                return false;
            }

            current = parent;
            created = parentCreated.Value;
        }

        return false;
    }

    private static uint SchedulerProcessId()
    {
        var seam = Environment.GetEnvironmentVariable(SchedulerProcessVariable);
        if (seam is not null)
        {
            return uint.TryParse(seam, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        }

        var manager = OpenSCManager(null, null, ServiceManagerConnect);
        if (manager == 0)
        {
            return 0;
        }

        try
        {
            var service = OpenService(manager, SchedulerServiceName, ServiceQueryStatus);
            if (service == 0)
            {
                return 0;
            }

            try
            {
                return QueryServiceStatusEx(service, StatusProcessInformation, out var status, (uint) sizeof(ServiceStatusProcess), out _)
                    ? status.ProcessId
                    : 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static Dictionary<uint, uint> ParentProcessIds()
    {
        Dictionary<uint, uint> parents = [];
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == -1)
        {
            return parents;
        }

        try
        {
            ProcessEntry entry = new()
            {
                Size = (uint) sizeof(ProcessEntry)
            };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }

        return parents;
    }

    private static long? CreationTime(uint processId)
    {
        var process = Native.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport(Kernel32Library, SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport(Kernel32Library, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport(Kernel32Library, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport(Advapi32Library, EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport(Advapi32Library, EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport(Advapi32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [DllImport(Advapi32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
