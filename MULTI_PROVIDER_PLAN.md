# Multi-provider chat support — next steps

Working plan for the `claude/multi-provider` branch. Picks up where the
two existing commits left off and breaks the remaining work into
PR-sized chunks. The big-picture design lives in
[IDEAS.md → "Multi-provider chat support"](IDEAS.md); this file is the
execution sequence.

## Where we are today

Two commits on this branch:

- **Phase 1** (`e1a50ea`) — `Services/Chat/` design seed:
  `ChatId`, `IncomingMessage`, `IChatProvider`, `ChatHtml`,
  `TelegramChatProvider`. All dead code; nothing is registered in DI;
  `TelegramBotService` still drives the bot directly.
- **Phase 2 partial** (`cd063d5`) — type-plumbing only:
  `JobRecord.ChatId` is now `ChatId?` (legacy `long` deserialises via
  `ChatIdJsonConverter`), `JobTracker.AssignChat(int, ChatId)` replaced
  the `long` overload, `ConversationStateTracker` is rekeyed by
  `ChatId`. `TelegramBotService` adapts at the boundary with a
  `using ProviderChatId = TeleTasks.Services.Chat.ChatId;` alias and
  inline `ChatId.FromTelegram(long)` wraps. Build green, 292 tests pass.

Open scaffolding that the next steps remove:

- `using ProviderChatId = …` alias in `TelegramBotService.cs` (line 14).
- `if (!long.TryParse(chatRef.Id, out var chatId)) continue;` in the
  notifier loop (`TelegramBotService.cs:131`).
- `_bot.SendMessage / SendPhoto / SendDocument / SendChatAction` —
  ~38 call sites still talk to `Telegram.Bot` directly.
- `TelegramChatProvider` is not yet registered in DI.

## Step 2b — `TelegramBotService` dispatches through `IChatProvider`

Goal: every bot send goes through `_provider.Send*Async(chat, …)`.
After this step, `TelegramBotService.cs` no longer imports
`Telegram.Bot.*`, the alias and the `long.TryParse` workaround are
deleted, and `TelegramChatProvider` is the only thing that owns a
`TelegramBotClient`.

The work splits into seven small commits. Each one builds clean,
keeps the 292-test suite green, and is reviewable on its own. Sends
keep working through the host's own `_bot` field across steps 2b.1
through 2b.6 — only step 2b.7 deletes it. That intermediate state is
fine because `TelegramBotClient` is just an HTTP wrapper; multiple
instances with the same token can post in parallel without
conflicting. The long-poll receive consumer is the only thing that
must be single-owner, and step 2b.1 hands that to the provider.

### 2b.1 — Receive moves to the provider

Largest of the small steps (~60–100 lines). Touches the lifecycle.

- `Program.cs` — register `TelegramChatProvider` as a singleton
  bound to both `IChatProvider` and its concrete type.
- `TelegramBotService` constructor takes `IChatProvider _provider`
  alongside the existing `_bot`.
- `ExecuteAsync`:
  - Drop `await _bot.GetMe()`, `_bot.DropPendingUpdates()`,
    `_bot.OnError += …`, `_bot.OnMessage += …`. The provider does all
    of these in `StartAsync`.
  - Add `await _provider.StartAsync(stoppingToken);`
    `_provider.OnMessage += OnIncomingAsync;`.
- New private `OnIncomingAsync(IncomingMessage msg)` wraps the
  existing `OnMessageAsync` body. At entry, adapt to the old shape:
  ```csharp
  var chatId = long.Parse(msg.Chat.Id);
  var userId = long.Parse(msg.UserId);
  var text = msg.Text;
  ```
  Everything below stays as is — sends still go through `_bot`.
- Delete the old `OnMessageAsync(Message, UpdateType)` and
  `OnErrorAsync(Exception, HandleErrorSource)` methods.

After this commit: receive flows through the provider; sends
unchanged.

### 2b.2 — `SendChatAction` → `SendTypingAsync`

One site (`TelegramBotService.cs:370`). One-line edit. Trivial.

### 2b.3 — `SendPhoto` / `SendDocument` → provider calls

Two sites in the notifier loop (`TelegramBotService.cs:944,951`). The
provider already takes `(path, caption)` directly — drop the
`InputFileStream` plumbing at the call sites since
`TelegramChatProvider` does it internally.

### 2b.4 — `SendMessage` for slash-command handlers

~20 sites: the `/help`, `/tasks`, `/reload`, `/whoami`, `/cancel`,
`/jobs`, `/job N`, `/stop N`, `/dry`, `/results`, "unknown command",
"not authorized" replies. Each is a mechanical
`bot.SendMessage(chatId, …, parseMode: Html, …)` →
`_provider.SendHtmlAsync(msg.Chat, …, ct)`. Plain-text (no
`parseMode`) sites become `SendTextAsync`.

### 2b.5 — `SendMessage` for matcher routing + conversational params

~10 sites: the LLM-routing path, parameter-collection prompts,
type-coercion error replies, "asking for next missing param".
Same mechanical swap as 2b.4.

### 2b.6 — `SendMessage` in the notifier loop + startup health check

