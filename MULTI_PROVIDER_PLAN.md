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

Touch list:

1. `Program.cs` — register `TelegramChatProvider` as
   `IChatProvider` (singleton) so DI can hand it to both
   `TelegramBotService` and the future `JobNotifierService`.
2. `Services/TelegramBotService.cs`:
   - Constructor takes `IChatProvider` (or `IEnumerable<IChatProvider>`
     — see step 2c) instead of constructing its own `TelegramBotClient`.
   - `ExecuteAsync` calls `provider.StartAsync(stoppingToken)`,
     subscribes to `provider.OnMessage`, awaits `stoppingToken`.
   - Convert `OnMessageAsync(Message msg, UpdateType _)` to
     `OnIncomingAsync(IncomingMessage msg)`. Inside, `chatId` becomes
     `msg.Chat` (already a `ChatId`); user id reads from `msg.UserId`;
     text from `msg.Text`. Allow-list check delegates to
     `provider.IsAuthorized(msg)`.
   - Replace each `bot.SendMessage(chatId, html, parseMode: Html, …)`
     with `provider.SendHtmlAsync(chat, html, ct)`.
     Plain-text (no `parseMode`) sites become `SendTextAsync`.
   - Replace `bot.SendChatAction(chatId, Typing, …)` with
     `provider.SendTypingAsync(chat, ct)`.
   - Replace the two `bot.SendPhoto / SendDocument` sites at
     `TelegramBotService.cs:944,951` with
     `provider.SendImageAsync` / `provider.SendDocumentAsync`. Path +
     caption are already the only inputs; `InputFileStream` wrangling
     moves into the provider (it's already implemented there).
   - Drop the `using ProviderChatId = …` alias and the
     `using Telegram.Bot*` block.
3. `Services/Chat/TelegramChatProvider.cs` — minor surface tweaks
   only if the rewire reveals gaps:
   - Confirm `OnMessage` raises for non-text messages too if any
     existing handler expects them (current `TelegramBotService` only
     reads `message.Text`, so likely a no-op).
   - The startup health-check DM in `TelegramBotService` currently
     uses `_options.AllowedUserIds[0]`. Switch to
     `provider.DefaultRecipient` so non-Telegram providers can
     supply their own primary recipient later.

Tests:

- Existing `JobTrackerTests` and `ConversationStateTrackerTests` keep
  passing unchanged (they already speak `ChatId`).
- New `TelegramChatProviderTests` with a fake `TelegramBotClient`
  surface (or skip and rely on the rewire being exercised by the
  end-to-end harness once it lands — no fake exists today).
- A focused regression test for the slash-command-addressed-to-other-bot
  case currently inside `TelegramBotService` moves to
  `TelegramChatProviderTests` (the `TryStripBotMention` path already
  lives in the provider).

Done when: `grep -r "Telegram\\.Bot" src/TeleTasks/Services/` returns
only `Services/Chat/TelegramChatProvider.cs`.

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
