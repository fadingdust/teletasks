# TeleTasks

A small .NET 8 service that bridges Telegram → a local Ollama LLM → a JSON-defined
task catalog on a Linux PC. You chat in natural language; the LLM picks the matching
task and extracts parameters; the service runs it and sends the result (text, files,
or images) back to the chat.

```
Telegram ──▶ TeleTasks (worker) ──▶ Ollama  (intent + parameters)
                       │
                       └────────▶  Process / log file / images directory
                                   ◀── result back to Telegram
```

## Features

- Define tasks in `tasks.json` (vscode-`tasks.json`-style) with a name, description,
  command, parameters, and an output spec.
- Local intent matching via Ollama's `/api/chat` JSON mode — no cloud round-trip.
- Output types: `Text`, `File`, `Image`, `Images` (from a directory), `LogTail`.
- Parameter substitution (`{name}`) inside `args`, `path`, `directory`, `caption`,
  `env`, `workingDirectory`, etc.
- Per-user / per-chat allow-list.
- Per-task timeout, environment vars, working directory.
- Hot-reload the catalog at runtime with `/reload`.

## Documentation

| Page | What's in it |
|---|---|
| [docs/cookbook.md](docs/cookbook.md)             | Annotated `tasks.json` recipes for the common patterns (latest screenshots, log tail, render with sidecar metadata, long-running jobs, shell-wraps-python, etc.). Copy & adapt. |
| [docs/discovery.md](docs/discovery.md)           | What `discover` does, the pipeline phases, what each log line means, every flag's effect. |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Diagnostic patterns: "captionFrom=no — why?", "wrapper isn't inheriting", "discover wrote to the wrong place", and the quick toolbox of `where` / `/dry` / `/latest`. |
| [SPECS.md](SPECS.md)                             | Running ledger of what's shipped on `main`, what's on open feature branches, what's on the backlog, and the design decisions worth remembering. |

## Prerequisites

