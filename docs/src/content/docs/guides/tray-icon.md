---
title: Tray icon
description: The notification-area icon that shows a job's output and stops it.
sidebar:
    order: 8
---

While the launcher waits on its job, it can show one icon of its own in the notification area, with the command line as its tooltip; several jobs show several icons. `--tray` takes an optional inline value:

1. `--tray=auto`, the default: the icon is shown when a person runs the launcher on their desktop, and not when the run has no visible desktop (session 0, a service, a task that runs whether or not the user is logged on), when Task Scheduler started it (its service process is an ancestor, directly or through the script its task runs), or with `--detach`. A scheduled task stays invisible with no flag to remember.
2. `--tray` or `--tray=true`: always show it.
3. `--tray=false`: never show it, for example from a script started by hand that should stay silent.

Right-click the icon for the menu:

1. The command line, the start time and the child's process id.
2. **Show output** / **Hide output**: a read-only window with the job's output, also toggled by double-clicking the icon. Closing that window only hides it; the job keeps running.
3. **Open stdout log** / **Open stderr log**: open a `--stdout` or `--stderr` file in its default application.
4. **Stop**: terminate the whole job tree. The launcher returns 130 and writes a diagnostic to stderr.

When an explicit `--tray` job ends, the icon changes, a notification shows the exit code, and the icon stays for 5 seconds with the code in its menu and a **Dismiss** item that ends the wait at once. Only explicit `--tray` runs pay those seconds; the exit code is the child's, as without the option. Under `--tray=auto` the icon goes as soon as the job ends, with no notification, so a run from a terminal returns as fast as before and its exit code is in that terminal. A job stopped from the menu ends at once.

Under `--tray=auto` the icon is a convenience: if there is no notification area to add it to, the job runs on without it and nothing is reported. An explicit `--tray` reports that on stderr.

The output window shows the last 256K characters of both streams together, decoded as UTF-8:

1. A stream redirected to a file is followed from that file. With `--append`, only what this run adds is shown.
2. Any other stream goes through a pipe that the launcher copies to wherever the stream would otherwise have gone, so the caller still receives every byte, and to the window. When stdout and stderr share one destination, their relative order there can differ from a run without `--tray`, since each passes through its own pipe. A stream with nowhere to go, such as the output of a launcher started from a shortcut, is still shown in the window.

The child still runs with `CREATE_NO_WINDOW`. Starting it with a hidden console to reveal later was rejected: a console shows nothing of redirected streams, which is how `headless-run` is normally used, and closing it would kill the job.

With other options:

1. `--timeout` works unchanged; the icon shows the timeout, and with an explicit `--tray` 124 is returned after the 5 seconds.
2. `--detach` cannot combine with `--tray` or `--tray=true`: a detached launcher exits at once, so nothing is left to show the job. The combination is a launcher failure (125). Under the default `--tray=auto`, `--detach` simply shows no icon.
3. `--elevate`: the unelevated caller shows the icon and the output, since every relayed byte passes through it. **Stop** ends the caller with 130, and the elevated copy then terminates the tree, as it does when the caller is killed. The menu shows no process id, because the caller cannot see inside the elevated job.
4. Without a notification area, as in a service or session 0, the launcher writes a diagnostic and the job runs on without the icon.
