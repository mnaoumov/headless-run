# headless-run development

`headless-run` is a Windows-subsystem launcher built with C# and .NET 10. It forwards the raw child command line through documented Win32 APIs. Keep the child tail opaque: only launcher options before `--` may be parsed. The one read of the tail is the program token's extension, which picks the host: a native executable gets the tail unchanged, `.cmd`/`.bat` get it wrapped as `cmd.exe /d /s /c "<tail>"`, and `.ps1` gets it appended to `pwsh -NoProfile -NonInteractive -File`. Never re-quote or split the tail for any host.

## L1. C# coding standards

Use Allman braces on separate lines and expand every control-flow body, including single-statement bodies. Do not keep multiple statements or compact type/method bodies on one line. Apply the same standards to integration fixtures. The root `.editorconfig` defines formatting and requires braces; builds enforce the brace diagnostic.

Declare one variable per declaration and put each declaration on its own line, including fields, constants and locals that share a type. `scripts/check-csharp-style.ps1` checks the C# syntax tree for combined declarations, compact/non-Allman blocks and declarations/statements sharing a line. It uses Roslyn bundled with PowerShell 7 and runs before every integration verification; comments and string literals are not scanned as code.

Replace unexplained numbers with descriptive constants, including Win32 flags, information classes, standard-handle identifiers, wait results, unit conversions and test timing limits. Keep trivially obvious zero/one values, array indices and explicit test data literal. Spell `Milliseconds` in full in identifiers. Use the `_IN_` form for unit-conversion constants.

Run `dotnet format style <project> --diagnostics IDE0011 --verify-no-changes` and `dotnet format whitespace <project> --verify-no-changes` for both C# projects. Keep `DOTNET_PROCESSOR_COUNT=2` while running formatting, builds and integration tests. Formatting checks do not replace the manual constant audit or integration verification.

## Build and verify

Run `pwsh -NoProfile -File scripts/test.ps1` on Windows. The dependency-free integration executable doubles as the real child-process fixture. Tests run serially; build parallelism is capped at two.

Run `dotnet publish src/headless-run -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64 -m:2` to build a native executable. NativeAOT requires the Visual Studio C++ toolchain. When it is unavailable locally, test the JIT build and use GitHub Actions for the native build. Always run the integration harness against the published executable too.

## Process lifetime and handles

`PROC_THREAD_ATTRIBUTE_JOB_LIST` attaches a child atomically to a kill-on-close job. The launcher alone owns the job handle. Only duplicated standard handles and explicit output files enter `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Detached mode omits the owned job and cannot use a timeout. The launcher waits for the direct child; its exit closes the job and terminates remaining descendants.

## Elevation

`--elevate` is in `Elevation.cs`. When the caller is unelevated, it starts a copy of the launcher with `ShellExecuteEx`'s `runas` verb, passing the original arguments behind the internal `--elevated-relay <caller pid>:<pipe base>` option. The copy reads the caller's environment and working directory from the `-env` pipe, adopts the `-in`/`-out`/`-err` pipes as its standard handles, and then runs `Launch` unchanged. It also waits on the caller's process, so a killed caller ends the job. The pipes are ACL'd to the caller's account and Administrators, and each side checks the peer's pid. `HEADLESS_RUN_TEST_RELAY_WITHOUT_UAC=1` is the test seam: it swaps `runas` for `open`, so the integration tests drive the whole relay with no prompt. Keep every relay test on that seam; only a person can answer a real prompt.

## Tray icon

`--tray` is in `Tray.cs` (window, menu, icon, output viewer, on its own STA thread with its own message loop), `OutputCapture.cs` (the viewer's bounded text, file tails) and `Tee.cs` (a pipe between the child and a stream's original handle). It is plain Win32 through `TrayNative.cs`, with an `[UnmanagedCallersOnly]` window procedure, so NativeAOT keeps working; do not bring in WinForms or WPF. One icon per launcher; under `--elevate` only the unelevated caller hosts it, and its Stop ends the caller, whose exit makes the elevated copy terminate the tree. The hidden tray window's title carries the job's state (`headless-run: running`, `headless-run: exited <code>`), and its `WM_COMMAND` ids are the menu's: the integration tests find that window by class and owner pid and drive it, so keep the ids in `Tray.cs` and the test constants in step.

## Event log

`--event-log` is in `JobEventLog.cs`: one Application-log event per run, written as `Main` returns, through P/Invoke `RegisterEventSourceW`/`ReportEventW` (no `System.Diagnostics.EventLog` package, so NativeAOT needs nothing extra). A code path that knows its outcome better than the exit code does (timeout, tray stop, detach, caller gone) calls `JobEventLog.Record`; otherwise the code decides. Under `--elevate` the copy writes the event and the caller sets `JobEventLog.Relayed`, so a relayed run is never logged twice. The source is never registered by the launcher, and a failed write is swallowed. The event ids are documented in the README; keep them, the `Outcome` enum and the integration test constants in step.

## Delivery

The default branch is `main`. CI tests both JIT and native x64 builds. Update the project version, `--help`/`--version` text and version assertion together. In the same release commit, before tagging, add the version's dated entry at the top of `CHANGELOG.md`; the release workflow does not write it. It publishes that section as the GitHub release notes, with the generated compare link below it, and fails when the section is missing or empty. Push a matching `vX.Y.Z` tag to publish a release. The release workflow tests native x64, x86 and ARM64 executables before publishing them with SHA-256 files. Download the released executables and verify their checksums and integration suite before calling the release delivered. Then update `bucket/headless-run.json` in the `mnaoumov/scoop-bucket` repo: version, and the URL and hash for each of `64bit`, `32bit` and `arm64`.

When the tests run inside a job object with a priority-class limit, as some automation hosts impose, every `--priority` value yields that class. The integration test detects the limit and expects the imposed class; CI has no limit and checks every class.
