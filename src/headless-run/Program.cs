using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

internal static class Program
{
    internal const int LaunchFailure = 125;
    internal const int TimedOut = 124;

    private static int Main()
    {
        Options options = new();
        try
        {
            options.Parse(Marshal.PtrToStringUni(Native.GetCommandLineW())!);
            if (options.Help || options.Version)
            {
                string text = options.Help ? Help : "headless-run 0.1.0\n";
                if (options.Output is not null)
                {
                    if (options.Append) File.AppendAllText(options.Output, text);
                    else File.WriteAllText(options.Output, text);
                }
                else Console.Write(text);
                return 0;
            }
            return Launch(options);
        }
        catch (Exception error)
        {
            Report(options, error.Message);
            return LaunchFailure;
        }
    }

    private static int Launch(Options options)
    {
        using Resources resources = new();
        nint input = StandardHandle(-10, null, false, true, resources);
        nint output = StandardHandle(-11, options.Output, options.Append, false, resources);
        nint error = options.Error is not null && options.Output is not null &&
            string.Equals(Path.GetFullPath(options.Error), Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase)
            ? output : StandardHandle(-12, options.Error, options.Append, false, resources);
        nint job = 0;
        if (!options.Detach)
        {
            job = resources.Handle(Native.CreateJobObject(0, 0));
            Native.ExtendedLimits limits = new() { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()));
        }

        nuint size = 0;
        Native.InitializeProcThreadAttributeList(0, options.Detach ? 1 : 2, 0, ref size);
        nint attributes = resources.Memory(checked((int)size));
        Check(Native.InitializeProcThreadAttributeList(attributes, options.Detach ? 1 : 2, 0, ref size));
        try
        {
            SetAttribute(0x20002, new[] { input, output, error }.Distinct().ToArray()); // HANDLE_LIST
            if (job != 0) SetAttribute(0x2000D, [job]); // JOB_LIST: assigned atomically, no suspended orphan window
            nint environment = 0;
            if (options.Environment.Count != 0)
            {
                SortedDictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) values[(string)entry.Key] = (string)entry.Value!;
                foreach (var entry in options.Environment) values[entry.Key] = entry.Value;
                string block = string.Join('\0', values.Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
                byte[] bytes = Encoding.Unicode.GetBytes(block);
                environment = resources.Memory(bytes.Length);
                Marshal.Copy(bytes, 0, environment, bytes.Length);
            }
            Native.StartupInfoEx startup = new()
            {
                Startup = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100, Input = input, Output = output, Error = error },
                Attributes = attributes
            };
            uint flags = 0x08000000 | 0x00080000 | 0x400; // NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | UNICODE_ENVIRONMENT
            if (options.BelowNormal) flags |= 0x4000;
            Check(Native.CreateProcessW(null, new StringBuilder(options.Tail), 0, 0, true, flags, environment,
                options.Directory, ref startup, out Native.ProcessInformation process));
            resources.Handle(process.Process);
            resources.Handle(process.Thread);
            if (options.Detach) return 0;
            uint wait = Native.WaitForSingleObject(process.Process, options.Timeout);
            if (wait == 0x102)
            {
                Check(Native.TerminateJobObject(job, TimedOut));
                Check(Native.WaitForSingleObject(process.Process, uint.MaxValue) == 0);
                byte[] diagnostic = Encoding.UTF8.GetBytes("headless-run: Timeout expired; the child job was terminated.\n");
                Native.WriteFile(error, diagnostic, (uint)diagnostic.Length, out _, 0);
                return TimedOut;
            }
            Check(wait == 0);
            Check(Native.GetExitCodeProcess(process.Process, out uint code));
            return unchecked((int)code);

            void SetAttribute(nuint attribute, nint[] handles)
            {
                nint memory = resources.Memory(handles.Length * nint.Size);
                Marshal.Copy(handles, 0, memory, handles.Length);
                Check(Native.UpdateProcThreadAttribute(attributes, 0, attribute, memory, (nuint)(handles.Length * nint.Size), 0, 0));
            }
        }
        finally { Native.DeleteProcThreadAttributeList(attributes); }
    }

    private static nint StandardHandle(int kind, string? path, bool append, bool input, Resources resources)
    {
        if (path is null)
        {
            nint inherited = Native.GetStdHandle(kind);
            if (inherited != 0 && inherited != -1)
            {
                Check(Native.DuplicateHandle(Native.GetCurrentProcess(), inherited, Native.GetCurrentProcess(), out nint duplicate, 0, true, 2));
                return resources.Handle(duplicate);
            }
        }
        Native.SecurityAttributes security = new() { Size = Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = 1 };
        // FILE_APPEND_DATA makes every write append, including writes from descendants.
        return resources.Handle(Native.CreateFile(path ?? "NUL", input ? 0x80000000 : append && path is not null ? 4u : 0x40000000,
            7, ref security, path is null ? 3u : append ? 4u : 2u, 0x80, 0));
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Report(Options options, string message)
    {
        string text = $"headless-run: {message}\n";
        try
        {
            if (options.Error is not null) File.AppendAllText(options.Error, text);
            else Console.Error.Write(text);
        }
        catch (Exception) { Console.Error.Write(text); }
    }

    private sealed class Resources : IDisposable
    {
        private readonly List<nint> handles = [];
        private readonly List<nint> memory = [];
        public nint Handle(nint handle)
        {
            if (handle == 0 || handle == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            handles.Add(handle);
            return handle;
        }
        public nint Memory(int size)
        {
            nint allocation = Marshal.AllocHGlobal(size);
            memory.Add(allocation);
            return allocation;
        }
        public void Dispose()
        {
            for (int index = handles.Count - 1; index >= 0; index--) Native.CloseHandle(handles[index]);
            foreach (nint allocation in memory) Marshal.FreeHGlobal(allocation);
        }
    }

    private const string Help = """
        headless-run 0.1.0 - launch a program without a console window
        Usage: headless-run.exe [options] -- <program> [arguments...]
        --stdout FILE       Redirect stdout (overwrite by default)
        --stderr FILE       Redirect stderr and launcher diagnostics
        --append            Append both output files
        --overwrite         Overwrite both output files (default)
        --cwd DIR           Child working directory
        --env NAME=VALUE    Override an environment variable (repeatable)
        --below-normal      Start at below-normal priority
        --timeout DURATION  Terminate the whole job after 250ms, 30s, 2m or 1h
        --detach            Return after start; no launcher-owned job or timeout
        --help              Print this help
        --version           Print version
        Exit: child's code; 124 timeout; 125 launcher failure; 0 detached/help/version.
        Quote executable paths containing spaces. No shell expansion is performed.
        """ + "\n";
}
