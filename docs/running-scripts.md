# Running scripts

This page describes how TeleTasks executes the `command` you put in a task
definition, what limits that imposes, and how to avoid the common failure
modes — especially when the script invokes another runtime like Node.js or
Python.

## Two execution paths

Which one you get depends on `longRunning` in the task definition.

### Short-running (`longRunning` absent or false)

`Services/TaskExecutor.cs` calls `Process.Start` with `task.Command` as the
binary and `task.Args` as the argument list. There is **no shell**.

- The default timeout is 60 s (`TaskCatalog.CommandTimeoutSeconds`); on
  expiry the runner sends `Process.Kill(entireProcessTree: true)` and
  returns exit code 124.
- stdout and stderr are read on background threads and accumulated in
  in-memory buffers, returned to the caller after exit.
- The bot's full environment is inherited; `task.env` keys override.
- No stdin is redirected.

### Long-running (`longRunning: true`)

`Services/JobTracker.Start` wraps the command in a detached shell:

```
/bin/bash -c "setsid bash -c '<cmd> <args>; echo $? > <exitcode>' \
              >log 2>&1 </dev/null & echo $!"
```

- `setsid` puts the workload in its own session so it survives a bot
  restart. The bot only records the PID and returns immediately.
- stdout and stderr are merged into a single log file under
  `~/.config/teletasks/run-logs/`.
- stdin is `/dev/null`.
- The wrapper writes `$?` of the inner command to a sidecar `.exitcode`
  file. The bot picks it up via `Reconcile()` when you query the job.
- `TimeoutSeconds` is **ignored** for long-running tasks. Use `/stop N`
  to cancel; the bot escalates from `Process.Kill(entireProcessTree)` to
  `kill -KILL -<pid>` against the session group.
- Liveness is read directly from `/proc/<pid>/stat` so zombie processes
  are correctly reported as dead.

## Limitations you should know about

### Short-running

| Limitation | Consequence |
|---|---|
| Not a shell | `command: "echo hi \| grep h"` does not work; pipes, redirects, globs, `&&`, `~`, `$VAR` are not interpreted |
| `command` is a single binary | `command: "node script.js"` fails with ENOENT — split into `command: "node"`, `args: ["script.js"]` |
| In-memory output | A script that prints megabytes will balloon the bot's RSS — there is no streaming, no log file, no cap |
| Process-tree kill is not session-based | A child that double-forks (e.g. `nohup`, `&` with `disown`, daemonized servers) escapes the timeout kill and leaks |
| No stdin | `read -p`, `npm login`, `ssh` password prompts hang until timeout |

### Long-running

| Limitation | Consequence |
|---|---|
| `set -e`, `pipefail` are NOT applied | Wrapper is `<cmd>; echo $?` — only the *last* command's exit code is captured |
| Single combined log | stderr is interleaved with stdout, you cannot recover them separately |
| No log rotation or size cap | A noisy server can fill the disk; logs are deleted only when the job record is pruned |
| PID reuse is not guarded | After a bot restart, liveness is checked by PID alone — long-uptime hosts with PID wrap can confuse the bot |
| Inner command is shell-quoted, not shell-interpreted | `command: "cd /x && node y"` is passed as one literal token; for shell features you must use `command: "bash"`, `args: ["-c", "..."]` |
| Restart reuses params and paths | If your script writes to a fixed output path, it must clean up itself |

### Both modes

- **PATH is whatever the bot inherited.** Under systemd, that is usually
  the minimal `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`.
  `node`, `python3.11`, anything in `~/.nvm/...` or `~/.asdf/...` is **not**
  on PATH. `command: "node"` then resolves to `/usr/bin/node`, not the
  version you tested with.
- **Non-interactive shell.** `~/.bashrc`, `~/.profile`, `nvm.sh`, conda's
  `activate` are not sourced by default.
- **Working directory defaults to the bot's CWD** if `workingDirectory`
  is empty. Anything that reads `./.env`, `./package.json`, `./config.yaml`
  needs `workingDirectory` set explicitly.

## Avoiding failures when calling Node.js

Most Node-related issues come from PATH leakage and unset HOME.

### Pin the interpreter via `env`

Don't rely on the shell init files to fix PATH for you. Set the
node-bin directory at the front of PATH:

