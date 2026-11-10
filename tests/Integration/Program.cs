using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private const int ChildSleepMilliseconds = 30000;
    private const int DetachedChildDelayMilliseconds = 500;
    private const int LauncherExitTimeoutMilliseconds = 10000;
    private const int LaunchTimeoutMilliseconds = 15000;
    private const int ConditionTimeoutMilliseconds = 10000;
    private const int ConditionPollMilliseconds = 25;
    private const int TimeoutExitCode = 124;
    private const int LaunchFailureExitCode = 125;
    private const int JobObjectBasicLimitInformation = 2;
    private const uint JobObjectLimitPriorityClass = 0x20;
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int kind, out BasicLimits limits, uint size, nint returnedSize);
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private static string Self => Environment.ProcessPath!;
    private static string launcher = "";
    private static string root = "";
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length != 0 && args[0] == "child")
        {
            return Child(args[1..]);
        }

        launcher = Path.GetFullPath(args[0]);
        root = Path.Combine(Path.GetTempPath(), "headless-run-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("Windows GUI subsystem", () =>
            {
                using var stream = File.OpenRead(launcher);
                using PEReader reader = new(stream);
                Assert(reader.PEHeaders.PEHeader!.Subsystem == Subsystem.WindowsGui, "must not allocate a launcher console");
            });
            Test("raw command line and argv parity", () =>
            {
                string[] tails = [
                    "\"a b\" \"say \\\"hi\\\"\" x&y 100% ^caret тест 日本語 \"C:\\dir with space\\\\\"",
                    "\"\" \"--\" \"--stdout\" a\t\t b   \"a\"\"b\" \\\"literal\\\"",
                    "\"trailing\\\\\\\\\" \"tabs\tinside\" %PATH% !var! < > | ( )",
                    "\"unterminated child quote" // our prefix parser must never validate the child tail
                ];
                foreach (var tail in tails)
                {
                    var direct = Path.Combine(root, "direct.json");
                    var indirect = Path.Combine(root, "indirect.json");
                    Assert(Run(Self, "child dump " + Quote(direct) + " " + tail).Code == 0, "direct launch");
                    Assert(Run(launcher, "-- " + Quote(Self) + " child dump " + Quote(indirect) + " " + tail).Code == 0, "wrapped launch");
                    Assert(File.ReadAllText(direct) == File.ReadAllText(indirect), "argv changed");
                    // Compare the entire raw tail after the dump-file operand, including repeated whitespace.
                    Assert(File.ReadAllText(direct + ".raw").Split(" --RAW-- ")[1] ==
                        File.ReadAllText(indirect + ".raw").Split(" --RAW-- ")[1], "raw tail changed");
                }
            });
            Test("full 32-bit exit codes", () =>
            {
                foreach (var code in new[] { "0", "7", "124", "125", "259", "-1", "-1073741819" })
                {
                    Assert(Run(launcher, "-- " + Quote(Self) + " child exit " + code).Code == int.Parse(code, CultureInfo.InvariantCulture), code);
                }
            });
            Test("quoted executable in a Unicode path with spaces", () =>
            {
                var directory = Path.Combine(root, "helper space 世界");
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Self)!))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }

                var helper = Path.Combine(directory, Path.GetFileName(Self));
                Assert(Run(launcher, "-- " + Quote(helper) + " child exit 17").Code == 17, "quoted program path");
            });
            Test("redirection, append, overwrite and shared destination", () =>
            {
                var output = Path.Combine(root, "out with space.txt");
                var error = Path.Combine(root, "err.txt");
                var flags = "--stdout " + Quote(output) + " --stderr " + Quote(error);
                var child = " -- " + Quote(Self) + " child streams";
                Assert(Run(launcher, flags + child).Code == 0, "stream launch");
                Assert(File.ReadAllText(output) == "stdout\n" && File.ReadAllText(error) == "stderr\n", "separate files");
                Assert(Run(launcher, flags + " --append" + child).Code == 0, "append launch");
                Assert(File.ReadAllText(output) == "stdout\nstdout\n", "append");
                Assert(Run(launcher, flags + " --append --overwrite" + child).Code == 0, "overwrite launch");
                Assert(File.ReadAllText(output) == "stdout\n", "overwrite");
                Assert(Run(launcher, "--stdout " + Quote(output) + " --stderr " + Quote(output) + child).Code == 0, "shared launch");
                Assert(File.ReadAllText(output) == "stdout\nstderr\n", "shared file truncated or overwritten");
                var inherited = Run(launcher, "-- " + Quote(Self) + " child streams");
                Assert(inherited.Output == "stdout\n" && inherited.Error == "stderr\n", "inherited pipes");
            });
            Test("cwd, environment, priority and hidden console", () =>
            {
                var directory = Path.Combine(root, "working directory");
                Directory.CreateDirectory(directory);
                var result = Path.Combine(root, "state.json");
                var run = Run(launcher, "--cwd " + Quote(directory) + " --env \"HEADLESS_TEST=first\" --env \"headless_test=héllo=世界\" --env HEADLESS_EMPTY= --priority below-normal -- " + Quote(Self) + " child state " + Quote(result));
                Assert(run.Code == 0, run.Error);
                using var state = JsonDocument.Parse(File.ReadAllText(result));
                Assert(state.RootElement.GetProperty("cwd").GetString() == directory, "cwd");
                Assert(state.RootElement.GetProperty("env").GetString() == "héllo=世界", "environment override");
                Assert(state.RootElement.GetProperty("priority").GetString() == "BelowNormal", "priority");
                Assert(state.RootElement.GetProperty("console").GetInt64() == 0, "console window created");
                Assert(Environment.GetEnvironmentVariable("HEADLESS_TEST") is null, "parent environment changed");
            });
            Test("every priority class, in both value spellings", () =>
            {
                // Windows grants realtime only with SeIncreaseBasePriorityPrivilege; otherwise it starts the child at high.
                (string Value, string[] Expected)[] cases = [
                    ("idle", ["Idle"]),
                    ("below-normal", ["BelowNormal"]),
                    ("normal", ["Normal"]),
                    ("above-normal", ["AboveNormal"]),
                    ("high", ["High"]),
                    ("realtime", ["RealTime", "High"])
                ];
                // An enclosing job with a priority-class limit overrides every class the launcher requests.
                var imposed = ImposedPriority();
                if (imposed is not null)
                {
                    Console.WriteLine("NOTE an enclosing job imposes priority " + imposed + "; expecting it for every class");
                }

                foreach (var (value, expected) in cases)
                {
                    foreach (var spelling in new[] { "--priority " + value, "--priority=" + value })
                    {
                        var actual = ChildPriority(spelling);
                        Assert(imposed is null ? expected.Contains(actual) : actual == imposed, spelling + " gave " + actual);
                    }
                }
                // Omitted: Windows' default, which is normal unless the parent runs at idle or below-normal.
                var own = Process.GetCurrentProcess().PriorityClass;
                var inherited = own is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal ? own : ProcessPriorityClass.Normal;
                Assert(ChildPriority("") == (imposed ?? inherited.ToString()), "omitted priority");
            });
            Test("equals form for every value option", () =>
            {
                var directory = Path.Combine(root, "equals cwd");
                Directory.CreateDirectory(directory);
                var output = Path.Combine(root, "equals-stdout.txt");
                var error = Path.Combine(root, "equals-stderr.txt");
                var result = Path.Combine(root, "equals-state.json");
                var run = Run(launcher, "--stdout=" + Quote(output) + " --stderr=" + Quote(error) + " --cwd=" + Quote(directory) + " --env=\"HEADLESS_TEST=a=b\" --timeout=30s -- " + Quote(Self) + " child state " + Quote(result));
                Assert(run.Code == 0, run.Error);
                using var state = JsonDocument.Parse(File.ReadAllText(result));
                Assert(state.RootElement.GetProperty("cwd").GetString() == directory, "cwd");
                Assert(state.RootElement.GetProperty("env").GetString() == "a=b", "environment");
                Assert(File.Exists(output) && File.Exists(error), "output files");
            });
            Test("output parents are created; a missing cwd needs --create-cwd", () =>
            {
                var output = Path.Combine(root, "logs", "nested deeper", "nightly-stdout.txt");
                var error = Path.Combine(root, "logs", "other", "nightly-stderr.txt");
                Assert(Run(launcher, "--stdout " + Quote(output) + " --stderr " + Quote(error) + " -- " + Quote(Self) + " child streams").Code == 0, "nested output launch");
                Assert(File.ReadAllText(output) == "stdout\n" && File.ReadAllText(error) == "stderr\n", "nested output files");
                var diagnostics = Path.Combine(root, "diagnostics", "nightly-stderr.txt");
                Assert(Run(launcher, "--stderr " + Quote(diagnostics) + " --bad").Code == LaunchFailureExitCode, "early diagnostic exit");
                Assert(File.ReadAllText(diagnostics).Contains("headless-run:"), "early diagnostic in a created directory");
                var missing = Path.Combine(root, "missing cwd", "deeper");
                var refused = Run(launcher, "--cwd " + Quote(missing) + " -- " + Quote(Self) + " child exit 0");
                Assert(refused.Code == LaunchFailureExitCode && refused.Error.Contains("--create-cwd"), "missing cwd must fail");
                Assert(!Directory.Exists(missing), "missing cwd was created without --create-cwd");
                var result = Path.Combine(root, "created-cwd.json");
                Assert(Run(launcher, "--create-cwd --cwd " + Quote(missing) + " -- " + Quote(Self) + " child state " + Quote(result)).Code == 0, "create cwd");
                using var state = JsonDocument.Parse(File.ReadAllText(result));
                Assert(state.RootElement.GetProperty("cwd").GetString() == missing, "created cwd");
            });
            Test("cmd and bat scripts run under cmd.exe with its argument rules", () =>
            {
                var directory = Path.Combine(root, "scripts with space");
                Directory.CreateDirectory(directory);
                foreach (var extension in new[] { ".cmd", ".bat", ".CMD" })
                {
                    var script = Path.Combine(directory, "echo args" + extension);
                    File.WriteAllText(script, "@echo off\r\necho [%~1] [%~2] [%3]\r\nexit /b 9\r\n");
                    // cmd.exe strips the quotes with %~1 and expands %NAME% inside arguments.
                    var run = Run(launcher, "--env HEADLESS_TEST=expanded -- " + Quote(script) + " \"a b\" %HEADLESS_TEST% \"q\"");
                    Assert(run.Code == 9, extension + " exit code: " + run.Code + " " + run.Error);
                    Assert(run.Output.Trim() == "[a b] [expanded] [\"q\"]", extension + " arguments: " + run.Output);
                }
            });
            Test("ps1 scripts run under pwsh, or Windows PowerShell on request", () =>
            {
                var directory = Path.Combine(root, "ps scripts");
                Directory.CreateDirectory(directory);
                var script = Path.Combine(directory, "echo args.ps1");
                File.WriteAllText(script, "param([string] $First, [string] $Second)\r\n[Console]::Out.Write(\"[$First] [$Second] $($PSVersionTable.PSEdition)\")\r\nexit 7\r\n");
                // pwsh -File passes arguments as literal strings: no variable expansion.
                var run = Run(launcher, "-- " + Quote(script) + " \"a b\" $env:HEADLESS_TEST");
                Assert(run.Code == 7, "pwsh exit code: " + run.Code + " " + run.Error);
                Assert(run.Output == "[a b] [$env:HEADLESS_TEST] Core", "pwsh arguments: " + run.Output);
                var legacy = Run(launcher, "--windows-powershell -- " + Quote(script) + " \"a b\" c");
                Assert(legacy.Code == 7 && legacy.Output == "[a b] [c] Desktop", "Windows PowerShell: " + legacy.Output + legacy.Error);
                var missing = Run(launcher, "--env " + Quote("PATH=" + directory) + " -- " + Quote(script));
                Assert(missing.Code == LaunchFailureExitCode && missing.Error.Contains("pwsh") && missing.Error.Contains("--windows-powershell"), "missing pwsh: " + missing.Error);
            });
            Test("launch and usage failures produce diagnostics", () =>
            {
                foreach (var command in new[] { "-- missing-headless-test-" + Guid.NewGuid() + ".exe", "--bad", "--", "", "--timeout 0s -- x", "--timeout -1s -- x", "--timeout 1 -- x", "--timeout 999999999h -- x", "--detach --timeout 1s -- x", "--env broken -- x", "--cwd", "--cwd \"\" -- x", "--cwd \"unterminated", "--below-normal -- x", "--priority bogus -- x", "--priority -- x", "--priority= -- x", "--append=yes -- x", "--bad=1 -- x", "--windows-powershell -- " + Quote(Self) + " child exit 0" })
                {
                    var run = Run(launcher, command);
                    Assert(run.Code == LaunchFailureExitCode && run.Error.Contains("headless-run:"), "missing failure: " + command);
                }
                var error = Path.Combine(root, "failure.txt");
                Assert(Run(launcher, "--stderr " + Quote(error) + " -- nonexistent-headless-test.exe").Code == LaunchFailureExitCode, "error file exit");
                Assert(File.ReadAllText(error).Contains("headless-run:"), "error file diagnostic");
                Assert(Run(launcher, "--stdout " + Quote(Path.Combine(root, "stdout-is-a-directory")) + " --create-cwd --cwd " + Quote(Path.Combine(root, "stdout-is-a-directory")) + " -- " + Quote(Self) + " child exit 0").Code == LaunchFailureExitCode, "bad redirection");
                Assert(Run(launcher, "--cwd " + Quote(Path.Combine(root, "absent")) + " -- " + Quote(Self) + " child exit 0").Code == LaunchFailureExitCode, "bad cwd");
            });
            Test("help and version", () =>
            {
                Assert(Run(launcher, "--help").Output.Contains("Usage:"), "help");
                Assert(Run(launcher, "--version   ").Output == "headless-run 1.0.0\n", "version");
            });
            Test("timeout writes redirected diagnostic", () =>
            {
                var error = Path.Combine(root, "timeout-error.txt");
                Assert(Run(launcher, "--stderr " + Quote(error) + " --timeout 50ms -- " + Quote(Self) + " child sleep").Code == TimeoutExitCode, "timeout exit");
                Assert(File.ReadAllText(error).Contains("Timeout expired"), "timeout diagnostic missing from file");
            });
            Test("timeout kills descendants", () => TreeTest("timeout"));
            Test("closing launcher kills descendants", () => TreeTest("kill"));
            Test("parent exit kills remaining descendants", () => TreeTest("exit"));
            Test("detach survives launcher exit", () =>
            {
                var marker = Path.Combine(root, "detached.txt");
                // Redirect both streams: inherited pipes intentionally remain open in a detached child.
                var run = Run(launcher, "--stdout " + Quote(marker + ".out") + " --stderr " + Quote(marker + ".err") + " --detach -- " + Quote(Self) + " child later " + Quote(marker));
                Assert(run.Code == 0, run.Error);
                WaitFor(() => File.Exists(marker), "detached child did not finish");
                Assert(File.ReadAllText(marker) == "done", "detached child killed");
            });
            Console.WriteLine($"PASS: {passed} integration groups");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("headless-run-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
            }

            Directory.Delete(root, true);
        }
    }

    private static int Child(string[] args)
    {
        switch (args[0])
        {
            case "dump":
                File.WriteAllText(args[1], JsonSerializer.Serialize(args[2..]));
                // The marker precedes the file argument, so strip it from argv but preserve raw text separately.
                var raw = Environment.CommandLine;
                var start = raw.IndexOf(args[1], StringComparison.Ordinal) + args[1].Length;
                if (raw[start] == '"')
                {
                    start++;
                }

                File.WriteAllText(args[1] + ".raw", " --RAW-- " + raw[start..]);
                return 0;
            case "exit":
                return int.Parse(args[1], CultureInfo.InvariantCulture);
            case "streams":
                Console.Write("stdout\n");
                Console.Error.Write("stderr\n");
                return 0;
            case "state":
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    cwd = Environment.CurrentDirectory,
                    env = Environment.GetEnvironmentVariable("HEADLESS_TEST"),
                    priority = Process.GetCurrentProcess().PriorityClass.ToString(),
                    console = (long) GetConsoleWindow()
                }));
                return 0;
            case "tree":
                using (var descendant = Process.Start(new ProcessStartInfo(Self) { Arguments = "child sleep", UseShellExecute = false, CreateNoWindow = true })!)
                {
                    PublishMarker(args[1], $"{Environment.ProcessId},{descendant.Id}");
                    if (args[2] == "exit")
                    {
                        return 0;
                    }

                    Thread.Sleep(ChildSleepMilliseconds);
                }
                return 0;
            case "sleep":
                Thread.Sleep(ChildSleepMilliseconds);
                return 0;
            case "later":
                Thread.Sleep(DetachedChildDelayMilliseconds);
                PublishMarker(args[1], "done");
                return 0;
            default:
                throw new ArgumentException("Unknown helper command");
        }
    }

    private static string? ImposedPriority()
    {
        // A null job handle queries the job that contains this process.
        if (!QueryInformationJobObject(0, JobObjectBasicLimitInformation, out var limits, (uint) Marshal.SizeOf<BasicLimits>(), 0) ||
            (limits.Flags & JobObjectLimitPriorityClass) == 0)
        {
            return null;
        }

        return ((ProcessPriorityClass) limits.Priority).ToString();
    }

    private static string ChildPriority(string options)
    {
        var result = Path.Combine(root, "priority-" + Guid.NewGuid().ToString("N") + ".json");
        var run = Run(launcher, options + " -- " + Quote(Self) + " child state " + Quote(result));
        Assert(run.Code == 0, options + ": " + run.Error);
        using var state = JsonDocument.Parse(File.ReadAllText(result));
        return state.RootElement.GetProperty("priority").GetString()!;
    }

    private static void PublishMarker(string path, string text)
    {
        File.WriteAllText(path + ".pending", text);
        File.Move(path + ".pending", path);
    }

    private static void TreeTest(string mode)
    {
        var marker = Path.Combine(root, mode + ".pids");
        using var process = Start(launcher, (mode == "timeout" ? "--timeout 3s " : "") + "-- " + Quote(Self) + " child tree " + Quote(marker) + " " + mode);
        int[] pids = [];
        try
        {
            WaitFor(() => File.Exists(marker) && File.ReadAllText(marker).Contains(','), "tree not ready");
            pids = File.ReadAllText(marker).Split(',').Select(int.Parse).ToArray();
            if (mode == "kill")
            {
                process.Kill();
            }

            Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "launcher did not exit");
            if (mode == "timeout")
            {
                Assert(process.ExitCode == TimeoutExitCode, "timeout code");
            }

            foreach (var pid in pids)
            {
                WaitFor(() => !Alive(pid), "surviving descendant " + pid);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            foreach (var pid in pids)
            {
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited)
                    {
                        child.Kill(true);
                    }
                }
                catch (ArgumentException)
                {
                    // The process exited before the cleanup lookup could find it.
                }
            }
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    private static Process Start(string executable, string arguments)
    {
        return Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
    }
    private static (int Code, string Output, string Error) Run(string executable, string arguments)
    {
        using var process = Start(executable, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(LaunchTimeoutMilliseconds))
        {
            process.Kill(true);
            throw new Exception("Launch hung: " + arguments);
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }
    private static void WaitFor(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > ConditionTimeoutMilliseconds)
            {
                throw new Exception(message);
            }

            Thread.Sleep(ConditionPollMilliseconds);
        }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }
}
