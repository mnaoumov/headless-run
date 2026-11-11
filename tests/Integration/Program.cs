using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

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
    private const string RelayWithoutUacVariable = "HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC";
    private const int StoppedExitCode = 130;
    private const string TrayWindowClass = "HeadlessRunTray";
    private const string ViewerWindowClass = "HeadlessRunOutput";
    private const int TrayCommandShowOutput = 1;
    private const int TrayCommandHideOutput = 2;
    private const int TrayCommandStop = 6;
    private const int TrayCommandDismiss = 7;
    private const int TrayLingerMilliseconds = 5000;
    private const int LingerToleranceMilliseconds = 500;
    private const int TrayTitleCharacters = 256;
    private const uint WmCommand = 0x0111;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const string EventSource = "headless-run";
    private const int EventQueryCount = 200;
    private const int EventExited = 1;
    private const int EventExitedNonzero = 2;
    private const int EventTimedOut = 3;
    private const int EventLaunchFailure = 5;
    private const int EventDetached = 6;
    private const int ScriptExitCode = 7;
    private const int ConfigKeyNotFound = 1;
    private const int ConfigNothingToUnset = 5;
    private const int StoreStubExitCode = 9009;
    private const string OldNodeVersion = "v20.11.0";
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, char[] text, int capacity);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, char[] lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
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

        // A copy of this harness renamed node.exe stands in for an old Node in the TypeScript gate test.
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.Write(OldNodeVersion + "\n");
            return 0;
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
            Test("ps1 scripts run under pwsh, or Windows PowerShell with --engine powershell", () =>
            {
                var directory = Path.Combine(root, "ps scripts");
                Directory.CreateDirectory(directory);
                var script = Path.Combine(directory, "echo args.ps1");
                File.WriteAllText(script, "param([string] $First, [string] $Second)\r\n[Console]::Out.Write(\"[$First] [$Second] $($PSVersionTable.PSEdition)\")\r\nexit 7\r\n");
                // pwsh -File passes arguments as literal strings: no variable expansion.
                var run = Run(launcher, "-- " + Quote(script) + " \"a b\" $env:HEADLESS_TEST");
                Assert(run.Code == 7, "pwsh exit code: " + run.Code + " " + run.Error);
                Assert(run.Output == "[a b] [$env:HEADLESS_TEST] Core", "pwsh arguments: " + run.Output);
                foreach (var engine in new[] { "--engine powershell", "--engine=powershell", "--engine PowerShell" })
                {
                    var legacy = Run(launcher, engine + " -- " + Quote(script) + " \"a b\" c");
                    Assert(legacy.Code == 7 && legacy.Output == "[a b] [c] Desktop", engine + ": " + legacy.Output + legacy.Error);
                }

                var missing = Run(launcher, "--env " + Quote("PATH=" + directory) + " -- " + Quote(script));
                Assert(missing.Code == LaunchFailureExitCode && missing.Error.Contains("pwsh") && missing.Error.Contains("--engine powershell"), "missing pwsh: " + missing.Error);
                var removed = Run(launcher, "--windows-powershell -- " + Quote(script));
                Assert(removed.Code == LaunchFailureExitCode && removed.Error.Contains("--engine powershell"), "removed option: " + removed.Error);
            });
            Test(".exe and .com programs run directly", () =>
            {
                var directory = Path.Combine(root, "com program");
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Self)!))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }

                // A .com that is a PE image starts like any .exe; the copy finds its .dll beside it.
                var com = Path.Combine(directory, "helper.com");
                File.Copy(Path.Combine(directory, Path.GetFileName(Self)), com);
                var direct = Path.Combine(root, "com-direct.json");
                var wrapped = Path.Combine(root, "com-wrapped.json");
                var tail = " child dump {0} \"a b\" x&y %PATH%";
                Assert(Run(com, string.Format(CultureInfo.InvariantCulture, tail, Quote(direct))[1..]).Code == 0, "direct .com");
                Assert(Run(launcher, "-- " + Quote(com) + string.Format(CultureInfo.InvariantCulture, tail, Quote(wrapped))).Code == 0, "wrapped .com");
                Assert(File.ReadAllText(direct) == File.ReadAllText(wrapped), ".com argv changed");
                Assert(Run(launcher, "-- " + Quote(com) + " child exit 23").Code == 23, ".com exit code");
                var system = Path.Combine(Environment.SystemDirectory, "tree.com");
                if (File.Exists(system))
                {
                    var tree = Run(launcher, "-- " + Quote(system) + " /?");
                    Assert(tree.Code == 0 && tree.Output.Length != 0, "tree.com: " + tree.Code + " " + tree.Error);
                }

                var forced = Run(launcher, "--engine node -- " + Quote(Self) + " child exit 0");
                Assert(forced.Code == LaunchFailureExitCode && forced.Error.Contains("--force-engine"), "--engine on an .exe: " + forced.Error);
            });
            Test("every built-in engine runs its script with the arguments untouched", () =>
            {
                var directory = Path.Combine(root, "engine scripts");
                Directory.CreateDirectory(directory);
                const string vbs = "Dim s, i\r\nFor i = 0 To WScript.Arguments.Count - 1\r\nIf i > 0 Then s = s & \"|\"\r\ns = s & WScript.Arguments(i)\r\nNext\r\nWScript.Echo s\r\nWScript.Quit 7\r\n";
                const string node = "console.log(process.argv.slice(2).join('|')); process.exit(7);\n";
                (string Engine, string Extension, string Script)[] cases = [
                    ("", ".js", node),
                    ("", ".mjs", node),
                    ("", ".cjs", node),
                    ("", ".ts", "const values: string[] = process.argv.slice(2);\nconsole.log(values.join('|'));\nprocess.exit(7);\n"),
                    ("deno", ".ts", "console.log(Deno.args.join('|')); Deno.exit(7);\n"),
                    ("bun", ".js", node),
                    ("", ".py", "import sys\nprint('|'.join(sys.argv[1:]))\nsys.exit(7)\n"),
                    ("", ".pl", "print join('|', @ARGV), \"\\n\"; exit 7;\n"),
                    ("", ".rb", "puts ARGV.join('|'); exit 7\n"),
                    ("", ".php", "<?php echo implode('|', array_slice($argv, 1)), \"\\n\"; exit(7);\n"),
                    ("", ".lua", "print(table.concat(arg, '|')); os.exit(7)\n"),
                    ("", ".R", "cat(paste(commandArgs(trailingOnly = TRUE), collapse = '|'), '\\n'); quit(status = 7)\n"),
                    ("", ".tcl", "puts [join $argv |]; exit 7\n"),
                    ("", ".sh", "IFS='|'; echo \"$*\"; exit 7\n"),
                    ("", ".vbs", vbs),
                    ("", ".wsf", "<job><script language=\"VBScript\">\r\n" + vbs + "</script></job>\r\n"),
                    ("cscript", ".js", "var a = []; for (var i = 0; i < WScript.Arguments.length; i++) { a.push(WScript.Arguments(i)); } WScript.Echo(a.join('|')); WScript.Quit(7);\r\n")
                ];
                foreach (var (engine, extension, script) in cases)
                {
                    var name = (engine.Length == 0 ? "default" : engine) + extension;
                    var file = Path.Combine(directory, "args " + name.Replace('.', '-') + extension);
                    File.WriteAllText(file, script);
                    var run = Run(launcher, (engine.Length == 0 ? "" : "--engine " + engine + " ") + "-- " + Quote(file) + " a \"b c\"");
                    if (run.Code == LaunchFailureExitCode && (run.Error.Contains("interpreter was not found") || run.Error.Contains("cannot run '.ts' files")))
                    {
                        Console.WriteLine("NOTE skipped " + name + ": " + run.Error.Trim());
                        continue;
                    }

                    if (run.Code == StoreStubExitCode && extension == ".py")
                    {
                        Console.WriteLine("NOTE skipped " + name + ": python.exe is the Microsoft Store stub, and no py.exe is installed");
                        continue;
                    }

                    Assert(run.Code == ScriptExitCode, name + " exit code " + run.Code + ": " + run.Error);
                    Assert(run.Output.Trim() == "a|b c", name + " arguments: " + run.Output + run.Error);
                }
            });
            Test("an old Node fails a TypeScript script with its version", () =>
            {
                var directory = Path.Combine(root, "old node");
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Self)!))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }

                File.Copy(Path.Combine(directory, Path.GetFileName(Self)), Path.Combine(directory, "node.exe"));
                var script = Path.Combine(directory, "typed.ts");
                File.WriteAllText(script, "const x: number = 1;\n");
                var run = Run(launcher, "--env " + Quote("PATH=" + directory) + " -- " + Quote(script));
                Assert(run.Code == LaunchFailureExitCode && run.Error.Contains(OldNodeVersion) && run.Error.Contains("22.18.0"), "old node: " + run.Error);
            });
            Test("a file no engine runs and that is no program fails clearly", () =>
            {
                var module = Path.Combine(root, "module.psm1");
                File.WriteAllText(module, "function Get-Nothing {}\n");
                var run = Run(launcher, "-- " + Quote(module));
                Assert(run.Code == LaunchFailureExitCode && run.Error.Contains("extension.psm1") && run.Error.Contains("--engine"), "psm1: " + run.Error);
                var text = Path.Combine(root, "forced.txt");
                File.WriteAllText(text, "console.log(process.argv.slice(2).join('|')); process.exit(4);\n");
                var refused = Run(launcher, "--engine node -- " + Quote(text));
                Assert(refused.Code == LaunchFailureExitCode && refused.Error.Contains("--force-engine") && refused.Error.Contains(".txt"), "mismatch: " + refused.Error);
                var forced = Run(launcher, "--engine node --force-engine -- " + Quote(text) + " x \"y z\"");
                if (forced.Code == LaunchFailureExitCode && forced.Error.Contains("interpreter was not found"))
                {
                    Console.WriteLine("NOTE --force-engine run skipped: " + forced.Error.Trim());
                    return;
                }

                Assert(forced.Code == 4 && forced.Output.Trim() == "x|y z", "--force-engine: " + forced.Code + " " + forced.Output + forced.Error);
            });
            Test("config: user engines, layers, get, set, add, unset and list", () =>
            {
                var home = Path.Combine(root, "config home");
                Dictionary<string, string> environment = new()
                {
                    ["APPDATA"] = Path.Combine(home, "appdata"),
                    ["ProgramData"] = Path.Combine(home, "programdata")
                };
                var global = Path.Combine(home, "appdata", "headless-run", "config.json");
                var system = Path.Combine(home, "programdata", "headless-run", "config.json");
                (int Code, string Output, string Error) Config(string arguments, string? directory = null)
                {
                    return Run(launcher, "config " + arguments, directory: directory, environment: environment);
                }

                void Ok(string arguments)
                {
                    var result = Config(arguments);
                    Assert(result.Code == 0, "config " + arguments + ": " + result.Code + " " + result.Error);
                }

                Ok("--system engine.probe.path " + Quote(Self));
                Ok("--system engine.probe.arguments child");
                Ok("--system --add engine.probe.arguments script");
                Ok("--system --add engine.probe.arguments system");
                Ok("--system extension.probe probe");
                var script = Path.Combine(home, "run me.probe");
                File.WriteAllText(script, "");
                (int Code, string Output, string Error) Probe(string options = "", string? directory = null)
                {
                    return Run(launcher, options + "-- " + Quote(script) + " a \"b c\"", directory: directory, environment: environment);
                }

                var run = Probe();
                Assert(run.Code == ScriptExitCode && run.Output == "system|run me.probe|a|b c\n", "system engine: " + run.Output + run.Error);
                // A global value replaces the system one, key by key.
                Ok("engine.probe.arguments child");
                Ok("--add engine.probe.arguments script");
                Ok("--global --add engine.probe.arguments global");
                Assert(File.Exists(global), "writes default to --global");
                Assert(Probe().Output == "global|run me.probe|a|b c\n", "global over system");
                var file = Path.Combine(home, "project.json");
                File.WriteAllText(file, "{\"engine\": {\"probe\": {\"arguments\": [\"child\", \"script\", \"file\"]}}}");
                Assert(Probe("--config project.json ", home).Output == "file|run me.probe|a|b c\n", "--config over global, relative to the launcher's directory");
                var get = Config("engine.probe.arguments");
                Assert(get.Code == 0 && get.Output == "child\nscript\nglobal\n", "get: " + get.Output);
                Assert(Config("--system engine.probe.arguments").Output == "child\nscript\nsystem\n", "get from one scope");
                Assert(Config("engine.probe.quoteCommand").Code == ConfigKeyNotFound, "get of an unset key");
                var list = Config("--list --show-origin");
                Assert(list.Code == 0 && list.Output.Contains("builtin\tengine.node.path=node.exe\n")
                    && list.Output.Contains("file:" + global + "\tengine.probe.arguments=global\n")
                    && list.Output.Contains("file:" + system + "\textension.probe=probe\n"), "list: " + list.Output);
                Assert(Config("--file " + Quote(file) + " --list").Output == "engine.probe.arguments=child\nengine.probe.arguments=script\nengine.probe.arguments=file\n", "list one file");
                Ok("engine.probe.extensions py,.Probe");
                Assert(Config("engine.probe.extensions").Output == ".py\n.probe\n", "extensions split and lowered");
                Assert(File.ReadAllText(global).IndexOf("\"$schema\"", StringComparison.Ordinal) < File.ReadAllText(global).IndexOf("\"engine\"", StringComparison.Ordinal), "key order kept");
                Assert(Config("--unset engine.probe.arguments").Code == 0, "unset a key");
                Assert(Probe().Output == "system|run me.probe|a|b c\n", "unset falls back to system");
                Assert(Config("--unset engine.probe").Code == 0 && Config("--unset engine.probe").Code == ConfigNothingToUnset, "unset an engine");
                foreach (var bad in new[] { "bogus.key 1", "engine.probe.quoteCommand maybe", "engine.probe.extensions .a.b", "--add extension.probe x", "--global --system --list", "--list --edit", "--show-origin engine.probe.path", "--frobnicate", "--file", "engine.probe.path a b" })
                {
                    var refused = Config(bad);
                    Assert(refused.Code == LaunchFailureExitCode && refused.Error.Contains("headless-run:"), "config " + bad + ": " + refused.Code + " " + refused.Error);
                }

                File.WriteAllText(file, "{\"engine\": {\"probe\": {\"argument\": []}}}");
                var malformed = Probe("--config " + Quote(file) + " ");
                Assert(malformed.Code == LaunchFailureExitCode && malformed.Error.Contains(file) && malformed.Error.Contains("engine.probe.argument"), "unknown key: " + malformed.Error);
                File.WriteAllText(file, "{\"engine\": ");
                malformed = Probe("--config " + Quote(file) + " ");
                Assert(malformed.Code == LaunchFailureExitCode && malformed.Error.Contains("line 1"), "invalid JSON: " + malformed.Error);
                var missing = Probe("--config " + Quote(Path.Combine(home, "absent.json")) + " ");
                Assert(missing.Code == LaunchFailureExitCode && missing.Error.Contains("absent.json"), "missing --config: " + missing.Error);
                Ok("--system engine.broken.path " + Quote(Path.Combine(home, "absent.exe")));
                var broken = Probe("--engine broken --force-engine ");
                Assert(broken.Code == LaunchFailureExitCode && broken.Error.Contains("engine.broken.path") && broken.Error.Contains("file:" + system), "missing interpreter: " + broken.Error);
                var unknown = Probe("--engine nothing ");
                Assert(unknown.Code == LaunchFailureExitCode && unknown.Error.Contains("Known engines:") && unknown.Error.Contains("probe"), "unknown engine: " + unknown.Error);
            });
            Test("config --edit creates, opens and validates the file", () =>
            {
                var home = Path.Combine(root, "edit home");
                var file = Path.Combine(home, "edited.json");
                Dictionary<string, string> environment = new()
                {
                    ["HEADLESS_RUN_EDITOR"] = Quote(Self) + " child edit"
                };
                var edit = Run(launcher, "config --file " + Quote(file) + " --edit", environment: environment);
                Assert(edit.Code == 0, "edit: " + edit.Error);
                var get = Run(launcher, "config --file " + Quote(file) + " engine.edited.path");
                Assert(get.Output == "edited.exe\n", "edited value: " + get.Output + get.Error);
                environment["HEADLESS_RUN_EDITOR"] = Quote(Self) + " child edit-broken";
                var broken = Run(launcher, "config --file " + Quote(file) + " --edit", environment: environment);
                Assert(broken.Code == LaunchFailureExitCode && broken.Error.Contains("line 4"), "broken edit: " + broken.Error);
                File.Delete(file);
                environment["HEADLESS_RUN_EDITOR"] = "cmd /c exit 0";
                Assert(Run(launcher, "config --file " + Quote(file) + " --edit", environment: environment).Code == 0, "skeleton");
                Assert(File.ReadAllText(file).Contains("\"$schema\""), "new file has $schema");
                // config is a subcommand only as the first argument.
                var program = Run(launcher, "-- config --list");
                Assert(program.Code == LaunchFailureExitCode && !program.Output.Contains("engine."), "config after --: " + program.Output);
            });
            Test("launch and usage failures produce diagnostics", () =>
            {
                foreach (var command in new[] { "-- missing-headless-test-" + Guid.NewGuid() + ".exe", "--bad", "--", "", "--timeout 0s -- x", "--timeout -1s -- x", "--timeout 1 -- x", "--timeout 999999999h -- x", "--detach --timeout 1s -- x", "--tray --detach -- x", "--tray=yes -- x", "--env broken -- x", "--cwd", "--cwd \"\" -- x", "--cwd \"unterminated", "--below-normal -- x", "--priority bogus -- x", "--priority -- x", "--priority= -- x", "--append=yes -- x", "--bad=1 -- x", "--elevate=yes -- x", "--elevated-relay bogus -- x", "--windows-powershell -- " + Quote(Self) + " child exit 0", "--force-engine -- x", "--engine -- x", "--engine= -- x", "--engine nope -- x.js", "--config " + Quote(Path.Combine(root, "absent.json")) + " -- " + Quote(Self) + " child exit 0", "--force-engine=yes -- x" })
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
            Test("--elevate relays stdin, stdout, stderr and the exit code", () =>
            {
                foreach (var code in new[] { "0", "7", "-1073741819" })
                {
                    var run = Run(launcher, "--elevate -- " + Quote(Self) + " child echo " + code, relay: true, input: "line one\nline two\n");
                    Assert(run.Code == int.Parse(code, CultureInfo.InvariantCulture), "relayed exit code " + run.Code + ": " + run.Error);
                    Assert(run.Output == "line one\nline two\n" && run.Error == "stderr\n", "relayed streams: " + run.Output + "|" + run.Error);
                }
                // Without stdin the child still sees EOF rather than waiting.
                var empty = Run(launcher, "--elevate -- " + Quote(Self) + " child echo 3", relay: true);
                Assert(empty.Code == 3 && empty.Output.Length == 0, "relayed empty stdin: " + empty.Error);
            });
            Test("--elevate relays the caller's environment and working directory", () =>
            {
                var directory = Path.Combine(root, "elevated cwd");
                Directory.CreateDirectory(directory);
                var output = Path.Combine(directory, "relative-stdout.txt");
                // Relative paths resolve against the caller's directory, as they do without --elevate.
                var run = Run(launcher, "--elevate --env HEADLESS_TEST=relayed --stdout relative-stdout.txt -- " + Quote(Self) + " child state state.json", relay: true, directory: directory);
                Assert(run.Code == 0, run.Error);
                Assert(File.Exists(output), "relative --stdout resolved elsewhere");
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "state.json")));
                Assert(state.RootElement.GetProperty("cwd").GetString() == directory, "relayed cwd");
                Assert(state.RootElement.GetProperty("env").GetString() == "relayed", "--env applied in the copy");
                Assert(state.RootElement.GetProperty("console").GetInt64() == 0, "console window created");
            });
            Test("--elevate timeout and diagnostics come back through the relay", () =>
            {
                var run = Run(launcher, "--elevate --timeout 50ms -- " + Quote(Self) + " child sleep", relay: true);
                Assert(run.Code == TimeoutExitCode && run.Error.Contains("Timeout expired"), "relayed timeout: " + run.Code + " " + run.Error);
                var missing = Run(launcher, "--elevate -- missing-headless-test-" + Guid.NewGuid() + ".exe", relay: true);
                Assert(missing.Code == LaunchFailureExitCode && missing.Error.Contains("headless-run:"), "relayed launch failure: " + missing.Error);
            });
            Test("--elevate: a timeout kills the tree", () => TreeTest("timeout", true));
            Test("--elevate: killing the caller kills the copy and the tree", () => TreeTest("kill", true));
            Test("--elevate: the child's exit kills remaining descendants", () => TreeTest("exit", true));
            Test("--elevate --detach starts the child and returns", () =>
            {
                var marker = Path.Combine(root, "elevated-detached.txt");
                var run = Run(launcher, "--elevate --detach -- " + Quote(Self) + " child later " + Quote(marker), relay: true);
                Assert(run.Code == 0, run.Error);
                WaitFor(() => File.Exists(marker), "elevated detached child did not finish");
            });
            Test("--event-log writes one Application event per run, with the outcome's id", () =>
            {
                // Each run gets its own working directory, whose name finds that run's events in the log.
                string EventDirectory(string name)
                {
                    return Path.Combine(root, "event-" + name + "-" + Guid.NewGuid().ToString("N"));
                }

                var exited = EventDirectory("exited");
                Assert(Run(launcher, "--event-log --create-cwd --cwd " + Quote(exited) + " -- " + Quote(Self) + " child exit 0").Code == 0, "exit 0");
                ExpectEvent(exited, EventExited, "exited with code 0");
                var nonzero = EventDirectory("nonzero");
                Assert(Run(launcher, "--event-log --create-cwd --cwd " + Quote(nonzero) + " -- " + Quote(Self) + " child exit 7").Code == 7, "exit 7");
                ExpectEvent(nonzero, EventExitedNonzero, "exited with code 7");
                var timeout = EventDirectory("timeout");
                Assert(Run(launcher, "--event-log --timeout 50ms --create-cwd --cwd " + Quote(timeout) + " -- " + Quote(Self) + " child sleep").Code == TimeoutExitCode, "timeout");
                ExpectEvent(timeout, EventTimedOut, "timeout expired");
                var failure = EventDirectory("failure");
                Assert(Run(launcher, "--event-log --create-cwd --cwd " + Quote(failure) + " -- missing-headless-test.exe").Code == LaunchFailureExitCode, "failure");
                ExpectEvent(failure, EventLaunchFailure, "Launcher failure");
                var detached = EventDirectory("detached");
                var marker = Path.Combine(root, "event-detached.txt");
                Assert(Run(launcher, "--event-log --stdout NUL --stderr NUL --detach --create-cwd --cwd " + Quote(detached) + " -- " + Quote(Self) + " child later " + Quote(marker)).Code == 0, "detach");
                ExpectEvent(detached, EventDetached, "started detached");
                WaitFor(() => File.Exists(marker), "detached child did not finish");
                // Under --elevate only the copy records the outcome, so a relayed timeout is one event, not two.
                var relayed = EventDirectory("relayed");
                Assert(Run(launcher, "--elevate --event-log --timeout 50ms --create-cwd --cwd " + Quote(relayed) + " -- " + Quote(Self) + " child sleep", relay: true).Code == TimeoutExitCode, "relayed timeout");
                ExpectEvent(relayed, EventTimedOut, "timeout expired");
                var silent = EventDirectory("silent");
                Assert(Run(launcher, "--create-cwd --cwd " + Quote(silent) + " -- " + Quote(Self) + " child exit 0").Code == 0, "no event log");
                Assert(Events(silent).Count == 0, "an event was written without --event-log");
            });
            Test("--elevate does nothing when the caller is already elevated", () =>
            {
                if (!Environment.IsPrivilegedProcess)
                {
                    // Unelevated, the real path would show a UAC prompt, which no unattended test can answer.
                    Console.WriteLine("NOTE not elevated; the no-op path is checked only in an elevated run, such as CI");
                    return;
                }

                var run = Run(launcher, "--elevate -- " + Quote(Self) + " child echo 5", input: "x");
                Assert(run.Code == 5 && run.Output == "x", "elevated no-op: " + run.Error);
            });
            Test("--tray: Stop from the menu ends the tree with 130", () =>
            {
                var marker = Path.Combine(root, "tray-stop.pids");
                using var process = Start(launcher, "--tray -- " + Quote(Self) + " child tree " + Quote(marker) + " tray");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                WaitFor(() => File.Exists(marker) && File.ReadAllText(marker).Contains(','), "tree not ready");
                var pids = File.ReadAllText(marker).Split(',').Select(int.Parse).ToArray();
                var tray = TrayWindow(process.Id);
                Assert(Title(tray) == "headless-run: running", "tray state: " + Title(tray));
                SendMessage(tray, WmCommand, TrayCommandStop, 0);
                Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "launcher did not exit after Stop");
                Assert(process.ExitCode == StoppedExitCode, "stop exit code " + process.ExitCode);
                Assert(error.GetAwaiter().GetResult().Contains("Stopped from the tray"), "stop diagnostic");
                output.GetAwaiter().GetResult();
                foreach (var pid in pids)
                {
                    WaitFor(() => !Alive(pid), "surviving descendant " + pid);
                }
            });
            Test("--tray: the viewer shows teed output, and inherited streams still get it", () =>
            {
                var marker = Path.Combine(root, "tray-chatty.txt");
                using var process = Start(launcher, "--tray -- " + Quote(Self) + " child chatty " + Quote(marker));
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                WaitFor(() => File.Exists(marker), "chatty child not ready");
                var tray = TrayWindow(process.Id);
                var viewer = OwnedWindow(ViewerWindowClass, process.Id);
                Assert(!IsWindowVisible(viewer), "viewer visible before Show");
                SendMessage(tray, WmCommand, TrayCommandShowOutput, 0);
                Assert(IsWindowVisible(viewer), "viewer hidden after Show");
                WaitFor(() =>
                {
                    SendMessage(tray, WmCommand, TrayCommandShowOutput, 0);
                    var text = ViewerText(viewer);
                    return text.Contains("chatty stdout") && text.Contains("chatty stderr");
                }, "viewer text: " + ViewerText(viewer));
                SendMessage(tray, WmCommand, TrayCommandHideOutput, 0);
                Assert(!IsWindowVisible(viewer), "viewer visible after Hide");
                SendMessage(tray, WmCommand, TrayCommandStop, 0);
                Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "launcher did not exit after Stop");
                Assert(output.GetAwaiter().GetResult() == "chatty stdout\n", "teed stdout: " + output.Result);
                Assert(error.GetAwaiter().GetResult().StartsWith("chatty stderr\n", StringComparison.Ordinal), "teed stderr: " + error.Result);
            });
            Test("--tray: log files are tailed, and the finish keeps the exit code until Dismiss", () =>
            {
                var output = Path.Combine(root, "tray-stdout.txt");
                var error = Path.Combine(root, "tray-stderr.txt");
                File.WriteAllText(output, "earlier run\n");
                using var process = Start(launcher, "--tray --append --stdout " + Quote(output) + " --stderr " + Quote(error) + " -- " + Quote(Self) + " child echo 7", input: "from stdin\n");
                var tray = TrayWindow(process.Id);
                WaitFor(() => Title(tray) == "headless-run: exited 7", "finish state: " + Title(tray));
                var viewer = OwnedWindow(ViewerWindowClass, process.Id);
                SendMessage(tray, WmCommand, TrayCommandShowOutput, 0);
                var text = ViewerText(viewer);
                Assert(text.Contains("from stdin") && text.Contains("stderr") && !text.Contains("earlier run"), "tailed logs: " + text);
                var clock = Stopwatch.StartNew();
                SendMessage(tray, WmCommand, TrayCommandDismiss, 0);
                Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "launcher did not exit after Dismiss");
                Assert(clock.ElapsedMilliseconds < TrayLingerMilliseconds, "Dismiss waited for the linger");
                Assert(process.ExitCode == 7, "finished exit code " + process.ExitCode);
            });
            Test("--tray: a timeout still returns 124, after the icon lingers", () =>
            {
                var clock = Stopwatch.StartNew();
                var run = Run(launcher, "--tray --timeout 300ms -- " + Quote(Self) + " child sleep");
                Assert(run.Code == TimeoutExitCode && run.Error.Contains("Timeout expired"), "tray timeout: " + run.Code + " " + run.Error);
                Assert(clock.ElapsedMilliseconds >= TrayLingerMilliseconds - LingerToleranceMilliseconds, "no linger: " + clock.ElapsedMilliseconds + " ms");
            });
            Test("--elevate --tray: the caller hosts the icon, and Stop ends the elevated tree", () =>
            {
                var marker = Path.Combine(root, "tray-elevated.pids");
                using var process = Start(launcher, "--elevate --tray -- " + Quote(Self) + " child tree " + Quote(marker) + " tray", relay: true);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                WaitFor(() => File.Exists(marker) && File.ReadAllText(marker).Contains(','), "tree not ready");
                var pids = File.ReadAllText(marker).Split(',').Select(int.Parse).ToArray();
                var copies = LauncherProcesses().Where(id => id != process.Id).ToArray();
                Assert(copies.Length == 1, "expected one relaying launcher copy, found " + copies.Length);
                var tray = TrayWindow(process.Id);
                Assert(FindOwnedWindow(TrayWindowClass, copies[0]) == 0, "the elevated copy shows a second icon");
                SendMessage(tray, WmCommand, TrayCommandStop, 0);
                Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "caller did not exit after Stop");
                Assert(process.ExitCode == StoppedExitCode, "elevated stop exit code " + process.ExitCode);
                output.GetAwaiter().GetResult();
                Assert(error.GetAwaiter().GetResult().Contains("Stopped from the tray"), "elevated stop diagnostic");
                foreach (var pid in pids.Concat(copies))
                {
                    WaitFor(() => !Alive(pid), "surviving process " + pid);
                }
            });
            Test("--elevate --tray: relayed streams reach the viewer and the caller", () =>
            {
                using var process = Start(launcher, "--elevate --tray -- " + Quote(Self) + " child echo 5", relay: true, input: "relayed line\n");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var tray = TrayWindow(process.Id);
                WaitFor(() => Title(tray) == "headless-run: exited 5", "elevated finish state: " + Title(tray));
                var viewer = OwnedWindow(ViewerWindowClass, process.Id);
                SendMessage(tray, WmCommand, TrayCommandShowOutput, 0);
                var text = ViewerText(viewer);
                Assert(text.Contains("relayed line") && text.Contains("stderr"), "elevated viewer: " + text);
                SendMessage(tray, WmCommand, TrayCommandDismiss, 0);
                Assert(process.WaitForExit(LauncherExitTimeoutMilliseconds), "caller did not exit after Dismiss");
                Assert(process.ExitCode == 5 && output.GetAwaiter().GetResult() == "relayed line\n", "elevated tray relay: " + process.ExitCode + " " + error.GetAwaiter().GetResult());
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
            case "echo":
                Console.Write(Console.In.ReadToEnd());
                Console.Error.Write("stderr\n");
                return int.Parse(args[1], CultureInfo.InvariantCulture);
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
            case "chatty":
                Console.Out.Write("chatty stdout\n");
                Console.Out.Flush();
                Console.Error.Write("chatty stderr\n");
                Console.Error.Flush();
                PublishMarker(args[1], "ready");
                Thread.Sleep(ChildSleepMilliseconds);
                return 0;
            case "later":
                Thread.Sleep(DetachedChildDelayMilliseconds);
                PublishMarker(args[1], "done");
                return 0;
            case "script":
                // An engine's interpreter: its engine arguments, then the script (by file name), then the script's arguments.
                Console.Write(string.Join("|", args[1..].Select(argument => File.Exists(argument) ? Path.GetFileName(argument) : argument)) + "\n");
                return ScriptExitCode;
            case "edit":
                File.WriteAllText(args[^1], "{\"engine\": {\"edited\": {\"path\": \"edited.exe\"}}}");
                return 0;
            case "edit-broken":
                File.WriteAllText(args[^1], "{\n  \"engine\": {\n    \"edited\": \n  }\n}");
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

    private static void TreeTest(string mode, bool relay = false)
    {
        var marker = Path.Combine(root, mode + (relay ? "-relay" : "") + ".pids");
        using var process = Start(launcher, (relay ? "--elevate " : "") + (mode == "timeout" ? "--timeout 3s " : "") + "-- " + Quote(Self) + " child tree " + Quote(marker) + " " + mode, relay);
        int[] pids = [];
        try
        {
            WaitFor(() => File.Exists(marker) && File.ReadAllText(marker).Contains(','), "tree not ready");
            pids = File.ReadAllText(marker).Split(',').Select(int.Parse).ToArray();
            if (relay)
            {
                // The relaying copy is a second launcher process, and it is part of the tree that must end.
                var copies = LauncherProcesses().Where(id => id != process.Id).ToArray();
                // In exit mode the child, and with it the copy, may already be gone.
                Assert(copies.Length == 1 || (mode == "exit" && copies.Length == 0), "expected one relaying launcher copy, found " + copies.Length);
                pids = [.. pids, .. copies];
            }
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

    private static int[] LauncherProcesses()
    {
        List<int> ids = [];
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(launcher)))
        {
            using (candidate)
            {
                try
                {
                    if (string.Equals(candidate.MainModule?.FileName, launcher, StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(candidate.Id);
                    }
                }
                catch (Win32Exception)
                {
                    // Another user's or an elevated launcher: not one this test started.
                }
                catch (InvalidOperationException)
                {
                    // It exited while being listed.
                }
            }
        }
        return [.. ids];
    }

    // The launcher's hidden tray window: its title carries the job's state, and WM_COMMAND drives its menu.
    private static nint TrayWindow(int processId)
    {
        return OwnedWindow(TrayWindowClass, processId);
    }

    private static nint OwnedWindow(string className, int processId)
    {
        nint found = 0;
        WaitFor(() => (found = FindOwnedWindow(className, processId)) != 0, className + " window of " + processId + " not found");
        return found;
    }

    private static nint FindOwnedWindow(string className, int processId)
    {
        nint window = 0;
        while ((window = FindWindowEx(0, window, className, null)) != 0)
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId)
            {
                return window;
            }
        }

        return 0;
    }

    private static string Title(nint window)
    {
        var buffer = new char[TrayTitleCharacters];
        var length = GetWindowText(window, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static string ViewerText(nint viewer)
    {
        var edit = FindWindowEx(viewer, 0, "Edit", null);
        var length = (int) SendMessage(edit, WmGetTextLength, 0, 0);
        var buffer = new char[length + 1];
        var copied = (int) SendMessage(edit, WmGetText, buffer.Length, buffer);
        return new string(buffer, 0, copied);
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
    private static Process Start(string executable, string arguments, bool relay = false, string? input = null, string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo start = new(executable)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // The launcher's config files live under these, so no test ever reads or writes the machine's own.
        start.Environment["APPDATA"] = Path.Combine(root, "appdata");
        start.Environment["ProgramData"] = Path.Combine(root, "programdata");
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        if (relay)
        {
            // The launcher's test seam: --elevate takes its full relay path with the "open" verb, so no UAC prompt.
            start.Environment[RelayWithoutUacVariable] = "1";
        }

        if (directory is not null)
        {
            start.WorkingDirectory = directory;
        }

        var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        return process;
    }
    private static (int Code, string Output, string Error) Run(string executable, string arguments, bool relay = false, string? input = null, string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = Start(executable, arguments, relay, input, directory, environment);
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
    // The launcher's Application-log events whose text names this directory, newest first.
    private static List<(int Id, string Text)> Events(string directory)
    {
        using var query = Process.Start(new ProcessStartInfo("wevtutil.exe")
        {
            ArgumentList = { "qe", "Application", "/q:*[System[Provider[@Name='" + EventSource + "']]]", "/c:" + EventQueryCount, "/rd:true", "/f:xml" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var xml = query.StandardOutput.ReadToEnd();
        query.WaitForExit();
        Assert(query.ExitCode == 0, "wevtutil failed: " + query.ExitCode);
        XNamespace events = "http://schemas.microsoft.com/win/2004/08/events/event";
        return XElement.Parse("<Events>" + xml + "</Events>").Elements(events + "Event")
            .Select(entry => (Id: int.Parse(entry.Element(events + "System")!.Element(events + "EventID")!.Value, CultureInfo.InvariantCulture),
                Text: string.Concat(entry.Descendants(events + "Data").Select(data => data.Value))))
            .Where(entry => entry.Text.Contains("Working directory: " + directory + "\n", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
    private static void ExpectEvent(string directory, int id, string text)
    {
        List<(int Id, string Text)> found = [];
        WaitFor(() => (found = Events(directory)).Count != 0, "no event for " + directory);
        Assert(found.Count == 1, found.Count + " events for one run: " + directory);
        Assert(found[0].Id == id, "event id " + found[0].Id + ", expected " + id + ": " + found[0].Text);
        Assert(found[0].Text.Contains(text, StringComparison.OrdinalIgnoreCase), "event text: " + found[0].Text);
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
