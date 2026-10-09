using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Outcome feedback (#43): signs, noticed afterwards, that a routing decision was not good enough.</summary>
public sealed class FeedbackTests : RouterHostTest
{
    private const string SystemPrompt = "You are a coding agent.";

    private Task<HttpClient> StartDispatchAsync(Action<PromptRouterMiddlewareOptions>? configure = null) =>
        StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
            configure?.Invoke(o);
        });

    private RoutingFeedbackLog Feedback => Services.GetRequiredService<RoutingFeedbackLog>();

    private static StringContent Chat(params string[] userTurns)
    {
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        for (int i = 0; i < userTurns.Length; i++)
        {
            messages.Add(new { role = "user", content = userTurns[i] });
            if (i < userTurns.Length - 1)
            {
                messages.Add(new { role = "assistant", content = "answer " + i });
            }
        }

        return new StringContent(JsonSerializer.Serialize(new { model = "auto", messages }), Encoding.UTF8, "application/json");
    }

    private static long Id(HttpResponseMessage response) => long.Parse(response.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_retry_is_feedback_on_the_answer_even_with_escalation_off()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        HttpResponseMessage again = await client.PostAsync("/v1/chat/completions", Chat("hello there"));

        again.Headers.GetValues("X-Prompt-Route-Reason").Single().Should().Be("SemanticMatch", "escalation is off: only recorded");
        RoutingFeedback retried = Feedback.Recent().Should().ContainSingle().Subject;
        retried.Should().Match<RoutingFeedback>(f => f.Kind == FeedbackKind.Retried && f.EntryId == Id(first) && f.Tier == 1 && f.NeededTier == 2 && f.Model == "local");
        retried.Detail.Should().Contain(Id(again).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_correction_or_a_callout_judges_the_previous_turn()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        await client.PostAsync("/v1/chat/completions", Chat("hello there", "that's wrong, it should greet the team"));
        HttpResponseMessage third = await client.PostAsync("/v1/chat/completions", Chat("hello there", "that's wrong, it should greet the team", "fine, thanks"));
        await client.PostAsync("/v1/chat/completions", Chat("hello there", "that's wrong, it should greet the team", "fine, thanks", "@cloud do it properly"));

        RoutingFeedback[] items = [.. Feedback.Recent().Reverse()];
        items.Select(f => (f.Kind, f.EntryId, f.NeededTier)).Should().Equal(
            (FeedbackKind.Corrected, Id(first), (int?)2),
            (FeedbackKind.Overridden, Id(third), (int?)4));
        items[1].Detail.Should().Contain("@cloud");
    }

    [Fact]
    public async Task An_explicit_choice_is_not_judged_and_feedback_can_be_switched_off()
    {
        HttpClient client = await StartDispatchAsync();
        await client.PostAsync("/v1/chat/completions", Chat("@local hello there"));
        await client.PostAsync("/v1/chat/completions", Chat("@local hello there", "that's wrong"));
        Feedback.Recent().Should().BeEmpty("the person chose that model; it is not the router's mistake");

        await DisposeAsync();
        client = await StartDispatchAsync(o => o.Feedback.Enabled = false);
        await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        Feedback.Recent().Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_calls_failing_on_two_turns_in_a_row_are_feedback_on_the_turn_running_the_task()
    {
        HttpClient client = await StartDispatchAsync();
        HttpResponseMessage started = await client.PostAsync("/v1/chat/completions", Chat("hello there"));

        // The client ran the tools and sends back results marked as errors (Anthropic carries is_error).
        StringContent Continuation(int failures) => new(JsonSerializer.Serialize(new
        {
            model = "claude-sonnet-4-5",
            max_tokens = 100,
            system = SystemPrompt,
            messages = new object[]
            {
                new { role = "user", content = "hello there" },
                new { role = "assistant", content = new object[] { new { type = "tool_use", id = "t" + failures, name = "Bash", input = new { command = "make" } } } },
                new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t" + failures, content = "make: *** Error 2", is_error = true } } },
            },
        }), Encoding.UTF8, "application/json");

        await client.PostAsync("/v1/messages", Continuation(1));
        Feedback.Recent().Should().BeEmpty("one failed tool call is normal work");
        await client.PostAsync("/v1/messages", Continuation(2));

        Feedback.Recent().Should().ContainSingle().Which.Should().Match<RoutingFeedback>(f => f.Kind == FeedbackKind.ToolErrors && f.EntryId == Id(started));
    }

    [Fact]
    public async Task An_empty_reply_is_feedback_on_its_own_decision()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":0}}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat("hello there"));

        Feedback.Recent().Should().ContainSingle().Which.Should().Match<RoutingFeedback>(f => f.Kind == FeedbackKind.EmptyReply && f.EntryId == Id(response));
    }

    [Fact]
    public async Task Activity_and_the_summary_show_feedback_per_decision_route_and_model()
    {
        HttpClient client = await StartDispatchAsync();
        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        await client.PostAsync("/v1/chat/completions", Chat("hello there"));
        await client.PostAsync("/v1/chat/completions", Chat("good morning"));

        using JsonDocument activity = JsonDocument.Parse(await client.GetStringAsync("/router/api/activity"));
        activity.RootElement.GetProperty("feedback").GetProperty(Id(first).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .EnumerateArray().Select(k => k.GetString()).Should().Equal("Retried");

        using JsonDocument summary = JsonDocument.Parse(await client.GetStringAsync("/router/api/feedback/summary?days=1"));
        JsonElement root = summary.RootElement;
        root.GetProperty("decisions").GetInt32().Should().Be(3);
        root.GetProperty("feedback").GetInt32().Should().Be(1);
        JsonElement route = root.GetProperty("byRoute").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "smalltalk");
        (route.GetProperty("decisions").GetInt32(), route.GetProperty("withFeedback").GetInt32()).Should().Be((3, 1));
        route.GetProperty("byKind").GetProperty("Retried").GetInt32().Should().Be(1);
        root.GetProperty("byModel").EnumerateArray().Should().Contain(g => g.GetProperty("key").GetString() == "local");

        (await client.GetAsync("/router/api/feedback")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("that's wrong, use the other table", true)]
    [InlineData("That’s not what I asked for", true)]
    [InlineData("No, I meant the staging database", true)]
    [InlineData("it still doesn't compile", true)]
    [InlineData("the tests are still failing after your change", true)]
    [InlineData("you forgot the null check", true)]
    [InlineData("try again", true)]
    [InlineData("No worries, thanks!", false)]
    [InlineData("now add an error handler for the wrong-password case", false)]
    [InlineData("great, that works", false)]
    public void Corrections_are_recognised_without_catching_new_instructions(string prompt, bool expected) =>
        Corrections.Matches(prompt).Should().Be(expected);
}
