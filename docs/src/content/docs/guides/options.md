---
title: Options
description: Every launcher option, before the -- that starts the child's command line.
sidebar:
    order: 5
---

All launcher options precede `--`. Values use Windows double-quote/backslash rules. Every option that takes a value accepts both `--name VALUE` and `--name=VALUE`, such as `--priority=below-normal` or `--env=NAME=VALUE`. Repeating an option uses its last value; `--env` keeps the last value per case-insensitive name.

1. `--stdout FILE`: redirect standard output to a file; overwrite by default. Missing parent directories are created. The path can hold `<date>`, `<time>`, `<program>` and `<pid>`: see [One file per run](#one-file-per-run).
2. `--stderr FILE`: redirect standard error and launcher diagnostics to a file; overwrite by default once stream setup reaches it. Diagnostics before stream setup append to preserve earlier messages. An error in the options can use only a `--stderr` parsed before that error. Missing parent directories are created.
3. `--append`: append both redirected files. Each write appends, including writes from descendants.
4. `--overwrite`: overwrite both redirected files (default). The last append/overwrite flag wins regardless of its position.
5. `--max-size SIZE`: cap the redirected files of an `--append` run. Before the child starts, a `--stdout` or `--stderr` file larger than `SIZE` is moved to the same name with `.1` added, such as `job.log.1`, replacing an older backup, and the run appends to a new file. So a job that runs every few minutes for months keeps at most about twice `SIZE` per stream, and the output of the run that crossed the cap is still there. `SIZE` is a byte count or a number with `K`, `M` or `G` (also `KB`/`KiB` and so on, all 1024-based), such as `10M`. `none` turns off a default set by the [`output.maxSize` config key](/headless-run/guides/configuration/) for this run; `0` is refused. A file that cannot be moved, because another program holds it without delete sharing, is appended to as it is, with a diagnostic on the `--stderr` destination. Without `--append` the option does nothing, since each run replaces the file. The idea comes from `SilentCMD`'s `/LOGSIZE`, which truncates the file instead.
6. `--cwd DIR`: set the child's working directory. It must exist: a missing directory is a launcher failure (125), so a mistyped path never runs the job in a fresh empty folder. Relative output paths resolve against the launcher's working directory, independently of this option.
7. `--create-cwd`: create the `--cwd` directory, with its parents, when it is missing.
8. `--env NAME=VALUE`: override one inherited environment variable; repeat for more variables. Values may contain `=`. An empty value sets an empty variable; it does not remove the name. The parent's environment stays unchanged.
9. `--priority CLASS`: start the child in a process priority class: `idle`, `below-normal`, `normal`, `above-normal`, `high` or `realtime`. Omitted, Windows chooses as usual: `normal`, or the launcher's own class when that is `idle` or `below-normal`. A priority class only orders who runs first when processors are contended; it is not a CPU cap, and a low-priority child still uses an idle processor fully. `realtime` preempts the threads of every other process, including system tasks, so a busy realtime child can leave the mouse unresponsive and disk caches unflushed, freezing the desktop; without the "Increase scheduling priority" privilege Windows starts it at `high` instead. An enclosing job with a priority limit overrides the requested class.
10. `--timeout DURATION`: wait at most a positive duration with `ms`, `s`, `m` or `h` suffix, such as `250ms`, `0.5s`, `30m`, `1h`. Fractions round up to milliseconds. Maximum: `4294967294ms` (about 49.7 days). Timing begins after process creation. Expiry terminates the job and returns 124.
11. `--detach`: return 0 after successful process creation, without creating a launcher-owned job. Cannot combine with `--timeout`. The child can outlive the launcher; an enclosing job imposed by the caller still applies. Redirect output to files for unattended use: inherited pipes remain open in the child and may keep the caller waiting for EOF.
12. `--engine NAME`: run the program under this engine instead of the one its extension picks, such as `--engine powershell` for Windows PowerShell 5.1. See [Programs and scripts](/headless-run/guides/programs-and-scripts/).
13. `--force-engine`: with `--engine`, run the program even when the engine does not accept its extension.
14. `--config FILE`: read engines and output defaults from this file too, over the system and global ones. See [Configuration](/headless-run/guides/configuration/).
15. `--elevate`: run the child elevated, as an administrator. See [Elevation](/headless-run/guides/elevation/).
16. `--tray[=true|false|auto]`: a notification-area icon while the job runs, to view its output, open its logs or stop it. The default, `auto`, shows it for a run started on a desktop and not under Task Scheduler, a service or `--detach`. See [Tray icon](/headless-run/guides/tray-icon/).
17. `--event-log`: write the run's outcome to the Windows Application event log. See [Event log](/headless-run/guides/event-log/).
18. `--echo`: start nothing, and show what the launcher received and what it would start. See [Checking what a caller passes](#checking-what-a-caller-passes).
19. `--help`: print usage and exit 0. Use `--stdout FILE` to view it when no output handle exists.
20. `--version`: print the version and exit 0. Supports `--stdout FILE` too.

## One file per run

A `--stdout` or `--stderr` path can hold placeholders, which the launcher fills in when it starts:

1. `<date>`: the launcher's start date, local time, as `yyyy-MM-dd`, such as `2026-11-21`.
2. `<time>`: its start time, local time, as `HH-mm-ss`, such as `07-30-00`.
3. `<program>`: the program's file name without its extension, such as `backup` for `C:\Scripts\backup.ps1`.
4. `<pid>`: the launcher's process id, which tells apart two runs started in the same second.

So a scheduled task can keep each run's output, with no wrapper script, although Task Scheduler expands nothing in its **Add arguments** field:

```text
--stdout "C:\Logs\<program>.<date>.<time>.log" --stderr "C:\Logs\<program>.<date>.<time>.log" -- C:\Scripts\backup.ps1
```

Names are case-insensitive, and a placeholder may appear in a directory part too, such as `C:\Logs\<date>\<program>.log`, whose missing directory is created. `<` and `>` cannot appear in a Windows file name, so a placeholder never collides with a real path and no escape is needed: a `<` or `>` that does not form a known placeholder is a launcher failure (125). From `cmd` or PowerShell, quote the path, since both shells read a bare `<` or `>` as a redirection. Under `--elevate` the elevated copy uses the unelevated launcher's start time and process id, so both name the same file. The pair with `--max-size` is the choice between one file per run and one capped file appended to. The idea comes from `hide-win32-console-window`, which names each run's log after the program and its start time.

## Standard handles

Unredirected standard handles are inherited when available and otherwise connect to `NUL`. Standard input follows that rule too. If stdout and stderr name the same absolute path, both share one file handle. Files open with read/write/delete sharing; only `--max-size` rotates them, and only before the child starts.

## Checking what a caller passes

Put `--echo` first and keep the rest of the call as it is. The launcher parses its options as usual, resolves the engine and interpreter, and then starts nothing. It shows:

1. the command line it received, and that line split into arguments;
2. the child tail after `--`, forwarded unchanged;
3. the engine and interpreter the tail's program picks, or that the program runs directly;
4. the exact command line the child would be started with, split into the arguments a C program would receive;
5. the `--cwd` directory and the `--env` overrides, when given.

Each argument is shown in brackets, `3: [a b]`, so an empty argument and leading or trailing spaces stay visible. For a `.cmd` or `.bat` file the split shown is the tail's, since `cmd.exe` runs the tail as a command rather than splitting it.

The text goes to the first place that can take it:

1. the file named by `--stdout FILE`;
2. a redirected standard output, such as `headless-run --echo -- tool.exe "a b" | more`;
3. the console of the shell that started the launcher;
4. a window, when there is no console at all: a shortcut, the Run dialog or Explorer.

The window shows the same text, and below it the received command line in a box you can edit. The arguments under that box are split again on every keystroke, by the same C runtime rule, so you can try a variant of the line, an added quote or a doubled backslash, where it actually runs. Ctrl+C copies the selection, or the whole box when nothing is selected; Tab moves between the boxes, and Esc or Close ends the launcher.

Under Task Scheduler with no desktop there is nowhere to show it, so pass `--stdout FILE` there. The exit code is 0, or 125 when the child command line cannot be built, for example when the engine's interpreter is not installed; the reason is then part of the text. `--echo` with no `--` shows the launcher's own arguments only.

Because a Windows-subsystem program does not hold the console, the text printed to a console can appear after the shell's next prompt. Pipe it, as in item 2, or use `start "" /wait` in `cmd`, to keep it in order.
