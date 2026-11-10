# Changelog

All notable changes to this project are documented in this file.

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
