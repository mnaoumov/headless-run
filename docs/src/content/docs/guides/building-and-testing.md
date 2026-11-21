---
title: Building and testing
description: Build the launcher, run its integration tests and publish a native executable.
sidebar:
    order: 10
---

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

The integration harness drives real children and compares direct versus wrapped argv and raw command lines, tests 32-bit exit codes, stream inheritance and file modes, created output directories, environment, cwd, every priority class, both option spellings, `.exe` and `.com` programs, every built-in engine whose interpreter the machine has (the others are skipped with a note) and their argument rules, `--engine` and `--force-engine`, the Node version gate for TypeScript, the config layers and every `config` action (with `%APPDATA%` and `%ProgramData%` pointed at a temporary folder, so the machine's own config is never read or written), console visibility, failures, timeout, descendant cleanup, launcher termination, detach, and the `--tray` menu, output window, finish linger and stop, driven through the launcher's hidden tray window, and the `--event-log` event for each outcome, read back from the Application log. `--elevate` is tested through its full relay. A test-only variable, `HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC=1`, starts the copy without the `runas` verb, so no prompt appears. An elevated test run, such as CI, also checks that the option does nothing there. Answering a real UAC prompt needs a person. It then runs `scripts/test-shell-examples.ps1`, which checks every example in [Calling it from a shell](/headless-run/guides/calling-from-a-shell/); run that script with `-Wsl` and `-TaskScheduler` to add the two examples it skips by default.

For a runtime-free executable, install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and run:

```powershell
dotnet publish src/headless-run -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64 -m:2
pwsh -NoProfile -File scripts/test.ps1 -Launcher ./artifacts/win-x64/headless-run.exe
```

Use `win-x86` for a 32-bit build, and `win-arm64` on an ARM64 machine. CI builds and tests NativeAOT for x64 and x86. Pushing a `vX.Y.Z` tag matching the project version publishes x64, x86 and ARM64 executables and their SHA-256 checksums after the native tests pass.

## Implementation references

The implementation uses [`CreateProcessW`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [`CREATE_NO_WINDOW`](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags), [`PROC_THREAD_ATTRIBUTE_JOB_LIST` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute), and [`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information). Job assignment happens inside process creation, avoiding a suspended process orphaned between creation and assignment. Only intended standard handles are inherited; the job handle stays owned by the launcher.
