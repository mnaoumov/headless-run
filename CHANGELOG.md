# Changelog

All notable changes to this project are documented in this file.

## 0.1.0 - 2026-11-02

First release.

- Launch a Windows console program without a console window, forwarding its raw command line unchanged and returning its full 32-bit exit code.
- Redirect standard output and standard error to files with `--stdout`, `--stderr`, `--append` and `--overwrite`.
- Set the child's working directory, environment variables and priority with `--cwd`, `--env` and `--below-normal`.
- Stop the child and its descendants after `--timeout`, returning 124.
- Contain the child and its descendants in a kill-on-close job, or leave it running with `--detach`.
- Native, self-contained x64 and ARM64 executables with SHA-256 checksums.
