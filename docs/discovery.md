# Discovery

`teletasks discover <mode>` scans a project / system and emits TaskDefinition
entries. This page describes the pipeline, what each pass logs, and which
flags flip which behaviours.

> Some passes below are on feature branches pending merge. Each section
> notes which branch it lives on. See [SPECS.md](../SPECS.md) for status.

## Modes

| Command | What it scans |
|---|---|
| `discover project [--path DIR]`    | Makefile / justfile / package.json / pyproject.toml / `*.py` (argparse) / `*.sh` / `.vscode/tasks.json` in DIR (default: cwd) |
| `discover systemd [--user]`        | systemctl-listed services → journalctl tail tasks |
| `discover git [--path DIR]`        | per-repo status / log / diff / branches (+ gh runs/PRs if `gh` is on PATH) |
| `discover logs [--path DIR]`       | `*.log` files in DIR, filtered by mtime / size |

All modes share the same `--write` / `--output` / `--llm` / `--inspect`
flags described below.

## Pipeline

A `discover project` run goes through these passes in order:

```
detectors  →  inspector  →  promoter  →  wrapper resolver  →  llm polish
                            (output spec)  (sh inherits py)  (descriptions)
```

Each pass logs one line per candidate to stderr (lines starting `# `) so a
"why didn't this happen" question is one log read away.

### 1. Detectors (always run)

Each detector reads its source and emits one or more `TaskCandidate`s. For
`discover project`:

| Detector | Reads | Emits |
|---|---|---|
| `MakefileDetector`            | `Makefile` (or `GNUmakefile`) targets, `.PHONY` lists, comments above targets | `make_<target>` candidates |
| `JustfileDetector`            | `justfile` recipes, recipe args + defaults | `just_<recipe>` candidates with parameters |
| `PackageJsonDetector`         | `"scripts"` block in `package.json`            | `npm_<script>` candidates |
| `PyprojectDetector`           | `[project.scripts]` and `[tool.poetry.scripts]` | `py_<entry>` candidates |
| `VsCodeTasksDetector`         | `.vscode/tasks.json` entries (with comments / trailing commas) | `vsc_<label>` candidates |
| `ArgparsePythonDetector`      | top-level `*.py` files that import argparse — runs a Python AST helper to read `add_argument` calls without executing the file | `py_<name>` candidates with typed parameters, defaults, choices, help text |
| `ShellScriptDetector`         | top-level `*.sh` files — `${1:-default}` and bare `$N` positional args, `getopts`, `# Description:` comments | `sh_<name>` candidates with positional parameters |

Each candidate carries a stable `source` string (e.g. `Makefile:build`,
`py:argparse:render.py`, `sh:run.sh`). On re-run, the merger uses this to
update existing entries in place rather than appending duplicates.

Each candidate also carries `SourceText` (the original file body, capped
at 2-2.5 KB) — used by later passes for sidecar detection, wrapper
resolution, and LLM context.

### 2. Inspector (default ON, opt out with `--no-inspect`)

`PathInspector` looks at each candidate's parameters. When a parameter's
name contains a path token (`output`, `dir`, `file`, `log`, `src`, `dst`,
`path`, `target`, `checkpoint`, ...) AND has a default value pointing at
something on disk, it `stat`s that path and appends a one-line
"current state" note to the task's description. Examples:

- `output_dir=dir, 12 file(s) [9.png, 2.log, 1.txt], latest 5m ago`
- `config_file=file, 47 KB, modified 2h ago`
- `missing_dir=missing`

Cheap (one stat per parameter, capped at 500 enumerated files per
directory). URL-shaped defaults are skipped.

### 3. Output-spec promoter [in-flight: `claude/output-spec-promotion`]

`OutputSpecPromoter` looks at each candidate's parameters. When exactly
ONE parameter has a strongly-named output token AND the task currently
has the default `Text` output, the promoter rewrites `output`:

| Parameter shape | Becomes |
|---|---|
| `output_dir`, `output`, `outputs`, `results_dir`, `renders_folder`, ... | `Images` with `directory: "{paramName}"`, `count: 4`, `sortBy: newest` |
| `log_file`, `log_path`, `logfile`, ...                                  | `LogTail` with `path: "{paramName}"`, `lines: 100` |
| `output_path`, `output_file`, `out_path`, `dest_file`, ...              | `File` with `path: "{paramName}"` |

Priority when multiple kinds match: **Images > LogTail > File** (the
"show me my renders" use case wins; logs accessible via `/job N` for
long-running tasks).

When the chosen parameter has no concrete default, the promoter globs
`WorkingDirectory` for conventional names (`outputs/`, `results/`,
`renders/`, `generated/`, `samples/`, `logs/`) and uses the first match
that actually contains files. If images live deeper than the immediate
convention dir (e.g. `results/<lora>/output/*.png`), the promoter walks
inward up to 3 levels and emits a smart-glob like `results/*/output` —
PathGlob expands it at runtime.

When the resolved directory has paired image + sidecar files (`.png` +
matching `.json` / `.txt` / `.yaml` / `.yml`), `captionFrom` is
auto-set:

```jsonc
"captionFrom": { "sidecar": ".json", "mode": "auto-diff" }
```

Logs:

```
# promote: py_render: Images <- param 'output_dir'
  (default=/proj/results/*/output,
   captionFrom=yes — detected 4/4 .json sidecars in /proj/results/lora-foo/output)

# promote: py_app: skipped (no output-shaped param recognised — params: prompt, model, lora)

# promote: my_task: skipped (ambiguous: multiple output-shaped params)
```

