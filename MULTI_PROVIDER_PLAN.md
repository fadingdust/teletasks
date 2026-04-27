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
(`RunJobNotifierLoopAsync`, `TelegramBotService.cs:93-232`) out of
`TelegramBotService` into a free-standing `BackgroundService` that
walks all jobs and dispatches per `ChatId.Provider`. This is the piece
that unblocks "two providers running side-by-side in one process".

Three small commits. Each builds clean, keeps the suite green, and
is reviewable on its own.

### 2c.1 — Extract `ChatResultDispatcher`

`SendResultAsync` (TelegramBotService.cs:919) is shared by the
notifier (`PushNewArtifactsAsync` at line 203), task execution
(line 459), `/results` (line 587), and `/job N` (line 754). The
notifier extraction needs it too, so pull it out first as a
standalone helper rather than duplicate the artifact-rendering
switch.

- New `Services/Chat/ChatResultDispatcher.cs`. Single entry point:
  ```csharp
  public Task DispatchAsync(
      IChatProvider provider,
      ChatId chat,
      TaskExecutionResult result,
      CancellationToken ct)
  ```
  Body is the existing `SendResultAsync` switch (text → SendHtmlAsync
  with `<pre>` wrap, image → SendImageAsync, file → SendDocumentAsync,
  trailing error message → SendHtmlAsync). No behavior change.
- `TelegramBotService`:
  - Constructor takes `ChatResultDispatcher _dispatcher`.
  - Each `await SendResultAsync(chatId, result, ct)` becomes
    `await _dispatcher.DispatchAsync(_provider, chat, result, ct)`.
    Five call sites (lines 203, 459, 587, 754, 916).
  - Delete the now-unused `SendResultAsync` private method.
- `Program.cs` registers `ChatResultDispatcher` as singleton.

Pure refactor. ~60 lines moved between files; no logic touched.

### 2c.2 — Add `JobNotifierService` as dead code

Drop the new file in without registering it as a hosted service. The
existing notifier in `TelegramBotService` keeps running; the new file
is on disk but no DI path wakes it up.

