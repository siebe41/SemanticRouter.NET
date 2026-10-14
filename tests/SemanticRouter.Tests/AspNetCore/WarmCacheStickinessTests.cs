using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Cache-aware stickiness through the gateway (#94): the model that answered records the prompt cache its backend
/// reported, and a later turn whose tier drops stays on it while moving would cost more.
/// </summary>
public sealed class WarmCacheStickinessTests : RouterHostTest
{
    // An agent's long system prompt: about 80k tokens, the part a prompt cache holds.
    private static readonly string SystemPrompt = string.Concat(Enumerable.Repeat("Follow the repository conventions. ", 9_000));

    public WarmCacheStickinessTests()
    {
        // The first message needs the strongest tier; later ones less.
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => new PromptClassificationResult
            {
                RequiredTier = prompt.StartsWith("design", StringComparison.Ordinal) ? 4 : 3,
                Reason = RoutingReason.SemanticMatch,
                RouteName = "coding",
                Complexity = new ComplexityAnalysis { Score = 0.5, EstimatedTokens = 20 },
            });
        AnthropicHttp = StubHttpMessageHandler.Json(
            """{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{"input_tokens":40,"output_tokens":5,"cache_creation_input_tokens":80000}}""");
        CloudHttp = StubHttpMessageHandler.Json(
            """{"id":"c","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":80040,"completion_tokens":5}}""");
    }

    private Task<HttpClient> StartConversationAsync(bool keepWarmCaches = true) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.Conversation.DecayTurns = 1;
                o.Conversation.KeepWarmCaches = keepWarmCaches;
            },
            routing: o =>
            {
                o.Models.Single(m => m.Name == "cloud").Enabled = false;
                o.Models.Add(new ModelDefinition
                {
                    Name = "big",
                    Connector = "anthropic-native",
                    Tier = 4,
                    InputCostPerMillionTokens = 4m,
                    OutputCostPerMillionTokens = 20m,
                    CacheReadCostPerMillionTokens = 0.2m,
                    CacheWriteCostPerMillionTokens = 5m,
                });
                o.Models.Add(new ModelDefinition
                {
                    Name = "mid",
                    Connector = "anthropic",
                    Model = "mid-model",
                    Tier = 3,
                    InputCostPerMillionTokens = 2m,
                    OutputCostPerMillionTokens = 10m,
                    CacheReadCostPerMillionTokens = 0.2m,
                    CacheWriteCostPerMillionTokens = 2.5m,
                });
            },
            services: s => s.Configure<SemanticRouterConnectorsOptions>(c => c.Connectors["anthropic-native"] = new ConnectorOptions
            {
                Type = ConnectorType.Anthropic,
                BaseAddress = new Uri("https://api.anthropic.example"),
                ApiKey = "sk-ant-test",
            }));

    /// <summary>One more turn of the conversation: the history so far plus <paramref name="prompt"/>.</summary>
    private static StringContent Turn(params string[] userTurns)
    {
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        foreach (string user in userTurns)
        {
            if (messages.Count > 1)
            {
                messages.Add(new { role = "assistant", content = "done" });
            }

            messages.Add(new { role = "user", content = user });
        }

        return new StringContent(JsonSerializer.Serialize(new { model = "auto", messages }), Encoding.UTF8, "application/json");
    }

    private static readonly string[] Conversation =
    [
        "design the parser module for the configuration language, with error recovery",
        "now write the unit tests for the parser module, covering every error case",
        "and add the documentation comments to every public member of the parser",
    ];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, int turns)
    {
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Turn(Conversation[..turns]));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }

    [Fact]
    public async Task A_turn_that_needs_a_lower_tier_stays_on_the_model_holding_the_cache()
    {
        HttpClient client = await StartConversationAsync();

        (await SendAsync(client, 1)).Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("big");
        (await SendAsync(client, 2)).Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("big", "the conversation's floor holds tier 4 for one more turn");

        HttpResponseMessage third = await SendAsync(client, 3);

        third.Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("big", "mid would re-send the 80k cached tokens");
        // Counted up to the request's estimated size, which the 80k-token cache covers.
        third.Headers.GetValues("X-Prompt-Route-Selection").Single().Should().MatchRegex(@"^prompt cache: big \([\d,]+ tokens cached\) over mid, which would cost \$0\.\d+ more this turn$");
        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();
        (entry.Tier, entry.Sticky).Should().Be((3, true));
    }

    [Fact]
    public async Task Without_keeping_warm_caches_the_conversation_moves_to_the_cheaper_model()
    {
        HttpClient client = await StartConversationAsync(keepWarmCaches: false);
        await SendAsync(client, 1);
        await SendAsync(client, 2);

        (await SendAsync(client, 3)).Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("mid");
    }

    [Fact]
    public void A_warm_cache_expires_after_the_prompt_cache_lifetime()
    {
        var clock = new ManualTimeProvider();
        var tracker = new ConversationTracker(clock);
        var options = new ConversationRoutingOptions();
        tracker.Record("c", 4, raisedByFloor: false, followUp: false, "big", options);
        tracker.Warmed("c", "big", 80_000, options);
        tracker.Warmed("c", "mid", 0, options);

        tracker.Lookup("c", "next message that is long enough not to be a short follow-up", options).WarmCaches
            .Should().Equal(new WarmCache("big", 80_000));

        clock.Advance(TimeSpan.FromMinutes(5));

        tracker.Lookup("c", "next message that is long enough not to be a short follow-up", options).WarmCaches
            .Should().BeNull("Anthropic's default cache entries live 5 minutes");
    }
}
