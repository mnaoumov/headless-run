# headless-run

Run a Windows console program without a console window, keep its exact command line, and return its exit code. Designed for Task Scheduler and background automation.

The launcher is a Windows-subsystem executable. It starts console programs with `CREATE_NO_WINDOW`, using documented Win32 APIs. It requires Windows 10 / Windows Server 2016 or later. Release executables are native, self-contained, and need no .NET runtime installed.

## Install

Download `headless-run-win-x64.exe`, `headless-run-win-x86.exe` or `headless-run-win-arm64.exe` from [Releases](https://github.com/mnaoumov/headless-run/releases), verify it against the adjacent `.sha256` file, and rename it to `headless-run.exe` in a directory on `PATH`.

The binaries are unsigned; Windows may ask you to confirm a downloaded executable.

## Usage

```text
headless-run.exe [options] -- <program> [arguments...]
```

For a scheduled task, set **Program/script** to the full path of `headless-run.exe`, and **Add arguments** to:

```text
--stderr "C:\Logs\nightly-stderr.txt" --stdout "C:\Logs\nightly-stdout.txt" --append --timeout 30m -- "C:\Scripts\nightly.ps1"
```

Quote the program path when it contains spaces. The launcher removes the option prefix, `--`, and separating spaces/tabs; it forwards every character from the program onwards unchanged. It never splits or re-quotes child arguments. Child programs still interpret their own command line according to their own rules.

A calling shell processes its own syntax before the launcher sees it; for precise automation use Task Scheduler's argument field or an API that accepts a raw command line.

## Programs and scripts

The extension of the program picks an **engine**, the interpreter that runs it. The comparison ignores case.

1. **A native executable**, `.exe` or `.com`, and any extension no engine claims: the raw text from the program onwards goes to `CreateProcessW` as the command line. There is no shell expansion: `&`, `^`, `%`, `|`, `$` and redirection characters are ordinary characters, and the program parses its own arguments. A file that is no Windows program and that no engine runs, such as a `.psm1`, is a launcher failure (125) that names the setting which would run it.
2. **A script**: runs as `"<interpreter>" <engine arguments> <raw text>`. The raw text is never split or re-quoted, so the interpreter applies its own argument rules to it, and the script receives its arguments as the interpreter parses them.

`--engine NAME` picks the engine instead of the extension, such as `--engine powershell` for Windows PowerShell 5.1 or `--engine deno` for a `.ts` file. An engine that does not accept the program's extension is a launcher failure (125) naming the engine, the extension and `--force-engine`, which runs the program with that engine anyway. Each engine finds its interpreter on `PATH` (an `--env PATH=...` override is searched instead of the launcher's own), or at a path; a missing one is a launcher failure (125) that says what was tried and how to point the engine at it. There is never a silent fallback to another engine.

The built-in engines:

| Engine | Interpreter | Arguments before the script | Accepts | Default for |
| --- | --- | --- | --- | --- |
| `cmd` | `%ComSpec%` | `/d /s /c`, and the raw text in one quote pair | `.cmd`, `.bat` | `.cmd`, `.bat` |
| `pwsh` | `pwsh.exe` (PowerShell 7) | `-NoProfile -NonInteractive -File` | `.ps1` | `.ps1` |
| `powershell` | Windows PowerShell 5.1 | `-NoProfile -NonInteractive -File` | `.ps1` | |
| `node` | `node.exe` | | `.js`, `.mjs`, `.cjs`, `.ts`, `.mts`, `.cts` | all six |
| `deno` | `deno.exe` | `run --allow-all` | the same six | |
| `bun` | `bun.exe` | | the same six | |
| `python` | `py.exe`, then `python.exe` | | `.py`, `.pyw` | `.py`, `.pyw` |
| `perl` | `perl.exe` | | `.pl` | `.pl` |
| `ruby` | `ruby.exe` | | `.rb` | `.rb` |
| `php` | `php.exe` | | `.php` | `.php` |
| `lua` | `lua.exe` | | `.lua` | `.lua` |
| `rscript` | `Rscript.exe` | | `.r` | `.r` |
| `tclsh` | `tclsh.exe` | | `.tcl` | `.tcl` |
| `bash` | Git for Windows' `bash.exe` | | `.sh` | `.sh` |
| `cscript` | `%SystemRoot%\System32\cscript.exe` | `//nologo` | `.vbs`, `.wsf`, `.js` | `.vbs`, `.wsf` |