- New `Services/JobNotifierService.cs : BackgroundService`:
  - Constructor takes `IEnumerable<IChatProvider> providers`,
    `JobTracker jobs`, `TaskExecutor executor`,
    `ChatResultDispatcher dispatcher`,
    `IOptions<TelegramOptions> options`,
    `ILogger<JobNotifierService> logger`.
  - `ExecuteAsync` builds
    `_providersByName = providers.ToDictionary(p => p.Name)` once,
    then runs the existing poll loop verbatim. `JobPollSeconds <= 0`
    behavior preserved.
  - `PollJobsOnceAsync` walks `_jobs.List(50)`, gates by
    `job.ChatId is { } chatRef`, looks up
    `_providersByName.TryGetValue(chatRef.Provider, out var provider)`.
    Missing-provider jobs get one warning (deduplicated via a
    `HashSet<string>` of providers we've already complained about)
    and are skipped on subsequent ticks too.
  - `PushNewArtifactsAsync(IChatProvider provider, ChatId chat, JobRecord job, ct)`
    and `PushCompletionAsync(IChatProvider provider, ChatId chat, JobRecord job, ct)`
    are direct ports of the existing methods, with `long chatId`
    replaced by `(IChatProvider, ChatId)` and the
    `long.TryParse(chatRef.Id, …)` guard removed.
  - The completion summary's `bot.SendMessage(chatId, summary, …, Html)`
    becomes `provider.SendHtmlAsync(chat, summary, ct)`. The
    artifact-bundle send becomes
    `_dispatcher.DispatchAsync(provider, chat, bundle, ct)`.
- No DI registration yet. `TelegramBotService.RunJobNotifierLoopAsync`
  still runs.

Build green. Suite green (no new tests; the new file is unreached).

### 2c.3 — Cut over

One commit: switch hosted-service registration, delete the old loop,
done.

- `Program.cs`: register `JobNotifierService` as a hosted service.
- `TelegramBotService`:
  - Delete `RunJobNotifierLoopAsync`, `PollJobsOnceAsync`,
    `PushNewArtifactsAsync`, `PushCompletionAsync`.
  - Replace `await RunJobNotifierLoopAsync(stoppingToken)` in
    `ExecuteAsync` with `await Task.Delay(Timeout.Infinite, stoppingToken)`
    (the host stays alive while messages route on the provider's
    callback thread).
- Constructor loses `JobTracker _jobs` and `TaskExecutor _executor`
  if they're now only referenced by message handlers — verify with a
  build, keep what `OnIncomingAsync` needs (matcher/`/jobs`/`/job N`
  command handlers still use both).

Behavior identical: the notifier just moved. Single Telegram-only
deployment can't tell the difference; multi-provider future is now
unblocked because the loop iterates `IEnumerable<IChatProvider>`.

### Notes on what 2c deliberately defers

- **`JobPollSeconds` config rename.** New service still reads
  `IOptions<TelegramOptions>.JobPollSeconds`. The move to
  `Chat:JobPollSeconds` (with one release of legacy fallback) belongs
  to step 2d's per-provider config rework — bundling it here would
  mean shipping a config-migration commit alongside a code
  refactor.
- **Startup health-check DM.** `CheckOllamaHealthAndNotifyAsync`
  stays in `TelegramBotService` for 2c. After 2b.6 it already uses
  `_provider.DefaultRecipient`, so it's provider-agnostic in spirit;
  the actual relocation (probably to a `ChatHost` or its own hosted
  service) happens in step 2d when `TelegramBotService` becomes
  `ChatHost`.
- **Notifier test coverage.** The notifier loop has zero automated
  tests today — behavior is verified manually in a real Telegram
  chat. Adding `JobNotifierServiceTests` (two fake providers, seeded
  `JobRecord`s, assert the right provider gets the artifact pushes
  and unknown-provider jobs get one warning then quiet) is genuinely
  useful but is its own ~150-line commit. Schedule as 2c.4 if you
  want the regression net before step 3 lands Discord, or skip and
  rely on the multi-provider integration test that step 3 brings.
- **Per-provider opt-out for noisy jobs.** IDEAS.md's
  "`/notify off N`" idea slots cleanly into the new service (one
  bool on `JobRecord`, one gate in `PushNewArtifactsAsync`). Out of
  scope for 2c — it's a feature, not part of the multi-provider
  refactor.

## Step 2d — Per-provider config + rename `TelegramBotService` → `ChatHost`

Goal: tidy the boundary so phase 3 (Discord) is a drop-in. Target shape
for `appsettings.json`:

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

The legacy `Telegram:*` path keeps working for one release; the bot
logs a deprecation hint at startup if it's the source of any value.

Six small commits. Same shape as 2b and 2c: incremental, each
buildable + green on its own. The big move (MessageRouter
extraction) is mechanical because the routing methods are already
self-contained after 2b/2c.

### 2d.1 — `ChatOptions` for cross-provider settings

Smallest of the config commits. Just the two non-provider-specific
fields (`JobPollSeconds`, `StartupNotificationsEnabled`) — the
per-provider section comes next.

- New `Configuration/ChatOptions.cs` POCO with the two fields and
  default values matching today's `TelegramOptions` defaults
  (`JobPollSeconds=30`, `StartupNotificationsEnabled=true`).
- `Program.cs` binds `Chat:` to `ChatOptions`.
- New `Configuration/ChatOptionsDefaults.cs : IConfigureOptions<ChatOptions>`
  reads `IOptions<TelegramOptions>` and fills in `ChatOptions` from
  `TelegramOptions` whenever the `Chat:` value is the default sentinel
  (i.e. the user didn't set it). Logs a one-shot deprecation hint
  per field when the legacy key was the source.
- Switch consumers:
  - `JobNotifierService` (from 2c) reads `IOptions<ChatOptions>.JobPollSeconds`.
  - `TelegramBotService.SendStartupNotificationAsync` reads
    `IOptions<ChatOptions>.StartupNotificationsEnabled`.
- `TelegramOptions.JobPollSeconds` and
  `TelegramOptions.StartupNotificationsEnabled` stay on the POCO so
  the legacy-fallback binder can read them; nobody else does.

### 2d.2 — `Chat:Providers:Telegram:*` for per-provider config

Parallel structure to 2d.1 but for the provider-shaped fields.

- New `Configuration/TelegramProviderOptions.cs` POCO:
  `Token`, `AllowedUserIds`, `AllowedChatIds`.
- `Program.cs` binds `Chat:Providers:Telegram:` to
  `TelegramProviderOptions`.
- `IConfigureOptions<TelegramProviderOptions>` falls back to the
  matching fields on `TelegramOptions` when `Chat:Providers:Telegram:*`
  isn't set; same one-shot deprecation hint pattern.
- `TelegramChatProvider` switches its constructor from
  `IOptions<TelegramOptions>` to `IOptions<TelegramProviderOptions>`.
- `TelegramBotService.IsAuthorized` (host's inline check, still
  present until 2d.3) keeps reading `IOptions<TelegramOptions>` —
  changing it now would tangle with the delegation step.

### 2d.3 — Allow-list delegation

Tiny commit. Removes the host's duplicate of the allow-list check.

- `TelegramBotService.OnIncomingAsync`: replace
  `if (!IsAuthorized(userId, chatId))` with
  `if (!_provider.IsAuthorized(msg))`.
- Delete the host's private `IsAuthorized(long, long)` method.
- `TelegramOptions` no longer has any direct readers (only the
  legacy-fallback binders touch it via `IOptions<TelegramOptions>`).

Behavior diff is genuinely zero — both implementations have
identical logic (default-deny when allow-list empty + per-message
warning, allow-list match by user id or chat id). Verified by
diffing the two methods during the commit.

### 2d.4 — Extract `MessageRouter`

The big move, but mechanical because the routing methods are
already self-contained after 2b (everything talks to `_provider`,
not `_bot`). Roughly 700 lines lift from `TelegramBotService.cs` to
a new file.

- New `Services/MessageRouter.cs` taking
  `IChatProvider provider`, `TaskRegistry registry`,
  `TaskMatcher matcher`, `TaskExecutor executor`,
  `OllamaClient ollama`, `ChatResultDispatcher dispatcher`,
  `JobTracker jobs`, `ConversationStateTracker conversation`,
  `ILogger<MessageRouter> logger`.
- Move the methods: `OnIncomingAsync`, `RouteCommandAsync` and all
  the per-command helpers (`SendJobsListAsync`,
  `SendLatestJobStatusAsync`, `SendJobStatusAsync`,
  `SendResultsAsync`, `ContinueParameterCollectionAsync`,
  `RenderDryRun`, `BuildHelp`, `BuildTaskList`, `FormatJobExit`,
  `FormatElapsed`, `HtmlEscape`, `TrySendAsync`).
- `TelegramBotService` shrinks to lifecycle:
  - Constructor takes `IChatProvider provider`, `MessageRouter router`,
    `IOptions<ChatOptions> chatOptions`, `OllamaClient ollama`,
    `ILogger<TelegramBotService> logger`.
  - `ExecuteAsync`: `await provider.StartAsync(ct);
    provider.OnMessage += router.HandleAsync;
    await CheckOllamaHealthAndNotifyAsync(ct);
    await Task.Delay(Timeout.Infinite, ct);`
  - Keeps `CheckOllamaHealthAndNotifyAsync` and
    `SendStartupNotificationAsync` — those are lifecycle, not
    routing.
- DI: register `MessageRouter` as singleton.

Behavior identical. Because the router takes a single
`IChatProvider`, multi-provider routing is still a step-3 concern —
this commit just creates the seam.

### 2d.5 — `MessageRouterTests` with `FakeChatProvider`

Adds the regression net that's been missing since the start.

- New `tests/TeleTasks.Tests/FakeChatProvider.cs` implementing
  `IChatProvider`: captures every `SendTextAsync`, `SendHtmlAsync`,
  `SendImageAsync`, `SendDocumentAsync`, `SendTypingAsync` call into
  in-memory lists; raises `OnMessage` programmatically via a
  `RaiseAsync(IncomingMessage)` helper.
- New `tests/TeleTasks.Tests/MessageRouterTests.cs` covering the
  paths most likely to regress during refactoring:
  - Slash commands round-trip: `/help`, `/tasks`, `/whoami`,
    `/cancel`, `/jobs`, `/job N`, `/stop N`, `/dry`, `/results`,
    `/reload`. Each asserts the right `Send*Async` was called with
    a substring of the expected response.
  - Authorization: unauthorized message → `SendTextAsync("Not authorized.")`
    and no further state mutation.
  - Conversational params: matcher returns task with missing
    required string → `Begin` lands in tracker, next user message
    becomes the value, second message starts the run.
  - Slash command during pending collection clears state.
  - Exact-task-name fast path: typing a task name skips the matcher
    (no `OllamaClient` call) and goes straight to param collection.
- Suite grows ~10–15 tests.

### 2d.6 — Rename `TelegramBotService` → `ChatHost`

Pure rename. After 2d.4 the class no longer mentions Telegram inside
its body, so the name change just makes the file name match what's
in it.

- `git mv src/TeleTasks/Services/TelegramBotService.cs src/TeleTasks/Services/ChatHost.cs`.
- Class name `TelegramBotService` → `ChatHost`.
- Update DI registration in `Program.cs`.
- Update logger generic param `ILogger<TelegramBotService>` →
  `ILogger<ChatHost>`.
- Update the test in `tests/` if any reference the type name (none
  today — message-routing tests added in 2d.5 target `MessageRouter`).

Build green. Suite green. After this, `grep -ri "TelegramBotService"
src/ tests/` returns nothing.

### Notes on what 2d deliberately defers

- **Removing `TelegramOptions` entirely.** Stays through one
  release as the source of legacy-fallback values. After step 3
  (Discord) lands and the deprecation period closes, a follow-up
  commit can delete `TelegramOptions.cs` and the
  `IConfigureOptions<*Defaults>` binders. Schedule the removal as a
  reminder in `IDEAS.md` rather than a blocker here.
- **Multi-provider router routing.** `MessageRouter` takes one
  `IChatProvider` after 2d.4. With Discord (step 3), DI registers
  one router per provider — each provider's `OnMessage` subscribes
  its own router instance, scoped by constructor. The alternative
  (one router taking `IEnumerable<IChatProvider>` and dispatching by
  `IncomingMessage.Chat.Provider`) is a small refactor that lands
  with step 3 if needed; today's design is the minimum that ships.
- **Config-migration integration test.** A test that loads a sample
  legacy-shape `appsettings.json` and asserts the deprecation log
  fires once is genuinely useful, but `IConfiguration` + custom
  `IConfigureOptions` setup is fiddly to mock. Defer until after
  step 3, when the multi-provider config story has settled and the
  test exercises the full bind path including Discord.
- **HTML escaping for non-Telegram providers.** `MessageRouter`
  emits Telegram-flavoured HTML (`<b>`, `<i>`, `<code>`, `<pre>`).
  Discord (step 3) brings the `HtmlToDiscordMarkdown` translator
  with its own test corpus; doing the translator in 2d would mean
  building it without a consumer.

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
