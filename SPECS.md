# TeleTasks — feature ledger

Running record of what this project does, what's shipped, what's still
on a feature branch, and what's still on the backlog. Updates stay in
sync as branches merge.

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
- **Virtual matcher routes**: `_show_tasks`, `_show_help`,
  `_show_results`, `_show_jobs`, `_check_latest_job` so meta questions
  don't get mis-routed to a real task.
- **Exact-task-name fast path** — typing the literal task name
  (`sh_run_local`) skips the LLM round-trip and goes straight to
  conversational parameter collection. Saves 20-30s on tiny models.
- **Hallucination guards** — for required string parameters, reject
  values that don't appear in the user's message after stripping the
  task name. Catches small-model "invented" parameter values that
  satisfy the schema but the user never said.
- **HTML rendering** with `<pre>` for command stdout (proper monospace,
  safe escaping).

### Task catalog

- `tasks.json` with `name`, `description`, `command`, `args`,
  `parameters`, `output`, optional `enabled`, `longRunning`,
  `timeoutSeconds`, `env`, `workingDirectory`, `source`.
- Output types: `Text`, `File`, `Image`, `Images`, `LogTail`.
- Parameter substitution `{name}` in args, paths, captions, env values.
  Multi-pass (cap 5) so `output_dir.default = "./results/{lora}/output"`
  resolves both layers when something else references `{output_dir}`.
- `enabled: false` to hide a task without deleting it.
- `source` field for re-run-safe merge: discover updates entries in
  place rather than appending duplicates.
- `--force-replace` to wipe stale source entries on re-run.

### Output runtime

- **`PathGlob`** — `*` and `?` expansion in `Images.directory`,
  `Image.path`, `File.path`, `LogTail.path`. Picks freshest match by
  mtime.
- **Sidecar metadata + auto-diff captions** —
  `captionFrom: { sidecar: ".json", mode: "auto-diff" }`. Top-level
  scalar fields constant across the batch land in a header message;
  per-image captions show only what varies. Fuzzy `SiblingPath` strips
  trailing `[_\-.]\d+` suffixes so `t2i-…_00.png` pairs with
  `t2i-….json`.
- **`siblings: [".json"]`** — paired files sent as Telegram documents
  alongside each image.
- **`/results <task>` + `_show_results`** — read a task's current
  output state without running its command. Backed by
  `TaskExecutor.EvaluateOutputAsync`, which `/job N` and the progressive
  push notifier also call.

### Long-running jobs

- **`longRunning: true` task flag** — Executor spawns detached via
  `setsid`, redirects stdout+stderr to a log file, returns a job ID
  immediately.
- **`JobTracker`** — in-memory + persisted to
  `~/.config/teletasks/jobs.json`. Survives bot restart; reconciles
  running PIDs at startup via `/proc/<pid>/stat`; recovers exit codes
  from sidecar files for jobs that finished while the bot was down.
