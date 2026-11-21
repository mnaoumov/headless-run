---
title: Exit codes and lifetime
description: What the exit code means, and how the job tree is contained and ended.
sidebar:
    order: 6
---

1. Normal completion returns the direct child's full 32-bit exit code, including nonzero codes and Windows exception codes.
2. `124` means the timeout expired; the job tree was terminated.
3. `125` means invalid options or a launcher failure, such as a missing program, inaccessible file, invalid directory or failed Win32 call. A diagnostic goes to stderr or the configured error file. Without an inherited error handle or `--stderr`, a GUI-subsystem process has nowhere visible to display it.
4. `130` means the job was stopped from the `--tray` menu; the job tree was terminated.
5. `0` means successful detached startup, help or version output.

A child can itself return 124, 125 or 130; these codes cannot be distinguished from launcher statuses by exit code alone. Read the diagnostic stream when that distinction matters.

By default the launcher waits for the direct child. A kill-on-close job contains it and its descendants from process creation. When that child exits, any remaining descendants are terminated. Terminating the launcher also closes the job and kills that tree. Use `--detach` for applications that intentionally leave descendants running. No graceful shutdown signal is sent on timeout. Existing parent-job restrictions can prevent startup; such failures are reported rather than silently bypassing containment.

`CREATE_NO_WINDOW` prevents console windows for console programs. A GUI program can still create its own windows, and a child can explicitly launch another process with a new console. This tool does not suppress application UI, and it changes privileges only when `--elevate` asks for it.
