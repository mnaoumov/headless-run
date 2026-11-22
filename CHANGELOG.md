# Changelog

All notable changes to this project are documented in this file.

## Unreleased

### Breaking changes

- `--windows-powershell` is gone: use `--engine powershell`.
- A `.js` file now runs under `node`; before, it went to `CreateProcessW` and failed. The same holds for `.mjs`, `.cjs`, `.ts`, `.mts`, `.cts`, `.py`, `.pyw`, `.pl`, `.rb`, `.php`, `.lua`, `.r`, `.tcl`, `.sh`, `.vbs` and `.wsf`, each under its own engine.

### New

- Engines: the extension of the program picks the interpreter from one table of engines: `cmd`, `pwsh`, `powershell`, `node`, `deno`, `bun`, `python`, `perl`, `ruby`, `php`, `lua`, `rscript`, `tclsh`, `bash` and `cscript`. `--engine NAME` picks one explicitly; an engine that does not accept the program's extension is refused unless `--force-engine` is passed. A missing interpreter fails with 125 and says how to point the engine at it.
- Node runs TypeScript (`.ts`, `.mts`, `.cts`) directly; a Node older than 22.18.0, or 23.0 to 23.5, fails with 125 naming the version.
- Config files register new engines or change built-in ones: `%ProgramData%\headless-run\config.json` for every account, `%APPDATA%\headless-run\config.json` for yours, and `--config FILE` for one run. A JSON schema validates them in an editor.
- `headless-run config` reads and writes those files after `git config`: `--list [--show-origin]`, `--edit`, get, set, `--add`, `--unset`, with `--global`, `--system` and `--file` scopes.
- `.exe` and `.com` programs are documented and tested. A file that is no Windows program and that no engine runs fails with a diagnostic naming the setting that would run it.

- `--tray` shows a notification-area icon while the job runs. Its menu shows the command line, start time and process id, shows or hides a window with the job's output, opens the `--stdout` and `--stderr` files, and stops the job, which then returns 130. When the job ends, a notification shows the exit code. It works with `--timeout` and `--elevate`; `--detach` refuses it. The default is `--tray=auto`: the icon is shown for a run started on a desktop, and not under Task Scheduler, a service, session 0 or `--detach`, so scheduled tasks stay invisible with no flag; `--tray=true` and `--tray=false` force it either way. Only an explicit `--tray` keeps the icon for 5 seconds after the job with its exit code.
- `--event-log` writes one event per run to the Windows Application log, under the source `headless-run`: the outcome (exit code, timeout, tray stop, launcher failure with its diagnostic, or detached start) with its own event id, the command line, working directory and duration. A failed scheduled run is then visible in Event Viewer with no log file. Writing it never fails the job.
- `--echo` starts nothing and shows what the launcher received: its command line split into arguments, the child tail, the engine and interpreter, and the exact child command line with the arguments a program would receive. The text goes to `--stdout FILE`, a redirected stdout or the calling console, and to a window when there is no console, as from a shortcut or the Run dialog, so any caller's quoting can be checked where it runs. In that window the received command line can be edited, and its arguments are split again as you type, so a variant of the line can be tried where it actually runs.
- The documentation moved from the README to a site, [mnaoumov.dev/headless-run](https://mnaoumov.dev/headless-run/), with one page per topic. The README keeps the usage, install and a link to each page.
- The documentation shows how to call the launcher from a `cmd` prompt, a batch file, PowerShell 7, Windows PowerShell 5.1, Git Bash, WSL, Task Scheduler, Node, Python and .NET, with each shell's quoting, waiting and `--` handling, and a list of pitfalls. Every example is run as written by `scripts/test-shell-examples.ps1`, which the test script includes.
- A [quoting playground](https://mnaoumov.dev/headless-run/guides/quoting-playground/) on the documentation site: type the arguments a program should receive and get the line to write in `cmd`, a batch file, PowerShell 7, Windows PowerShell 5.1, Git Bash, WSL, a shortcut, Task Scheduler, the Run dialog, a raw command line, Node, Python or C#, or type a line and see the arguments it becomes. Each caller's model is checked against the real caller by `scripts/test-quoting-models.ps1`, which the test script includes.
- `--max-size SIZE` caps the `--stdout` and `--stderr` files of an `--append` run: before the child starts, a file larger than `SIZE` (such as `10M`) moves to `FILE.1`, replacing the older backup, so a job that runs every few minutes for months keeps its logs bounded and still has the run that crossed the cap. The `output.maxSize` config key sets a default for every run, and `--max-size none` turns it off for one. The idea is `SilentCMD`'s `/LOGSIZE`, which truncates instead.
- `--stdout` and `--stderr` paths can hold `<date>`, `<time>`, `<program>` and `<pid>`, filled in when the launcher starts, so a scheduled job writes one file per run, such as `--stdout "C:\Logs\<program>.<date>.<time>.log"`, with no wrapper script. Task Scheduler expands nothing in its arguments, so this was not possible before. `<` and `>` cannot appear in a Windows file name, so a placeholder never collides with a real path; an unknown one fails with 125. The idea is `hide-win32-console-window`'s per-run log name.
- The executable's file properties name the publisher (`mnaoumov`) and the copyright, instead of repeating `headless-run` as the company.

## 1.0.0 - 2026-11-10

### Breaking changes

- `--below-normal` is gone: use `--priority below-normal`.
- A `.cmd`, `.bat` or `.ps1` program now runs under its interpreter, with that interpreter's argument rules. A native executable still gets its raw command line unchanged.
- A missing `--cwd` directory is now a launcher failure unless `--create-cwd` is passed.

### New

- `--priority idle|below-normal|normal|above-normal|high|realtime` chooses the child's priority class. Omitted, Windows' default applies.
- `.cmd` and `.bat` run as `cmd.exe /d /s /c "<command line>"`; `.ps1` runs as `pwsh -NoProfile -NonInteractive -File <command line>`, failing clearly when `pwsh` is missing. `--windows-powershell` runs a `.ps1` under Windows PowerShell 5.1 instead.
- `--create-cwd` creates a missing working directory.
- Missing parent directories of `--stdout` and `--stderr` files are created.
- Every option that takes a value also accepts `--name=VALUE`.
- A native x86 executable ships beside the x64 and ARM64 ones.

## 0.1.0 - 2026-11-02

First release.

- Launch a Windows console program without a console window, forwarding its raw command line unchanged and returning its full 32-bit exit code.
- Redirect standard output and standard error to files with `--stdout`, `--stderr`, `--append` and `--overwrite`.
- Set the child's working directory, environment variables and priority with `--cwd`, `--env` and `--below-normal`.
- Stop the child and its descendants after `--timeout`, returning 124.
- Contain the child and its descendants in a kill-on-close job, or leave it running with `--detach`.
- Native, self-contained x64 and ARM64 executables with SHA-256 checksums.
