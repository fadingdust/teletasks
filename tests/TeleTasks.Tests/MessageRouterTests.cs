using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleTasks.Configuration;
using TeleTasks.Models;
using TeleTasks.Services;
using TeleTasks.Services.Chat;
using Xunit;

namespace TeleTasks.Tests;

[Collection("EnvironmentMutating")]
public sealed class MessageRouterTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _savedEnv;
    private readonly FakeChatProvider _chat = new();

    public MessageRouterTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "teletasks-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _savedEnv = Environment.GetEnvironmentVariable(UserConfigDirectory.EnvVar);
        Environment.SetEnvironmentVariable(UserConfigDirectory.EnvVar, _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UserConfigDirectory.EnvVar, _savedEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private static readonly ChatId TestChat = ChatId.FromTelegram(42);

    private MessageRouter BuildRouter(string? tasksJson = null)
    {
        var catalogPath = Path.Combine(_configDir, "tasks.json");
        File.WriteAllText(catalogPath, tasksJson ?? "{\"tasks\":[]}");

        var catalogOptions = Options.Create(new TaskCatalogOptions { Path = catalogPath });
        var ollamaOptions = Options.Create(new OllamaOptions { Endpoint = "http://127.0.0.1:1", Model = "none" });

        var registry = new TaskRegistry(catalogOptions, new TestEnv(_configDir), NullLogger<TaskRegistry>.Instance);
        registry.Load();

        var jobTracker = new JobTracker(NullLogger<JobTracker>.Instance, Options.Create(new ChatOptions()));
        var outputCollector = new OutputCollector(NullLogger<OutputCollector>.Instance);
        var executor = new TaskExecutor(catalogOptions, outputCollector, jobTracker, NullLogger<TaskExecutor>.Instance);
        var ollamaClient = new OllamaClient(new NullHttpClientFactory(), ollamaOptions, NullLogger<OllamaClient>.Instance);
        var matcher = new TaskMatcher(ollamaClient, registry, NullLogger<TaskMatcher>.Instance);
        var conversation = new ConversationStateTracker();
        var dispatcher = new ChatResultDispatcher();

        return new MessageRouter(
            _chat, registry, matcher, executor,
            dispatcher, jobTracker, conversation,
            NullLogger<MessageRouter>.Instance);
    }

    // ─── Slash commands ────────────────────────────────────────────────

    [Fact]
    public async Task Help_returns_commands_list()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/help"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Commands:", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Start_is_alias_for_help()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/start"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Commands:", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Tasks_with_empty_registry_reports_no_tasks()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));
        Assert.Single(_chat.SentHtmls);
        Assert.Contains("No tasks configured", _chat.SentHtmls[0].Html);
    }

    [Fact]
    public async Task Tasks_lists_configured_task_names()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"ping","command":"/bin/echo","args":["pong"]}]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));
        Assert.Single(_chat.SentHtmls);
        Assert.Contains("ping", _chat.SentHtmls[0].Html);
    }

    [Fact]
    public async Task Whoami_returns_chat_id()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/whoami"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("42", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Cancel_with_nothing_pending_acknowledges()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/cancel"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Nothing pending", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Jobs_with_no_jobs_returns_empty_message()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/jobs"));
        Assert.Single(_chat.SentHtmls);
        Assert.Contains("No jobs yet", _chat.SentHtmls[0].Html);
    }

    [Fact]
    public async Task Job_N_for_unknown_id_reports_not_found()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/job 99"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("No job with id 99", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Stop_N_for_unknown_id_reports_not_found()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/stop 99"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("No job with id 99", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Restart_without_arg_returns_usage()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/restart"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Usage", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Restart_unknown_id_reports_not_found()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/restart 99"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("No job with id 99", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Results_without_task_name_prompts_for_task_name()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/results"));
        Assert.Single(_chat.SentHtmls);
        Assert.Contains("Which task", _chat.SentHtmls[0].Html);
    }

    [Fact]
    public async Task Results_prompt_attaches_buttons_for_non_text_output_tasks()
    {
        var router = BuildRouter("""
            {"tasks":[
              {"name":"ping","command":"/bin/echo"},
              {"name":"snap","command":"/bin/snap","output":{"type":"Image","path":"/tmp/x.png"}},
              {"name":"render","command":"/bin/render","output":{"type":"Images","directory":"/tmp"}}
            ]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/results"));

        Assert.Single(_chat.SentHtmlsWithKeyboard);
        var keyboard = _chat.SentHtmlsWithKeyboard[0].Keyboard;
        Assert.NotNull(keyboard);
        // Only snap and render — ping has Text output, so it's excluded.
        Assert.Equal(2, keyboard.Count);
        Assert.Equal("snap",            keyboard[0][0].Label);
        Assert.Equal("/results snap",   keyboard[0][0].CallbackData);
        Assert.Equal("render",          keyboard[1][0].Label);
        Assert.Equal("/results render", keyboard[1][0].CallbackData);
    }

    [Fact]
    public async Task Results_prompt_with_only_text_tasks_attaches_no_keyboard()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"ping","command":"/bin/echo"}]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/results"));
        Assert.Single(_chat.SentHtmlsWithKeyboard);
        Assert.Null(_chat.SentHtmlsWithKeyboard[0].Keyboard);
    }

    [Fact]
    public async Task Results_followup_with_task_name_evaluates_that_task()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"ping","command":"/bin/echo"}]}
            """);

        await router.HandleAsync(FakeChatProvider.Msg(42, "/results"));
        _chat.SentTexts.Clear();
        _chat.SentHtmls.Clear();

        // Reply with the task name — the router resolves the pending Show intent.
        await router.HandleAsync(FakeChatProvider.Msg(42, "ping"));

        // ping has Text output, so SendResultsAsync explains there's no cached state.
        // Either way, the bot must mention "ping" — which means the followup landed.
        var corpus = string.Join(" ",
            _chat.SentTexts.Select(m => m.Text).Concat(_chat.SentHtmls.Select(m => m.Html)));
        Assert.Contains("ping", corpus);
    }

    [Fact]
    public async Task Results_followup_cancelled_via_slash_cancel()
    {
        var router = BuildRouter();

        await router.HandleAsync(FakeChatProvider.Msg(42, "/results"));
        _chat.SentTexts.Clear();
        _chat.SentHtmls.Clear();

        await router.HandleAsync(FakeChatProvider.Msg(42, "/cancel"));

        Assert.Single(_chat.SentTexts);
        Assert.Contains("Cancelled", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Results_for_unknown_task_reports_not_found()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/results nosuch"));
        Assert.Single(_chat.SentHtmls);
        Assert.Contains("nosuch", _chat.SentHtmls[0].Html);
    }

    [Fact]
    public async Task Unknown_command_suggests_help()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/florp"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Unknown command", _chat.SentTexts[0].Text);
    }

    // ─── Authorization ─────────────────────────────────────────────────

    [Fact]
    public async Task Unauthorized_message_returns_not_authorized()
    {
        var router = BuildRouter();
        _chat.DenyAll();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/help"));
        Assert.Single(_chat.SentTexts);
        Assert.Equal("Not authorized.", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Authorized_user_passes_through()
    {
        var router = BuildRouter();
        _chat.Allow("7");
        await router.HandleAsync(FakeChatProvider.Msg(42, "/help", userId: "7"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Commands:", _chat.SentTexts[0].Text);
    }

    // ─── Exact task-name fast path + parameter collection ──────────────

    [Fact]
    public async Task Exact_task_name_with_required_param_prompts_for_value()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"say","command":"/bin/echo","parameters":[{"name":"msg","type":"string","required":true}]}]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "say"));

        // Should have sent a typing indicator + a "needs more values" html + a prompt
        Assert.NotEmpty(_chat.SentHtmls);
        var allHtml = string.Join(" ", _chat.SentHtmls.Select(m => m.Html));
        Assert.Contains("say", allHtml);
        Assert.Contains("needs", allHtml);
    }

    [Fact]
    public async Task Parameter_collection_completes_and_runs_task()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"say","command":"/bin/echo","parameters":[{"name":"msg","type":"string","required":true}]}]}
            """);

        // Step 1: type task name - enters param collection
        await router.HandleAsync(FakeChatProvider.Msg(42, "say"));
        _chat.SentTexts.Clear();
        _chat.SentHtmls.Clear();

        // Step 2: provide the parameter value - should trigger execution
        await router.HandleAsync(FakeChatProvider.Msg(42, "hello world"));

        // Should have sent a "Running" html message
        Assert.NotEmpty(_chat.SentHtmls);
        Assert.Contains(_chat.SentHtmls, m => m.Html.Contains("Running"));
    }

    [Fact]
    public async Task Slash_command_during_param_collection_cancels_and_processes_command()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"say","command":"/bin/echo","parameters":[{"name":"msg","type":"string","required":true}]}]}
            """);

        // Start collection
        await router.HandleAsync(FakeChatProvider.Msg(42, "say"));
        _chat.SentTexts.Clear();
        _chat.SentHtmls.Clear();

        // Send a slash command - should cancel collection and reply "Nothing pending."
        await router.HandleAsync(FakeChatProvider.Msg(42, "/cancel"));

        Assert.NotEmpty(_chat.SentTexts);
        // First text is the cancellation confirmation from the inline cancel
        Assert.Contains(_chat.SentTexts, m => m.Text.Contains("Cancelled") || m.Text.Contains("Nothing pending"));
    }

    [Fact]
    public async Task Reload_reloads_registry_and_reports_count()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"ping","command":"/bin/echo"}]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/reload"));
        Assert.Single(_chat.SentTexts);
        Assert.Contains("Reloaded 1 task(s)", _chat.SentTexts[0].Text);
    }

    [Fact]
    public async Task Empty_message_is_silently_dropped()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, ""));
        Assert.Empty(_chat.SentTexts);
        Assert.Empty(_chat.SentHtmls);
    }

    // ─── Inline keyboard buttons ───────────────────────────────────────

    [Fact]
    public async Task Jobs_with_no_jobs_sends_no_keyboard()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/jobs"));
        Assert.Single(_chat.SentHtmlsWithKeyboard);
        Assert.Null(_chat.SentHtmlsWithKeyboard[0].Keyboard);
    }

    [Fact]
    public async Task Job_status_for_unknown_id_sends_no_keyboard()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/job 99"));
        // Falls back to SendTextAsync — no html at all.
        Assert.Empty(_chat.SentHtmlsWithKeyboard);
    }

    [Fact]
    public async Task Tasks_attaches_one_button_per_task_with_callback_equal_to_task_name()
    {
        var router = BuildRouter("""
            {"tasks":[
              {"name":"ping","command":"/bin/echo"},
              {"name":"render","command":"/bin/render","longRunning":true}
            ]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));

        Assert.Single(_chat.SentHtmlsWithKeyboard);
        var keyboard = _chat.SentHtmlsWithKeyboard[0].Keyboard;
        Assert.NotNull(keyboard);
        Assert.Equal(2, keyboard.Count);
        Assert.Equal("ping",   keyboard[0][0].Label);
        Assert.Equal("ping",   keyboard[0][0].CallbackData);
        Assert.Equal("render", keyboard[1][0].Label);
        Assert.Equal("render", keyboard[1][0].CallbackData);
    }

    [Fact]
    public async Task Tasks_with_empty_registry_attaches_no_keyboard()
    {
        var router = BuildRouter();
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));
        Assert.Single(_chat.SentHtmlsWithKeyboard);
        Assert.Null(_chat.SentHtmlsWithKeyboard[0].Keyboard);
    }

    [Fact]
    public async Task Tasks_renders_intent_badges_per_row()
    {
        var router = BuildRouter("""
            {"tasks":[
              {"name":"ping","command":"/bin/echo"},
              {"name":"render","command":"/bin/render","longRunning":true,
                "output":{"type":"Images","directory":"/tmp"}}
            ]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));
        Assert.Single(_chat.SentHtmls);
        var html = _chat.SentHtmls[0].Html;

        Assert.Contains("<code>ping</code> [Run]", html);
        Assert.Contains("<code>render</code> [Run · Show · Status · Stop · Restart]", html);
    }

    [Fact]
    public async Task Tasks_converts_backticks_in_descriptions_to_code_tags()
    {
        var router = BuildRouter("""
            {"tasks":[{"name":"sh_run","command":"/bin/bash","args":["run.sh"],
                       "description":"Run `run.sh`."}]}
            """);
        await router.HandleAsync(FakeChatProvider.Msg(42, "/tasks"));
        Assert.Single(_chat.SentHtmls);
        var html = _chat.SentHtmls[0].Html;
        Assert.Contains("Run <code>run.sh</code>.", html);
        Assert.DoesNotContain("`run.sh`", html);
    }

    [Fact]
    public void FormatDescription_escapes_html_before_converting_backticks()
    {
        // <foo> inside backticks must end up as &lt;foo&gt; inside <code>,
        // not as literal HTML tags that Telegram would try to parse.
        var actual = MessageRouter.FormatDescription("see `<foo>` for more");
        Assert.Equal("see <code>&lt;foo&gt;</code> for more", actual);
    }

    [Fact]
    public void FormatDescription_passes_text_without_backticks_through_escaped()
    {
        Assert.Equal("a &amp; b &lt; c",
            MessageRouter.FormatDescription("a & b < c"));
    }

    [Fact]
    public void IntentsFor_text_output_short_running_is_Run_only()
    {
        var task = new TaskDefinition { Name = "ping" };
        Assert.Equal(new[] { TaskIntent.Run }, MessageRouter.IntentsFor(task));
    }

    [Fact]
    public void IntentsFor_image_output_short_running_adds_Show()
    {
        var task = new TaskDefinition { Name = "snap" };
        task.Output.Type = TaskOutputType.Image;
        Assert.Equal(new[] { TaskIntent.Run, TaskIntent.Show }, MessageRouter.IntentsFor(task));
    }

    [Fact]
    public void IntentsFor_long_running_adds_Status_Stop_Restart()
    {
        var task = new TaskDefinition { Name = "render", LongRunning = true };
        task.Output.Type = TaskOutputType.Images;
        Assert.Equal(
            new[] { TaskIntent.Run, TaskIntent.Show, TaskIntent.Status, TaskIntent.Stop, TaskIntent.Restart },
            MessageRouter.IntentsFor(task));
    }

    // ─── TaskMatcher static helpers ────────────────────────────────────

    [Theory]
    [InlineData("_show_tasks",       true)]
    [InlineData("_show_help",        true)]
    [InlineData("_show_results",     true)]
    [InlineData("_show_jobs",        true)]
    [InlineData("_check_latest_job", true)]
    [InlineData("render",            false)]
    [InlineData("",                  false)]
    [InlineData(null,                false)]
    public void IsVirtualRoute_identifies_virtual_routes(string? name, bool expected)
    {
        Assert.Equal(expected, TaskMatcher.IsVirtualRoute(name));
    }

    // ─── Private test helpers ──────────────────────────────────────────

    private sealed class TestEnv : IHostEnvironment
    {
        public TestEnv(string contentRootPath) => ContentRootPath = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "TeleTasks.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
