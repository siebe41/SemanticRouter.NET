using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Cloud models' list prices from OpenRouter's public model list, matched to the connector that serves them.</summary>
public sealed class ModelPriceListTests : RouterHostTest
{
    // The shape of GET https://openrouter.ai/api/v1/models, prices in USD per token.
    private const string List = """
        {"data":[
          {"id":"anthropic/claude-sonnet-5.5","context_length":1000000,"pricing":{"prompt":"0.000002","completion":"0.00001","input_cache_read":"0.0000001","input_cache_write":"0.0000025"}},
          {"id":"anthropic/claude-sonnet-5.5:batch","pricing":{"prompt":"0.000001","completion":"0.000005"}},
          {"id":"~anthropic/claude-opus-latest","pricing":{"prompt":"0.000004","completion":"0.00002","input_cache_read":"0.0000002","input_cache_write":"0.000005"}},
          {"id":"anthropic/claude-opus-5.5","pricing":{"prompt":"0.000004","completion":"0.00002"}},
          {"id":"openai/gpt-6.1-sol","context_length":1050000,"pricing":{"prompt":"0.000002","completion":"0.00001","input_cache_read":"0.0000001"}},
          {"id":"google/gemini-3.5-flash","pricing":{"prompt":"0.0000003","completion":"0.0000025"}},
          {"id":"meta-llama/llama-4-maverick","pricing":{"prompt":"0.00000015","completion":"0.0000006"}},
          {"id":"openrouter/auto","pricing":{"prompt":"-1","completion":"-1"}}
        ]}
        """;

    private static readonly ModelPriceList Prices = ModelPriceList.Parse(List, ModelPriceList.DefaultSource, DateTimeOffset.UnixEpoch);

    private static ConnectorOptions Http(string address, string type = ConnectorTypes.OpenAICompatible) => new() { Type = type, BaseAddress = new Uri(address) };

    [Fact]
    public void Prices_are_per_million_tokens_and_variable_prices_are_left_out()
    {
        ModelPrice sonnet = Prices.Prices.Single(p => p.Id == "anthropic/claude-sonnet-5.5");
        sonnet.Should().Be(new ModelPrice
        {
            Id = "anthropic/claude-sonnet-5.5",
            Input = 2m,
            Output = 10m,
            CacheRead = 0.1m,
            CacheWrite = 2.5m,
            ContextWindow = 1_000_000,
        });
        sonnet.Input.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("2", "no trailing zeros in the draft");
        Prices.Prices.Should().NotContain(p => p.Id == "openrouter/auto");
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", "anthropic/claude-sonnet-5.5")]
    [InlineData("claude-sonnet-5-5-20260901", "anthropic/claude-sonnet-5.5")]
    [InlineData("claude-opus-5-5", "anthropic/claude-opus-5.5")]
    public void An_anthropic_connector_finds_anthropics_price_by_its_own_model_id(string model, string expected)
    {
        ModelPriceMatch? match = Prices.Find(Http("https://api.anthropic.com", ConnectorTypes.Anthropic), model);
        match.Should().NotBeNull();
        (match!.Price.Id, match.SameProvider).Should().Be((expected, true));
    }

    [Fact]
    public void A_provider_host_finds_only_its_own_models()
    {
        Prices.Find(Http("https://api.openai.com"), "gpt-6.1-sol")!.Price.Should().Match<ModelPrice>(p => p.Id == "openai/gpt-6.1-sol" && p.CacheRead == 0.1m && p.CacheWrite == null);
        Prices.Find(Http("https://generativelanguage.googleapis.com"), "models/gemini-3.5-flash")!.Price.Id.Should().Be("google/gemini-3.5-flash");
        Prices.Find(Http("https://api.openai.com"), "claude-sonnet-5-5").Should().BeNull("OpenAI does not serve Claude");
    }

    [Fact]
    public void On_openrouter_the_exact_model_is_priced_variants_included()
    {
        Prices.Find(Http("https://openrouter.ai"), "anthropic/claude-sonnet-5.5:batch")!.Price.Input.Should().Be(1m);
        Prices.Find(Http("https://openrouter.ai"), "claude-sonnet-5-5").Should().BeNull("OpenRouter needs the vendor's prefix");
    }

    [Fact]
    public void Subscriptions_are_valued_at_the_model_makers_price_claude_code_aliases_included()
    {
        var claudeCode = new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode };
        Prices.Find(claudeCode, "opus")!.Price.Id.Should().Be("~anthropic/claude-opus-latest");
        Prices.Find(claudeCode, "claude-sonnet-5-5")!.SameProvider.Should().BeTrue();

        ModelPriceMatch copilot = Prices.Find(new ConnectorOptions { Type = "Copilot" }, "claude-sonnet-5.5")!;
        (copilot.Price.Id, copilot.SameProvider).Should().Be(("anthropic/claude-sonnet-5.5", true));
    }

    [Fact]
    public void Another_host_gets_the_model_makers_price_marked_as_such()
    {
        ModelPriceMatch? match = Prices.Find(Http("https://api.groq.example"), "llama-4-maverick");
        match.Should().NotBeNull();
        (match!.Price.Id, match.SameProvider).Should().Be(("meta-llama/llama-4-maverick", false));
        Prices.Find(Http("https://api.groq.example"), "unknown-model").Should().BeNull();
    }

    [Fact]
    public async Task The_lookup_endpoint_fetches_the_list_once_and_answers_per_model()
    {
        var list = StubHttpMessageHandler.Json(List);
        HttpClient client = await StartAsync(services: s => s.AddHttpClient(ModelPriceList.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => list));
        var body = new StringContent("""
            {"models":[{"model":"claude-sonnet-5-5","type":"Anthropic","baseAddress":"https://api.anthropic.com"},{"model":"nothing-like-it","type":"Anthropic"},{"model":"opus","type":"Cli","preset":"ClaudeCode"}]}
            """, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PostAsync("/router/api/prices/lookup", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        await client.PostAsync("/router/api/prices/lookup", new StringContent("""{"models":[]}""", Encoding.UTF8, "application/json"));

        ((string?)answer["source"]).Should().Be("openrouter.ai");
        answer["problem"].Should().BeNull();
        JsonArray prices = answer["prices"]!.AsArray();
        ((decimal)prices[0]!["input"]!, (decimal)prices[0]!["cacheWrite"]!, (bool)prices[0]!["sameProvider"]!).Should().Be((2m, 2.5m, true));
        prices[1].Should().BeNull();
        ((string?)prices[2]!["id"], (decimal)prices[2]!["output"]!).Should().Be(("~anthropic/claude-opus-latest", 20m));
        list.Requests.Should().ContainSingle("the list is kept for six hours");
    }

    [Fact]
    public async Task Offline_mode_fetches_nothing_and_says_why()
    {
        var list = StubHttpMessageHandler.Json(List);
        HttpClient client = await StartAsync(services: s =>
        {
            s.AddHttpClient(ModelPriceList.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => list);
            s.AddSingleton(new MachinePolicy { Offline = true });
        });

        JsonNode answer = JsonNode.Parse(await (await client.PostAsync("/router/api/prices/lookup",
            new StringContent("""{"models":[{"model":"claude-sonnet-5-5","type":"Anthropic"}]}""", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync())!;

        ((string?)answer["problem"]).Should().Contain("offline mode");
        answer["prices"]!.AsArray().Should().ContainSingle().Which.Should().BeNull();
        list.Requests.Should().BeEmpty();
    }
}
