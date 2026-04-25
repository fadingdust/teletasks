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

## Prerequisites

- .NET 8 SDK
- A Telegram bot token (talk to [@BotFather](https://t.me/BotFather))
- Ollama running locally with a chat model that supports JSON mode, e.g.
  `ollama pull llama3.1` (Llama 3.1, Qwen 2.5, Mistral Nemo, etc. all work)

## Configure

Copy the examples and fill them in:

```bash
cp src/TeleTasks/appsettings.example.json src/TeleTasks/appsettings.json
cp src/TeleTasks/tasks.example.json       src/TeleTasks/tasks.json
```

Set at minimum:

- `Telegram:Token` — your bot token
- `Telegram:AllowedUserIds` — your numeric Telegram user ID. Send any message to the
  running bot and check the logs, or use `/whoami` after temporarily allow-listing
  the chat.

Any setting can also be supplied via env vars prefixed with `TELETASKS_`, e.g.
`TELETASKS_Telegram__Token=...`.

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

## Built-in commands

- `/help`, `/start` – usage
- `/tasks` – list configured tasks
- `/reload` – reload `tasks.json` without restarting
- `/whoami` – show your user / chat IDs (handy for the allow-list)

## Project layout

```
src/TeleTasks/
  Program.cs                     # host + DI wiring
  Configuration/AppOptions.cs    # Telegram / Ollama / TaskCatalog options
  Models/                        # task & result types
  Services/
    TaskRegistry.cs              # loads tasks.json
    OllamaClient.cs              # /api/chat with format=json
    TaskMatcher.cs               # NL → (task, parameters)
    TaskExecutor.cs              # process invocation + parameter substitution
    OutputCollector.cs           # text / file / image / images / log-tail
    TelegramBotService.cs        # background polling + dispatch
    ParameterTemplate.cs         # {name} substitution
  appsettings.example.json
  tasks.example.json
```
