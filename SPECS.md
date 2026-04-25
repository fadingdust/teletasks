# TeleTasks — feature ledger

Running record of what this project does, what's been shipped, what's
still on a feature branch, and what's still on the backlog. Updates
stay in sync as branches merge.

## Vision

Bridge a Telegram chat to a Linux PC, mediated by a small local Ollama
LLM that understands natural language and routes it to a JSON-defined
task catalog. The catalog can be hand-written or auto-discovered from a
project / system. Outputs (text, files, images, log tails) come back to
chat. Long-running jobs run detached and surface their state on demand.

Everything runs on the user's machine — no cloud round-trips. Small
models (1B parameters or less) are fine because the matcher uses
JSON-Schema-constrained output.

---

## Shipped (in `main`)

### Bot core

- **Telegram polling** via `Telegram.Bot` 22.x, event-style API.
- **Allow-list authorization** by user ID and/or chat ID.
- **JSON-Schema-constrained Ollama matcher** — small models stay
  reliable because the response shape is locked to the task catalog's
  enum.
- **Virtual matcher routes**: `_show_tasks`, `_show_help` so meta
  questions don't get mis-routed to a real task.
- **HTML rendering** with `<pre>` for command stdout (proper monospace,
  safe escaping).

### Task catalog

- `tasks.json` with `name`, `description`, `command`, `args`,
  `parameters`, `output`, optional `enabled`, `timeoutSeconds`, `env`,
  `workingDirectory`, `source`.
- Output types: `Text`, `File`, `Image`, `Images`, `LogTail`.
- Parameter substitution `{name}` in args, paths, captions, env values.
- `enabled: false` to hide a task without deleting it.
- `source` field for re-run-safe merge: discover updates entries in
  place rather than appending duplicates.
- `--force-replace` to wipe stale source entries on re-run.

### Discovery

- `discover project` — Makefile, justfile, package.json, pyproject.toml,
  `.vscode/tasks.json`, `*.sh` (positional + `getopts`),
  `*.py` (argparse via Python AST helper).
- `discover systemd` — per-unit journalctl tail tasks.
- `discover git` — per-repo status / log / diff / branches; gh runs/PRs
  if `gh` is installed.
- `discover logs` — `*.log` files filtered by mtime, size, glob.
- `--inspect` (default ON) — appends current-state notes to task
  descriptions (file / dir size, latest mtime, file counts by extension).
- `--llm` — Ollama-polished descriptions, schema-constrained, fed the
  underlying script body so it can ground descriptions in actual code.
- `--write`, `-o`, `--force-replace` — catalog merge knobs.
- Source-keyed merge (idempotent re-runs).

### Configuration

- **User config dir**: `$TELETASKS_CONFIG_DIR` →
  `$XDG_CONFIG_HOME/teletasks` →
  `Environment.GetFolderPath(ApplicationData)` → `$HOME/.config/teletasks`
  → ... fallback chain. Wizard, discover, and bot all agree.
- **First-run seeding**: bot copies the bundled `tasks.json` to the
  user config dir on first run.
- **Setup wizard**: validates token via Telegram `getMe`, captures
  user ID via `getUpdates` (no need to look it up), tests Ollama
  connectivity, saves `appsettings.Local.json`.
- **Where command**: `dotnet TeleTasks.dll where` prints every layer of
  resolution.
- **Startup health check** + Telegram DM if Ollama is broken or the
  configured model isn't pulled.
- **Startup config-source log** — bot prints every JSON file it loaded
  with `(loaded)` / `(missing, optional)` so config diagnosis is one
  log line away.
- **Forward-compatible runtime**: `RollForward=Major` lets a net8.0
  build run on net10+ runtimes.

### Built-in commands

- `/help`, `/start`, `/tasks`, `/reload`, `/whoami`
- `/dry <text>` — resolve a task and show what would run, without running

---

## In flight (open feature branches)

### `claude/output-runtime`

Project-type-agnostic runtime primitives carved out of the original
output-spec-promotion branch. Lands first; long-running and
output-spec-promotion both build on it.

| Feature | Notes |
|---|---|
| `PathGlob` | `*` and `?` expansion in `Images.directory`, `Image.path`, `File.path`, `LogTail.path`. Picks freshest match by mtime. |
| Multi-pass parameter substitution | `output_dir.default = "./results/{lora}/output"` resolves both layers when something references `{output_dir}`. Caps at 5 passes for cycle protection. |
| Sidecar metadata + auto-diff captions | `captionFrom: { sidecar: ".json", mode: "auto-diff" }`. Top-level scalar fields constant across the batch go to a header message; per-image captions show only what varies. |
| `siblings: [".json"]` | Send paired files as Telegram documents alongside each image. |
| `/results <task>` + `_show_results` | Read a task's current output state without running its command. NL routing via the virtual matcher route. |
| `TaskExecutor.EvaluateOutputAsync` | Shared "evaluate output spec without running command" helper — used by `/results` here and by `/job N` when long-running merges. |

### `claude/output-spec-promotion` (rebases on output-runtime)

Discovery-time heuristics that produce useful output specs automatically.
After `output-runtime` lands, this branch shrinks to just the discovery
work — no runtime changes.

