---
title: Programs and scripts
description: How the program's extension picks an engine, and the built-in engines.
sidebar:
    order: 3
---

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
6. **`bash`**: Git for Windows' `bash.exe`, found under the 64-bit and then the native `Program Files` (`%ProgramW6432%` and `%ProgramFiles%`, `Git\bin`), `%LOCALAPPDATA%\Programs\Git\bin` or a Scoop install. The `bash.exe` in `System32` is WSL's, which cannot open a Windows path, so it is never used; a WSL engine needs path translation and is left to a config entry.
7. Interpreters installed under another name, such as `lua54.exe` or `tclsh86.exe`, are reached by setting the engine's `path`.

Considered and left out: `dotnet run app.cs` and `dotnet fsi script.fsx`, because both read options such as `--help` after the script as their own, so the raw text could not reach the script untouched; and AutoHotkey, a GUI runtime. Any of them can still be registered in a config file.

The interpreter's exit code is returned: `exit /b <n>` from a batch file, `exit <n>` from a script.
