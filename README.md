# headless-run

Run a Windows program without a console window, keep its exact command line, and return its exit code. Designed for Task Scheduler and background automation.

The launcher is a Windows-subsystem executable. It starts console programs with `CREATE_NO_WINDOW`, using documented Win32 APIs. It requires Windows 10 / Windows Server 2016 or later. Release executables are native, self-contained, and need no .NET runtime installed.

## Install

Download `headless-run-win-x64.exe` or `headless-run-win-arm64.exe` from [Releases](https://github.com/mnaoumov/headless-run/releases), verify it against the adjacent `.sha256` file, and rename it to `headless-run.exe` in a directory on `PATH`.

The binaries are unsigned; Windows may ask you to confirm a downloaded executable.

## Usage

```text
headless-run.exe [options] -- <program> [arguments...]
```

For a scheduled task, set **Program/script** to the full path of `headless-run.exe`, and **Add arguments** to:

```text
--stderr "C:\Logs\nightly.err" --stdout "C:\Logs\nightly.out" --append --timeout 30m -- "C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -File "C:\Scripts\nightly.ps1"
```

Quote the program path when it contains spaces. The launcher removes the option prefix, `--`, and separating spaces/tabs; it forwards every character from the program onwards unchanged to `CreateProcessW`. It never splits or re-quotes child arguments. Child programs still interpret their own command line according to their own rules.

There is no shell expansion: `&`, `^`, `%`, `|`, `$` and redirection characters are ordinary characters unless you explicitly invoke a shell. To run a `.cmd`/`.bat` file, invoke `cmd.exe /c` and follow its quoting rules. A calling shell processes its own syntax before the launcher sees it; for precise automation use Task Scheduler's argument field or an API that accepts a raw command line.

## Options

All launcher options precede `--`. Values use Windows double-quote/backslash rules. Repeating an option uses its last value; `--env` keeps the last value per case-insensitive name.

1. `--stdout FILE`: redirect standard output to a file; overwrite by default.
2. `--stderr FILE`: redirect standard error and launcher diagnostics to a file; overwrite by default once stream setup reaches it. Diagnostics before stream setup append to preserve earlier messages. An error in the options can use only a `--stderr` parsed before that error.
3. `--append`: append both redirected files. Each write appends, including writes from descendants.
4. `--overwrite`: overwrite both redirected files (default). The last append/overwrite flag wins regardless of its position.
5. `--cwd DIR`: set the child's working directory. Relative output paths resolve against the launcher's working directory, independently of this option. Directories must already exist.
6. `--env NAME=VALUE`: override one inherited environment variable; repeat for more variables. Values may contain `=`. An empty value sets an empty variable; it does not remove the name. The parent's environment stays unchanged.
7. `--below-normal`: start the child with below-normal process priority.
8. `--timeout DURATION`: wait at most a positive duration with `ms`, `s`, `m` or `h` suffix, such as `250ms`, `0.5s`, `30m`, `1h`. Fractions round up to milliseconds. Maximum: `4294967294ms` (about 49.7 days). Timing begins after process creation. Expiry terminates the job and returns 124.
9. `--detach`: return 0 after successful process creation, without creating a launcher-owned job. Cannot combine with `--timeout`. The child can outlive the launcher; an enclosing job imposed by the caller still applies. Redirect output to files for unattended use: inherited pipes remain open in the child and may keep the caller waiting for EOF.
10. `--help`: print usage and exit 0. Use `--stdout FILE` to view it when no output handle exists.
11. `--version`: print the version and exit 0. Supports `--stdout FILE` too.

Unredirected standard handles are inherited when available and otherwise connect to `NUL`. Standard input follows that rule too. If stdout and stderr name the same absolute path, both share one file handle. Files open with read/write/delete sharing; the tool does not create parent directories or rotate logs.

## Exit codes and lifetime

1. Normal completion returns the direct child's full 32-bit exit code, including nonzero codes and Windows exception codes.
2. `124` means the timeout expired; the job tree was terminated.
3. `125` means invalid options or a launcher failure, such as a missing program, inaccessible file, invalid directory or failed Win32 call. A diagnostic goes to stderr or the configured error file. Without an inherited error handle or `--stderr`, a GUI-subsystem process has nowhere visible to display it.
4. `0` means successful detached startup, help or version output.

A child can itself return 124 or 125; these codes cannot be distinguished from launcher statuses by exit code alone. Read the diagnostic stream when that distinction matters.

By default the launcher waits for the direct child. A kill-on-close job contains it and its descendants from process creation. When that child exits, any remaining descendants are terminated. Terminating the launcher also closes the job and kills that tree. Use `--detach` for applications that intentionally leave descendants running. No graceful shutdown signal is sent on timeout. Existing parent-job restrictions can prevent startup; such failures are reported rather than silently bypassing containment.

`CREATE_NO_WINDOW` prevents console windows for console programs. A GUI program can still create its own windows, and a child can explicitly launch another process with a new console. This tool does not suppress application UI or change privileges.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

The integration harness drives real children and compares direct versus wrapped argv and raw command lines, tests 32-bit exit codes, stream inheritance and file modes, environment, cwd, priority, console visibility, failures, timeout, descendant cleanup, launcher termination and detach.

For a runtime-free executable, install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and run:

```powershell
dotnet publish src/headless-run -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64 -m:2
pwsh -NoProfile -File scripts/test.ps1 -Launcher ./artifacts/win-x64/headless-run.exe
```

Use `win-arm64` on an ARM64 machine. CI builds and tests NativeAOT. Pushing a `vX.Y.Z` tag matching the project version publishes both architectures and their SHA-256 checksums after the native tests pass.

## Implementation references

The implementation uses [`CreateProcessW`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [`CREATE_NO_WINDOW`](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags), [`PROC_THREAD_ATTRIBUTE_JOB_LIST` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute), and [`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information). Job assignment happens inside process creation, avoiding a suspended process orphaned between creation and assignment. Only intended standard handles are inherited; the job handle stays owned by the launcher.

## License

[MIT](LICENSE).