- .NET 8 SDK
- A Telegram bot token (talk to [@BotFather](https://t.me/BotFather))
- Ollama running locally. The matcher uses **JSON-Schema-constrained output**, so
  small models work well — `ollama pull llama3.2:1b` (~1.3 GB, the default) or
  `ollama pull qwen2.5:0.5b` for an even lighter option. Larger models work too,
  but the schema constraint means there's little quality gain from spending more
  RAM.

## Where configuration lives

All persistent state — the wizard's output, your hand-edits, and the
`tasks.json` catalog — goes into a single user-level config directory:

```
$TELETASKS_CONFIG_DIR        # if you set it
  → $XDG_CONFIG_HOME/teletasks/
  → $HOME/.config/teletasks/  ← Linux/macOS default
  → %APPDATA%\teletasks\      ← Windows fallback
```

This dodges a footgun where the bot would read from `bin/Debug/net8.0/`
while `dotnet run -c Release` would put a fresh build in `bin/Release/net8.0/`
and silently drop your `Local.json`.

The bot logs every config source it loaded at startup, so if something looks
off you can always see where it came from:

```
info: TeleTasks.Configuration[0] Config dir: /home/me/.config/teletasks
info: TeleTasks.Configuration[0]   json /opt/teletasks/appsettings.json (loaded)
info: TeleTasks.Configuration[0]   json /opt/teletasks/appsettings.Local.json (missing, optional)
info: TeleTasks.Configuration[0]   json /home/me/.config/teletasks/appsettings.Local.json (loaded)
info: TeleTasks.Services.TaskRegistry[0] Loaded 14 task(s) from /home/me/.config/teletasks/tasks.json
```

A `Local.json` next to the binary is still loaded if it exists (legacy
installs), but the user-config-dir copy overrides it.

To see exactly which paths the binary will use right now:

```bash
dotnet TeleTasks.dll where
```

Prints the resolved config dir, the Local.json path, the tasks.json path,
and the full resolution chain (env vars + .NET special folders) so you can
diagnose any mismatch in one command. The resolver uses
`Environment.GetFolderPath(ApplicationData)` ahead of `$HOME`, so even a
shell with `HOME=""` still lands at the user's real config directory via
`getpwuid()`.

## Configure

The first time you run the bot with no configuration, it launches an
**interactive wizard** that walks you through:

1. Your bot token (validated against Telegram's `getMe`)
2. Your Telegram user ID — captured automatically by asking you to send any
   message to your bot, then polling `getUpdates`. No need to look up the
   numeric ID yourself.
3. Ollama endpoint and model (queries `/api/tags` to show what's pulled)

Answers are saved to `appsettings.Local.json` next to the binary. That file is
already covered by `.gitignore` and is layered on top of `appsettings.json` at
load time, so secrets never go into source control.

Re-run the wizard later with:

```bash
dotnet run --project src/TeleTasks -- setup
# or, for a published binary:
dotnet TeleTasks.dll setup
```

If you'd rather configure by hand, copy the example and edit it:

```bash
cp src/TeleTasks/appsettings.example.json src/TeleTasks/appsettings.Local.json
cp src/TeleTasks/tasks.example.json       src/TeleTasks/tasks.json
```

Any setting can also be supplied via env vars prefixed with `TELETASKS_`
(highest priority below the command line), e.g.
`TELETASKS_Telegram__Token=...`. Configuration precedence (highest wins):

1. Command-line args (`--Telegram:Token=...`)
2. `TELETASKS_*` env vars
3. `appsettings.Local.json` (the wizard's output)
4. `appsettings.{Environment}.json`
5. `appsettings.json`

If the bot starts with no token AND stdin is not a terminal (e.g. running under
systemd), it logs an error and exits — it will not block waiting for input.
Run `setup` interactively once, then start the service.

## Run

```bash
dotnet run --project src/TeleTasks
```

Then chat with the bot. Try:

```
/help
/tasks
how's my system doing?
show me the last 100 lines of /var/log/syslog
send me my 5 latest screenshots
```

## Defining a task

```jsonc
{
  "name": "tail_log",                       // unique id used by the LLM
  "description": "Tail the last N lines of a log file.",
  "parameters": [                           // exposed to the LLM for extraction
    { "name": "path",  "type": "string",  "required": true },
    { "name": "lines", "type": "integer", "default": 50 }
  ],
  "command": null,                          // no process needed for LogTail
  "args": [],
  "output": {
    "type": "LogTail",                      // Text | File | Image | Images | LogTail
    "path": "{path}",
    "lines": "{lines}",
    "caption": "{path}"
  }
}
```

For commands that produce stdout you want to forward as a chat message:

```jsonc
{
  "name": "system_status",
  "description": "Show CPU, memory, disk usage.",
  "command": "/bin/bash",
  "args": ["-c", "uptime; free -h; df -h /"],
  "output": { "type": "Text", "maxLength": 3500 }
}
```

For commands that write a file you want to send back:

```jsonc
{
  "name": "send_journal",
  "command": "/bin/bash",
  "args": ["-c", "journalctl -n {lines} > /tmp/j.txt"],
  "parameters": [{ "name": "lines", "type": "integer", "default": 500 }],
  "output": { "type": "File", "path": "/tmp/j.txt" }
}
```

For sending the most recent files in a directory (e.g. screenshots):

```jsonc
{
  "name": "latest_screenshots",
  "parameters": [{ "name": "count", "type": "integer", "default": 3 }],
  "output": {
    "type": "Images",
    "directory": "/home/me/Pictures/Screenshots",
    "pattern": "*.png",
    "sortBy": "newest",        // "newest" | "oldest" | "name"
    "count": "{count}"
  }
}
```

### Output spec reference

| field          | applies to                | description |
| -------------- | ------------------------- | ----------- |
| `type`         | all                       | `Text`, `File`, `Image`, `Images`, or `LogTail` |
| `path`         | File, Image, LogTail      | file path (templatable) |
| `directory`    | Images                    | directory to scan |
| `pattern`      | Images                    | glob, default `*` |
| `count`        | Images                    | how many files to send (int or `{param}`) |
| `sortBy`       | Images                    | `newest` (default), `oldest`, or `name` |
| `lines`        | LogTail                   | tail length (int or `{param}`) |
| `maxLength`    | Text, LogTail             | truncate output, default 3500 |
| `caption`      | File, Image, Images, LogTail | optional, templatable |
| `includeStderr`| Text                      | include stderr alongside stdout, default `true` |

## Security notes

- The bot only responds to user/chat IDs in the allow-list. With no allow-list it
  rejects everything.
- Parameter values are passed as **arguments**, not concatenated into a shell
  string — so a malicious `{path}` cannot inject extra commands when the task is
  `["/usr/bin/tail", "-n", "{lines}", "{path}"]`.
- If a task uses `bash -c "...{param}..."`, the parameter **is** part of the shell
  string. Only do that for tasks where you trust the parameter source (the LLM
  extracts the value from the user's chat message).
- The LLM is not asked to run arbitrary commands — it only chooses a task name
  and fills declared parameters. Task definitions are the trust boundary.

## Discovering tasks automatically

Rather than hand-writing `tasks.json`, you can scan your machine and projects:

```bash
# scan a project for Makefile/justfile/package.json/pyproject.toml/sh/.vscode/argparse
dotnet run --project src/TeleTasks -- discover project --path ~/code/my-script

# emit journalctl tail tasks per systemd unit
dotnet run --project src/TeleTasks -- discover systemd --running       # only running
dotnet run --project src/TeleTasks -- discover systemd --user --all    # user scope, all units

# per-repo git tasks (status, log, diff, branches; +gh runs/PRs if `gh` is installed)
dotnet run --project src/TeleTasks -- discover git --path ~/code/myrepo

# tail-tasks for *.log files in a directory
dotnet run --project src/TeleTasks -- discover logs --path /var/log --since 2d
dotnet run --project src/TeleTasks -- discover logs --path ~/.cache/myapp --recursive
```

By default, output goes to **stdout** so you can review and pipe it. Add `-w` to
merge into `./tasks.json`, or `-o path/to/tasks.json` to write somewhere else.

### Re-running is safe

Each discovered task has a stable `source` field (e.g. `Makefile:build`,
`git:teletasks:status`, `log:/var/log/syslog`). On re-run:

- Existing tasks with the **same source** are **updated in place**. Their
  `name` and `enabled` flag are preserved, so any hand-renaming or disabling
  you've done sticks. Description, command, args, parameters, and output spec
  are refreshed from the detector.
- Tasks **without a source** (hand-written) are never touched.
- New incoming tasks whose name collides with an existing hand-written task
  get a `_2`, `_3`, ... suffix.

That means you can run `discover project -w` after every change to your
Makefile or scripts and the catalog stays clean — no duplicate `make_build_2`
entries, no clobbering of the description you tweaked, no surprises for tasks
you maintain by hand.

The CLI prints a per-run summary:

```
# wrote to /home/me/code/myrepo/tasks.json: 0 added, 14 updated, 0 renamed, 0 removed
```

### Cleaning up stale entries

If you rename or remove a Makefile target / justfile recipe / etc., the old
discovered task hangs around because nothing claims its source. To wipe stale
entries first:

```bash
dotnet run --project src/TeleTasks -- discover project --force-replace
```

This removes every existing task whose source category (the part of `source`
before the last colon) matches one of the incoming sources, then merges
fresh. So `discover project --force-replace` clears all `Makefile:*`,
`justfile:*`, `package.json:*`, etc. entries before adding new ones.
Hand-written tasks (no source) are still left alone.

Discovery is **deterministic by default** — no LLM call is made. Add `--llm` to
have Ollama (using the same `Ollama:Model` from appsettings) rewrite both the
task `description` and each parameter's `description` in one schema-constrained
call per task. Structural fields (name, command, args, parameter names/types/
defaults) are always produced by the parsers, never the model, so a 1B model is
plenty for the polish pass.

### What gets detected from a project

| Source                              | Becomes                                     | Parameters lifted |
| ----------------------------------- | ------------------------------------------- | ----------------- |
| `Makefile` PHONY/regular targets    | `make_<target>`                             | —                 |
| `justfile` recipes                  | `just_<recipe>`                             | recipe args + defaults |
| `package.json` `scripts`            | `npm_<script>`                              | —                 |
| `pyproject.toml` `[project.scripts]` and `[tool.poetry.scripts]` | `py_<entry>` | —                 |
| `.vscode/tasks.json`                | `vsc_<label>`                               | —                 |
| `*.sh` (top-level)                  | `sh_<name>`                                 | `${1:-default}` and `$N` positional args; `getopts` flags listed in description |
| `*.py` (top-level, uses `argparse`) | `py_<name>`                                 | positional args + `--name VALUE` options become typed parameters with defaults, `help` text, and `choices` (mapped to `enum`). `store_true`/`store_false` flags are listed in description (no clean way to template a conditional flag). Requires `python3` on PATH; the helper uses `ast` only and does not import your code. |

Comments above a `Makefile` target / `justfile` recipe / `# Description:` line
in a shell script are picked up as the task `description`.

### What gets detected from systemd

- A `journal_system` (or `journal_user`) task that dumps the journal to a file
  with a `lines` parameter
- One `journal_<unit>` text task per discovered service unit, using the unit's
  `Description=` as the task description

### What gets detected from a git repo

For the repo at `--path` (defaults to cwd):

- `git_<repo>_status`   — `git status --short --branch`
- `git_<repo>_log`      — `git log --oneline --decorate -n {count}` (default 10)
- `git_<repo>_diff`     — uncommitted diff written to `/tmp/teletasks-<repo>-diff.patch` and sent as a file
- `git_<repo>_branches` — `git branch -vv --sort=-committerdate`
- `gh_<repo>_runs` and `gh_<repo>_prs` — only emitted if the `gh` CLI is on PATH

### What gets detected from a logs directory

`discover logs --path DIR [--since 7d] [--max 100] [--pattern *.log] [--recursive]`

Walks `DIR` (top-level by default; `--recursive` opts into subdirs) and emits
one `log_<basename>` `LogTail` task per file that:

- matches the glob pattern (default `*.log`)
- was modified within `--since` days (default 7)
- is non-empty and not larger than `--max` MB (default 100)
- is readable by the current user (silently skipped if perms fail)

Each emitted task has a `lines` parameter (default 100) so the bot can answer
"show me the last 50 lines of nginx" naturally.

### Workflow

```bash
# 1. dry-run, look at the JSON
dotnet /path/to/TeleTasks.dll discover project > /tmp/draft.json
$EDITOR /tmp/draft.json                              # trim or rename

# 2. either paste into your real tasks.json or write directly
dotnet /path/to/TeleTasks.dll discover project -o /etc/teletasks/tasks.json
```

Then in Telegram, `/reload` picks up the new tasks without restarting.

## Don't be silent

Three things make sure problems don't get swallowed:

1. **Startup health check** — the bot pings Ollama as soon as it's connected
   to Telegram. If Ollama is unreachable, or the configured model isn't
   pulled, the bot logs a warning *and* sends a Telegram message to the first
   allow-listed user with the exact `ollama pull <model>` command needed.
   Disable with `Telegram:StartupNotificationsEnabled = false`.
2. **Friendly runtime errors** — when a chat request fails because Ollama
   doesn't have the model pulled (HTTP 404 / "not found") or isn't running
   (connection refused), the bot replies with an actionable message rather
   than the raw HTTP body.
3. **Virtual router targets** — meta questions like "what tasks are
   available?", "what can you do?", "help" route to the `/tasks` and `/help`
   responses instead of being forced into a real task. This matters most
   with very small models and a small task catalog: the matcher would
   otherwise pick the only available task rather than admitting it doesn't
   know.

The matcher's response schema includes two reserved values, `_show_tasks` and
`_show_help`, which the bot intercepts. Real tasks are not allowed to start
with `_` (the registry rejects them at load).

## Long-running tasks

Set `"longRunning": true` on a task to fire-and-forget: the executor spawns the
process detached (via `setsid` so it survives bot restarts), redirects
stdout+stderr to a log file in `~/.config/teletasks/run-logs/`, and immediately
returns with a job ID. The configured `commandTimeoutSeconds` is ignored — you
own the lifecycle.

```jsonc
{
  "name": "render_loop",
  "description": "Run the SDXL render loop until I /stop it.",
  "longRunning": true,
  "command": "/usr/bin/env",
  "args": ["python3", "render.py", "--prompt", "{prompt}"],
  "parameters": [{ "name": "prompt", "type": "string", "required": true }],
  "output": {
    "type": "Images",
    "directory": "/home/me/Pictures/renders",
    "sortBy": "newest",
    "count": 4
  }
}
```

When you message the bot to run this, the reply is immediate:

```
Started job 7 (render_loop, pid 12345).
Log: /home/me/.config/teletasks/run-logs/render_loop-7-...log
Use /job 7 to check progress, /stop 7 to kill.
```

Three commands manage running and recently-finished jobs:

- **`/jobs`** — active jobs first, then the last 10 finished
- **`/job <N>`** — for job N: status, the last 30 lines of its log, AND the
  task's *original* output spec re-evaluated (so an `Images`-output task pulls
  the latest renders right into the chat, a `LogTail` task tails the live log,
  etc.)
- **`/stop <N>`** — SIGKILL the job's process tree

Three matcher virtual routes also pick this up from natural language:

- "what's running?" / "any jobs?" → `/jobs`
- "how's the render going?" / "is it done yet?" → `/job <latest>`

State persists to `~/.config/teletasks/jobs.json`. If the bot restarts mid-job,
running PIDs are reconciled at startup so jobs are still visible. Real exit
codes are recovered when a job finishes naturally; killed jobs have `null`
exit (we killed the wrapper before the exit code could be recorded).

## Built-in commands

- `/help`, `/start` – usage
- `/tasks` – list configured tasks (and disabled ones, separately)
- `/reload` – reload `tasks.json` without restarting
- `/dry <text>` – resolve a task and show what *would* run, without running it.
  Useful to verify the LLM picked the right one and parameters look right.
- `/jobs` – list active and recent long-running jobs
- `/job <N>` – status, log tail, and current output for job N
- `/stop <N>` – kill a running job
- `/whoami` – show your user / chat IDs (handy for the allow-list)

## Disabling tasks

Set `"enabled": false` on a task in `tasks.json` to hide it from the matcher
without deleting it. `/tasks` shows disabled ones in a separate section. If
the field is omitted, the task is enabled. Useful for keeping discovered tasks
around for review without exposing them to the bot.

## Project layout

```
src/TeleTasks/
  Program.cs                     # host + DI wiring + CLI dispatch
  Configuration/AppOptions.cs    # Telegram / Ollama / TaskCatalog options
  Models/                        # task & result types
  Services/
    TaskRegistry.cs              # loads tasks.json
    OllamaClient.cs              # /api/chat with format=json or JSON Schema
    TaskMatcher.cs               # NL → (task, parameters), schema-constrained
    TaskExecutor.cs              # process invocation + parameter substitution
    OutputCollector.cs           # text / file / image / images / log-tail
    TelegramBotService.cs        # background polling + dispatch
    TaskCatalogWriter.cs         # load / merge / write tasks.json
    ParameterTemplate.cs         # {name} substitution
  Cli/
    DiscoverCommand.cs           # `teletasks discover ...`
  Discovery/
    TaskCandidate.cs
    ProjectDiscoverer.cs         # orchestrates per-project detectors
    SystemdDiscoverer.cs         # systemctl + journalctl
    GitDiscoverer.cs             # per-repo status/log/diff/branches (+gh)
    LogsDiscoverer.cs            # *.log files in a directory
    Detectors/
      MakefileDetector.cs
      JustfileDetector.cs
      PackageJsonDetector.cs
      PyprojectDetector.cs
      ShellScriptDetector.cs
      VsCodeTasksDetector.cs
      ArgparsePythonDetector.cs
  appsettings.example.json
  tasks.example.json
```
