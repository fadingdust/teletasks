# TeleTasks Cookbook

Worked examples of `tasks.json` entries for the common patterns. Copy, adapt,
drop into `~/.config/teletasks/tasks.json`, then `/reload` (or restart the
bot) to pick them up.

> Some features below are on feature branches pending merge. Each recipe
> notes which branch it depends on. See [SPECS.md](../SPECS.md) for the
> running ledger.

## Conventions

- Every task has a unique `name`, a one-line `description`, and an `output`
  spec describing what comes back to chat.
- Parameters are extracted from natural-language messages by Ollama (so
  give them descriptive names and `description` text — the model uses
  both).
- `{paramName}` placeholders in `args`, `path`, `directory`, `caption`,
  `env`, `workingDirectory`, etc. get substituted at runtime from the
  resolved parameters.

---

## 1. System status (text output)

Send back the output of a shell pipeline. Simplest possible task.

```jsonc
{
  "name": "system_status",
  "description": "Show CPU load, memory usage, and disk usage of the root filesystem.",
  "command": "/bin/bash",
  "args": [
    "-c",
    "echo '== uptime =='; uptime; echo; echo '== memory =='; free -h; echo; echo '== disk =='; df -h /"
  ],
  "output": { "type": "Text" }
}
```

Asking the bot "how's my system doing?" or "show CPU load" routes here. The
output renders in a Telegram `<pre>` block so column-aligned tools like
`free -h` and `df -h` keep their alignment.

---

## 2. Tail a log file

```jsonc
{
  "name": "tail_log",
  "description": "Tail the last N lines of a log file.",
  "parameters": [
    { "name": "path",  "type": "string",  "required": true, "description": "Absolute path to the log file" },
    { "name": "lines", "type": "integer", "default": 50, "description": "How many lines to show" }
  ],
  "output": {
    "type": "LogTail",
    "path": "{path}",
    "lines": "{lines}",
    "caption": "{path}"
  }
}
```

No `command` because `LogTail` reads the file directly. "tail my syslog 100
lines" extracts `path=/var/log/syslog`, `lines=100`. If the message doesn't
include the path, the bot prompts for it (one parameter at a time —
type `/cancel` to abort).

---

## 3. Latest screenshots from a directory

```jsonc
{
  "name": "latest_screenshots",
  "description": "Send the most recent screenshots from the screenshots folder.",
  "parameters": [
    { "name": "count", "type": "integer", "default": 3 }
  ],
  "output": {
    "type": "Images",
    "directory": "/home/me/Pictures/Screenshots",
    "pattern": "*.png",
    "sortBy": "newest",
    "count": "{count}"
  }
}
```

`sortBy` is `newest`, `oldest`, or `name`. `pattern` is a top-level glob
(no recursion) — for nested patterns use the path-glob form (recipe 6).

---

## 4. Render task with image+sidecar metadata

The pattern: each render writes `0007.png` plus `0007.json` carrying the
prompt / seed / model / cfg. Discover detects this automatically; the
hand-written form looks like:

```jsonc
{
  "name": "render_loop",
  "description": "Render frames with the SDXL pipeline.",
  "command": "/usr/bin/env",
  "args": ["python3", "render.py", "--prompt", "{prompt}", "--output-dir", "{output_dir}"],
  "parameters": [
    { "name": "prompt",     "type": "string",  "required": true },
    { "name": "output_dir", "type": "string",  "default": "./outputs" }
  ],
  "output": {
    "type": "Images",
    "directory": "{output_dir}",
    "sortBy": "newest",
    "count": 4,
    "captionFrom": {
      "sidecar": ".json",
      "mode": "auto-diff"
    },
    "siblings": [".json"]
  }
}
```

What the chat looks like after the task finishes:

```
Shared: model: sdxl-base, scheduler: euler-ancestral, steps: 20, width: 1024, height: 1024
[photo 0001.png]   prompt: misty forest, seed: 42, cfg: 7.5
[document 0001.json]
[photo 0002.png]   prompt: foggy mountain, seed: 99, cfg: 7.5
[document 0002.json]
...
```

Three `captionFrom.mode` values:

- `auto-diff` (default) — fields constant across the batch go to a single
  header, per-image captions show only what varies. Best for "show me the
  4 most recent renders" cases where you want to see what changed.
- `template` — caption from a custom template like
  `"{prompt} (seed {seed})"`. No header.
- `verbatim` — every key appears in every caption. Use for tiny sidecars.

`siblings` ships extra files alongside each photo as Telegram documents
— useful when sidecars are too rich for a 1024-char caption.

---

## 5. Render task with templated path

If your script writes to `./results/<lora_name>/output/`, parameterise the
path through another parameter. Multi-pass substitution resolves both
levels:

