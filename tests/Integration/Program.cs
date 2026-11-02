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
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
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
                var run = Run(launcher, "--cwd " + Quote(directory) + " --env \"HEADLESS_TEST=first\" --env \"headless_test=héllo=世界\" --env HEADLESS_EMPTY= --below-normal -- " + Quote(Self) + " child state " + Quote(result));
                Assert(run.Code == 0, run.Error);
                using var state = JsonDocument.Parse(File.ReadAllText(result));
                Assert(state.RootElement.GetProperty("cwd").GetString() == directory, "cwd");
                Assert(state.RootElement.GetProperty("env").GetString() == "héllo=世界", "environment override");
                Assert(state.RootElement.GetProperty("priority").GetString() == "BelowNormal", "priority");
                Assert(state.RootElement.GetProperty("console").GetInt64() == 0, "console window created");
                Assert(Environment.GetEnvironmentVariable("HEADLESS_TEST") is null, "parent environment changed");
            });
            Test("launch and usage failures produce diagnostics", () =>
            {
                foreach (var command in new[] { "-- missing-headless-test-" + Guid.NewGuid() + ".exe", "--bad", "--", "", "--timeout 0s -- x", "--timeout -1s -- x", "--timeout 1 -- x", "--timeout 999999999h -- x", "--detach --timeout 1s -- x", "--env broken -- x", "--cwd", "--cwd \"\" -- x", "--cwd \"unterminated" })
                {
                    var run = Run(launcher, command);
                    Assert(run.Code == LaunchFailureExitCode && run.Error.Contains("headless-run:"), "missing failure: " + command);
                }
                var error = Path.Combine(root, "failure.txt");
                Assert(Run(launcher, "--stderr " + Quote(error) + " -- nonexistent-headless-test.exe").Code == LaunchFailureExitCode, "error file exit");
                Assert(File.ReadAllText(error).Contains("headless-run:"), "error file diagnostic");
                Assert(Run(launcher, "--stdout " + Quote(Path.Combine(root, "absent", "file")) + " -- " + Quote(Self) + " child exit 0").Code == LaunchFailureExitCode, "bad redirection");
                Assert(Run(launcher, "--cwd " + Quote(Path.Combine(root, "absent")) + " -- " + Quote(Self) + " child exit 0").Code == LaunchFailureExitCode, "bad cwd");
            });
            Test("help and version", () =>
            {
                Assert(Run(launcher, "--help").Output.Contains("Usage:"), "help");
                Assert(Run(launcher, "--version   ").Output == "headless-run 0.1.0\n", "version");
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
                catch (ArgumentException) { }
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
