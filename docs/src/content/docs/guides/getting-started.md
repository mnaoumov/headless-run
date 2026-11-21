---
title: Getting started
description: Install headless-run and run a first program without a console window.
sidebar:
    order: 1
---

The launcher is a Windows-subsystem executable. It starts console programs with `CREATE_NO_WINDOW`, using documented Win32 APIs. It requires Windows 10 / Windows Server 2016 or later. Release executables are native, self-contained, and need no .NET runtime installed.

## Install

With [Scoop](https://scoop.sh), from the author's bucket:

```powershell
scoop bucket add mnaoumov https://github.com/mnaoumov/scoop-bucket
scoop install mnaoumov/headless-run
```

For a scheduled task, `~\scoop\apps\headless-run\current\headless-run.exe` is the full path to use, and it stays the same across `scoop update`.

Or download `headless-run-win-x64.exe`, `headless-run-win-x86.exe` or `headless-run-win-arm64.exe` from [Releases](https://github.com/mnaoumov/headless-run/releases), verify it against the adjacent `.sha256` file, and rename it to `headless-run.exe` in a directory on `PATH`.

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

A calling shell processes its own syntax before the launcher sees it, so the same call is typed differently in each shell. [Calling it from a shell](/headless-run/guides/calling-from-a-shell/) shows each one.