```jsonc
{
  "name": "render_lora",
  "command": "/usr/bin/env",
  "args": ["python3", "render.py", "--lora", "{lora}", "--output-dir", "{output_dir}"],
  "parameters": [
    { "name": "lora",       "default": "lora-foo" },
    { "name": "output_dir", "default": "./results/{lora}/output" }
  ],
  "output": {
    "type": "Images",
    "directory": "{output_dir}",
    "captionFrom": { "sidecar": ".json", "mode": "auto-diff" }
  }
}
```

`{output_dir}` resolves to `./results/{lora}/output`, then `{lora}` resolves
to `lora-foo`. End result: `./results/lora-foo/output`.

---

## 6. Latest from a globbed path

When you don't have a single canonical lora value — "send me the latest
from whichever run finished most recently" — use a wildcard:

```jsonc
"output": {
  "type": "Images",
  "directory": "./results/*/output",
  "sortBy": "newest",
  "count": 4
}
```

`*` and `?` are expanded by `PathGlob` at runtime. When multiple
directories match, the freshest by mtime wins. Works in `Image.path`,
`File.path`, `LogTail.path`, and `Images.directory`.

---

## 7. Long-running render with check-ins

```jsonc
{
  "name": "render_long",
  "longRunning": true,
  "command": "/usr/bin/env",
  "args": ["python3", "render.py", "--prompt", "{prompt}"],
  "parameters": [
    { "name": "prompt", "type": "string", "required": true }
  ],
  "env": {
    "PYTHONUNBUFFERED": "1"
  },
  "output": {
    "type": "Images",
    "directory": "/home/me/renders",
    "sortBy": "newest",
    "count": 4,
    "captionFrom": { "sidecar": ".json", "mode": "auto-diff" }
  }
}
```

`longRunning: true` makes the executor spawn the process detached (via
`setsid`), redirect stdout+stderr to a log file, and reply immediately:

```
Started job 7 (render_long, pid 12345).
Log: /home/me/.config/teletasks/run-logs/render_long-7-...log
Use /job 7 to check progress, /stop 7 to kill.
```

The bot then pushes results back to the chat without you having to
ask:

- A 30s poller (`Telegram:JobPollSeconds`, set `0` to disable) re-runs
  the output spec and sends any new artifacts. Each pushed image's
  caption is tagged `Job 7 • render_long` so you can tell unsolicited
  pushes apart from `/job 7` replies.
- When the job finishes the bot sends a one-line summary
  (`✅ Job 7 render_long ok after 4m 12s.`). Jobs `/stop`-ped show as
  `killed` instead of `exit unknown`.
- Use `/jobs` for the list, `/job 7` for a status check (header, log
  tail, current artifacts), `/stop 7` to kill (verified — escalates to
  `kill -KILL -<pid>` against the session group if `Process.Kill` is
  partial). Natural-language "what's running?" / "how's the render
  going?" routes to the same handlers.

Jobs persist to `~/.config/teletasks/jobs.json` so they survive bot
restart; `Reconcile` walks `/proc/<pid>/stat` at startup to detect any
job that ended while the bot was down.

> **Python stdout buffering footgun**: when stdout is redirected to a
> file (which `setsid` does for us), Python switches from line buffering
> to block buffering, so `/job N` log tails are empty for a long time.
> The `PYTHONUNBUFFERED=1` env entry above fixes it; alternatives are
> `python -u` or `stdbuf -oL python …`. The bot calls this out
> explicitly when the log file exists but is whitespace.

---

## 8. Send a journal as a file

```jsonc
{
  "name": "send_journal",
  "description": "Send the systemd journal as a downloadable file.",
  "parameters": [
    { "name": "service", "type": "string", "required": false },
    { "name": "lines",   "type": "integer", "default": 500 }
  ],
  "command": "/bin/bash",
  "args": [
    "-c",
    "if [ -n \"{service}\" ]; then journalctl -u \"{service}\" -n {lines} --no-pager > /tmp/teletasks-journal.txt; else journalctl -n {lines} --no-pager > /tmp/teletasks-journal.txt; fi"
  ],
  "output": {
    "type": "File",
    "path": "/tmp/teletasks-journal.txt",
    "caption": "journal {service}"
  }
}
```

Pattern: command writes to a known temp path, `output` sends it back as a
Telegram document.

---

## 9. Webcam snapshot (disabled by default)

```jsonc
{
  "name": "take_webcam_photo",
  "description": "Capture a still frame from /dev/video0.",
  "enabled": false,
  "command": "/usr/bin/ffmpeg",
  "args": [
    "-y",
    "-f", "v4l2",
    "-video_size", "1280x720",
    "-i", "/dev/video0",
    "-frames:v", "1",
    "/tmp/teletasks-webcam.jpg"
  ],
  "timeoutSeconds": 30,
  "output": {
    "type": "Image",
    "path": "/tmp/teletasks-webcam.jpg",
    "caption": "Webcam snapshot"
  }
}
```

