using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

// The launcher is Windows-only throughout; this states it for the platform analyzer.
[assembly: SupportedOSPlatform("windows")]

namespace HeadlessRun;

// --elevate: an unelevated launcher starts an elevated copy of itself through the UAC prompt
// and relays the caller's environment, working directory and standard streams over named pipes.
internal static class Elevation
{
    // Test seam: start the relaying copy with the "open" verb, so the whole relay runs with no UAC prompt.
    internal const string RelayWithoutUacVariable = "HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC";
    private const string EnvironmentPipe = "-env";
    private const string InputPipe = "-in";
    private const string OutputPipe = "-out";
    private const string ErrorPipe = "-err";
    private const int ConnectTimeoutMilliseconds = 30000;
    private const int PipeBufferBytes = 4096;
    private static readonly List<IDisposable> Relays = [];

    private static bool RelayWithoutUac => Environment.GetEnvironmentVariable(RelayWithoutUacVariable) == "1";

    // True when this launcher must hand the run to an elevated copy.
    internal static bool Required(Options options)
    {
        if (!options.Elevate || options.ElevatedRelay is not null)
        {
            return false;
        }

        if (RelayWithoutUac)
        {
            return true;
        }

        if (IsElevated())
        {
            return false;
        }

        if (!InteractiveWindowStation())
        {
            throw new InvalidOperationException(
                "--elevate needs a UAC prompt, which this non-interactive session cannot show (a service, " +
                "an S4U scheduled task or session 0). Run the task with highest privileges instead.");
        }

        return true;
    }

    // The caller's side: start the elevated copy, relay to it, and return its exit code.
    internal static int Run(Options options)
    {
        var name = $"headless-run-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var security = PipeAccess();
        using var environment = Server(name + EnvironmentPipe, PipeDirection.Out, security);
        using var input = options.Detach ? null : Server(name + InputPipe, PipeDirection.Out, security);
        using var output = options.Detach || options.Output is not null ? null : Server(name + OutputPipe, PipeDirection.In, security);
        using var error = options.Detach || options.Error is not null ? null : Server(name + ErrorPipe, PipeDirection.In, security);
        // --tray: this unelevated caller hosts the icon. It sees every relayed byte, and its own windows
        // need no UIPI exceptions to hear Explorer.
        var capture = options.Tray ? new OutputCapture() : null;
        var sharedFile = Program.SharesFile(options);
        capture?.FollowLogs(options, sharedFile);
        Process copy;
        try
        {
            copy = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = $"--elevated-relay {Environment.ProcessId}:{name} {options.Arguments}",
                UseShellExecute = true,
                Verb = RelayWithoutUac ? "open" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.CurrentDirectory
            }) ?? throw new InvalidOperationException("The elevated launcher did not start.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == Native.ErrorCancelled)
        {
            throw new InvalidOperationException("The elevation prompt was declined; nothing ran.", exception);
        }

        using (copy)
        {
            using CancellationTokenSource stop = new();
            var copyId = (uint) copy.Id;
            var errorConnected = false;
            List<Task> streams = [Serve(environment, copyId, stop.Token, pipe => pipe.Write(EnvironmentPayload()))];
            if (input is not null)
            {
                // Not awaited: the caller's stdin may never reach EOF, and the child's exit ends the run.
                _ = Serve(input, copyId, stop.Token, pipe => Pump(Console.OpenStandardInput(), pipe, null));
            }

            if (output is not null)
            {
                var source = capture?.NewSource();
                streams.Add(Serve(output, copyId, stop.Token, pipe => Pump(pipe, Console.OpenStandardOutput(), source)));
            }

            if (error is not null)
            {
                var source = capture?.NewSource();
                streams.Add(Serve(error, copyId, stop.Token, pipe =>
                {
                    errorConnected = true;
                    Pump(pipe, Console.OpenStandardError(), source);
                }));
            }

            using var tray = capture is null ? null : Tray.Start(new TrayJob(options.Tail, DateTime.Now, null, options.Output,
                sharedFile ? null : options.Error, capture, message =>
                {
                    Program.Report(options, message);
                    capture.Note($"headless-run: {message}\n");
                }));
            if (tray is not null)
            {
                using ManualResetEvent exited = new(false)
                {
                    SafeWaitHandle = new SafeWaitHandle(copy.Handle, false)
                };
                if (WaitHandle.WaitAny([exited, tray.StopRequested]) == 1)
                {
                    // This launcher's exit is what ends the elevated copy, which then ends the whole tree:
                    // an unelevated process cannot terminate it any other way.
                    Program.Report(options, "Stopped from the tray; the elevated job is being terminated.");
                    tray.Finish(Program.Stopped, true);
                    JobEventLog.Record(JobEventLog.Outcome.Stopped);
                    return Program.Stopped;
                }
            }

            copy.WaitForExit();
            // A pipe the copy never connected would wait forever; connected ones end at EOF,
            // which the copy's kill-on-close job guarantees once it has exited.
            stop.Cancel();
            foreach (var stream in streams)
            {
                try
                {
                    stream.Wait();
                }
                catch (AggregateException)
                {
                    // A relay that was cancelled or broken has nothing more to deliver.
                }
            }

            var code = copy.ExitCode;
            if (code == Program.LaunchFailure && error is not null && !errorConnected)
            {
                throw new InvalidOperationException("The elevated launcher failed before it connected its error stream.");
            }

            tray?.Finish(code, false);
            // The elevated copy recorded this outcome itself, knowing a timeout from a child's 124.
            JobEventLog.Relayed = true;
            return code;
        }
    }