- **`/jobs`, `/job <N>`, `/stop <N>`** — list active + recent finished;
  status + log tail + re-evaluated output spec; verified kill (escalates
  to `kill -KILL -<pid>` against the session group when the initial
  `Process.Kill(entireProcessTree)` doesn't take), `Killed` flag tracked
  separately from natural exit.
- **Push notifier loop** — 30s poll
  (`Telegram:JobPollSeconds`, 0 disables). Per active job: pushes new
  artifacts (mtime ≥ start, finished-job upper-bound at finish + 10s
  grace) and a one-line completion summary when the job ends. Each
  pushed artifact's caption is tagged `Job N • <task>` for context.
  Restart-safe via persisted `SeenArtifactPaths` and `CompletionNotified`
  on `JobRecord`.
- **Empty-log diagnostic** on `/job N` — points at Python stdout
  buffering with the `PYTHONUNBUFFERED` / `python -u` / `stdbuf -oL`
  recommendation when the log file exists but is whitespace.

### Conversational parameter collection

- **Multi-turn prompts** — when the matcher resolves a task but a
  required parameter wasn't extracted (or was hallucinated), the bot
  asks for each missing value in turn, validates by type, runs when
  complete.
- **`ConversationStateTracker`** — in-memory per-chat state, expires
  after 15 minutes idle.
- **`ParameterValueParser`** — type coercion for
  integer / number / boolean / enum with re-prompts on bad input.
- **`/cancel`** — abort a pending collection. Slash commands during a
  pending state also clear it automatically.

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
- **`OutputSpecPromoter`** — auto-rewrites `Text` output to
  `Images` / `LogTail` / `File` based on parameter names (`output_dir`,
  `log_file`, ...). Smart-glob detection for nested image dirs
  (`results/*/output`). Auto-fills `captionFrom` when paired
  image+sidecar files are detected.
- **`ShellWrapperResolver`** — sh that wraps `python <file>.py`
  inherits the python's promoted output spec; copies parameters the
  spec templates against (`{output_dir}`, `{lora}`). Falls back to a
  lazy single-file scan when the python lives in a subdir below the
  discovery floor (`python scripts/foo.py`).
- **2 MB sh source budget** — `ShellScriptDetector` keeps the full
  script body so regex-based wrapper / sidecar / param scans see
  everything. The LLM polish step truncates to a 2 KB preview at the
  call site.
- **`-i` / `--interactive`** — per-candidate prompt loop after polish:
  include?, long-running? (heuristic suggestion based on imports /
  heavy params / venv activation), enabled? Pairs with `-w`.
- Verbose discover logs — each pipeline pass logs why each candidate
  was promoted / skipped / matched.

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
- `/results <task>` — show the latest output of a task without re-running
- `/jobs`, `/job <N>`, `/stop <N>` — long-running job management
- `/cancel` — abort a pending parameter-collection prompt

---

## Backlog / proposed

| Feature | Notes / status |
|---|---|
| `discover docker` | Containers / compose projects → logs/exec/restart tasks. |
| `discover media` | Image/screenshot dirs (`~/Pictures/Screenshots`, etc.), webcams under `/dev/video*`. |
| Recursive git scan | `discover git --scan ~/code --depth N` to find repos under a path and emit per-repo tasks for each. |
| Localhost web UI | Browser-based catalog editor / preview, bound to localhost only. |
| Per-job `/notify off N` | Toggle the 30s push poller off for a single job if it gets noisy. |
| Per-user state in conversational params | Currently keyed by `chatId`; per-user keying is a one-line change. |
| SQLite index of sidecars | Query renders by structured field ("show me where prompt contains forest"). |
| Boolean-flag template syntax | Conditional flag emission like `{verbose?--verbose}` for argparse `store_true`/`store_false`. |
| PID-cookie hardening for `JobTracker` | Compare `/proc/<pid>` start-time against `job.StartedAtUtc` so a recycled PID doesn't look "alive". |
| Test suite | xUnit project under `tests/` covering pure-logic helpers (`PathGlob`, `ParameterTemplate`, `SidecarMetadata`, `ParameterValueParser`, `OutputSpecPromoter.Classify`, `TaskCatalogWriter.Merge`, `HasUsableValue`) and detector parsing. ~40-60 tests. |

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
`_show_results`, `_check_latest_job`) gives the model somewhere to
route those queries gracefully.

### Hallucination guard belt-and-suspenders

Tiny models still occasionally invent string values for required
parameters even with a "NEVER invent" rule in the system prompt. The
bot-side guard tokenizes the value (≥3 char tokens, split on common
separators) and checks each token against the original user message
*after* stripping the matched task name from the search space — so
typing just the task name no longer accidentally validates an invented
value whose tokens overlap with the task name itself. Numbers / bools /
enums skip the check because their schema-pinned valid space is small
enough that hallucination is structurally constrained.

### Exact-task-name fast path

When `routedText.Trim()` is exactly a task name, we skip the LLM call
and synthesize an empty-parameters match. Saves the entire matcher
round-trip (~20-30s on qwen2.5:0.5b), eliminates hallucination risk for
that case, and lets the conversational loop walk the user through every
required parameter.

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
isn't the parent anymore. `/stop` escalates to `kill -KILL -<pid>` (the
session group) when `Process.Kill(entireProcessTree)` misses
grandchildren that re-parented to init.

### Polling over FileSystemWatcher for progressive pushes

The notifier loop polls every 30s and diffs the artifact set against a
persisted `SeenArtifactPaths`. Polling avoids inotify event-buffer
overflows, partial-file edge cases (PNGs caught mid-write), and
filesystem-specific behavior on NFS / FUSE / bind mounts. mtime
stability for one tick is a fine "settled file" heuristic. Cost is one
directory glob per active job per 30s — negligible.

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
