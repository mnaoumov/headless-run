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
                var text = options.Help ? Help : "headless-run 0.1.0\n";
                if (options.Output is not null)
                {
                    if (options.Append)
                    {
                        File.AppendAllText(options.Output, text);
                    }
                    else
                    {
                        File.WriteAllText(options.Output, text);
                    }
                }
                else
                {
                    Console.Write(text);
                }

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
        var input = StandardHandle(Native.StandardInputHandle, null, false, true, resources);
        var output = StandardHandle(Native.StandardOutputHandle, options.Output, options.Append, false, resources);
        var error = options.Error is not null && options.Output is not null &&
            string.Equals(Path.GetFullPath(options.Error), Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase)
            ? output : StandardHandle(Native.StandardErrorHandle, options.Error, options.Append, false, resources);
        nint job = 0;
        if (!options.Detach)
        {
            job = resources.Handle(Native.CreateJobObject(0, 0));
            Native.ExtendedLimits limits = new()
            {
                Basic = new()
                {
                    Flags = Native.JobObjectLimitKillOnJobClose
                }
            }; // KILL_ON_JOB_CLOSE
            Check(Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, (uint) Marshal.SizeOf<Native.ExtendedLimits>()));
        }

        var attributeCount = Native.HandleListAttributeCount + (options.Detach ? 0 : Native.JobListAttributeCount);
        nuint size = 0;
        Native.InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);
        var attributes = resources.Memory(checked((int) size));
        Check(Native.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size));
        try
        {
            SetAttribute(Native.ProcessThreadAttributeHandleList, new[] { input, output, error }.Distinct().ToArray()); // HANDLE_LIST
            if (job != 0)
            {
                SetAttribute(Native.ProcessThreadAttributeJobList, [job]); // JOB_LIST: assigned atomically, no suspended orphan window
            }

            nint environment = 0;
            if (options.Environment.Count != 0)
            {
                SortedDictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    values[(string) entry.Key] = (string) entry.Value!;
                }

                foreach (var entry in options.Environment)
                {
                    values[entry.Key] = entry.Value;
                }

                var block = string.Join('\0', values.Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
                var bytes = Encoding.Unicode.GetBytes(block);
                environment = resources.Memory(bytes.Length);
                Marshal.Copy(bytes, 0, environment, bytes.Length);
            }
            Native.StartupInfoEx startup = new()
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<Native.StartupInfoEx>(),
                    Flags = Native.StartUsingStandardHandles,
                    Input = input,
                    Output = output,
                    Error = error
                },
                Attributes = attributes
            };
            var flags = Native.CreateNoWindow | Native.ExtendedStartupInfoPresent | Native.CreateUnicodeEnvironment; // NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | UNICODE_ENVIRONMENT
            if (options.BelowNormal)
            {
                flags |= Native.BelowNormalPriorityClass;
            }

            Check(Native.CreateProcessW(null, new StringBuilder(options.Tail), 0, 0, true, flags, environment,
                options.Directory, ref startup, out var process));
            resources.Handle(process.Process);
            resources.Handle(process.Thread);
            if (options.Detach)
            {
                return 0;
            }

            var wait = Native.WaitForSingleObject(process.Process, options.Timeout);
            if (wait == Native.WaitTimeout)
            {
                Check(Native.TerminateJobObject(job, TimedOut));
                Check(Native.WaitForSingleObject(process.Process, Native.InfiniteWait) == Native.WaitObjectSignaled);
                var diagnostic = Encoding.UTF8.GetBytes("headless-run: Timeout expired; the child job was terminated.\n");
                Native.WriteFile(error, diagnostic, (uint) diagnostic.Length, out _, 0);
                return TimedOut;
            }
            Check(wait == Native.WaitObjectSignaled);
            Check(Native.GetExitCodeProcess(process.Process, out var code));
            return unchecked((int) code);

            void SetAttribute(nuint attribute, nint[] handles)
            {
                var memory = resources.Memory(handles.Length * nint.Size);
                Marshal.Copy(handles, 0, memory, handles.Length);
                Check(Native.UpdateProcThreadAttribute(attributes, 0, attribute, memory, (nuint) (handles.Length * nint.Size), 0, 0));
            }
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attributes);
        }
    }

    private static nint StandardHandle(int kind, string? path, bool append, bool input, Resources resources)
    {
        if (path is null)
        {
            var inherited = Native.GetStdHandle(kind);
            if (inherited != 0 && inherited != -1)
            {
                Check(Native.DuplicateHandle(Native.GetCurrentProcess(), inherited, Native.GetCurrentProcess(), out var duplicate, 0, true, Native.DuplicateSameAccess));
                return resources.Handle(duplicate);
            }
        }
        Native.SecurityAttributes security = new()
        {
            Size = Marshal.SizeOf<Native.SecurityAttributes>(),
            Inherit = 1
        };
        // FILE_APPEND_DATA makes every write append, including writes from descendants.
        return resources.Handle(Native.CreateFile(path ?? "NUL", input ? Native.GenericRead : append && path is not null ? Native.FileAppendData : Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite | Native.FileShareDelete, ref security, path is null ? Native.OpenExisting : append ? Native.OpenAlways : Native.CreateAlways, Native.FileAttributeNormal, 0));
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void Report(Options options, string message)
    {
        var text = $"headless-run: {message}\n";
        try
        {
            if (options.Error is not null)
            {
                File.AppendAllText(options.Error, text);
            }
            else
            {
                Console.Error.Write(text);
            }
        }
        catch (Exception)
        {
            Console.Error.Write(text);
        }
    }

    private sealed class Resources : IDisposable
    {
        private readonly List<nint> handles = [];
        private readonly List<nint> memory = [];
        public nint Handle(nint handle)
        {
            if (handle == 0 || handle == -1)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            handles.Add(handle);
            return handle;
        }
        public nint Memory(int size)
        {
            var allocation = Marshal.AllocHGlobal(size);
            memory.Add(allocation);
            return allocation;
        }
        public void Dispose()
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                Native.CloseHandle(handles[index]);
            }

            foreach (var allocation in memory)
            {
                Marshal.FreeHGlobal(allocation);
            }
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