~8 remaining sites: per-job push notifications, completion summaries,
the Ollama-startup-health DM. Health DM also switches from
`_options.AllowedUserIds[0]` to `_provider.DefaultRecipient` so
non-Telegram providers can supply their own primary recipient later.

After this commit: zero `_bot.X` calls remain in
`TelegramBotService.cs`.

### 2b.7 — Remove the boundary scaffolding

Pure cleanup. No behavior change.

- Delete the `private TelegramBotClient? _bot;` field and its
  construction in `ExecuteAsync`.
- Delete the `private string? _botUsername;` field and the
  `Telegram bot @{Username} started` log line (provider logs its own).
- Drop all `using Telegram.Bot.*` from `TelegramBotService.cs`.
- Drop the `using ProviderChatId = TeleTasks.Services.Chat.ChatId;`
  alias — `Telegram.Bot.Types.ChatId` is no longer in scope, so the
  bare name resolves cleanly.
- Drop the `using Microsoft.Extensions.Options` import if
  `_options` is now only used for `JobPollSeconds` (still needed
  until step 2c moves the notifier).

Done when: `grep -r "Telegram\\.Bot" src/TeleTasks/Services/`
returns only `Services/Chat/TelegramChatProvider.cs`.

### Notes on what's deliberately deferred

- **No `IChatProvider.OnError` event.** The host's existing
  `OnErrorAsync` only logs; the provider already logs equivalently in
  `OnTelegramError`. Dropping the host subscription in 2b.1 loses no
  real behavior. If a future step needs error visibility from the
  host (e.g. retry/backoff coordination), add the interface event
  then.
- **No mention-stripping cleanup.** `_botUsername` is vestigial after
  2b.1 because the provider strips `/cmd@MyBot` before raising
  `OnMessage`. Field deletion happens in 2b.7 with the rest of the
  scaffolding.
- **No allow-list delegation.** The host's inline `IsAuthorized`
  check stays unchanged through all of 2b. Switching to
  `_provider.IsAuthorized(msg)` is a real behavior diff (default-deny
  semantics, log-warning frequency) — it deserves its own commit
  rather than riding inside one of the mechanical send-swaps.
  Schedule it as 2b.8 if needed, or roll it into step 2d's
  per-provider config rework.
- **No new tests in 2b itself.** Suite stays at 292. The
  `MessageRouter` extraction (step 2d) is the right place to add a
  `FakeChatProvider` harness with routing-pipeline coverage; doing
  it inside 2b just means rewriting the tests during 2d's refactor.

## Step 2c — Extract `JobNotifierService`

Goal: pull the 30s push loop
(`RunJobNotifierLoopAsync`, `TelegramBotService.cs:100-280`-ish) out of
`TelegramBotService` into a free-standing `BackgroundService` that
walks all jobs and dispatches per `ChatId.Provider`. This is the piece
that unblocks "two providers running side-by-side in one process".

1. New `Services/JobNotifierService.cs : BackgroundService`:
   - Constructor takes `IEnumerable<IChatProvider>`, `JobTracker`,
     `TaskExecutor`, `OutputCollector`, `ILogger`, the relevant
     options.
   - Builds `Dictionary<string, IChatProvider>` keyed by
     `provider.Name` once at start.
   - Per-tick: for each active job with a `ChatId`, look up
     `_providers[job.ChatId.Provider]`; if missing, log once and
     skip; otherwise run the existing artifact-diff +
     completion-summary logic against that provider's `Send*Async`.
   - Drop the `long.TryParse(chatRef.Id, …)` guard — the provider
     handles its own id parsing.
2. `TelegramBotService` shrinks to "host one provider + route incoming
   messages". Notifier code is gone.
3. Move `JobPollSeconds` config out of `TelegramOptions` into a new
   `ChatOptions` (`Chat:JobPollSeconds`); read both for one release
   with a fallback so existing `appsettings.Local.json` files don't
   break. (Actual rename can happen in step 2d.)
4. `Program.cs` registers `JobNotifierService` as a hosted service
   alongside `TelegramBotService`.

Tests:

- New `JobNotifierServiceTests` with two fake providers and seeded
  `JobRecord`s that point at each. Asserts the right provider gets
  the artifact pushes and the unknown-provider job is skipped with
  a single warning.
- Existing notifier-loop assertions inside the integration harness
  re-target the new service.

## Step 2d — Per-provider config + rename `TelegramBotService` → `ChatHost`

Goal: tidy the boundary so phase 3 (Discord) is a drop-in.

1. New `ChatOptions` section:
   ```jsonc
   "Chat": {
     "JobPollSeconds": 30,
     "StartupNotificationsEnabled": true,
     "Providers": {
       "Telegram": {
         "Token": "...",
         "AllowedUserIds": [],
         "AllowedChatIds": []
       }
     }
   }
   ```
   `TelegramOptions` keeps reading the legacy `Telegram:*` path for
   one release; the bot logs a deprecation hint at startup if the
   legacy path is the one that supplied the value.
2. Rename `Services/TelegramBotService.cs` → `Services/ChatHost.cs`.
   It owns the `IChatProvider` lifecycle and the `OnMessage` →
   `MessageRouter` dispatch; nothing inside is Telegram-shaped any more.
