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

The extension of the program decides how it starts. The comparison ignores case.

1. **A native executable** (anything other than the extensions below): the raw text from the program onwards goes to `CreateProcessW` as the command line. There is no shell expansion: `&`, `^`, `%`, `|`, `$` and redirection characters are ordinary characters, and the program parses its own arguments.
2. **`.cmd` or `.bat`**: runs as `cmd.exe /d /s /c "<raw text>"`, where `cmd.exe` is `%ComSpec%`. `/d` skips AutoRun commands, and `/s` makes `cmd.exe` strip exactly the outer quote pair it was given, so the raw text runs as written. `cmd.exe` rules then apply to the arguments: `%NAME%` expands, `&`, `|`, `<`, `>` and `^` are operators unless quoted or escaped, and the script reads `%1` with its quotes and `%~1` without them.
3. **`.ps1`**: runs as `pwsh -NoProfile -NonInteractive -File <raw text>`, with `pwsh.exe` found on `PATH` (an `--env PATH=...` override is searched instead of the launcher's own). `-File` binds each argument as a literal string, so `$name` and `$(...)` are not evaluated. A missing `pwsh` is a launcher failure (125) that names the fix; Windows PowerShell 5.1 runs a script only when `--windows-powershell` asks for it, never as a silent fallback. Execution policy is the host's own: `pwsh` on Windows defaults to `RemoteSigned`. For other host flags, run `pwsh.exe` as the program yourself.

The interpreter's exit code is returned: `exit /b <n>` from a batch file, `exit <n>` from a script.

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
11. `--windows-powershell`: run a `.ps1` program under Windows PowerShell 5.1 (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`) instead of `pwsh`. Any other program with this option is a launcher failure.
12. `--elevate`: run the child elevated, as an administrator. See [Elevation](#elevation).
13. `--help`: print usage and exit 0. Use `--stdout FILE` to view it when no output handle exists.
14. `--version`: print the version and exit 0. Supports `--stdout FILE` too.

Unredirected standard handles are inherited when available and otherwise connect to `NUL`. Standard input follows that rule too. If stdout and stderr name the same absolute path, both share one file handle. Files open with read/write/delete sharing; the tool does not rotate logs.

## Exit codes and lifetime

1. Normal completion returns the direct child's full 32-bit exit code, including nonzero codes and Windows exception codes.
2. `124` means the timeout expired; the job tree was terminated.
3. `125` means invalid options or a launcher failure, such as a missing program, inaccessible file, invalid directory or failed Win32 call. A diagnostic goes to stderr or the configured error file. Without an inherited error handle or `--stderr`, a GUI-subsystem process has nowhere visible to display it.
4. `0` means successful detached startup, help or version output.

A child can itself return 124 or 125; these codes cannot be distinguished from launcher statuses by exit code alone. Read the diagnostic stream when that distinction matters.

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

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

The integration harness drives real children and compares direct versus wrapped argv and raw command lines, tests 32-bit exit codes, stream inheritance and file modes, created output directories, environment, cwd, every priority class, both option spellings, `.cmd`/`.bat`/`.ps1` dispatch and their argument rules, console visibility, failures, timeout, descendant cleanup, launcher termination and detach. `--elevate` is tested through its full relay. A test-only variable, `HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC=1`, starts the copy without the `runas` verb, so no prompt appears. An elevated test run, such as CI, also checks that the option does nothing there. Answering a real UAC prompt needs a person.

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
| [`Hidden Start`](https://www.ntwind.com/software/hstart.html) | Runs console programs and batch files with no window; tray, minimised and maximised modes; start delay; idle priority; starting directory; elevation without a UAC prompt; commercial | Same goal as `Hidden Start`; omits: tray and window modes, delay, elevation without a UAC prompt; adds: built-in output files, timeout, process-tree cleanup; open source |
| [`HiddenRun`](https://github.com/unt-cas/hiddenrun) | VBScript run through Windows Script Host; runs a command hidden; logs start and exit code to the Application event log | Same goal as `HiddenRun`; a native executable rather than a script; returns the child's exit code to the caller rather than logging it |
| [`quietrun`](https://github.com/dltkdrms1717-create/quietrun) | Runs Task Scheduler Python jobs under `pythonw.exe` with `CREATE_NO_WINDOW`; appends output to a log; returns the job's exit code; sweeps orphaned Selenium drivers | Same goal as `quietrun`, for any console program rather than Python jobs; omits: driver sweep; adds: separate output files, timeout, environment, priority, job-based process-tree cleanup |
| [`RunHidden`](https://github.com/LesFerch/RunHidden) | Runs scripts and console programs with the console suppressed; separate `RunHiddenWait.exe` waits and returns the exit code | Same goal as `RunHidden`; one executable that always waits unless detached; adds: output files, timeout, process-tree cleanup |
| [`RunHiddenConsole`](https://github.com/SeidChr/RunHiddenConsole) | Forwards calls to a target program started without a window; file redirection listed as future work | Same goal as `RunHiddenConsole`; adds: output files, timeout, process-tree cleanup |
| [`SilentCMD`](https://github.com/stbrenner/SilentCMD) | Runs a batch file without a command prompt window; log file with overwrite, append and size options; start delay | Broader than `SilentCMD`: any console program, not only batch files; omits: log size limit, delay; adds: separate stdout and stderr files, timeout, process-tree cleanup |

Last searched for similar tools on 2026-11-10.

## Changelog

All notable changes to this project will be documented in the [CHANGELOG](./CHANGELOG.md).

## License

[MIT](LICENSE).
