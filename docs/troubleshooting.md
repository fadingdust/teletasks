# Troubleshooting

The bot tries to be loud about why things didn't work. Most issues can be
diagnosed by reading the discover stderr output or the bot's startup log.
This page maps what the logs say to what to change.

## "I don't think I'm using the right model"

```
# llm: model=llama3.2:1b endpoint=http://localhost:11434
#   json: /opt/.../appsettings.json (loaded)
#   json: /opt/.../appsettings.Local.json (missing, optional)
#   json: /home/me/.config/teletasks/appsettings.Local.json (loaded)
```

(Logged by `discover --llm` and on every bot startup.) The line tells you
which model the configured Ollama call will use AND which JSON files
contributed to that decision. If `appsettings.Local.json` says `(missing,
optional)` but you expected to be using a Local override, you wrote it
somewhere else.

```bash
dotnet TeleTasks.dll where
```

…prints the resolved config dir and every fallback (`$TELETASKS_CONFIG_DIR`
→ `$XDG_CONFIG_HOME/teletasks` → `$HOME/.config/teletasks` → ...). The
resolver uses `Environment.GetFolderPath(ApplicationData)` ahead of `$HOME`
so even shells with `HOME=""` land at the right place via `getpwuid()`.

## "captionFrom=no — why?"

```
# promote: py_render: Images <- param 'output_dir'
  (default=/proj/results/*,
   captionFrom=no — /proj/results/lora-foo has 2 image(s) but no matching sidecars
   (paired counts: 0.json, 0.txt, 0.yaml, 0.yml))
```

The reason is always specific. Common ones:

| Reason | What it means | Fix |
|---|---|---|
| `no default path to scan` | Parameter has no concrete default and no glob fallback fired | Add a `default` to the parameter, or run discover from a project root that has the convention dir on disk |
| `glob X matched nothing on disk` | Smart-glob pattern doesn't resolve to a real path | Run discover after at least one render has produced output, OR hand-edit the directory path |
| `<dir> has no images directly` | Promoter picked a parent that's empty; real images are in a deeper subdir not detected | Hand-set `output.directory` to the deeper path with `*` for variable segments |
| `<dir> has N image(s) but no matching sidecars (paired counts: ...)` | Your renders just don't have sidecar files | Either drop `captionFrom` from the spec, or change `sidecar` to whatever extension you actually write |

If your renders DO have sidecars but at a different extension (e.g.
`.meta` or `.yaml`), set `captionFrom.sidecar` explicitly:

```jsonc
"captionFrom": { "sidecar": ".meta", "mode": "auto-diff" }
```

## "sh wrapper isn't inheriting the python's output"

```
# wrapper: sh_install: found .py tokens [setup.py, install.py]
  but none matched candidate map [render.py, app.py]
```

The shell mentions `.py` files but none of them are tasks we discovered.
Likely: those `.py` files don't have `argparse`, so `ArgparsePythonDetector`
skipped them. Either (a) accept that wrapping isn't relevant for that
shell, or (b) hand-author the shell task with the output spec you want.

```
# wrapper: sh_basic: no *.py tokens found in script body
  (first 120 chars: #!/bin/bash\nset -e\necho hello\n…)
```

The shell doesn't reference any `.py` file. Either it's not actually a
wrapper, or the python invocation lives past the 2 KB SourceText cap (rare;
ShellScriptDetector caps at 2 KB but most wrapper scripts are smaller).

```
# wrapper: sh_run -> py_app: target's output is still Text
  (promoter didn't fire) — nothing to inherit
```

The wrapping was detected, but the python candidate itself never got
promoted (its `output` is still `Text`). Look at the `# promote: py_app:`
line — it'll explain why (no output-shaped param, ambiguous params,
already-customised, etc.).

## "discover wrote to the wrong place"

```bash
dotnet TeleTasks.dll where
```

Tells you the resolved config dir. By default `~/.config/teletasks/`. The
discover output also prints `# config dir: ...` at the top of every run.

If `where` shows a different dir than you expected, check the env vars:

```
1. $TELETASKS_CONFIG_DIR    = (set?)
2. $XDG_CONFIG_HOME         = (set?)
3. SpecialFolder.ApplicationData = ...
4. $HOME                    = ...
```

Set `TELETASKS_CONFIG_DIR=/some/path` to override.

## "discover -w writes nothing"

You forgot `-w`. Discover prints a hint:

```
# (preview only — re-run with -w to save to /home/me/.config/teletasks/tasks.json)
```

Re-run with `-w` (or `-o /custom/path/tasks.json`).

## "the matcher routed my message to the wrong task"

```
You: Latest output for py_render
Bot: → Running sh_install ...
```

Two ways to verify what the matcher did:

```
You: /dry latest output for py_render
Bot: Dry run: sh_install (lora=...)
     Would run: /bin/bash /home/me/install.sh ...
```

