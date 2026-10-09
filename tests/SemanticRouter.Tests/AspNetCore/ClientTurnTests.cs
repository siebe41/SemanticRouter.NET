using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using SemanticRouter.AspNetCore;

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
}