```json
"env": {
  "PATH": "/home/svc/.nvm/versions/node/v20.11.1/bin:/usr/local/bin:/usr/bin:/bin",
  "HOME": "/home/svc",
  "NPM_CONFIG_PREFIX": "/home/svc/.npm-global",
  "NPM_CONFIG_CACHE": "/home/svc/.npm",
  "NODE_ENV": "production"
}
```

`NPM_CONFIG_PREFIX` and `NPM_CONFIG_CACHE` keep `npm install -g` and the
package cache out of `/usr` and `/`. An unset `HOME` makes npm fall back
to `/`, which produces permission-denied errors that look like "trying
to write to /usr".

### Make the script honest about exit codes

Inside Node:

```js
process.on('unhandledRejection', e => { console.error(e); process.exit(1); });
process.on('uncaughtException',  e => { console.error(e); process.exit(1); });
```

Otherwise an unhandled rejection lets the process exit 0 even though
nothing useful happened, and TeleTasks reports success.

### Don't share `node_modules` between concurrent jobs

Two long-running jobs in the same `workingDirectory` running `npm install`
or `npm ci` will corrupt each other. There is no per-job lock.

### Don't expect interactivity

stdin is `/dev/null`. Pass tokens through `env`, not prompts.
Use `npm ci --yes` rather than `npm install`.

## When you actually need shell features

Either way, do not rely on `bash -lc` to source nvm — the `-l` flag only
sources login files (`~/.profile` / `~/.bash_profile`). nvm's installer
puts its init in `~/.bashrc`, which is **not** read by a non-interactive
login shell. Set PATH explicitly via `env` instead.

If you need pipes, redirects, or `&&` in a long-running task, the inner
command in `JobTracker` is *literal*, not interpreted. You must invoke
bash yourself:

```json
"command": "bash",
"args": [
  "-c",
  "set -euo pipefail; cd /srv/myapp && node ./build.js | tee build.log"
]
```

Note: `bash -c` takes **one** command-string argument. Anything after that
becomes positional parameters (`$0`, `$1`, …) inside that string. A common
mistake:

```json
"args": ["-lc", "set -euo pipefail", "/path/to/script.sh", "{arg1}"]
```

This runs `set -euo pipefail` as the entire script, with the script path
landing in `$0` and `{arg1}` in `$1` — the script is never executed.
Either inline everything into one string and reference positional args
with `"$@"`:

```json
"args": [
  "-c",
  "set -euo pipefail; exec /path/to/script.sh \"$@\"",
  "script-name",
  "{arg1}"
]
```

…or skip the wrapper and exec the script directly. If the script begins
with `#!/usr/bin/env bash` and `set -euo pipefail`, this is cleaner:

```json
"command": "/path/to/script.sh",
"args": ["{arg1}"]
```

## Sanity-check task

After any deploy, systemd change, or PATH edit, run a small env-probe
task once:

```json
{
  "name": "envcheck",
  "description": "Print the runtime env the bot exposes to tasks.",
  "command": "bash",
  "args": [
    "-c",
    "echo PATH=$PATH; echo HOME=$HOME; echo PWD=$(pwd); echo USER=$(id -un); command -v node && node -v; command -v python3 && python3 -V"
  ]
}
```

90% of "it works in my shell, not via the bot" issues become obvious
from this output.

## Discovery may overwrite your edits

Task definitions auto-generated by `/discover` carry a `source` field
like `sh:teletasks:askpi.sh`. From `Models/TaskDefinition.cs`:

> Empty/null means the task is hand-managed and discover will not touch
> it on re-run.

If you hand-edit a discovered task and then re-run `/discover`, your
changes to `command`, `args`, `env`, and `workingDirectory` get clobbered.
Either:

- Clear the field: `"source": ""` (or remove the line), or
- Rename the task to something the discoverer won't reproduce
  (e.g. add a `_prod` suffix) and disable the auto-generated version
  with `"enabled": false`.

## Operational tips

- **Set `workingDirectory` explicitly.** Don't rely on the bot's CWD.
- **Use `longRunning: true` for anything that might exceed 60 s.**
  Otherwise it dies with exit 124 and any double-forked node child leaks.
  Long-running uses `setsid` and a session-group SIGKILL, which is
  reliable.
- **Cap output volume.** For short-running tasks, prefer `--quiet` and
  emit a summary; large outputs grow the bot's RSS. For long-running
  tasks, rotate logs externally if the job runs continuously for days.
- **Verify exit codes.** With long-running, `set -euo pipefail` inside
  the script (or the bash wrapper) is the only way to make the captured
  `$?` reflect failures in any but the last command.
