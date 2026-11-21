---
title: Event log
description: One Application event log entry per run with --event-log.
sidebar:
    order: 9
---

`--event-log` is opt-in. It writes one event per run to the Application log, under the source `headless-run`, when the run ends, so a failed scheduled run shows in Event Viewer without a `--stderr` file. Task Scheduler's own history records only the exit code; this event also says whether 124, 125 or 130 came from the launcher or from the child, and carries the launcher's diagnostic.

| Event id | Level | Outcome |
| --- | --- | --- |
| 1 | Information | The child exited with code 0. |
| 2 | Warning | The child exited with a nonzero code. |
| 3 | Error | The timeout expired and the job was terminated (124). |
| 4 | Warning | The job was stopped from the `--tray` menu (130). |
| 5 | Error | Launcher failure (125), with the diagnostic. |
| 6 | Information | A `--detach` child started. |

The text also holds the command line, the working directory, the start time, the duration and the launcher's process id. The child's output is never written to the log. There is no start event for a waited run: Task Scheduler already records starts, and one event per run keeps a frequent job from flooding the log.

Register the source once, from an elevated Windows PowerShell 5.1 (`New-EventLog` is not in PowerShell 7):

```powershell
New-EventLog -LogName Application -Source headless-run
```

Unregistered, the events are still written under that source name, but Event Viewer prefixes each with "The description for Event ID ... cannot be found" before showing the text. A run that cannot write its event, for any reason, carries on and returns its usual exit code.

Under `--elevate`, the elevated copy writes the event, since it alone tells a timeout from a child's own 124; the unelevated caller writes one only for what happens on its side, such as a declined prompt or a tray stop. Read the events with `Get-WinEvent -LogName Application -FilterXPath "*[System[Provider[@Name='headless-run']]]"`, which works whether or not the source is registered; a `ProviderName` filter finds them only once it is.
