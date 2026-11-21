# headless-run

Run a Windows console program without a console window, keep its exact command line, and return its exit code. Designed for Task Scheduler and background automation.

The launcher is a Windows-subsystem executable. It starts console programs with `CREATE_NO_WINDOW`, using documented Win32 APIs, contains the child's process tree in a kill-on-close job, and returns the child's exit code. It requires Windows 10 / Windows Server 2016 or later. Release executables are native, self-contained, and need no .NET runtime installed.

## Documentation

The full documentation is at [mnaoumov.dev/headless-run](https://mnaoumov.dev/headless-run/): [calling it from each shell](https://mnaoumov.dev/headless-run/guides/calling-from-a-shell/), a [quoting playground](https://mnaoumov.dev/headless-run/guides/quoting-playground/), [programs and scripts](https://mnaoumov.dev/headless-run/guides/programs-and-scripts/), [configuration](https://mnaoumov.dev/headless-run/guides/configuration/), [options](https://mnaoumov.dev/headless-run/guides/options/), [exit codes](https://mnaoumov.dev/headless-run/guides/exit-codes-and-lifetime/), [elevation](https://mnaoumov.dev/headless-run/guides/elevation/), the [tray icon](https://mnaoumov.dev/headless-run/guides/tray-icon/), the [event log](https://mnaoumov.dev/headless-run/guides/event-log/) and [building and testing](https://mnaoumov.dev/headless-run/guides/building-and-testing/).

## Usage

```text
headless-run.exe [options] -- <program> [arguments...]
```

For a scheduled task, set **Program/script** to the full path of `headless-run.exe`, and **Add arguments** to:

```text
--stderr "C:\Logs\nightly-stderr.txt" --stdout "C:\Logs\nightly-stdout.txt" --append --timeout 30m -- "C:\Scripts\nightly.ps1"
```

Everything after `--` reaches the child unchanged: the launcher never splits or re-quotes it. A calling shell still processes its own syntax first, so [Calling it from a shell](https://mnaoumov.dev/headless-run/guides/calling-from-a-shell/) shows the same call typed from each one, and the [quoting playground](https://mnaoumov.dev/headless-run/guides/quoting-playground/) escapes any argument list for each.

## Install

With [Scoop](https://scoop.sh), from the author's bucket:

```powershell
scoop bucket add mnaoumov https://github.com/mnaoumov/scoop-bucket
scoop install mnaoumov/headless-run
```

For a scheduled task, `~\scoop\apps\headless-run\current\headless-run.exe` is the full path to use, and it stays the same across `scoop update`.

Or download `headless-run-win-x64.exe`, `headless-run-win-x86.exe` or `headless-run-win-arm64.exe` from [Releases](https://github.com/mnaoumov/headless-run/releases), verify it against the adjacent `.sha256` file, and rename it to `headless-run.exe` in a directory on `PATH`.

The binaries are unsigned; Windows may ask you to confirm a downloaded executable.

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

## Support

<!-- markdownlint-disable MD033 -->

<a href="https://www.buymeacoffee.com/mnaoumov" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60" width="217"></a>

<!-- markdownlint-enable MD033 -->

## License

[MIT](LICENSE).