`/dry` runs the matcher and shows the resolved command without executing.
If the route is wrong, the system prompt isn't biasing strongly enough
toward the right virtual route.

For "show me the output of X" specifically, use the explicit form:

```
/results py_render
```

…which bypasses the matcher entirely. Bot looks up the task by name,
applies its parameter defaults, runs `OutputCollector.CollectAsync`
against the current state of disk, sends the artifacts. No command
execution. [in-flight: `claude/output-spec-promotion`]

## "the bot keeps asking for parameters one by one"

[in-flight: `claude/conversational-params`]

That's the conversational-collection feature. When a task has `required:
true` parameters that the matcher couldn't extract from your message, the
bot asks for each one in turn. Reply with the value, or `/cancel` to abort.

Make a parameter optional (or give it a `default`) to stop the prompt:

```jsonc
{ "name": "seed", "type": "integer", "required": false, "default": 42 }
```

## "I'm getting Ollama 404 model not found"

```
fail: TeleTasks.Services.OllamaClient[0] Ollama returned 404:
  {"error":"model 'llama3.2:1b' not found"}
```

The configured model isn't pulled. The bot's startup health check also
DMs you:

```
⚠️ I'm online, but Ollama doesn't have model llama3.2:1b pulled.
On the host machine run:
  ollama pull llama3.2:1b
```

Pull it, or change the model in `appsettings.Local.json`:

```jsonc
"Ollama": { "Model": "qwen2.5:0.5b" }
```

To silence the startup ping (e.g. for noisy restarts):

```jsonc
"Telegram": { "StartupNotificationsEnabled": false }
```

## "the task description says 'A string to pass to the script' — useless"

That's the LLM polish output. The default polish prompt now also receives
the script's source text, so descriptions are grounded in actual usage.
For shell scripts the LLM should infer that `$1` is "the prompt to feed
the model" or whatever, based on how it's used in the body. If the
descriptions are still generic, the model is the limit — try a slightly
larger model (`llama3.2:1b` instead of `qwen2.5:0.5b`).

If you'd rather hand-author the descriptions, just edit `tasks.json`. On
re-run, source-keyed merge updates `description` from the detector — but
if you also delete the `source` field, the entry becomes "hand-managed"
and discover leaves it alone.

## "Ctrl+C crashes the bot"

That was a real bug; fixed in the merged main. If you still see
`ObjectDisposedException` on the SIGINT thread, you're running an older
build. `git pull && dotnet build src/TeleTasks -c Release`.

## "I changed appsettings.json but the bot still uses the old value"

`dotnet run` rebuilds and copies the source-tree `appsettings.json` to the
bin output — that part's fine. But edits to `appsettings.Local.json` are
NOT in source (it's `.gitignore`d). The wizard saves to
`~/.config/teletasks/appsettings.Local.json`, NOT to bin.

If you have stale bin copies of `Local.json` from old wizard runs, they're
still loaded as a legacy fallback. Delete them:

```bash
find . -path '*/bin/*/appsettings.Local.json' -delete
```

The user-config-dir copy always wins, so this rarely matters in practice.

## Runtime task fails with "Output directory not found"

```
Output collection failed: Output directory not found: /path/to/results/*/output
```

(Pre-`PathGlob`.) Globs in directory paths are now expanded by
`OutputCollector` — `*` and `?` get resolved to the freshest match by
mtime. If you see this error on the latest build, the glob matched nothing:

```
Output directory glob matched nothing: /path/to/results/nope-*/output
```

Means there's no real directory matching `nope-*`. Check what's actually
on disk:

```bash
ls /path/to/results/
```

[Glob expansion: in-flight `claude/output-spec-promotion`]

## "/jobs is empty even though I have a render running"

[in-flight: `claude/long-running-jobs`]

Two possibilities:

- The task isn't `longRunning: true`. `/jobs` only tracks tasks with that
  flag; a regular task runs synchronously and doesn't get a job ID.
- The job tracker's state file got cleared. Check
  `~/.config/teletasks/jobs.json`. If your render's PID is alive but not
  listed, something deleted the file (e.g. a manual `rm -rf
  ~/.config/teletasks` or a fresh setup wizard run).

## Diagnostic toolbox at a glance

| Question | Command |
|---|---|
| Where does config live? | `dotnet TeleTasks.dll where` |
| Did discover write where I expected? | Look for `# config dir:` and `# wrote to:` in stderr |
| Why didn't a task get promoted? | Look for `# promote: <name>: skipped (...)` |
| Why didn't a sh inherit a py? | Look for `# wrapper: <name>: ...` |
| Which Ollama model is being used? | `# llm: model=...` (during `discover --llm`) or bot startup log |
| Did the matcher pick what I think? | `/dry <natural-language request>` |
| Show me a task's output without running? | `/results <task-name>` |
| What's running? | `/jobs` |
| Status of job N? | `/job N` |
| Stop job N? | `/stop N` |
| Reload tasks.json without restart? | `/reload` |
