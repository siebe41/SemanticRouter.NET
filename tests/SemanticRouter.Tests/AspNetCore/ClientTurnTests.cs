using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Agentic clients' own requests (#47): titles, topic checks, compaction, warm-ups and command checks go to tier 1
/// without classification; a session resumed from a compaction summary goes to the agentic tier.
/// </summary>
/// <remarks>
/// The bodies are reconstructions of what Claude Code and Codex send (their wording, trimmed), not captured traffic.
/// </remarks>
public sealed class ClientTurnTests : RouterHostTest
{
    private const string ClaudeCodeSystem = "You are Claude Code, Anthropic's official CLI for Claude.";

    private Task<HttpClient> StartDispatchAsync(Action<PromptRouterMiddlewareOptions>? configure = null) =>
        StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
            configure?.Invoke(o);
        });

    private static StringContent Anthropic(string? system, params (string Role, string Text)[] messages) => new(
        JsonSerializer.Serialize(new
        {
            model = "claude-sonnet-4-5",
            max_tokens = 512,
            system,
            messages = messages.Select(m => new { role = m.Role, content = new object[] { new { type = "text", text = m.Text } } }),
        }),
        Encoding.UTF8,
        "application/json");

    private static async Task<(string Tier, string Reason)> RouteAsync(HttpClient client, string path, HttpContent body)
    {
        HttpResponseMessage response = await client.PostAsync(path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (response.Headers.GetValues("X-Prompt-Route-Tier").Single(), response.Headers.GetValues("X-Prompt-Route-Reason").Single());
    }

    public static TheoryData<string, string?, string> ClaudeCodeSideRequests => new()
    {
        {
            "title",
            ClaudeCodeSystem,
            "Please write a 5-10 word title for the following conversation:\n\nUser: the login test is flaky on CI\nClaude: I'll look at the retry logic.\n\nRespond with the title for the conversation and nothing else."
        },
        {
            "session title (2.1.300)",
            null,
            "<session>\nUser: the login test is flaky on CI\nClaude: I'll look at the retry logic.\n</session>\n\nWrite the title in the predominant language of the session — not the language of these instructions. Reply with the title only."
        },
        {
            "short title",
            "Summarize this coding conversation in under 50 characters.\nCapture the main task, key files, problems addressed, and current status.",
            "the login test is flaky on CI, can you look at the retry logic"
        },
        {
            "topic",
            "Analyze if this message indicates a new conversation topic. If it does, extract a 2-3 word title that captures the new topic. Format your response as a JSON object with two fields: 'isNewTopic' (boolean) and 'title' (string, or null if isNewTopic is false). Only include these fields, no other text.",
            "now let's move on to the billing export"
        },
        {
            "warm-up",
            ClaudeCodeSystem,
            "Warmup"
        },
        {
            "command check",
            ClaudeCodeSystem,
            "<policy_spec>\n# Claude Code Code Bash command prefix detection\n\nThis document defines risk levels for actions that the Claude Code agent may take.\n</policy_spec>\n\nThe user has allowed certain command prefixes to be run, and will otherwise be asked to approve or deny the command.\nYour task is to determine the command prefix for the following command.\n\nCommand: git status --short"
        },
    };

    [Theory]
    [MemberData(nameof(ClaudeCodeSideRequests))]
    public async Task Claude_code_side_requests_go_to_tier_1_without_classification(string kind, string? system, string prompt)
    {
        HttpClient client = await StartDispatchAsync();
        ClassifyAs(tier: 4, route: "agentic-coding");

        (string tier, string reason) = await RouteAsync(client, "/v1/messages", Anthropic(system, ("user", prompt)));

        (tier, reason).Should().Be(("1", "ClientHousekeeping"), kind);
        Classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never(), "no embedding call for a side request");
    }

    [Fact]
    public async Task Compaction_in_the_middle_of_a_hard_task_is_not_held_at_the_tasks_tier()
    {
        HttpClient client = await StartDispatchAsync();
        ClassifyAs(tier: 4, route: "agentic-coding");
        (string User, string Assistant)[] history = [("refactor the payment module and update every caller", "Done: 14 files changed.")];
        await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", history[0].User)));
        (string tier, _) = await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", history[0].User), ("assistant", history[0].Assistant), ("user", "ok")));
        tier.Should().Be("4", "a short follow-up keeps the task's tier");

        (string compactTier, string compactReason) = await RouteAsync(client, "/v1/messages", Anthropic(
            ClaudeCodeSystem,
            ("user", history[0].User),
            ("assistant", history[0].Assistant),
            ("user", "Your task is to create a detailed summary of the conversation so far, paying close attention to the user's explicit requests and your previous actions.")));

        (compactTier, compactReason).Should().Be(("1", "ClientHousekeeping"));
    }

    [Fact]
    public async Task A_session_title_request_is_not_a_turn_of_the_conversation()
    {
        HttpClient client = await StartDispatchAsync(o => o.Conversation.DecayTurns = 1);
        ClassifyAs(tier: 4, route: "agentic-coding");
        const string Task = "refactor the payment module and update every caller";
        const string Title = "<session>\nUser: refactor the payment module and update every caller\n</session>\n\nWrite the title in the predominant language of the session — not the language of these instructions.";
        async Task<(string Tier, string Reason)> SendAsync(HttpContent body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = body };
            request.Headers.Add("x-claude-code-session-id", "session-1");
            HttpResponseMessage response = await client.SendAsync(request);
            return (response.Headers.GetValues("X-Prompt-Route-Tier").Single(), response.Headers.GetValues("X-Prompt-Route-Reason").Single());
        }

        await SendAsync(Anthropic(ClaudeCodeSystem, ("user", Task)));
        (string Tier, string Reason) title = await SendAsync(Anthropic(null, ("user", Title)));
        ClassifyAs(tier: 1, route: "smalltalk");
        (string Tier, string Reason) next = await SendAsync(Anthropic(ClaudeCodeSystem, ("user", Task), ("assistant", "Done."), ("user", "now write a short note for the changelog about what changed in the module")));

        title.Should().Be(("1", "ClientHousekeeping"));
        next.Should().Be(("4", "ConversationFloor"), "the title request did not count as a simpler turn, so the floor has not decayed");
    }

    [Fact]
    public async Task Codex_compaction_through_the_responses_door_is_recognised()
    {
        HttpClient client = await StartDispatchAsync(o => o.ResponsesPathPrefixes.Add("/v1/responses"));
        ClassifyAs(tier: 4, route: "agentic-coding");
        var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = "gpt-5-codex",
                instructions = "You are Codex, a coding agent.",
                input = "You are performing a CONTEXT CHECKPOINT COMPACTION. Create a handoff summary for another LLM that will resume the task.",
            }),
            Encoding.UTF8,
            "application/json");

        (string tier, string reason) = await RouteAsync(client, "/v1/responses", body);

        (tier, reason).Should().Be(("1", "ClientHousekeeping"));
    }

    [Fact]
    public async Task A_session_resumed_from_a_summary_goes_to_the_agentic_tier_and_holds_it()
    {
        HttpClient client = await StartDispatchAsync();
        ClassifyAs(tier: 1, route: "smalltalk");
        const string Summary = "This session is being continued from a previous conversation that ran out of context. The conversation is summarized below:\nAnalysis: the user asked to migrate the scheduler to the new queue.";

        (string tier, string reason) = await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", Summary), ("user", "Please continue the conversation from where we left it off.")));
        (string next, string nextReason) = await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", Summary), ("assistant", "Continuing with the queue migration."), ("user", "ok")));

        (tier, reason).Should().Be(("4", "ResumedSession"));
        (next, nextReason).Should().Be(("4", "ConversationFloor"), "the resumed session's tier is the conversation's floor");
    }

    [Fact]
    public async Task Ordinary_requests_for_a_title_or_a_summary_are_classified_as_usual()
    {
        HttpClient client = await StartDispatchAsync();
        ClassifyAs(tier: 2, route: "rewrite-summarize");

        (string, string) blog = await RouteAsync(client, "/v1/messages", Anthropic(null, ("user", "write a title for my blog post about Rust lifetimes")));
        (string, string) article = await RouteAsync(client, "/v1/messages", Anthropic(null, ("user", "summarize this article in three bullet points: ...")));
        (string, string) mention = await RouteAsync(client, "/v1/messages", Anthropic(null, ("user", "how should I warmup the cache before the load test?")));

        new[] { blog, article, mention }.Should().AllBeEquivalentTo(("2", "SemanticMatch"));
    }

    [Fact]
    public async Task Other_clients_wording_can_be_added_and_the_switch_turns_it_off()
    {
        HttpClient client = await StartDispatchAsync(o => o.Housekeeping.Patterns.Add(@"^generate a commit message for the staged diff"));
        ClassifyAs(tier: 3, route: null, reason: RoutingReason.NoMatchFallback);

        (await RouteAsync(client, "/v1/messages", Anthropic(null, ("user", "Generate a commit message for the staged diff:\n+ fix")))).Should().Be(("1", "ClientHousekeeping"));

        await DisposeAsync();
        client = await StartDispatchAsync(o => o.Housekeeping.Enabled = false);
        ClassifyAs(tier: 3, route: null, reason: RoutingReason.NoMatchFallback);
        (await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", "Warmup")))).Should().Be(("3", "NoMatchFallback"));
    }

    [Fact]
    public void The_built_in_patterns_are_a_data_file_with_one_entry_per_wording()
    {
        ClientTurnTable table = ClientTurnTable.BuiltIn;

        table.Source.Should().Be("built-in v1");
        table.Warning.Should().BeNull();
        table.Patterns.Select(p => p.Entry.Id).Should().OnlyHaveUniqueItems().And.Contain(["warmup", "claude-code/resumed", "claude-code/title", "claude-code/session-title", "codex/compaction", "claude-code/command-check"]);
        table.Patterns.Select(p => p.Entry.Kind).Take(2).Should().Equal([ClientTurnKind.Warmup, ClientTurnKind.Resumed], "a warm-up and a resumed session are tried first");
    }

    [Fact]
    public async Task A_file_next_to_the_configuration_replaces_adds_and_turns_off_patterns()
    {
        string directory = Directory.CreateTempSubdirectory("sr-turns-").FullName;
        try
        {
            string configuration = Path.Combine(directory, "semantic-router.json");
            new JsonFileRouterConfigurationStore(configuration).Save(new RouterConfiguration());
            await File.WriteAllTextAsync(ClientTurnTable.FileNextTo(configuration), """
                {
                  // Claude Code renamed its title request; a new client; no command checks.
                  "turns": [
                    { "id": "claude-code/title", "client": "Claude Code", "kind": "Title", "pattern": "^name this session in \\d+ words" },
                    { "id": "aider/commit", "client": "Aider", "kind": "Summary", "pattern": "^write a one-line commit message" },
                    { "id": "claude-code/command-check", "enabled": false },
                  ]
                }
                """);
            HttpClient client = await StartAsync(
                o =>
                {
                    o.Mode = PromptRouterMode.Dispatch;
                    o.PathPrefixes.Add("/v1/chat/completions");
                },
                services: s => s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(configuration)));
            ClassifyAs(tier: 3, route: null, reason: RoutingReason.NoMatchFallback);

            (await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", "Name this session in 4 words: the login test is flaky"))))
                .Should().Be(("1", "ClientHousekeeping"), "the replaced pattern applies");
            (await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", "Please write a 5-10 word title for the following conversation: …"))))
                .Should().Be(("3", "NoMatchFallback"), "the replaced built-in wording no longer applies");
            (await RouteAsync(client, "/v1/messages", Anthropic(null, ("user", "Write a one-line commit message for this diff"))))
                .Should().Be(("1", "ClientHousekeeping"), "an added pattern applies");
            (await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", "<policy_spec>\n…</policy_spec>\nCommand: git status"))))
                .Should().Be(("3", "NoMatchFallback"), "a turned-off pattern does not apply");
            (await RouteAsync(client, "/v1/messages", Anthropic(ClaudeCodeSystem, ("user", "Warmup"))))
                .Should().Be(("1", "ClientHousekeeping"), "the other built-in patterns stay");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_edited_file_applies_without_a_restart()
    {
        string file = Path.Combine(Directory.CreateTempSubdirectory("sr-turns-").FullName, ClientTurnTable.FileName);
        try
        {
            var time = new ManualTimeProvider();
            var patterns = new ClientTurnPatterns(NullLogger<ClientTurnPatterns>.Instance, new ClientTurnFile(file), time: time);
            patterns.Current.Should().BeSameAs(ClientTurnTable.BuiltIn, "there is no file yet");

            await File.WriteAllTextAsync(file, """{ "turns": [ { "id": "zed/title", "kind": "Title", "pattern": "^summarize this thread as a title" } ] }""");
            time.Advance(TimeSpan.FromSeconds(6));
            patterns.Current.Patterns.Should().Contain(p => p.Entry.Id == "zed/title");
            patterns.Current.Source.Should().EndWith($"{file} (1 entry)");

            await File.WriteAllTextAsync(file, """{ "turns": [ { "id": "zed/title", "enabled": false } ] }""");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            patterns.Current.Patterns.Should().Contain(p => p.Entry.Id == "zed/title", "it is checked at most every few seconds");
            time.Advance(TimeSpan.FromSeconds(6));
            patterns.Current.Patterns.Should().NotContain(p => p.Entry.Id == "zed/title");

            File.Delete(file);
            time.Advance(TimeSpan.FromSeconds(6));
            patterns.Current.Should().BeSameAs(ClientTurnTable.BuiltIn, "the file is gone");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void A_bad_entry_is_skipped_with_a_warning_and_a_bad_file_falls_back_to_the_built_in_patterns()
    {
        string file = Path.Combine(Directory.CreateTempSubdirectory("sr-turns-").FullName, ClientTurnTable.FileName);
        try
        {
            File.WriteAllText(file, """
                { "turns": [
                  { "id": "claude-code/title", "kind": "Title", "pattern": "(unclosed" },
                  { "id": "mine", "kind": "Configured", "pattern": "x" },
                  { "kind": "Title", "pattern": "x" },
                  { "id": "ok", "kind": "Topic", "pattern": "^is this a new topic" }
                ] }
                """);
            ClientTurnTable table = ClientTurnTable.Load(file);

            table.Patterns.Select(p => p.Entry.Id).Should().Contain("ok").And.Contain("claude-code/title", "an invalid replacement keeps the built-in pattern").And.NotContain("mine");
            table.Warning.Should().Contain("entry claude-code/title has an invalid pattern").And.Contain("entry mine has kind Configured").And.Contain("entry (no id) needs an id");

            File.WriteAllText(file, "{ not json");
            table = ClientTurnTable.Load(file);

            table.Patterns.Should().BeEquivalentTo(ClientTurnTable.BuiltIn.Patterns);
            table.Warning.Should().Contain("could not be read").And.Contain("using the built-in patterns");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
