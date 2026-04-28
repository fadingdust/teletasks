# TeleTasks — ideas & feature backlog

Loose brainstorm file. Anything that's been discussed but not yet
built, plus rougher "could be useful" notes. Items here aren't
committed work — once an idea graduates to "yes, we're building this",
it moves to [SPECS.md](SPECS.md)'s Backlog table or directly into a
feature branch.

Format: grouped by area. Each entry has a short rationale + open
questions / risks. Add freely; promote to SPECS when scope is clear.

---

## Bot / UX

### Inline keyboard buttons for job actions
"Started job 5 …" message attaches `[Status]` `[Stop]` buttons that
fire callback queries; bot handler routes them through the same
`SendJobStatusAsync(5)` / `Stop(5)` paths the slash commands already
use. Avoids relying on the undocumented `tg://msg?text=…` scheme. Same
pattern for `/jobs` list rows and the completion summary.
- Lift: ~50-80 lines (OnCallbackQuery handler + button construction in
  3-4 send sites + callback_data parser).
- Open: should `/jobs` rows have inline buttons or stay as text? Inline
  keyboards are paginated awkwardly when many jobs.

### Per-job push toggle (`/notify off N` / `/notify on N`)
Once the 30s push poller is real, some jobs will be noisy (renders
producing 50+ images). Per-job opt-out keeps `/job N` polling alive
but skips progressive pushes. Persist on `JobRecord`.
- Lift: 1 boolean field + slash command + gate in `PushNewArtifactsAsync`.

### Per-user state in conversational params
`ConversationStateTracker` is currently keyed by `chatId`. In a group
chat with multiple authorized users, a second user's reply could
clobber the first's pending prompt. Switch to `(chatId, userId)`.
- Lift: one-line dictionary key change, plus the OnMessage handler
  passes user id through.

### `/history N` per task
"Last N runs of `<task>` with their parameters and outcomes." Useful
when iterating on a render task — copy a previous param set with one
edit. Backed by extending `JobTracker` (or a sibling history file)
with non-long-running runs too.

### Param-value autocomplete from prior runs
When the conversational loop asks for `prompt`, surface 3-5 recent
values as suggestions. Inline keyboard buttons or text quick-reply.
Pairs naturally with `/history`.

### Reply-to-job to add a comment / re-run
Telegram supports replying to a specific message. If the user replies
to "Started job 5 …" with text, treat it as `/job 5 <text>` style —
either a tag (saved on `JobRecord`) or a re-run with new params.

---

## Discovery

### `discover docker`
Containers / `docker-compose.yml` projects → tasks for
`logs <service>`, `restart <service>`, `exec <service> <command>`. The
trick is identifying the right tasks without flooding the catalog with
every container.
- Open: should bound containers (named ones with stable names) be
  candidates and ephemeral ones skipped?

### `discover media`
Image / screenshot dirs (`~/Pictures/Screenshots`, `~/Pictures/`),
webcams under `/dev/video*`. Emits `Images` tasks with sensible
glob patterns and `LogTail`-style "latest" tasks.

### Recursive `discover git --scan ~/code --depth N`
One-shot onboarding for everyone-has-a-`~/code-or-Projects` setup.
Walks directories, calls existing `discover git` per repo.
- Open: how to disambiguate task names across repos
  (`status_<reponame>` works but ugly).

### Long-running heuristic improvements
Current heuristic catches PyTorch-family imports + heavy parameter
names + venv-activating sh wrappers. Gaps:
- Docker / docker-compose invocations (often slow first time).
- CUDA library loads via `os.environ`.
- Make targets that run a build (`make build`).
Probably needs a per-source heuristic table rather than one big regex.

### Discover from Tasker-style automations
If user has Tasker / Termux configs, surface them as candidates. Niche
but interesting for Android-PC bridging.

### External venv resolution (pyenv-virtualenv, `~/.virtualenvs`, `WORKON_HOME`)
The current Python venv probe in `ArgparsePythonDetector.ResolveProjectPython`
only looks for in-tree layouts (`.venv/bin/python`, `venv/bin/python`,
`env/bin/python`). Real-world setups also park venvs outside the project
tree:
- `pyenv-virtualenv` writes to `~/.pyenv/versions/<env>/bin/python`, with
  the project linked via a `.python-version` file at the project root.
