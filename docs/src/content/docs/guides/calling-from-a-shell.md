---
title: Calling it from a shell
description: The same call typed from cmd, PowerShell, Git Bash, WSL, Task Scheduler, a shortcut, the Run dialog and code, each run as a test.
sidebar:
    order: 2
---

Every example on this page makes the same call. `headless-run` starts `C:\Tools\My Tool\tool.exe` with `GREETING` set to `hello world`, and `tool.exe` receives these eight arguments:

<!-- example-argv -->
1. `hello world`
2. `say "hi"`
3. `--verbose`
4. `/quiet`
5. `--`
6. `100%`
7. `a&b`
8. `C:\Log files\`

Where the example has a second line, it runs `C:\Scripts\job.ps1 -Name "nightly run" -DryRun`, and the script, which declares `param([string] $Name, [switch] $DryRun)`, receives `$Name` as `nightly run` and `$DryRun` set.

The first `--` ends the launcher's options. The fifth argument, a second `--`, is part of the child's command line and reaches `tool.exe` like any other argument, since nothing after the first `--` is parsed by the launcher. Each shell below passes both of them through unchanged.

`scripts/test-shell-examples.ps1` runs each example block on this page exactly as written, with `C:\Tools` and `C:\Scripts` pointed at a temporary folder, and checks what `tool.exe` and `job.ps1` received against the lists above. `scripts/test.ps1`, which CI runs, includes it for `cmd`, PowerShell 7, Windows PowerShell 5.1, Git Bash, a shortcut, Node, Python and C#, skipping a caller the machine lacks with a note. `-Wsl` and `-TaskScheduler` add the WSL and Task Scheduler examples, which were checked on Windows 11 with WSL 2 Ubuntu.

## `cmd.exe` prompt

<!-- example: cmd-prompt -->
```bat
start "" /wait headless-run --env "GREETING=hello world" -- "C:\Tools\My Tool\tool.exe" "hello world" "say \"hi\"" --verbose /quiet -- 100% "a&b" "C:\Log files\\"
start "" /wait headless-run -- C:\Scripts\job.ps1 -Name "nightly run" -DryRun
```

1. The prompt does not wait for a Windows-subsystem program, so without `start "" /wait` it shows the next prompt at once and `%ERRORLEVEL%` is not the job's exit code. The empty `""` is `start`'s window title, which it would otherwise take from the first quoted argument.
2. `&`, `|`, `<`, `>` and `^` are `cmd` operators outside quotes: quote the argument, as in `"a&b"`, or escape the character, as in `a^&b`.
3. `cmd` does not know `\"`: it sees the `"` in `"say \"hi\""` as the end of a quoted part, so `hi` is outside quotes for `cmd`, though `tool.exe` reads one argument. That is harmless here, but an operator such as `&` in that part would split the command.
4. `%NAME%` expands when `NAME` is set; an unset one, and a lone `%` as in `100%`, stay as typed at the prompt.
5. A trailing backslash before a closing quote is doubled, as in `"C:\Log files\\"`, since `\"` would otherwise be read as a literal quote.

## `.cmd` or `.bat` file

<!-- example: cmd-batch -->
```bat
headless-run --env "GREETING=hello world" -- "C:\Tools\My Tool\tool.exe" "hello world" "say \"hi\"" --verbose /quiet -- 100%% "a&b" "C:\Log files\\"
headless-run -- C:\Scripts\job.ps1 -Name "nightly run" -DryRun
```

A batch file waits for each command and sets `%ERRORLEVEL%`, so `start /wait` is not needed. The prompt's rules apply, except that `%` is doubled to `%%` for a literal `%`, and an unset `%NAME%` becomes empty.

## PowerShell 7

<!-- example: pwsh -->
```powershell
headless-run --env 'GREETING=hello world' -- 'C:\Tools\My Tool\tool.exe' 'hello world' 'say "hi"' --verbose /quiet -- 100% 'a&b' 'C:\Log files\' | Out-Host
headless-run -- C:\Scripts\job.ps1 -Name 'nightly run' -DryRun | Out-Host
```

