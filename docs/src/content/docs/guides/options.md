---
title: Options
description: Every launcher option, before the -- that starts the child's command line.
sidebar:
    order: 5
---

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
11. `--engine NAME`: run the program under this engine instead of the one its extension picks, such as `--engine powershell` for Windows PowerShell 5.1. See [Programs and scripts](/headless-run/guides/programs-and-scripts/).
12. `--force-engine`: with `--engine`, run the program even when the engine does not accept its extension.
13. `--config FILE`: read engines from this file too, over the system and global ones. See [Configuration](/headless-run/guides/configuration/).
14. `--elevate`: run the child elevated, as an administrator. See [Elevation](/headless-run/guides/elevation/).
15. `--tray[=true|false|auto]`: a notification-area icon while the job runs, to view its output, open its logs or stop it. The default, `auto`, shows it for a run started on a desktop and not under Task Scheduler, a service or `--detach`. See [Tray icon](/headless-run/guides/tray-icon/).
16. `--event-log`: write the run's outcome to the Windows Application event log. See [Event log](/headless-run/guides/event-log/).
17. `--help`: print usage and exit 0. Use `--stdout FILE` to view it when no output handle exists.
18. `--version`: print the version and exit 0. Supports `--stdout FILE` too.

Unredirected standard handles are inherited when available and otherwise connect to `NUL`. Standard input follows that rule too. If stdout and stderr name the same absolute path, both share one file handle. Files open with read/write/delete sharing; the tool does not rotate logs.