| Feature | Notes |
|---|---|
| `OutputSpecPromoter` | Auto-rewrite `Text` output to `Images` / `LogTail` / `File` based on parameter names (`output_dir`, `log_file`, etc.). Smart-glob detection for nested image dirs (`results/*/output`). |
| `ShellWrapperResolver` | sh that wraps `python <file>.py` inherits the python's promoted output spec. Copies parameters the spec templates against (`{output_dir}`, `{lora}`) so substitution works on the shell side. |
| Verbose discover logs | Each pipeline pass logs why each candidate was promoted / skipped / matched. |

### `claude/long-running-jobs`

| Feature | Notes |
|---|---|
| `longRunning: true` task flag | Executor spawns detached via `setsid`, redirects stdout+stderr to a log file, returns a job ID immediately. |
| `JobTracker` | In-memory + persisted to `~/.config/teletasks/jobs.json`. Survives bot restart; reconciles running PIDs at startup; recovers exit codes from sidecar files for jobs that finish naturally. |
| `/jobs`, `/job <N>`, `/stop <N>` | List active + recent finished, status + log tail + re-evaluated output spec, SIGKILL the process tree. |
| Virtual routes `_show_jobs`, `_check_latest_job` | NL routing for "what's running?" / "how's the render going?" |

### `claude/conversational-params`

| Feature | Notes |
|---|---|
| Multi-turn parameter collection | When the matcher couldn't extract required params, the bot asks for each in turn, validates by type, runs when complete. |
| `ConversationStateTracker` | In-memory per-chat state. Self-expires after 15 minutes idle. |
| `ParameterValueParser` | Type coercion for integer/number/boolean/enum with friendly re-prompts. |
| `/cancel` | Abort the pending collection. Slash commands during a pending state also clear it. |

---

## Backlog / proposed

| Feature | Notes / status |
|---|---|
| `discover docker` | Containers / compose projects → logs/exec/restart tasks. Discussed; not yet built. |
| `discover media` | Image/screenshot dirs (`~/Pictures/Screenshots`, etc.), webcams under `/dev/video*`. Discussed; not yet built. |
| Recursive git scan | `discover git --scan ~/code --depth N` to find repos under a path and emit per-repo tasks for each. |
| Localhost web UI | Browser-based catalog editor / preview, bound to localhost only. |
| Push notifications | When a long-running job finishes, optionally DM a "done" message. |
| FileSystemWatcher | "Send each new image as it appears in this dir." Companion to long-running renders. |
| Per-user state in conversational params | Currently keyed by `chatId`. Per-user keying is a one-line change. |
| SQLite index of sidecars | Query renders by structured field ("show me where prompt contains forest"). |
| Boolean-flag template syntax | Conditional flag emission like `{verbose?--verbose}` for argparse `store_true`/`store_false`. |
| Test suite | xUnit project under `tests/` covering pure-logic helpers (`SidecarMetadata`, `PathGlob`, `PathInspector`, `OutputSpecPromoter.Classify`, `TaskCatalogWriter.Merge`) and detector parsing. ~40-60 tests. |

---

## Design decisions worth remembering

### Schema-constrained matcher over JSON mode

Ollama's `format: <JsonSchema>` mode lets us pin `task` to an enum of the
catalog's names + virtual routes. Small models (qwen2.5:0.5b,
llama3.2:1b) are reliable because they can't hallucinate task names or
malformed JSON. Without this, we'd need a 7B+ model just to keep the
output structure stable.

### Virtual matcher routes for "intent escape hatches"

When the catalog has only one or two real tasks, a constrained model
will pick whichever fits closest, even for meta questions. Adding
explicit virtual routes (`_show_tasks`, `_show_help`, `_show_jobs`,
`_show_results`) gives the model somewhere to route those
queries gracefully.

### Single user config dir for everything

Wizard, discover, and bot all use the same path resolution
(`UserConfigDirectory.Resolve`) so they always agree. Avoids the trap
where the wizard saves to `bin/Debug/...` but `dotnet run -c Release`
looks in `bin/Release/...` and silently drops the config.

### Source-keyed catalog merge

Each discovered task carries a stable `source` ("Makefile:build",
"py:argparse:render.py"). On re-run, merge updates entries by source
rather than by name, which means hand-renaming a task or editing
`enabled` doesn't get clobbered. Hand-written tasks (no source) are
never touched.

### Output-spec promoter / sh-wrapper resolver as separate passes

Each discover pass does one thing and logs one line per candidate. So
"why isn't my sh wrapper picking up the python's output" can be
answered by reading the `# wrapper:` line — no debugger needed.

### `setsid` for detached jobs

Long-running jobs need to survive bot restarts. `setsid` creates a new
session, so the job's PPID becomes 1 (init) — the bot's process group
exit doesn't propagate. Plus the wrapper writes its own exit code to a
sidecar file so we can recover it after the fact even though the bot
isn't the parent anymore.

### Telegram HTML over Markdown

Markdown's escaping rules are fragile and arbitrary stdout content
breaks the Telegram parser regularly. HTML mode with `<pre>` is more
forgiving — only `&`, `<`, `>` need escaping, and `<pre>` blocks render
in monospace so column-aligned tools (`free -h`, `df -h`) keep their
shape.

### `RollForward=Major` instead of multi-targeting

A net8.0 build runs on net10 via runtime roll-forward. Cheaper than
multi-targeting and handles the common case (deploy on a machine that
only has the latest LTS).