1. PowerShell does not wait for a Windows-subsystem program unless its output is piped, so `| Out-Host` makes it wait and set `$LASTEXITCODE`.
2. Single quotes keep `$`, `` ` `` and `"` literal. PowerShell 7.3 and later quote each argument for the program by Windows' rules (`$PSNativeCommandArgumentPassing` is `Windows`, which treats `headless-run.exe` as `Standard`), so an embedded `"` and a trailing `\` need no escaping.
3. `--` and arguments that start with `-` or `/` reach a native program unchanged.
4. After the stop-parsing token `--%`, the rest of the line goes to the program as typed: single quotes are no longer quotes, and `%NAME%` expands instead of `$name`.
5. With `$PSNativeCommandArgumentPassing = 'Legacy'`, PowerShell 7 behaves like Windows PowerShell 5.1 below.

## Windows PowerShell 5.1

<!-- example: powershell -->
```powershell
headless-run --env 'GREETING=hello world' -- 'C:\Tools\My Tool\tool.exe' 'hello world' 'say \"hi\"' --verbose /quiet -- 100% 'a&b' 'C:\Log files\\' | Out-Host
headless-run -- C:\Scripts\job.ps1 -Name 'nightly run' -DryRun | Out-Host
```

Windows PowerShell 5.1 wraps an argument with a space in quotes without escaping what is inside it. So an embedded `"` is written `\"`, or `tool.exe` receives `say hi`, and a trailing backslash in an argument with a space is doubled, or `tool.exe` receives `C:\Log files"`. `| Out-Host` makes it wait, as in PowerShell 7.

## Git Bash

<!-- example: git-bash -->
```bash
MSYS_NO_PATHCONV=1 headless-run --env 'GREETING=hello world' -- 'C:\Tools\My Tool\tool.exe' 'hello world' 'say "hi"' --verbose /quiet -- 100% 'a&b' 'C:\Log files\'
MSYS_NO_PATHCONV=1 headless-run -- 'C:\Scripts\job.ps1' -Name 'nightly run' -DryRun
```

Git Bash rewrites an argument that looks like a POSIX path into a Windows path before it starts a Windows program: without `MSYS_NO_PATHCONV=1`, `/quiet` reaches `tool.exe` as `quiet` under the Git install folder, such as `C:/Program Files/Git/quiet`. `MSYS_NO_PATHCONV=1` turns that off for the command, so paths are written in their Windows form. To keep the conversion for other arguments, write a leading `//` instead, as in `//quiet`, or list the prefixes to leave alone in `MSYS2_ARG_CONV_EXCL`. Bash waits for the launcher and sets `$?`.

## WSL

<!-- example: wsl -->
```bash
headless-run.exe --env 'GREETING=hello world' -- 'C:\Tools\My Tool\tool.exe' 'hello world' 'say "hi"' --verbose /quiet -- 100% 'a&b' 'C:\Log files\'
headless-run.exe -- 'C:\Scripts\job.ps1' -Name 'nightly run' -DryRun
```

1. A Windows program is called by its `.exe` name. It is found on `PATH` when WSL appends the Windows `PATH`, as it does by default.
2. WSL quotes each argument by Windows' rules when it builds the command line, as PowerShell 7 does, and it waits for the launcher.
3. The program and every path the child reads are Windows paths: a Linux path such as `/mnt/c/Tools/My Tool/tool.exe` makes the launcher fail with 125. `wslpath -w` converts one, as in `"$(wslpath -w ~/job.sh)"`.

## Task Scheduler

<!-- example: task-scheduler -->
```powershell
$tool = New-ScheduledTaskAction -Execute 'C:\Tools\headless-run.exe' -Argument '--env "GREETING=hello world" -- "C:\Tools\My Tool\tool.exe" "hello world" "say \"hi\"" --verbose /quiet -- 100% a&b "C:\Log files\\"'
$job = New-ScheduledTaskAction -Execute 'C:\Tools\headless-run.exe' -Argument '-- C:\Scripts\job.ps1 -Name "nightly run" -DryRun'
Register-ScheduledTask -TaskName 'headless-run example' -Action $tool, $job
```

1. The `-Argument` string, which is the **Add arguments** field of the task's action and the `Arguments` element of its XML, is the raw command line after the program, so it follows Windows' rules alone: `\"` for an embedded quote, a doubled backslash before a closing quote, and no shell operators.
2. Task Scheduler expands `%NAME%` in it when it runs the task, so `%USERNAME%` reaches the child as the user's name. A lone `%`, as in `100%`, stays.
3. `schtasks /create /tr` takes the program and its arguments as one string, written inside `cmd`'s quotes with `\"` for each embedded quote. It refuses a string longer than 261 characters, and one close to that length can be stored cut short with no error: on Windows 11 a string of exactly 261 characters lost its last 8. The call above does not fit, so use `Register-ScheduledTask` or a task XML file instead.

## Shortcut

<!-- example: shortcut -->
```powershell
$shell = New-Object -ComObject WScript.Shell
$tool = $shell.CreateShortcut('C:\Tools\tool.lnk')
$tool.TargetPath = 'C:\Tools\headless-run.exe'
$tool.Arguments = '--env "GREETING=hello world" -- "C:\Tools\My Tool\tool.exe" "hello world" "say \"hi\"" --verbose /quiet -- 100% a&b "C:\Log files\\"'
$tool.Save()
$job = $shell.CreateShortcut('C:\Scripts\job.lnk')
$job.TargetPath = 'C:\Tools\headless-run.exe'
$job.Arguments = '-- C:\Scripts\job.ps1 -Name "nightly run" -DryRun'
$job.Save()
Invoke-Item -Path 'C:\Tools\tool.lnk', 'C:\Scripts\job.lnk'
```