- `virtualenvwrapper` / `pipenv` / classic mkvirtualenv use `$WORKON_HOME`
  (default `~/.virtualenvs`).
- Some projects pin via a `Pipfile` / `poetry.lock` and the venv lives
  under `~/.cache/pypoetry/virtualenvs/<hash>/`.

Plausible follow-up: read `.python-version` for pyenv, check
`$WORKON_HOME` / `~/.virtualenvs/<basename(workingDirectory)>` for
virtualenvwrapper, fall through to current in-tree probe if nothing
matches.
- Open: should it `pyenv exec python -V` to confirm the resolved python
  actually runs, or trust the path?

### Conda environment detection
Conda layouts differ from venv:
- Per-env path: `<conda-root>/envs/<name>/bin/python` rather than
  `<env>/bin/python`.
- Activation modifies `CONDA_PREFIX`, `CONDA_DEFAULT_ENV`, prepends
  multiple directories to `PATH` (not just `bin`).
- Project-pinned env via an `environment.yml` / `conda-meta/` directory
  marker rather than a per-project venv folder.

Probably worth a separate `CondaEnvironmentResolver` that walks
`environment.yml` for the env name then resolves
`$CONDA_HOME/envs/<name>/bin/python` (or `~/miniconda3/envs/...`,
`~/anaconda3/envs/...`).
- Open: do we need to set the full activation env vars (`CONDA_PREFIX`
  etc.) or is invoking the env's python enough? Some packages (PyTorch,
  CUDA libraries) read `CONDA_PREFIX` to locate their own resources.

---

## Job system

### `/restart N` — re-run a previous job
Take an existing `JobRecord`'s task + parameters and start a fresh job
with the same inputs. Useful for "render failed at step 28, run it
again" or quick iteration when the answer is the same parameters.
Should the new job be a sibling (new id) or a continuation (same id,
appended log)? Probably new id with a `RestartedFromJobId` field for
traceability.
- Lift: ~30 lines (slash command + `JobTracker.Restart(int oldId)`
  that calls Start with the persisted task+params).
- Open: should `/restart N` work for finished failures only, or also
  restart a still-running job (i.e. `/stop N && /restart N`)?

### Old jobs clearing / reset
`jobs.json` accumulates indefinitely. After dozens of runs `/jobs` is
cluttered with finished entries we don't care about.

**Retention policy** (applied at startup, running and un-notified jobs
always exempt):

1. For each task name, keep the `JobRetentionMinPerTask` most recent
   finished jobs unconditionally (floor, regardless of age). Default 5.
   Preserves enough history for `/history N` comparisons and
   `/restart N` for every task.
2. Prune any finished job beyond that floor if it is also older than
   `JobRetentionDays` days. Default 14.
3. After per-task floors are satisfied, enforce `JobRetentionMaxTotal`
   as a hard cap on total finished jobs across all tasks. Prune
   oldest-first until under the cap. Default 200. Prevents runaway
   growth when many tasks each hit the K=5 floor simultaneously.
4. The protected record per task is the most recent *run* regardless of
   exit code - parameters are what matter for restart, and `/results`
   already handles the no-output case gracefully.
5. Tasks removed from `tasks.json` still receive the per-task floor
   protection on their historical records.

**Config** (under `Chat:`):
```json
"Chat": {
  "JobRetentionDays": 14,
  "JobRetentionMinPerTask": 5,
  "JobRetentionMaxTotal": 200,
  "JobRetentionKeepFailed": true
}
```
`JobRetentionKeepFailed`: when false, failed/killed jobs don't count
toward the per-task floor and are pruned more aggressively. Default true.

**Log files** (`~/.config/teletasks/run-logs/`): pruned in sympathy -
orphan logs for pruned job records are deleted. Logs can be large (MB
range) so a separate `JobLogRetentionMinPerTask` (default lower than
`JobRetentionMinPerTask`) or `JobLogRetentionDays` lets you keep
compact job records longer than their verbose logs.

**`/clear-jobs`** slash command for on-demand purge. Two modes:
- `/clear-jobs` - applies the same retention policy as the startup
  pruner (respects `JobRetentionMinPerTask` floor per task).