    // The elevated copy's side: adopt the caller's environment, directory and streams.
    // Returns a handle that is signaled when the caller exits.
    internal static nint Attach(Options options)
    {
        var separator = options.ElevatedRelay!.IndexOf(':');
        if (separator <= 0 || !uint.TryParse(options.ElevatedRelay[..separator], out var callerId))
        {
            throw new ArgumentException("--elevated-relay is internal to --elevate.");
        }

        var name = options.ElevatedRelay[(separator + 1)..];
        var caller = Native.OpenProcess(Native.Synchronize, false, callerId);
        if (caller == 0)
        {
            throw new Win32Exception();
        }

        using (var environment = Client(name + EnvironmentPipe, PipeDirection.In, callerId))
        {
            using MemoryStream payload = new();
            environment.CopyTo(payload);
            ApplyEnvironment(Encoding.UTF8.GetString(payload.ToArray()));
        }

        if (!options.Detach)
        {
            Adopt(Native.StandardInputHandle, Client(name + InputPipe, PipeDirection.In, callerId));
            if (options.Output is null)
            {
                Adopt(Native.StandardOutputHandle, Client(name + OutputPipe, PipeDirection.Out, callerId));
            }

            if (options.Error is null)
            {
                Adopt(Native.StandardErrorHandle, Client(name + ErrorPipe, PipeDirection.Out, callerId));
            }
        }

        return caller;
    }

    private static bool IsElevated()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out var token))
        {
            throw new Win32Exception();
        }

        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenElevationClass, out var elevated, sizeof(uint), out _))
            {
                throw new Win32Exception();
            }

            return elevated != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    // A UAC prompt needs a visible window station; services and S4U tasks have none.
    private static bool InteractiveWindowStation()
    {
        var station = Native.GetProcessWindowStation();
        return station != 0
            && Native.GetUserObjectInformation(station, Native.UserObjectFlagsIndex, out var flags, (uint) Marshal.SizeOf<Native.UserObjectFlags>(), out _)
            && (flags.Flags & Native.WindowStationVisible) != 0;
    }

    // The caller and Administrators: the elevated copy may run as another administrator
    // when a standard user types an administrator's credentials into the prompt.
    private static PipeSecurity PipeAccess()
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static NamedPipeServerStream Server(string name, PipeDirection direction, PipeSecurity security)
    {
        // One instance: a second server or client under this unguessable name is refused.
        return NamedPipeServerStreamAcl.Create(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            PipeBufferBytes, PipeBufferBytes, security);
    }

    private static Task Serve(NamedPipeServerStream pipe, uint expectedClient, CancellationToken stop, Action<NamedPipeServerStream> relay)
    {
        return Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(stop);
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var client) || client != expectedClient)
            {
                throw new InvalidOperationException("An unexpected process connected to the elevation relay.");
            }

            try
            {
                relay(pipe);
            }
            finally
            {
                pipe.Dispose();
            }
        });
    }

    private static NamedPipeClientStream Client(string name, PipeDirection direction, uint expectedServer)
    {
        NamedPipeClientStream pipe = new(".", name, direction, PipeOptions.None);
        pipe.Connect(ConnectTimeoutMilliseconds);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server) || server != expectedServer)
        {
            pipe.Dispose();
            throw new InvalidOperationException("The elevation relay pipe does not belong to the launcher that started this copy.");
        }

        return pipe;
    }

    // The copy's standard handle becomes the pipe; Launch then hands it to the child like any inherited handle.
    private static void Adopt(int kind, NamedPipeClientStream pipe)
    {
        Relays.Add(pipe);
        if (!Native.SetStdHandle(kind, pipe.SafePipeHandle.DangerousGetHandle()))
        {
            throw new Win32Exception();
        }
    }

    // capture: the --tray viewer's copy of the stream, if any.
    private static void Pump(Stream source, Stream destination, OutputCapture.Source? capture)
    {
        var buffer = new byte[PipeBufferBytes];
        var forward = true;
        try
        {
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (forward)
                {
                    try
                    {
                        destination.Write(buffer, 0, count);
                        destination.Flush();
                    }
                    catch (IOException)
                    {
                        // The caller's stream went away; keep draining, so the viewer still gets the rest.
                        forward = false;
                    }
                }

                capture?.Write(buffer.AsSpan(0, count));
            }
        }
        catch (IOException)
        {
            // The other end closed: the child exited.
        }
    }

    // The working directory, then each NAME=VALUE, separated by NUL.
    private static byte[] EnvironmentPayload()
    {
        StringBuilder payload = new(Environment.CurrentDirectory);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            payload.Append('\0').Append((string) entry.Key).Append('=').Append((string?) entry.Value);
        }

        return Encoding.UTF8.GetBytes(payload.ToString());
    }

    private static void ApplyEnvironment(string payload)
    {
        var parts = payload.Split('\0');
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf('=', 1);
            // Names that start with '=' are cmd.exe's per-drive directories, which cannot be set by name.
            if (separator > 0 && !part.StartsWith('='))
            {
                values[part[..separator]] = part[(separator + 1)..];
            }
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string) entry.Key;
            if (!key.StartsWith('=') && !values.ContainsKey(key))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        foreach (var (key, value) in values)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        Environment.CurrentDirectory = parts[0];
    }
}