1. A shortcut's arguments, the part of its **Target** field after the program, are the raw command line after the program, as in a scheduled task: `\"` for an embedded quote, a doubled backslash before a closing quote, and no shell operators. `Invoke-Item` opens a shortcut as a double-click does.
2. Windows expands `%NAME%` in the arguments when it opens the shortcut, so `%USERNAME%` reaches the child as the user's name. An unset `%NAME%` and a lone `%`, as in `100%`, stay as written.
3. A shortcut holds at most 1,023 characters of arguments. `CreateShortcut` cuts a longer string to that length with no error, so a long call belongs in a `.cmd` file that the shortcut runs through `headless-run`.

## Run dialog

Type the whole line into the Run dialog (Win+R) and press Enter:

```text
C:\Tools\headless-run.exe --env "GREETING=hello world" -- "C:\Tools\My Tool\tool.exe" "hello world" "say \"hi\"" --verbose /quiet -- 100% a&b "C:\Log files\\"
```

1. The Run dialog passes the line the way a shortcut does: the same raw command line, `\"` for an embedded quote, a doubled backslash before a closing quote, and no shell operators, so `a&b` arrives as one argument.
2. It expands `%NAME%` too: `%USERNAME%` reaches the child as the user's name, while an unset `%NAME%` and the lone `%` in `100%` stay as written.
3. Its field holds at most 259 characters. A longer line is cut there with no error, so put a long call in a `.cmd` file or a shortcut.

This example was checked by hand on Windows 11, since the test script cannot type into the Run dialog. Run from another folder, the line delivered the eight arguments listed above and the `GREETING` variable, with `%USERNAME%` and `%NO_SUCH_VAR%` appended to check the expansion.

## Code

These start `headless-run` with an argument list, and each runtime quotes the list by Windows' rules, so the arguments are written as plain strings.

<!-- example: node -->
```js
import { spawnSync } from 'node:child_process';

spawnSync('headless-run', ['--env', 'GREETING=hello world', '--', 'C:\\Tools\\My Tool\\tool.exe', 'hello world', 'say "hi"', '--verbose', '/quiet', '--', '100%', 'a&b', 'C:\\Log files\\']);
```

<!-- example: python -->
```python
import subprocess

subprocess.run(['headless-run', '--env', 'GREETING=hello world', '--', r'C:\Tools\My Tool\tool.exe', 'hello world', 'say "hi"', '--verbose', '/quiet', '--', '100%', 'a&b', 'C:\\Log files\\'])
```

<!-- example: csharp -->
```csharp
using System.Diagnostics;

var start = new ProcessStartInfo("headless-run")
{
    ArgumentList = { "--env", "GREETING=hello world", "--", @"C:\Tools\My Tool\tool.exe", "hello world", "say \"hi\"", "--verbose", "/quiet", "--", "100%", "a&b", @"C:\Log files\" },
};
using var process = Process.Start(start)!;
process.WaitForExit();
```

1. Node's `windowsVerbatimArguments: true` passes the strings joined by spaces without quoting them, so each one must then be quoted by hand, as on the Task Scheduler line.
2. Python's `subprocess.list2cmdline` shows the command line a list becomes.
3. .NET's `ProcessStartInfo.Arguments`, a single string, is passed as written, while `ArgumentList` quotes each item.

## Pitfalls

1. **The launcher's `--`**: everything after the first `--` belongs to the child, including another `--`. No shell above removes either one.
2. **Waiting**: a `cmd` prompt and PowerShell return before a Windows-subsystem program ends; use `start "" /wait` or pipe the output, as above.
3. **`%`**: doubled in a batch file, expanded at a prompt when the variable is set, and expanded by Task Scheduler and a shortcut.
4. **Embedded quotes**: `\"` on every Windows command line, and in Windows PowerShell 5.1 even inside a single-quoted string; PowerShell 7, Git Bash, WSL and the code callers quote for you.
5. **A trailing backslash before a closing quote**: doubled wherever you write the command line yourself (`cmd`, Windows PowerShell 5.1, Task Scheduler, a shortcut).
6. **Path conversion in Git Bash**: `/quiet` and other slash-led arguments become Windows paths unless `MSYS_NO_PATHCONV=1` is set.
7. **`schtasks /tr`**: at most 261 characters, and a string near that length can lose its end with no error.
