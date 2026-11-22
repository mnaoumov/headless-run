---
title: Configuration
description: Config files that add or change engines and output defaults, and the config command that edits them.
sidebar:
    order: 4
---

Config files add engines, change a built-in one, change which engine an extension runs under, or set a default size cap for output files. They are read in this order, and a key set in a later one replaces the same key of an earlier one:

1. The [built-in engines](/headless-run/guides/programs-and-scripts/).
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
  },
  "output": {
    "maxSize": "10M"
  }
}
```

1. `engine.<name>.path`: the interpreter, as a file name searched on `PATH` (`.exe` is added when it has none) or a path. `%NAME%` expands from the child's environment. An array lists candidates, and the first that exists wins.
2. `engine.<name>.arguments`: the arguments placed between the interpreter and the program. Each is quoted by Windows' rules when it needs quotes.
3. `engine.<name>.extensions`: the extensions `--engine` accepts without `--force-engine`.
4. `engine.<name>.quoteCommand`: `true` wraps the raw text in one quote pair, as `cmd.exe /s /c` needs.
5. `extension.<ext>`: the engine an extension runs under when no `--engine` is given, by extension without its dot. An extension mapped to an engine is accepted by it too.
6. `output.maxSize`: the default for [`--max-size`](/headless-run/guides/options/), so every `--append` job on the machine keeps its output files bounded with no flag. A string such as `"10M"`, or a byte count; `"none"` turns off a cap that a lower layer set. `--max-size` on the command line wins over it.

Engine names have letters, digits, `-` and `_`, and are compared without case. A key replaces the same key below it, so `engine.python.path` in the global file keeps the built-in `python` engine's extensions. The schema at `$schema` lets an editor validate and complete the file.

## The `config` command

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
headless-run config --system output.maxSize 10M
headless-run config --list --show-origin
headless-run config --unset engine.python
```

The launcher is a Windows-subsystem program, so a terminal does not wait for it. When its output is not redirected, `config` writes to the console of the terminal that started it, but `cmd.exe` can show its prompt first and does not see the exit code. Pipe the output, such as `headless-run config --list | more`, or in PowerShell `headless-run config --list | Out-Host`, to have the shell wait and receive the exit code.
