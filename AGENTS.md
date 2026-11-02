# headless-run development

`headless-run` is a Windows-subsystem launcher built with C# and .NET 10. It forwards the raw child command line through documented Win32 APIs. Keep the child tail opaque: only launcher options before `--` may be parsed.

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

## Delivery

The default branch is `main`. CI tests both JIT and native x64 builds. Update the project version, `--help`/`--version` text and version assertion together. Push a matching `vX.Y.Z` tag to publish a release. The release workflow tests native x64 and ARM64 executables before publishing them with SHA-256 files. Download the released executable and verify its checksum and integration suite before calling the release delivered.
