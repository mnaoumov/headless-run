---
title: Elevation
description: Run the child as an administrator with --elevate.
sidebar:
    order: 7
---

`--elevate` runs the child with an administrator token:

1. **Already elevated**: the option does nothing, so a script can pass it unconditionally.
2. **Not elevated**: Windows shows its UAC prompt, and an elevated copy of `headless-run` runs the child. The copy relays everything back to the caller, so the call behaves as it would without the option. Standard input, output and error go through named pipes. The copy receives the caller's environment variables and working directory, so relative paths resolve as usual. The exit code is the child's, and 124 and 125 keep their meaning. Every other option applies in the copy. Killing the caller ends the copy and the whole tree, as it does without the option.
3. **Prompt declined**: nothing runs, and the launcher fails with 125.
4. **Non-interactive session**: a service, a task that runs whether the user is logged on or not (S4U), and session 0 cannot show the prompt. The launcher fails at once with 125 rather than waiting for a consent that cannot come. Set such a task to **Run with highest privileges** instead.

Caveats:

1. A standard user answers the prompt with an administrator's credentials, so the child runs as that administrator, but with the caller's environment.
2. Drive letters mapped in the unelevated session are not visible to elevated processes unless Windows' `EnableLinkedConnections` policy is set. Use UNC paths.
3. With `--detach` nothing is relayed: unredirected streams of the child connect to `NUL`, and the launcher returns 0 once the copy has started the child.
4. The pipes accept only the caller's account and Administrators, and each side checks the other's process id, so no other process can read the relayed streams or feed the elevated child's input.

`--elevated-relay` is internal: the elevated copy receives it. Do not pass it yourself.
