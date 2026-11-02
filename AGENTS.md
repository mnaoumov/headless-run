# headless-run development

`headless-run` is a Windows-subsystem launcher built with C# and .NET 10. It forwards the raw child command line through documented Win32 APIs. Keep the child tail opaque: only launcher options before `--` may be parsed.

## Build and verify

Run `pwsh -NoProfile -File scripts/test.ps1` on Windows. The dependency-free integration executable doubles as the real child-process fixture. Tests run serially; build parallelism is capped at two.

Run `dotnet publish src/headless-run -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64 -m:2` to build a native executable. NativeAOT requires the Visual Studio C++ toolchain. When it is unavailable locally, test the JIT build and use GitHub Actions for the native build. Always run the integration harness against the published executable too.

## Process lifetime and handles

`PROC_THREAD_ATTRIBUTE_JOB_LIST` attaches a child atomically to a kill-on-close job. The launcher alone owns the job handle. Only duplicated standard handles and explicit output files enter `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Detached mode omits the owned job and cannot use a timeout. The launcher waits for the direct child; its exit closes the job and terminates remaining descendants.

## Delivery

The default branch is `main`. CI tests both JIT and native x64 builds. Update the project version, `--help`/`--version` text and version assertion together. Push a matching `vX.Y.Z` tag to publish a release. The release workflow tests native x64 and ARM64 executables before publishing them with SHA-256 files. Download the released executable and verify its checksum and integration suite before calling the release delivered.