- `/clear-jobs all` - full wipe of all finished jobs regardless of
  floor. Running and un-notified jobs always survive either way.
Both show a confirmation line before acting ("Cleared 47 finished jobs,
kept 12 per retention floor." / "Cleared all 59 finished jobs.").

### PID-cookie hardening for `JobTracker`
Compare `/proc/<pid>` start-time against `job.StartedAtUtc` in
`Reconcile` so a recycled PID after long bot downtime doesn't look
"alive". `clock_gettime(CLOCK_MONOTONIC)` boot-time + the start-time
field of `/proc/<pid>/stat` is the canonical Linux idiom.

### Cron-style scheduled jobs
"Render this every morning at 9am." Probably handled by systemd timers
(out-of-band of the bot), but a built-in `/schedule <task> <cron>`
that wires up `~/.config/systemd/user/teletasks-<n>.timer` would be
slick.
- Open: who owns the running job — the scheduled timer spawning a
  detached process, or the bot picking it up via JobTracker?

### Per-task max-concurrent-jobs
"Don't start a render if one's already running." Prevents accidental
double-kicks of expensive workloads.

### Live progress for jobs that report it
If the script writes a parseable progress line ("step 12/30",
`tqdm`-style `12%|`), parse and surface as edited bot messages instead
of the per-poll log tail dump.

---

## Output / rendering

### Inline image grids
Currently each Image artifact is its own message. Telegram's media
groups allow up to 10 images in one batch — quieter on the chat.
- Caveat: media-group captions only attach to the first image.

### SQLite index of sidecars
`captionFrom: { sidecar: ".json" }` already reads each sidecar at
display time. A persistent SQLite index keyed by image path would let
"show me renders where prompt contains forest" be a real chat query.
- Open: what's the indexer trigger? Watch the directories vs. lazy
  index on `/results`?

### Boolean-flag template syntax
`{verbose?--verbose}` to conditionally emit a flag for argparse
`store_true`/`store_false` parameters. Currently we skip these and
show "(boolean flags: --verbose — edit args to enable)" in the
description, which means the user has to hand-edit tasks.json.

### Output type: `Json`
Pretty-print a JSON file with optional jq filter. Useful for "what's
the current config?" tasks.

### Output type: `Process` (live attach)
For tasks where stdout matters in real time, attach to a live
detached process and echo each line to chat as a typing indicator
+ message. Differs from `LogTail`'s post-hoc tailing.

---

## Infrastructure / quality

### Test suite (xUnit)
Pure-logic helpers first: `PathGlob`, `ParameterTemplate.Apply`,
`SidecarMetadata` (auto-diff + fuzzy `SiblingPath`),
`ParameterValueParser`, `OutputSpecPromoter.Classify`,
`TaskCatalogWriter.Merge`, `HasUsableValue` hallucination guard.
~40-60 tests covers the bug-prone surface.

### Integration tests with fakes
Fake `ITelegramBotClient` + fake Ollama server + tmp jobs.json. Lets
us test `OnMessageAsync` flows end-to-end. Significantly more setup
than unit tests; punt until #1 has caught the obvious bugs.

### Localhost web UI
Browser-based catalog editor / preview, bound to localhost. Useful
for editing `tasks.json` with schema validation rather than raw JSON.
Possible companions: live render preview, sidecar inspector.

### Webhook delivery instead of long-poll
Currently the bot uses long-polling. A webhook would cut latency for
inbound messages and reduce idle Ollama calls (the "typing" indicator
fires before the LLM round-trip). Requires a public HTTPS endpoint —
either Cloudflare Tunnel / Tailscale or a published reverse proxy.

### Multi-LLM fallback
Tiny matching model (qwen2.5:0.5b) for routing, switch to
llama3.2:3b for `--llm` polish during discover. Reduces hallucination
risk in polish without paying the bigger-model latency cost in
the chat hot path.

---

## Multi-provider chat support

Today the bot is hard-bound to Telegram via `TelegramBotService`.
A four-phase plan makes adding Discord / Matrix / Slack later a
self-contained job per provider:

### Phase 1: Provider abstraction (no functional change)

New types in `Services/Chat/`:
- `ChatId` — provider-qualified `(Provider, Id)` struct, canonical
  string form `"telegram:42"` / `"discord:1234567890"`. JSON converter
  reads legacy bare-`long` form for backward compat with existing
  `jobs.json`.
- `IncomingMessage` — `(ChatId, UserId, Username, Text)` record.
- `IChatProvider` — `OnMessage` event, `StartAsync` / `StopAsync`,
  `SendTextAsync` (plain), `SendHtmlAsync` (canonical
  Telegram-style HTML — providers translate to native flavour),
  `SendImageAsync`, `SendDocumentAsync`, `SendTypingAsync`,
  `IsAuthorized`.
- `ChatHtml.Escape` — provider-agnostic escape for the canonical
  HTML markup, used by the routing pipeline before
  `SendHtmlAsync`.

Refactor surface inside this phase:
- `JobRecord.ChatId`: `long?` → `ChatId?`. Custom `JsonConverter`
  handles legacy `long` deserialisation.
- `ConversationStateTracker` keyed by `ChatId` instead of `long`.
- Allow-list moves from `TelegramOptions` into per-provider config
  (`Chat:Providers:Telegram:AllowedUserIds` etc.).
- `JobTracker.AssignChat(int id, ChatId chat)`.

### Phase 2: Telegram becomes a provider

- `TelegramChatProvider : IChatProvider` wraps the existing
  `TelegramBotClient`, owns `_botUsername`, strips
  `/cmd@MyBot` mentions before raising `OnMessage`.
- `TelegramBotService` shrinks to a thin host that registers
  the provider and runs the message router + notifier loop.
- The dispatch logic in `OnMessageAsync` calls `provider.SendXAsync`
  instead of `bot.SendX(...)`.
- Eventually rename `TelegramBotService` → `ChatHost` (post-phase-2
  cleanup).

### Phase 3: Discord (~1-2 days)

- `DiscordChatProvider : IChatProvider` using `Discord.Net`.
- `DiscordOptions` with bot token + allow-list (UserIds, GuildIds,
  ChannelIds; defaults to DM-only).
- HTML → Discord-Markdown translator (triple-backtick code blocks,
  `**bold**`, `_italic_`). Escape literal triple-backticks in
  user content via zero-width space insertion.
- Setup wizard branch: `dotnet TeleTasks.dll setup --provider discord`
  prompts for token, validates via `GET /users/@me`, prints the
  invite URL with the right intent flags
  (`MessageContent` is required).

### Phase 4: OAuth-ready (designed in, not built)

- `IOAuthChatProvider : IChatProvider` extends with
  `BeginAuthorizationAsync` / `CompleteAuthorizationAsync` /
  `RefreshTokenAsync` / `TokenIsValid`.
- Tokens persisted in user config dir, encrypted at rest via
  `ProtectedData` or Linux keyring lookup.
- Setup wizard's main loop branches on `IOAuthChatProvider`:
  prints redirect URL, listens on `localhost:port` for the
  code, exchanges, persists.
- A token-refresh task on `ChatHost` polls each
  `IOAuthChatProvider.TokenIsValid` and refreshes proactively.
- This way Slack / Microsoft Teams / Microsoft Graph adoption
  later is a fresh `IOAuthChatProvider` implementation —
  no churn through the rest of the codebase.

### Open questions

- Single-process multi-provider needs a `ChatHost` background
  service that owns all providers and their notifier loop.
  Current design has notification logic inside
  `TelegramBotService`; phase 2 cleanup should extract it
  into a `JobNotifierService` that walks all jobs (each
  `JobRecord.ChatId.Provider` tells it which provider to send
  through).
- HTML → mrkdwn (Slack) / matrix.html / Discord-Markdown
  translators each need test coverage. Round-trip a
  representative set of bot output snippets through each
  to validate.
- Setup wizard's interactive flow is currently
  Telegram-shaped (token + user-id capture via `getUpdates`).
  Each new provider needs its own analogue — Discord has
  no `getUpdates` equivalent, so the "send a message after
  invite" trick uses gateway events instead.

---

## Intents — verbs that apply to any task

The current matcher is task-name-keyed: every variation of "do X"
needs a task definition or a virtual route (`_show_results`,
`_check_latest_job`, etc.). Intents would split the LLM's job into
two: pick a verb, pick a target. The verb set is small and
provider-agnostic; targets are the existing task catalog.

### What this looks like in chat

Without intents (today):
- "render some skulls"        → `render` task, prompt=skulls
- "results for render"        → `_show_results` virtual route
- "is the render done?"       → `_check_latest_job` virtual route
- "stop the render"           → `/stop <N>` slash command
- everything else needs a hand-coded path

With intents:
- "render some skulls"        → `intent=Run`, target=`render`, params={prompt:skulls}
- "redo my last render"       → `intent=Restart`, target=`render`
- "what were the last 5 renders' prompts?" → `intent=History`, target=`render`, n=5
- "schedule render every morning at 9" → `intent=Schedule`, target=`render`, cron="0 9 * * *"
- "stop the long-running one"  → `intent=Stop`, target=latest-active
- "show me only the renders with forest in the prompt" → `intent=Filter`, target=`render`, where={prompt~forest}

### Proposed verb set

| Intent     | Description                                              |
|------------|----------------------------------------------------------|
| `Run`      | Execute a task (the default — current matcher behavior)  |
| `Show`     | View latest output without running (`/results` today)    |
| `Status`   | Job status / progress (`/job <N>`, "how's it going?")    |
| `Stop`     | Kill a running job (`/stop <N>`)                         |
| `Restart`  | Re-run a previous job with the same parameters           |
| `History`  | Last N runs of a task with their parameters / outcomes   |
| `Schedule` | Cron-style recurrence (depends on the scheduled-jobs idea) |
| `Filter`   | "Show me Xs where param Y matches Z" — sidecar query     |
| `Cancel`   | Abort a pending parameter-collection prompt              |
| `Help`     | Meta — what tasks exist, what params they need           |

`Run` is the default, kept compatible with the current matcher so
existing tasks.json files still work without intent annotations.

### Implementation sketch

The matcher's response schema gets a new top-level field:

```json
{
  "intent": "Run" | "Show" | "Status" | "Stop" | "Restart" |
            "History" | "Schedule" | "Filter" | "Cancel" | "Help",
  "task": "<task-name> | _virtual | null",
  "parameters": { ... },
  "reasoning": "..."
}
```

Each intent has a per-intent handler in `TelegramBotService` (or
the future `MessageRouter`). The handler validates that `task` is
appropriate for that intent — `Run` against `_show_results` is
nonsense, `History` against `_check_latest_job` is nonsense — and
either dispatches or asks the user to clarify.

Most virtual routes collapse into the intent system:
- `_show_results` → `intent=Show, task=<name>`.
- `_check_latest_job` → `intent=Status, task=null` (target=latest).
- `_show_jobs` → `intent=Status, task=null, filter=active`.
- `_show_tasks` → `intent=Help, task=null`.
- `_show_help` → `intent=Help, task=null`.

So the matcher's enum shrinks (fewer virtual routes) and the
schema gets a more structured second field that the bot can
dispatch on.

### Test infrastructure

A new `IntentMatcherTests` rig with canned user phrasings:

| Phrasing                                  | Expected intent + target |
|-------------------------------------------|--------------------------|
| "render some forest"                      | `Run` / `render`         |
| "run render with prompt forest"           | `Run` / `render`         |
| "redo my last render"                     | `Restart` / `render`     |
| "stop the render"                         | `Stop` / `render`        |
| "what's the latest render?"               | `Show` / `render`        |
| "is render done?"                         | `Status` / `render`      |
| "show me my last 5 render prompts"        | `History` / `render`     |
| "show only renders where prompt has forest" | `Filter` / `render`    |

Run a small model (qwen2.5:0.5b) against the table at every
PR; expected accuracy bar is some-percentage rather than
exact-match because the model is small. Catches regression
without being flaky.

### Open questions

- **Do we need per-task intent allow-lists?** A task definition
  could declare `supportedIntents: ["Run", "Show", "Restart"]`
  to opt out of e.g. `Schedule`. Default is "all intents are
  fine" — opt-out semantics keep the catalog migration easy.
- **`Filter` is a database query in disguise.** Without the
  SQLite sidecar index (also in IDEAS), `Filter` would have
  to scan files at runtime — slow for big render directories.
  Probably build `Show` / `Status` / `Restart` first; defer
  `Filter` until the sidecar index lands.
- **Multi-step intents** ("schedule a render of skulls every
  morning at 9 and notify me when each one finishes") are
  combinations. Support them later by allowing the matcher
  to emit a sequence of intents, or punt to the explicit
  task-chaining feature (also in IDEAS).
- **Conversational params still apply.** If `intent=Run,
  task=render` extracts no `prompt`, the existing
  conversational loop kicks in and asks for it. The intent
  handler shouldn't bypass that.
- **Backwards compat.** A response without an `intent` field
  defaults to `Run` so we can roll the schema out without
  breaking the bot mid-deploy.

### Surfacing intents in /tasks and /jobs listings

Intents aren't just an LLM-routing concept — they're metadata
that should appear in the lists so the user sees what's possible
per row without having to remember.

**`/tasks` rendering**:

```
Available tasks:
• render          [Run · Show · Restart · History]
• system_status   [Run · History]
• tail_log        [Run · Show]
• send_journal    [Run · Show · Restart · History]
```

Disabled tasks render the same way but greyed conceptually
(e.g. with a `(disabled)` suffix as today).

**`/jobs` rendering** — intents are filtered by job state, so the
list only shows verbs that *currently* apply:

```
Active:
• /job 5 — render running 4m 12s   [Status · Stop · Show]
• /job 7 — train running 28m       [Status · Stop]

Recent:
• /job 4 — render ok after 6m 3s   [Show · Restart · History]
• /job 3 — render killed after 2m  [Restart · History]
```

Pairs naturally with the **inline keyboard buttons** idea earlier
in this file: each intent label becomes a tappable button that
fires the matching handler. So `/jobs` shows running jobs with
`[Status]`/`[Stop]` buttons, finished jobs with
`[Show]`/`[Restart]` buttons. The label text and the callback
data both come from the intent system — single source of truth.

**Where intent lists come from**:

- Default (per task) inferred from task definition shape:
  - `Run` always.
  - `Show` when `output.Type` is anything other than `Text`.
  - `Status` / `Stop` when `longRunning: true`.
  - `Restart` and `History` always (assuming the persistent
    job log lands).
  - `Schedule` only when scheduling is configured.
- Override per task via an optional `intents:` array in
  `tasks.json`, which adds to or replaces the inferred defaults.
- Discover suggests `intents:` based on the same heuristics
  it uses to suggest `longRunning:` (heavy ML imports,
  inference_steps params, venv activation in shell wrappers).

**Where intent lists come from for jobs**: derived from the job
state at display time. A job that's running shows `Status` and
`Stop`. A finished job shows `Show` (to re-render the artifacts)
and `Restart`. `History` only shows up when there are >=2 runs
of the same task — first job has no history to compare against.

This is the actual user-visible payoff of intents — discoverability
in the lists is the killer feature; the routing-pipeline
abstraction is mostly machinery to support it.

### Why now (after multi-provider lands)

Intents are mostly orthogonal to which chat backend you're on,
but the routing pipeline that intents touch is exactly the code
that gets refactored in phases 1+2 of the multi-provider work.
Doing intents AFTER the `MessageRouter` extraction means the new
intent handler chain can sit cleanly alongside the existing
slash-command / NL dispatch — no second restructuring.

---

## Wild ideas

### Voice notes
Telegram lets users send voice messages. Pipe through a local
whisper.cpp transcription and feed the result into the matcher.
"Render some skulls" but spoken. Probably more demoable than useful.

### Group-chat ops console
Instead of a single allow-listed user, a small group of admins each
authorized for different task subsets. Effectively /etc/sudoers for
tasks.

### Task chaining
"After render finishes, upload the freshest image to S3." Either a
`onSuccess` field on `TaskDefinition` or a chat-driven workflow
("…then post it to slack"). Risk: turns into a half-baked workflow
engine.

### Natural-language stop
"stop the render" routes to `_stop_latest_job`. We already have
`_check_latest_job`; the symmetric stop is one more virtual route.

### Local catalog versioning
Snapshot `tasks.json` to git before each `discover -w` so you can
revert promotion mistakes. Probably a `.config/teletasks/.history/`
dir with timestamped copies.
