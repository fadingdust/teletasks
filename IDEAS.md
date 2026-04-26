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

---

## Job system

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