The "skipped" reason is always specific: lists the params that exist, or
notes the conflict.

Opt out with `--no-promote-output`.

### 4. Shell wrapper resolver [in-flight: `claude/output-spec-promotion`]

Common pattern: a Python script does the actual work; a shell script
wraps it with environment setup. `ShellWrapperResolver` detects the wrap
and copies the Python's output spec to the shell candidate.

For each shell candidate, scans the source body for any `*.py` token and
looks it up in the python candidate map. Matches all the common
invocation styles:

```bash
python3 app.py ...
python -u app.py
pipenv run python app.py
poetry run python app.py
uv run app.py
$PYTHON app.py
./app.py
exec app.py
```

When a match is found AND the python candidate has a non-`Text` output
spec, the resolver:

1. Deep-clones the python's output spec onto the shell candidate.
2. Copies any parameters the spec templates against (`{output_dir}`,
   `{lora}`, etc.) so substitution works at runtime even though the
   shell normally only takes positional `$1`/`$2`.
3. Appends `(wraps py_<name>)` to the description.

Logs:

```
# wrapper: sh_run <- py_render
  (Images, captionFrom=yes, params copied=output_dir,lora)

# wrapper: sh_install: found .py tokens [setup.py, install.py] but none matched
  candidate map [render.py, app.py]

# wrapper: sh_basic: no *.py tokens found in script body
  (first 120 chars: #!/bin/bash\nset -e\necho hello\n…)
```

Skip conditions (logged):

- No source text on candidate (truncated SourceText)
- Found `.py` tokens but none correspond to a known argparse candidate
- Target's output is still `Text` (promoter didn't fire on the python)
- Shell already has non-`Text` output (kept — don't clobber user edits)

### 5. LLM polish (opt-in with `--llm`)

When `--llm` is set, each candidate is sent to Ollama with its current
description, command, parameters, AND its `SourceText` (the raw script /
recipe body). The model returns a polished `description` and per-parameter
`description` keyed by name. Schema-constrained so the response shape is
guaranteed.

The polish step prints the resolved Ollama config so the "wrong model is
being used" question is answerable at a glance:

```
# llm: model=qwen2.5:0.5b endpoint=http://localhost:11434
#   json: /opt/teletasks/.../appsettings.json (loaded)
#   json: /opt/teletasks/.../appsettings.Local.json (missing, optional)
#   json: /home/me/.config/teletasks/appsettings.Local.json (loaded)
#   env: TELETASKS_*
```

Structural fields (name, command, args, parameter types, defaults,
output spec) are NEVER written by the model — only the descriptions get
refined.

## Catalog merge

After the pipeline runs, `TaskCatalogWriter.Merge` writes the candidates
to `tasks.json` with re-run-safe semantics:

| Existing entry | Incoming candidate | Result |
|---|---|---|
| Same `source` | — | **Updated in place** (description / command / args / parameters / output spec refreshed). `name` and `enabled` flag are preserved. |
| Same name, no source | — | Hand-written task. **Left alone**. Incoming gets suffixed `_2`, `_3`, ... |
| No match anywhere | — | **Appended** fresh |

The CLI prints a summary:

```
# wrote to /home/me/.config/teletasks/tasks.json:
  0 added, 14 updated, 0 renamed, 0 removed
```

`--force-replace` removes existing tasks whose source category matches an
incoming source (e.g. all `Makefile:*` entries get wiped before adding new
ones). Hand-written tasks (no source) survive `--force-replace`. Use this
when you've removed Makefile targets and want stale entries gone.

## Flag reference

| Flag | Default | Effect |
|---|---|---|
| `--write`, `-w`              | off | Save to tasks.json instead of stdout. Default location: `~/.config/teletasks/tasks.json`. |
| `--output PATH`, `-o`        | —   | Write to a specific path. Implies `--write`. |
| `--force-replace`            | off | Remove existing tasks with matching source category before merge. Implies `--write`. |
| `--inspect` / `--no-inspect` | ON  | Run PathInspector to enrich descriptions with current-state notes. |
| `--promote-output` / `--no-promote-output` | ON | Run OutputSpecPromoter + ShellWrapperResolver. |
| `--llm` / `--no-llm`         | OFF | Run LLM polish step. Uses the configured Ollama model. |

For `discover logs` specifically:

| Flag | Default | Effect |
|---|---|---|
| `--since 7d`     | 7   | Skip files not modified within N days |
| `--max 100`      | 100 | Skip files larger than N MB |
| `--pattern *.log`| `*.log` | Glob pattern |
| `--recursive`    | off | Walk subdirectories |

For `discover systemd`:

| Flag | Default | Effect |
|---|---|---|
| `--user`           | off | User-scope services instead of system |
| `--running` / `--all` | all | Filter to running units only |

## Reading the full output

Run with both stdout and stderr captured to see everything:

```bash
dotnet run --project src/TeleTasks -- discover project --path . --no-llm 2>&1
```

Stderr carries the diagnostic lines (`#`-prefixed). Stdout carries either
the JSON preview (no `-w`) or nothing (with `-w` — the file gets the JSON).

If the diagnostic logs ever say something you can't decipher, paste the
stderr block — every "skipped" reason is designed to be specific enough
to tell you what to change.