3. Move the message-routing logic (slash commands + matcher dispatch +
   conversational params) into a new `Services/MessageRouter.cs` so
   `ChatHost` is just a hosted-service wrapper. This is the seam the
   intents work (IDEAS.md) plugs into later.

Tests:

- A `MessageRouterTests` rig with a fake provider that captures every
  `Send*Async` call. Re-targets the existing slash-command coverage
  at this seam instead of `TelegramBotService` internals.
- Config-migration test: load a sample `appsettings.json` with the
  legacy `Telegram:*` shape, assert resolved options match the new
  `Chat:Providers:Telegram:*` shape and a deprecation log fires once.

## Step 3 — Discord provider

Independent of step 2d; can land in parallel once 2c is in.

1. Add `Discord.Net` (latest stable) to `TeleTasks.csproj`.
2. `Services/Chat/DiscordChatProvider.cs : IChatProvider`:
   - Gateway-WebSocket connection via
     `DiscordSocketClient` with the `MessageContent` and
     `GuildMessages` + `DirectMessages` intents.
   - `OnMessage` translates `SocketMessage` → `IncomingMessage`,
     strips leading `<@123…>` self-mention.
   - `IsAuthorized` checks `DiscordOptions.AllowedUserIds`,
     `AllowedGuildIds`, `AllowedChannelIds`. Default-deny if no
     allow-list is set, mirroring Telegram.
3. New `Services/Chat/HtmlToDiscordMarkdown.cs`: translates the
   canonical `<b>`, `<i>`, `<code>`, `<pre>` HTML to
   `**bold**`, `_italic_`, `` `code` ``, ```` ``` ```` blocks.
   Triple-backtick literals inside user content get a zero-width-space
   inserted to defuse the fence. Unit-test against the existing bot
   output corpus (sample `BuildHelp()`, `BuildTaskList()`, `/job N`
   render) — every snippet round-trips losslessly to Discord-readable
   text.
4. `DiscordOptions` under `Chat:Providers:Discord:*` (token, allow-lists).
5. Setup-wizard branch: `dotnet TeleTasks.dll setup --provider discord`
   prompts for the bot token, validates via
   `GET https://discord.com/api/users/@me`, prints the OAuth invite URL
   with the right scopes + permission integer
   (`bot applications.commands` + `Send Messages`, `Attach Files`,
   `Embed Links` = `52224`), and tells the user to invite the bot
   manually — Discord has no `getUpdates` analogue, so the user-id
   capture trick uses gateway events: the wizard connects, prompts the
   user to send any message, captures the first event's `Author.Id`,
   persists.
6. `Program.cs` registers `DiscordChatProvider` only when
   `Chat:Providers:Discord:Token` is non-empty so the service
   starts cleanly without it.

## Step 4 — `IOAuthChatProvider` design seed (no implementation)

Lands as design-only so Slack / MS Teams later are a fresh provider
class instead of a refactor:

1. `Services/Chat/IOAuthChatProvider.cs : IChatProvider` adds
   `BeginAuthorizationAsync`, `CompleteAuthorizationAsync`,
   `RefreshTokenAsync`, `bool TokenIsValid`.
2. `Services/Chat/TokenStore.cs` — encrypted-at-rest key/value store
   under `~/.config/teletasks/tokens/<provider>.json`.
   `ProtectedData.Protect` on Windows; on Linux fall back to
   `libsecret` via P/Invoke if available, otherwise plain JSON with
   `chmod 0600` and a warning.
3. `ChatHost` runs a per-provider refresh tick that calls
   `RefreshTokenAsync` on any `IOAuthChatProvider` whose token's TTL
   is below threshold.
4. Setup wizard's main loop branches on `IOAuthChatProvider`: prints
   the redirect URL, listens on `localhost:<port>` for the OAuth code,
   exchanges, persists. No actual provider implements the interface
   yet — Slack would be the obvious first one in a follow-up.

## Cross-cutting concerns

**Backward compat for `jobs.json`.** Legacy bare-`long` `chatId` is
already handled by `ChatIdJsonConverter`. The
`LoadAndReconcile_reads_legacy_long_chat_ids_as_telegram_provider`
test pins this; keep it green through every step.

**Backward compat for `appsettings.Local.json`.** Step 2d introduces
the dual-read with deprecation log. Drop the legacy path one release
after `Chat:Providers:Telegram:*` ships.

**Build hygiene.** Solution has warnings-as-errors. Each step lands as
a single commit that stays green; no "WIP build broken" intermediate
states (the previous `cd063d5` commit deliberately preserves this
property and the next ones should too).

**Testing baseline.** 292 tests pass on this branch today. Each step
above adds tests rather than replacing existing ones; the suite
should grow monotonically.

## North-star follow-on (out of scope here)

Once step 2d's `MessageRouter` exists, the **intents** work
(IDEAS.md → "Intents — verbs that apply to any task") slots in cleanly
as a per-intent handler chain on the router. That's the actual
user-visible payoff of this refactor; the multi-provider angle is the
infrastructure that makes a chat-backend-agnostic intent system
possible.
