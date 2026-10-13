using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The prompt-cache numbers (#93): cached tokens reach the client and the dashboard whatever the backend, the
/// <c>prompt_cache_key</c> crosses between the OpenAI protocols, and "saved by routing" is not a cache discount.
/// </summary>
public sealed class PromptCacheReportingTests : RouterHostTest
{
    private const string Prompt = "hello there";

    private Task<HttpClient> StartDispatchAsync(Action<SemanticRouterOptions>? routing = null, Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing,
            services);

    private static void AddNativeAnthropic(IServiceCollection services) =>
        services.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic-native"] = new ConnectorOptions
        {
            Type = ConnectorType.Anthropic,
            BaseAddress = new Uri("https://api.anthropic.example"),
            ApiKey = "sk-ant-test",
        });

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static List<(string Event, JsonElement Data)> Events(string sse) =>
        [.. sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\n'))
            .Select(lines => (lines[0]["event: ".Length..], JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone()))];

    private RoutingActivityEntry LastEntry => Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();

    [Fact]
    public async Task A_streamed_claude_code_turn_served_by_an_openai_backend_reports_its_cached_tokens()
    {
        CloudHttp = new StubHttpMessageHandler((_, _) => Sse(
            """
            data: {"id":"c","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":null}]}

            data: {"id":"c","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"c","object":"chat.completion.chunk","model":"m","choices":[],"usage":{"prompt_tokens":5000,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4800}}}

            data: [DONE]


            """));
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/messages", Json($$"""{"model":"cloud","max_tokens":10,"stream":true,"messages":[{"role":"user","content":"{{Prompt}}"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement usage = Events(await response.Content.ReadAsStringAsync()).Single(e => e.Event == "message_delta").Data.GetProperty("usage");
        (usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32(), usage.GetProperty("cache_read_input_tokens").GetInt32())
            .Should().Be((200, 5, 4800), "Anthropic counts cached tokens apart from input_tokens");
        (LastEntry.InputTokens, LastEntry.CacheReadTokens).Should().Be((200, 4800));
    }

    [Fact]
    public async Task A_translated_anthropic_stream_keeps_cache_counts_reported_on_message_delta()
    {
        AnthropicHttp = new StubHttpMessageHandler((_, _) => Sse(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":30,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":6,\"cache_read_input_tokens\":7000,\"cache_creation_input_tokens\":250}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"));
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("native", 4, costPerMillion: 3m, connector: "anthropic-native")),
            services: AddNativeAnthropic);

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json($$"""{"model":"native","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"{{Prompt}}"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement usage = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e["data: ".Length..])
            .Where(d => d != "[DONE]")
            .Select(d => JsonDocument.Parse(d).RootElement.Clone())
            .Single(e => e.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
            .GetProperty("usage");
        usage.GetProperty("prompt_tokens").GetInt32().Should().Be(30 + 7000 + 250, "OpenAI's prompt_tokens includes the cached tokens");
        usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(7000);
        (LastEntry.InputTokens, LastEntry.CacheReadTokens, LastEntry.CacheCreationTokens).Should().Be((30, 7000, 250));
    }

    [Fact]
    public async Task The_prompt_cache_key_crosses_between_the_openai_protocols()
    {
        CloudHttp = StubHttpMessageHandler.Json("""{"id":"c","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""");
        AnthropicHttp = StubHttpMessageHandler.Json("""{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}""");
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("native", 4, costPerMillion: 3m, connector: "anthropic-native")),
            services: AddNativeAnthropic);

        HttpResponseMessage toChat = await client.PostAsync(
            "/v1/responses", Json($$"""{"model":"cloud","prompt_cache_key":"repo-42","input":"{{Prompt}}"}"""));

        toChat.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(CloudHttp.Requests.Single().Body).RootElement.GetProperty("prompt_cache_key").GetString().Should().Be("repo-42");
        toChat.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();

        HttpResponseMessage toAnthropic = await client.PostAsync(
            "/v1/chat/completions", Json($$"""{"model":"native","prompt_cache_key":"repo-42","messages":[{"role":"user","content":"{{Prompt}}"}]}"""));

        toAnthropic.StatusCode.Should().Be(HttpStatusCode.OK);
        toAnthropic.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse("Anthropic caches by prefix and needs no key; Codex sends one on every turn");
        AnthropicHttp.Requests.Single().Body.Should().NotContain("prompt_cache_key");
    }

    [Fact]
    public async Task Saved_by_routing_prices_cached_tokens_at_the_reference_models_cache_prices()
    {
        CloudHttp = StubHttpMessageHandler.Json("""{"id":"c","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1100,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":1000}}}""");
        HttpClient client = await StartDispatchAsync(routing: o =>
        {
            ModelDefinition reference = Model("reference", 4, costPerMillion: 10m);
            reference.CacheReadCostPerMillionTokens = 1m;
            reference.Enabled = false;
            o.Models.Add(reference);
        });
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Usage.ReferenceModel = "reference";

        (await client.PostAsync("/v1/chat/completions", Json($$"""{"model":"cloud","messages":[{"role":"user","content":"{{Prompt}}"}]}""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        JsonNode totals = JsonNode.Parse(await client.GetStringAsync("/router/api/usage?window=day"))!["totals"]!;
        totals["cacheReadTokens"]!.GetValue<long>().Should().Be(1000);
        totals["costUsd"]!.GetValue<decimal>().Should().Be((100 + 100 + 1000) * 3m / 1_000_000m, "cloud has no cache price, so its reads cost the input price");
        totals["referenceCostUsd"]!.GetValue<decimal>().Should().Be(((100 + 100) * 10m + (1000 * 1m)) / 1_000_000m, "the reference model would have read the same cache");
    }
}
