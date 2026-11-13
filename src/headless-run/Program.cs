using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

internal static class Program
{
    internal const int LaunchFailure = 125;
    internal const int TimedOut = 124;
    // Stopped from the --tray menu: the shell's code for a job the user interrupted.
    internal const int Stopped = 130;
    private const int TeeDrainMilliseconds = 5000;

    private static int Main()
    {
        var commandLine = Marshal.PtrToStringUni(Native.GetCommandLineW())!;
        if (ConfigArguments(commandLine) is { } configArguments)
        {
            return ConfigCommand.Run(configArguments);
        }

        JobEventLog.Begin();
        Options options = new();
        int code;
        string? diagnostic = null;
        try
        {
            options.Parse(commandLine);
            if (options.Help || options.Version)
            {
                var text = options.Help ? Help : "headless-run 1.0.0\n";
                if (options.Output is not null)
                {
                    CreateParentDirectory(options.Output);
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

            // The launcher that hosts the icon decides; an elevated copy never shows one.
            options.Tray = options.ElevatedRelay is null && TrayAuto.Resolve(options);
            if (Elevation.Required(options))
            {
                code = Elevation.Run(options);
            }
            else
            {
                var caller = options.ElevatedRelay is null ? 0 : Elevation.Attach(options);
                code = Launch(options, caller);
            }
        }
        catch (Exception error)
        {
            Report(options, error.Message);
            diagnostic = error.Message;
            code = LaunchFailure;
        }

        JobEventLog.Write(options, code, diagnostic);
        return code;
    }

    // The text after "config" when it is the first argument, so a program named config after -- never matches.
    private static string? ConfigArguments(string commandLine)
    {
        var position = 0;
        try
        {
            Options.ReadToken(commandLine, ref position);
            var first = Options.ReadToken(commandLine, ref position);
            return first == ConfigCommand.Name ? commandLine[position..] : null;
        }
        catch (ArgumentException)
        {
            // An unclosed quote: Parse reports it.
            return null;
        }
    }

    // caller: under --elevate, the unelevated launcher this copy relays for; its exit ends the run.
    private static int Launch(Options options, nint caller)
    {
        using Resources resources = new();
        if (caller != 0)
        {
            resources.Handle(caller);
        }

        var (application, commandLine) = ResolveCommand(options);
        PrepareWorkingDirectory(options);
        // Under --elevate the unelevated caller hosts the icon, so the elevated copy never shows a second one.
        var capture = options.Tray && caller == 0 ? new OutputCapture() : null;
        var sharedFile = SharesFile(options);
        capture?.FollowLogs(options, sharedFile);
        var input = StandardHandle(Native.StandardInputHandle, null, false, true, resources);
        var output = StandardHandle(Native.StandardOutputHandle, options.Output, options.Append, false, resources);
        var error = sharedFile ? output : StandardHandle(Native.StandardErrorHandle, options.Error, options.Append, false, resources);
        // Launcher diagnostics go where the child's stderr would have gone, never into a tee's pipe.
        var diagnostics = error;
        // A tee forwards to the handle the child would otherwise have had, NUL included.
        using var outputTee = options.Output is null && capture is not null ? new Tee(output, capture.NewSource()) : null;
        using var errorTee = options.Error is null && capture is not null ? new Tee(error, capture.NewSource()) : null;
        if (outputTee is not null)
        {
            output = outputTee.ChildHandle;
        }

        if (errorTee is not null)
        {
            error = errorTee.ChildHandle;
        }

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
            flags |= options.PriorityClass; // 0 when --priority is omitted: Windows picks the default class.
            if (!Native.CreateProcessW(application, new StringBuilder(commandLine), 0, 0, true, flags, environment,
                options.Directory, ref startup, out var process))
            {
                var failure = Marshal.GetLastWin32Error();
                if (application is null && failure == Native.ErrorBadExeFormat)
                {
                    throw new InvalidOperationException(NotAProgram(options.Tail));
                }

                throw new Win32Exception(failure);
            }

            resources.Handle(process.Process);
            resources.Handle(process.Thread);
            if (options.Detach)
            {
                JobEventLog.Record(JobEventLog.Outcome.Detached);
                return 0;
            }

            outputTee?.Start();
            errorTee?.Start();
            using var tray = capture is null ? null : Tray.Start(new TrayJob(options.Tail, DateTime.Now, process.ProcessId,
                options.Output, sharedFile ? null : options.Error, capture, options.TrayMode == TrayMode.Auto,
                message => Diagnose(diagnostics, capture, message)));
            List<nint> waits = [process.Process];
            var callerIndex = -1;
            var stopIndex = -1;
            if (caller != 0)
            {
                callerIndex = waits.Count;
                waits.Add(caller);
            }

            if (tray is not null)
            {
                stopIndex = waits.Count;
                waits.Add(tray.StopHandle);
            }

            var wait = Native.WaitForMultipleObjects((uint) waits.Count, [.. waits], false, options.Timeout);
            int code;
            var stopped = false;
            if (callerIndex >= 0 && wait == Native.WaitObjectSignaled + (uint) callerIndex)
            {
                // The caller is gone, so nobody is left to relay to: end the tree as its own kill would have.
                Check(Native.TerminateJobObject(job, LaunchFailure));
                JobEventLog.Record(JobEventLog.Outcome.LaunchFailure, "The unelevated caller exited; the elevated job was terminated.");
                return LaunchFailure;
            }

            if (stopIndex >= 0 && wait == Native.WaitObjectSignaled + (uint) stopIndex)
            {
                stopped = true;
                JobEventLog.Record(JobEventLog.Outcome.Stopped);
                code = End(Stopped, "Stopped from the tray; the child job was terminated.");
            }
            else if (wait == Native.WaitTimeout)
            {
                JobEventLog.Record(JobEventLog.Outcome.TimedOut);
                code = End(TimedOut, "Timeout expired; the child job was terminated.");
            }
            else
            {
                Check(wait == Native.WaitObjectSignaled);
                Check(Native.GetExitCodeProcess(process.Process, out var exit));
                code = unchecked((int) exit);
            }

            if (tray is not null)
            {
                // The tees end once no process holds their pipes, so end the descendants first, as the
                // job's close would have, and let the viewer have the last of the output.
                Check(Native.TerminateJobObject(job, unchecked((uint) code)));
                outputTee?.Join(TeeDrainMilliseconds);
                errorTee?.Join(TeeDrainMilliseconds);
                tray.Finish(code, stopped);
            }

            return code;

            int End(int result, string message)
            {
                Check(Native.TerminateJobObject(job, unchecked((uint) result)));
                Check(Native.WaitForSingleObject(process.Process, Native.InfiniteWait) == Native.WaitObjectSignaled);
                Diagnose(diagnostics, capture, message);
                return result;
            }

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

    // True when --stdout and --stderr name one file, which then shares one handle.
    internal static bool SharesFile(Options options)
    {
        return options.Error is not null && options.Output is not null &&
            string.Equals(Path.GetFullPath(options.Error), Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase);
    }

    // A native executable gets the raw tail unchanged. A script goes to its engine's interpreter,
    // which then applies its own argument rules to the same raw text.
    private static (string? Application, string CommandLine) ResolveCommand(Options options)
    {
        var extension = ProgramExtension(options.Tail);
        var engine = EngineTable.Load(options.ConfigFile).Select(options.Engine, extension, options.ForceEngine);
        if (engine is null)
        {
            return (null, options.Tail);
        }

        var interpreter = Interpreter.Find(engine, options.Environment);
        Interpreter.CheckTypeScript(engine, interpreter, extension);
        return (interpreter, Interpreter.CommandLine(interpreter, engine, options.Tail));
    }

    private static string ProgramExtension(string tail)
    {
        return Path.GetExtension(ProgramToken(tail)).ToLowerInvariant();
    }

    // CreateProcessW refused a file that is no Windows program: say which engine setting would run it.
    private static string NotAProgram(string tail)
    {
        var extension = ProgramExtension(tail);
        var name = extension.Length > 1 ? extension[1..] : "<ext>";
        return $"'{ProgramToken(tail)}' is not a Windows program, and no engine runs '{extension}' files. " +
            $"Pass --engine NAME, or map the extension: headless-run config --global extension.{name} <engine>";
    }

    // The program token as CreateProcessW reads it: up to the closing quote, or up to whitespace.
    private static string ProgramToken(string tail)
    {
        if (tail.StartsWith('"'))
        {
            var end = tail.IndexOf('"', 1);
            return end < 0 ? tail[1..] : tail[1..end];
        }

        var space = tail.IndexOfAny([' ', '\t']);
        return space < 0 ? tail : tail[..space];
    }

    private static void PrepareWorkingDirectory(Options options)
    {
        if (options.Directory is null || Directory.Exists(options.Directory))
        {
            return;
        }

        if (!options.CreateDirectory)
        {
            throw new DirectoryNotFoundException($"Working directory '{options.Directory}' does not exist. Pass --create-cwd to create it.");
        }

        Directory.CreateDirectory(options.Directory);
    }

    private static void CreateParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static nint StandardHandle(int kind, string? path, bool append, bool input, Resources resources)
    {
        if (path is not null)
        {
            CreateParentDirectory(path);
        }
        else
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

    // A diagnostic once the streams are set up: to the child's stderr destination, and into the --tray viewer.
    private static void Diagnose(nint error, OutputCapture? capture, string message)
    {
        var text = $"headless-run: {message}\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        Native.WriteFile(error, bytes, (uint) bytes.Length, out _, 0);
        capture?.Note(text);
    }

    internal static void Report(Options options, string message)
    {
        var text = $"headless-run: {message}\n";
        try
        {
            if (options.Error is not null)
            {
                CreateParentDirectory(options.Error);
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
        headless-run 1.0.0 - run a console program without a console window
        Usage: headless-run.exe [options] -- <program> [arguments...]
               headless-run.exe config ...   (see headless-run config --help)
        --stdout FILE         Redirect stdout (overwrite by default)
        --stderr FILE         Redirect stderr and launcher diagnostics
        --append              Append both output files
        --overwrite           Overwrite both output files (default)
        --cwd DIR             Child working directory; must exist
        --create-cwd          Create the --cwd directory when it is missing
        --env NAME=VALUE      Override an environment variable (repeatable)
        --priority CLASS      idle, below-normal, normal, above-normal, high or realtime
        --timeout DURATION    Terminate the whole job after 250ms, 30s, 2m or 1h
        --detach              Return after start; no launcher-owned job or timeout
        --engine NAME         Run the program under this engine (pwsh, powershell, node...)
        --force-engine        Use --engine even when it does not accept the extension
        --config FILE         Read engines from this file too, over the global ones
        --elevate             Run elevated, after a UAC prompt; streams are relayed
        --tray[=WHEN]         Tray icon while the job runs: show output, open logs, stop.
                              WHEN is true (--tray alone), false or auto (default):
                              auto shows it on a desktop run, not under Task Scheduler,
                              a service, session 0 or --detach
        --event-log           Write the run's outcome to the Application event log
        --help                Print this help
        --version             Print version
        Value options also accept --name=VALUE. Output parent directories are created.
        The extension picks the engine: .cmd/.bat cmd, .ps1 pwsh, .js/.ts node, .py python...
        List them with: headless-run config --list. Other programs run directly.
        Exit: child's code; 124 timeout; 125 launcher failure; 130 stopped from the tray;
        0 detached/help/version.
        Quote program paths containing spaces. No shell expansion is performed.
        """ + "\n";
}