`"enabled": false` keeps the task in your catalog (visible in `/tasks` under
"Disabled") but excludes it from the matcher's choices. Flip to `true`
once you've confirmed the camera works.

---

## 10. Shell wrapping a Python script

When `run.sh` invokes `python3 app.py …`, discover detects the wrap and
copies `app.py`'s output spec to the shell candidate, plus copies any
parameters the spec templates against. The hand-written equivalent if you
prefer to author it directly:

```jsonc
{
  "name": "sh_run",
  "description": "Render via the run.sh wrapper.",
  "command": "/bin/bash",
  "args": ["/home/me/image-renderer/run.sh", "{lora}", "{prompt}"],
  "parameters": [
    { "name": "lora",       "type": "string", "required": true },
    { "name": "prompt",     "type": "string", "required": true },
    { "name": "output_dir", "type": "string", "default": "./results/{lora}/output" }
  ],
  "output": {
    "type": "Images",
    "directory": "{output_dir}",
    "captionFrom": { "sidecar": ".json", "mode": "auto-diff" }
  }
}
```

The shell forwards `{lora}` and `{prompt}` to its `$1` / `$2`. The Python
script writes to `./results/<lora>/output/`. The bot reads from the same
templated path and sends back the latest renders.

---

## 11. Git status / log / diff [discover-emitted]

`teletasks discover git --path ~/code/myrepo -w` produces (per repo):

| Name | Output | Notes |
|---|---|---|
| `git_<repo>_status`   | Text  | `git status --short --branch` |
| `git_<repo>_log`      | Text  | `git log --oneline -n {count}`, default 10 |
| `git_<repo>_diff`     | File  | uncommitted diff written to `/tmp/teletasks-<repo>-diff.patch` |
| `git_<repo>_branches` | Text  | `git branch -vv --sort=-committerdate` |
| `gh_<repo>_runs`      | Text  | `gh run list -L {count}` (only if `gh` is installed) |
| `gh_<repo>_prs`       | Text  | `gh pr list` (only if `gh` is installed) |

You can edit / disable / rename these freely; on re-run, discover updates
in place via the `source` field rather than appending duplicates.

---

## 12. Show latest output without re-running

Two ways to evaluate a task's output spec without triggering its command:

```
/results render_loop
```

…or in natural language: "latest output for render_loop", "show me my
last screenshots from take_screenshot", "what did the build_logs task
produce". The matcher routes these to the same handler.

For `Text`-output tasks the bot replies "this task's output is its stdout
— run it to see results." For `Images` / `File` / `LogTail` it reads
whatever's on disk and sends back the artifacts.

---

## 13. Disable noisy detection without removing the task

```jsonc
{
  "name": "system_status",
  "enabled": false,
  ...
}
```

Tasks with `enabled: false`:

- Don't appear in the matcher's catalog (the LLM can't pick them).
- Show up under "Disabled" in `/tasks` so you remember they exist.
- Skip the discover update-in-place step (so toggling `enabled` doesn't
  get reverted on the next `discover -w`).

Useful for "I want this task in my repo for later but not active right now"
or for hand-edited tasks you want to gate behind a manual re-enable.

---

## 14. Asking the user for missing parameters

No special config — this kicks in automatically. When the matcher
resolves a task but a required parameter wasn't extracted from the
message (or was hallucinated by a small model), the bot asks for each
missing value in turn:

```
You: tail a log
Bot: → tail_log needs 1 more value(s). Send each one in turn,
     or /cancel to abort.
     What's the value for path?
You: /var/log/syslog
Bot: → Running tail_log (path=/var/log/syslog, lines=50)
     [last 50 lines of /var/log/syslog]
```

The bot also recognises typing the literal task name as the fastest
way into this flow:

```
You: tail_log
```

That's the **exact-task-name fast path** — the bot skips the LLM call
entirely (saves 20-30s on tiny models), synthesizes an empty match,
and walks you through every required parameter from scratch. Useful
when you remember the task name but not the natural-language
incantation.

Replies are coerced by `ParameterValueParser`:

- `integer` / `number` → must parse as the right type, otherwise reprompts.
- `boolean` → accepts `y`/`yes`/`true`/`1` and `n`/`no`/`false`/`0`.
- `enum` → must match one of the declared `enum` values; lists them on bad input.
- `string` → trimmed; empty replies reprompt.

Slash commands during a pending prompt clear the state automatically
(so `/tasks`, `/help`, `/jobs` always work). Explicit `/cancel`
acknowledges and exits the flow. Pending state self-expires after 15
minutes idle.

### Hallucination guard

Small matchers (`qwen2.5:0.5b`) sometimes invent values for required
string parameters to satisfy the JSON schema. The bot rejects values
whose ≥3-character tokens don't appear in the original user message
(after stripping the task name), treating them as missing and falling
through to the prompt loop. Numbers / bools / enums skip the check
because their valid space is small enough that hallucination is
structurally constrained.