Notes on them:

1. **`cmd`**: `/d` skips AutoRun commands, and `/s` makes `cmd.exe` strip exactly the outer quote pair it was given, so the raw text runs as written. `cmd.exe` rules then apply to the arguments: `%NAME%` expands, `&`, `|`, `<`, `>` and `^` are operators unless quoted or escaped, and the script reads `%1` with its quotes and `%~1` without them.
2. **`pwsh` and `powershell`**: `-File` binds each argument as a literal string, so `$name` and `$(...)` are not evaluated. Execution policy is the host's own: `pwsh` on Windows defaults to `RemoteSigned`. A missing `pwsh` never falls back to Windows PowerShell; pass `--engine powershell` for that.
3. **`node` and TypeScript**: Node runs `.ts`, `.mts` and `.cts` itself by stripping the types, by default from Node 22.18.0 on the 22 line and from 23.6.0 ([v22.18.0](https://nodejs.org/en/blog/release/v22.18.0), [v23.6.0](https://nodejs.org/en/blog/release/v23.6.0)). An older Node is a launcher failure (125) naming the version found and the minimum. Type stripping only erases type syntax: `enum`, `namespace` and parameter properties need Node's `--experimental-transform-types`, which you can add to `engine.node.arguments`, and imports must name their `.ts` file. `.js` runs under `node` rather than Windows Script Host; `--engine cscript` runs a `.js` under WSH.
4. **`deno`**: `--allow-all` gives the script what `node` would give it. Set `engine.deno.arguments` to narrower permissions.
5. **`python`**: `py.exe` is the Python launcher, which also honours a script's `#!` line. `.pyw` runs under the console interpreter too, because `pythonw` has no standard streams for `--stdout` and `--stderr` to capture, and the launcher hides the console anyway. The `python.exe` of a Windows without Python is the Microsoft Store stub, which exits with 9009.
6. **`bash`**: Git for Windows' `bash.exe`, found at `%ProgramFiles%\Git\bin`, `%LOCALAPPDATA%\Programs\Git\bin` or a Scoop install. The `bash.exe` in `System32` is WSL's, which cannot open a Windows path, so it is never used; a WSL engine needs path translation and is left to a config entry.
7. Interpreters installed under another name, such as `lua54.exe` or `tclsh86.exe`, are reached by setting the engine's `path`.

Considered and left out: `dotnet run app.cs` and `dotnet fsi script.fsx`, because both read options such as `--help` after the script as their own, so the raw text could not reach the script untouched; and AutoHotkey, a GUI runtime. Any of them can still be registered in a config file.

The interpreter's exit code is returned: `exit /b <n>` from a batch file, `exit <n>` from a script.

## Configuration

Config files add engines, change a built-in one, or change which engine an extension runs under. They are read in this order, and a key set in a later one replaces the same key of an earlier one:

1. The built-in engines above.
2. **System**: `%ProgramData%\headless-run\config.json`, for every account. A Task Scheduler job that runs as `SYSTEM` or a service account reads this one, since its `%APPDATA%` is not yours.
3. **Global**: `%APPDATA%\headless-run\config.json`, for your account.
4. **`--config FILE`** on the command line, such as `--config .\.headless-run.json` for a project's own engines. A relative path resolves against the launcher's working directory, like `--stdout`, independently of `--cwd`.

Nothing else is read: no file is picked up from the working directory, and no environment variable names a config file, so what changes a job's engines is always in its own command line or in these two fixed files. A missing or malformed file named by `--config`, and a malformed system or global file, is a launcher failure (125) that names the file, and the line for invalid JSON. Under `--elevate` the elevated copy reads the same files, since it runs with the caller's environment and working directory.

A config file is strict JSON, without comments, so that `headless-run config` can rewrite it without losing anything:

```json
{
  "$schema": "https://raw.githubusercontent.com/mnaoumov/headless-run/main/config.schema.json",
  "engine": {
    "python": {
      "path": "C:\\Python313\\python.exe",
      "arguments": ["-u"]
    },
    "wsl-bash": {
      "path": "wsl.exe",
      "arguments": ["--exec", "bash"],
      "extensions": [".sh"]
    }
  },
  "extension": {
    "js": "deno"
  }
}
```

1. `engine.<name>.path`: the interpreter, as a file name searched on `PATH` (`.exe` is added when it has none) or a path. `%NAME%` expands from the child's environment. An array lists candidates, and the first that exists wins.
2. `engine.<name>.arguments`: the arguments placed between the interpreter and the program. Each is quoted by Windows' rules when it needs quotes.
3. `engine.<name>.extensions`: the extensions `--engine` accepts without `--force-engine`.
4. `engine.<name>.quoteCommand`: `true` wraps the raw text in one quote pair, as `cmd.exe /s /c` needs.
5. `extension.<ext>`: the engine an extension runs under when no `--engine` is given, by extension without its dot. An extension mapped to an engine is accepted by it too.

Engine names have letters, digits, `-` and `_`, and are compared without case. A key replaces the same key below it, so `engine.python.path` in the global file keeps the built-in `python` engine's extensions. The schema at `$schema` lets an editor validate and complete the file.

## `config` command

`headless-run config` reads and writes these files, after `git config`. It is recognised only as the first argument, so a program named `config` after `--` still runs.

```text
headless-run config [--global | --system | --file FILE] <action>
```

1. `--list` (`-l`) `[--show-origin]`: every entry, one line per value, as `key=value`. With no scope, every layer in order, the built-in ones first, so the last line for a key is the value that applies. `--show-origin` prefixes each with `builtin` or `file:<path>` and a tab.
2. `--edit` (`-e`): create the file when missing, with its `$schema` line and an empty engine list, open it in an editor and wait, then validate it, naming the line of any JSON error. The editor is the first of `HEADLESS_RUN_EDITOR`, `VISUAL` and `EDITOR` that is set, run as a command through `cmd.exe` so that `code --wait` works, and otherwise `notepad.exe`. These variables only choose the editor.
3. `<key>`: print a value, one line per array item. With no scope, the value that applies. Exit 1 when the key is not set.
4. `<key> <value>`: set a value. `engine.<name>.extensions` takes a comma-separated list, such as `.py,.pyw`; `path` and `arguments` take one value, and `--add <key> <value>` appends another.
5. `--unset <key>`: remove a key, or a whole engine with `engine.<name>`. Exit 5 when there was nothing to remove.

With no scope, `--edit` and every write go to `--global`. `--system` writes need an elevated prompt; a write without one fails with 125 and says so. A write keeps the rest of the file in its order, checks the result before it saves it, and replaces the file atomically. Invalid usage and an invalid file are 125, with a diagnostic.

```text
headless-run config --global engine.python.path C:\Python313\python.exe
headless-run config --global extension.js deno
headless-run config --list --show-origin
headless-run config --unset engine.python
```

The launcher is a Windows-subsystem program, so a terminal does not wait for it. When its output is not redirected, `config` writes to the console of the terminal that started it, but `cmd.exe` can show its prompt first and does not see the exit code. Pipe the output, such as `headless-run config --list | more`, or in PowerShell `headless-run config --list | Out-Host`, to have the shell wait and receive the exit code.

## Options

All launcher options precede `--`. Values use Windows double-quote/backslash rules. Every option that takes a value accepts both `--name VALUE` and `--name=VALUE`, such as `--priority=below-normal` or `--env=NAME=VALUE`. Repeating an option uses its last value; `--env` keeps the last value per case-insensitive name.

1. `--stdout FILE`: redirect standard output to a file; overwrite by default. Missing parent directories are created.
2. `--stderr FILE`: redirect standard error and launcher diagnostics to a file; overwrite by default once stream setup reaches it. Diagnostics before stream setup append to preserve earlier messages. An error in the options can use only a `--stderr` parsed before that error. Missing parent directories are created.
3. `--append`: append both redirected files. Each write appends, including writes from descendants.
4. `--overwrite`: overwrite both redirected files (default). The last append/overwrite flag wins regardless of its position.
5. `--cwd DIR`: set the child's working directory. It must exist: a missing directory is a launcher failure (125), so a mistyped path never runs the job in a fresh empty folder. Relative output paths resolve against the launcher's working directory, independently of this option.
6. `--create-cwd`: create the `--cwd` directory, with its parents, when it is missing.
7. `--env NAME=VALUE`: override one inherited environment variable; repeat for more variables. Values may contain `=`. An empty value sets an empty variable; it does not remove the name. The parent's environment stays unchanged.
8. `--priority CLASS`: start the child in a process priority class: `idle`, `below-normal`, `normal`, `above-normal`, `high` or `realtime`. Omitted, Windows chooses as usual: `normal`, or the launcher's own class when that is `idle` or `below-normal`. A priority class only orders who runs first when processors are contended; it is not a CPU cap, and a low-priority child still uses an idle processor fully. `realtime` preempts the threads of every other process, including system tasks, so a busy realtime child can leave the mouse unresponsive and disk caches unflushed, freezing the desktop; without the "Increase scheduling priority" privilege Windows starts it at `high` instead. An enclosing job with a priority limit overrides the requested class.
9. `--timeout DURATION`: wait at most a positive duration with `ms`, `s`, `m` or `h` suffix, such as `250ms`, `0.5s`, `30m`, `1h`. Fractions round up to milliseconds. Maximum: `4294967294ms` (about 49.7 days). Timing begins after process creation. Expiry terminates the job and returns 124.
10. `--detach`: return 0 after successful process creation, without creating a launcher-owned job. Cannot combine with `--timeout`. The child can outlive the launcher; an enclosing job imposed by the caller still applies. Redirect output to files for unattended use: inherited pipes remain open in the child and may keep the caller waiting for EOF.
11. `--engine NAME`: run the program under this engine instead of the one its extension picks, such as `--engine powershell` for Windows PowerShell 5.1. See [Programs and scripts](#programs-and-scripts).
12. `--force-engine`: with `--engine`, run the program even when the engine does not accept its extension.
13. `--config FILE`: read engines from this file too, over the system and global ones. See [Configuration](#configuration).
14. `--elevate`: run the child elevated, as an administrator. See [Elevation](#elevation).
15. `--tray`: show a notification-area icon while the job runs, to view its output, open its logs or stop it. See [Tray icon](#tray-icon).
16. `--event-log`: write the run's outcome to the Windows Application event log. See [Event log](#event-log).
17. `--help`: print usage and exit 0. Use `--stdout FILE` to view it when no output handle exists.
18. `--version`: print the version and exit 0. Supports `--stdout FILE` too.

Unredirected standard handles are inherited when available and otherwise connect to `NUL`. Standard input follows that rule too. If stdout and stderr name the same absolute path, both share one file handle. Files open with read/write/delete sharing; the tool does not rotate logs.

## Exit codes and lifetime

1. Normal completion returns the direct child's full 32-bit exit code, including nonzero codes and Windows exception codes.
2. `124` means the timeout expired; the job tree was terminated.
3. `125` means invalid options or a launcher failure, such as a missing program, inaccessible file, invalid directory or failed Win32 call. A diagnostic goes to stderr or the configured error file. Without an inherited error handle or `--stderr`, a GUI-subsystem process has nowhere visible to display it.
4. `130` means the job was stopped from the `--tray` menu; the job tree was terminated.
5. `0` means successful detached startup, help or version output.

A child can itself return 124, 125 or 130; these codes cannot be distinguished from launcher statuses by exit code alone. Read the diagnostic stream when that distinction matters.

By default the launcher waits for the direct child. A kill-on-close job contains it and its descendants from process creation. When that child exits, any remaining descendants are terminated. Terminating the launcher also closes the job and kills that tree. Use `--detach` for applications that intentionally leave descendants running. No graceful shutdown signal is sent on timeout. Existing parent-job restrictions can prevent startup; such failures are reported rather than silently bypassing containment.

`CREATE_NO_WINDOW` prevents console windows for console programs. A GUI program can still create its own windows, and a child can explicitly launch another process with a new console. This tool does not suppress application UI, and it changes privileges only when `--elevate` asks for it.

## Elevation

`--elevate` runs the child with an administrator token:

1. **Already elevated**: the option does nothing, so a script can pass it unconditionally.
2. **Not elevated**: Windows shows its UAC prompt, and an elevated copy of `headless-run` runs the child. The copy relays everything back to the caller, so the call behaves as it would without the option. Standard input, output and error go through named pipes. The copy receives the caller's environment variables and working directory, so relative paths resolve as usual. The exit code is the child's, and 124 and 125 keep their meaning. Every other option applies in the copy. Killing the caller ends the copy and the whole tree, as it does without the option.
3. **Prompt declined**: nothing runs, and the launcher fails with 125.
4. **Non-interactive session**: a service, a task that runs whether the user is logged on or not (S4U), and session 0 cannot show the prompt. The launcher fails at once with 125 rather than waiting for a consent that cannot come. Set such a task to **Run with highest privileges** instead.

Caveats:

1. A standard user answers the prompt with an administrator's credentials, so the child runs as that administrator, but with the caller's environment.
2. Drive letters mapped in the unelevated session are not visible to elevated processes unless Windows' `EnableLinkedConnections` policy is set. Use UNC paths.
3. With `--detach` nothing is relayed: unredirected streams of the child connect to `NUL`, and the launcher returns 0 once the copy has started the child.
4. The pipes accept only the caller's account and Administrators, and each side checks the other's process id, so no other process can read the relayed streams or feed the elevated child's input.

`--elevated-relay` is internal: the elevated copy receives it. Do not pass it yourself.

## Tray icon

`--tray` is opt-in, so an unattended scheduled task stays invisible by default. While the launcher waits on its job, it shows one icon of its own in the notification area, with the command line as its tooltip; several jobs show several icons. Right-click it for the menu:

1. The command line, the start time and the child's process id.
2. **Show output** / **Hide output**: a read-only window with the job's output, also toggled by double-clicking the icon. Closing that window only hides it; the job keeps running.
3. **Open stdout log** / **Open stderr log**: open a `--stdout` or `--stderr` file in its default application.
4. **Stop**: terminate the whole job tree. The launcher returns 130 and writes a diagnostic to stderr.

When the job ends, the icon changes, a notification shows the exit code, and the icon stays for 5 seconds with the code in its menu and a **Dismiss** item that ends the wait at once. Only `--tray` runs pay those seconds; the exit code is the child's, as without the option. A job stopped from the menu ends at once.

The output window shows the last 256K characters of both streams together, decoded as UTF-8:

1. A stream redirected to a file is followed from that file. With `--append`, only what this run adds is shown.
2. Any other stream goes through a pipe that the launcher copies to wherever the stream would otherwise have gone, so the caller still receives every byte, and to the window. When stdout and stderr share one destination, their relative order there can differ from a run without `--tray`, since each passes through its own pipe. A stream with nowhere to go, such as the output of a launcher started from a shortcut, is still shown in the window.

The child still runs with `CREATE_NO_WINDOW`. Starting it with a hidden console to reveal later was rejected: a console shows nothing of redirected streams, which is how `headless-run` is normally used, and closing it would kill the job.

With other options:

1. `--timeout` works unchanged; the icon shows the timeout, and 124 is returned after the 5 seconds.
2. `--detach` cannot combine with `--tray`: a detached launcher exits at once, so nothing is left to show the job. The combination is a launcher failure (125).
3. `--elevate`: the unelevated caller shows the icon and the output, since every relayed byte passes through it. **Stop** ends the caller with 130, and the elevated copy then terminates the tree, as it does when the caller is killed. The menu shows no process id, because the caller cannot see inside the elevated job.
4. Without a notification area, as in a service or session 0, the launcher writes a diagnostic and the job runs on without the icon.

## Event log

`--event-log` is opt-in. It writes one event per run to the Application log, under the source `headless-run`, when the run ends, so a failed scheduled run shows in Event Viewer without a `--stderr` file. Task Scheduler's own history records only the exit code; this event also says whether 124, 125 or 130 came from the launcher or from the child, and carries the launcher's diagnostic.

| Event id | Level | Outcome |
| --- | --- | --- |
| 1 | Information | The child exited with code 0. |
| 2 | Warning | The child exited with a nonzero code. |
| 3 | Error | The timeout expired and the job was terminated (124). |
| 4 | Warning | The job was stopped from the `--tray` menu (130). |
| 5 | Error | Launcher failure (125), with the diagnostic. |
| 6 | Information | A `--detach` child started. |

The text also holds the command line, the working directory, the start time, the duration and the launcher's process id. The child's output is never written to the log. There is no start event for a waited run: Task Scheduler already records starts, and one event per run keeps a frequent job from flooding the log.

Register the source once, from an elevated Windows PowerShell 5.1 (`New-EventLog` is not in PowerShell 7):

```powershell
New-EventLog -LogName Application -Source headless-run
```

Unregistered, the events are still written under that source name, but Event Viewer prefixes each with "The description for Event ID ... cannot be found" before showing the text. A run that cannot write its event, for any reason, carries on and returns its usual exit code.

Under `--elevate`, the elevated copy writes the event, since it alone tells a timeout from a child's own 124; the unelevated caller writes one only for what happens on its side, such as a declined prompt or a tray stop. Read the events with `Get-WinEvent -LogName Application -FilterXPath "*[System[Provider[@Name='headless-run']]]"`, which works whether or not the source is registered; a `ProviderName` filter finds them only once it is.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

The integration harness drives real children and compares direct versus wrapped argv and raw command lines, tests 32-bit exit codes, stream inheritance and file modes, created output directories, environment, cwd, every priority class, both option spellings, `.exe` and `.com` programs, every built-in engine whose interpreter the machine has (the others are skipped with a note) and their argument rules, `--engine` and `--force-engine`, the Node version gate for TypeScript, the config layers and every `config` action (with `%APPDATA%` and `%ProgramData%` pointed at a temporary folder, so the machine's own config is never read or written), console visibility, failures, timeout, descendant cleanup, launcher termination, detach, and the `--tray` menu, output window, finish linger and stop, driven through the launcher's hidden tray window, and the `--event-log` event for each outcome, read back from the Application log. `--elevate` is tested through its full relay. A test-only variable, `HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC=1`, starts the copy without the `runas` verb, so no prompt appears. An elevated test run, such as CI, also checks that the option does nothing there. Answering a real UAC prompt needs a person.

For a runtime-free executable, install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and run:

```powershell
dotnet publish src/headless-run -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64 -m:2
pwsh -NoProfile -File scripts/test.ps1 -Launcher ./artifacts/win-x64/headless-run.exe
```

Use `win-x86` for a 32-bit build, and `win-arm64` on an ARM64 machine. CI builds and tests NativeAOT for x64 and x86. Pushing a `vX.Y.Z` tag matching the project version publishes x64, x86 and ARM64 executables and their SHA-256 checksums after the native tests pass.

## Implementation references

The implementation uses [`CreateProcessW`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [`CREATE_NO_WINDOW`](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags), [`PROC_THREAD_ATTRIBUTE_JOB_LIST` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute), and [`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information). Job assignment happens inside process creation, avoiding a suspended process orphaned between creation and assignment. Only intended standard handles are inherited; the job handle stays owned by the launcher.

## See also

| Tool | Features | How `headless-run` differs |
| --- | --- | --- |
| [`Hidden Start`](https://www.ntwind.com/software/hstart.html) | Runs console programs and batch files with no window; tray, minimised and maximised modes; start delay; idle priority; starting directory; elevation without a UAC prompt; commercial | Same goal as `Hidden Start`; omits: minimised and maximised window modes, delay, elevation without a UAC prompt; adds: built-in output files, timeout, process-tree cleanup, an output window in its tray menu; open source |
| [`HiddenRun`](https://github.com/unt-cas/hiddenrun) | VBScript run through Windows Script Host; runs a command hidden; logs start and exit code to the Application event log | Same goal as `HiddenRun`; a native executable rather than a script; returns the child's exit code to the caller, and logs the outcome to the event log only with `--event-log` |
| [`quietrun`](https://github.com/dltkdrms1717-create/quietrun) | Runs Task Scheduler Python jobs under `pythonw.exe` with `CREATE_NO_WINDOW`; appends output to a log; returns the job's exit code; sweeps orphaned Selenium drivers | Same goal as `quietrun`, for any console program rather than Python jobs; omits: driver sweep; adds: separate output files, timeout, environment, priority, job-based process-tree cleanup |
| [`RunHidden`](https://github.com/LesFerch/RunHidden) | Runs scripts and console programs with the console suppressed; separate `RunHiddenWait.exe` waits and returns the exit code | Same goal as `RunHidden`; one executable that always waits unless detached; adds: output files, timeout, process-tree cleanup |
| [`RunHiddenConsole`](https://github.com/SeidChr/RunHiddenConsole) | Forwards calls to a target program started without a window; file redirection listed as future work | Same goal as `RunHiddenConsole`; adds: output files, timeout, process-tree cleanup |
| [`SilentCMD`](https://github.com/stbrenner/SilentCMD) | Runs a batch file without a command prompt window; log file with overwrite, append and size options; start delay | Broader than `SilentCMD`: any console program, not only batch files; omits: log size limit, delay; adds: separate stdout and stderr files, timeout, process-tree cleanup |

Last searched for similar tools on 2026-11-10.

## Changelog

All notable changes to this project will be documented in the [CHANGELOG](./CHANGELOG.md).

## License

[MIT](LICENSE).
