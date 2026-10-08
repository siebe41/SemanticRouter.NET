using System.Net;
using System.Text.Json;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Suggesting catalog entries for what a connector offers: Claude's families on their own tiers, local models by size.</summary>
public sealed class ModelLineupTests : RouterHostTest
{
    private static readonly ConnectorOptions ClaudeCli = new() { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode, ToolCalling = CliToolCalling.Prompted };
    private static readonly ConnectorOptions LmStudio = new() { BaseAddress = new Uri("http://localhost:1234") };

    private static IReadOnlyList<ModelLineupEntry> Suggest(string connector, ConnectorOptions options, params string[] ids) =>
        ModelLineup.Suggest(connector, options, ids.Select(id => new DiscoveredModel(id)).ToArray(), ModelLoading.Unknown, []);

    [Fact]
    public void Claude_code_gets_one_tier_per_family_and_fable_starts_disabled()
    {
        IReadOnlyList<ModelLineupEntry> lineup = Suggest("claude-cli", ClaudeCli, [.. ModelLineup.CliModelIds(CliPreset.ClaudeCode)]);

        lineup.Select(e => (e.Suggestion!.Name, e.Suggestion.Model, e.Suggestion.Tier, e.Suggestion.Enabled)).Should().Equal(
            ("claude-haiku", "haiku", 2, true),
            ("claude-sonnet", "sonnet", 3, true),
            ("claude-opus", "opus", 4, true),
            ("claude-fable-5-1", "claude-fable-5-1", 5, false));
        lineup.Should().OnlyContain(e => e.Recommended && !e.Suggestion!.IsLocal && e.Suggestion.InputCostPerMillionTokens == 0 && e.Suggestion.ContextWindow == 200_000);
        lineup.Select(e => e.Suggestion!.Capabilities).Should().AllSatisfy(c => c.Should().Contain(["chat", "code", "reasoning", "tools"]).And.NotContain("vision"));
        lineup[3].Reason.Should().Contain("enable it if your plan includes");
    }

    [Fact]
    public void A_cli_without_prompted_tool_calling_does_not_claim_tools()
    {
        var plain = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };

        Suggest("claude", plain, "sonnet").Single().Suggestion!.Capabilities.Should().NotContain("tools");
    }

    [Fact]
    public void The_anthropic_api_gets_list_prices_context_and_vision()
    {
        var api = new ConnectorOptions { Type = ConnectorType.Anthropic, BaseAddress = new Uri("https://api.anthropic.com") };

        IReadOnlyList<ModelLineupEntry> lineup = Suggest("anthropic", api, "claude-opus-5-5", "claude-haiku-4-5", "claude-fable-5-1");

        lineup[0].Suggestion.Should().Match<ModelDefinition>(m => m.Tier == 4 && m.InputCostPerMillionTokens == 4m && m.OutputCostPerMillionTokens == 20m && m.ContextWindow == 1_000_000);
        lineup[0].Suggestion!.Capabilities.Should().Contain(["tools", "vision"]);
        lineup[1].Suggestion.Should().Match<ModelDefinition>(m => m.Tier == 2 && m.InputCostPerMillionTokens == 1m && m.ContextWindow == 200_000);
        lineup[2].Suggestion.Should().Match<ModelDefinition>(m => m.Tier == 5 && m.Enabled && m.InputCostPerMillionTokens == 10m, "an API key that lists Fable can use it");
        lineup[0].Reason.Should().Contain("list price");
    }

    [Fact]
    public void Copilot_claude_models_are_recognised_and_unknown_families_are_left_to_the_user()
    {
        var copilot = new ConnectorOptions { Type = ConnectorType.Copilot };

        IReadOnlyList<ModelLineupEntry> lineup = Suggest("copilot", copilot, "claude-sonnet-5.5", "gpt-6.1-sol", "gpt-6-mini");

        lineup[0].Should().Match<ModelLineupEntry>(e => e.Recommended && e.Suggestion!.Tier == 3 && e.Suggestion.InputCostPerMillionTokens == 0);
        lineup[0].Suggestion!.Capabilities.Should().Contain("tools").And.NotContain("vision");
        lineup[1].Should().Match<ModelLineupEntry>(e => !e.Recommended && e.Suggestion!.Tier == 3 && e.Reason.Contains("review"));
        lineup[2].Suggestion!.Tier.Should().Be(2);
        lineup[2].Reason.Should().NotContain("prices", "a subscription has no per-token price to set");
    }

    [Fact]
    public async Task Local_models_are_tiered_by_size_and_loaded_ones_are_preferred()
    {
        ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync("lmstudio", LmStudio, new HttpClient(LocalServers.LmStudio(justInTime: true)), TimeSpan.FromSeconds(5));

        IReadOnlyList<ModelLineupEntry> lineup = ModelLineup.Suggest("lmstudio", LmStudio, probe.Details, probe.Loading, []);

        ModelDefinition small = lineup.Single(e => e.Model.Id == "qwen/qwen3-8b").Suggestion!;
        small.Should().Match<ModelDefinition>(m => m.Name == "lmstudio-qwen3-8b" && m.Tier == 1 && m.IsLocal && m.ContextWindow == 32768 && m.Priority == 0 && m.Enabled);
        small.Capabilities.Should().Contain("tools");
        ModelLineupEntry coder = lineup.Single(e => e.Model.Id == "qwen/qwen3-coder-30b");
        coder.Suggestion.Should().Match<ModelDefinition>(m => m.Tier == 2 && m.Priority == 1 && m.ContextWindow == 0 && m.Enabled);
        coder.Suggestion!.Capabilities.Should().Contain(["code", "tools"]);
        coder.Reason.Should().Contain("not loaded").And.Contain("supports up to 262,144");
        lineup.Single(e => e.Model.Id == "google/gemma-3-12b").Suggestion!.Capabilities.Should().Contain("vision");
        ModelLineupEntry embedding = lineup.Single(e => e.Model.Kind == DiscoveredModelKind.Embedding);
        embedding.Suggestion.Should().BeNull();
        embedding.Recommended.Should().BeFalse();
        lineup.Where(e => e.Suggestion is not null).Should().OnlyContain(e => e.Recommended);
    }

    [Fact]
    public async Task Ollama_cloud_models_are_not_local_and_unknown_local_servers_are_left_to_the_user()
    {
        var ollama = new ConnectorOptions { BaseAddress = new Uri("http://localhost:11434") };
        ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync("ollama", ollama, new HttpClient(LocalServers.Ollama()), TimeSpan.FromSeconds(5));
        IReadOnlyList<ModelLineupEntry> lineup = ModelLineup.Suggest("ollama", ollama, probe.Details, probe.Loading, []);
        var proxy = new ConnectorOptions { BaseAddress = new Uri("http://localhost:4000") };

        ModelLineupEntry proxied = ModelLineup.Suggest("litellm", proxy, [new DiscoveredModel("qwen3-32b")], ModelLoading.Unknown, []).Single();

        lineup.Single(e => e.Model.Id == "llama3.2:latest").Suggestion!.IsLocal.Should().BeTrue();
        ModelLineupEntry cloud = lineup.Single(e => e.Model.Id == "gpt-oss:120b-cloud");
        cloud.Suggestion!.IsLocal.Should().BeFalse("its prompts go to ollama.com, so it must never serve a LocalOnly route");
        cloud.Recommended.Should().BeFalse();
        cloud.Reason.Should().Contain("forwards it to a cloud service");
        proxied.Suggestion!.IsLocal.Should().BeTrue();
        proxied.Recommended.Should().BeFalse("a server on localhost may still be a proxy to a cloud API");
        proxied.Reason.Should().Contain("does not forward prompts to a cloud API");
    }

    [Fact]
    public async Task Models_a_server_will_not_serve_are_suggested_disabled()
    {
        ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync("lmstudio", LmStudio, new HttpClient(LocalServers.LmStudio(justInTime: false)), TimeSpan.FromSeconds(5));

        ModelLineupEntry coder = ModelLineup.Suggest("lmstudio", LmStudio, probe.Details, probe.Loading, []).Single(e => e.Model.Id == "qwen/qwen3-coder-30b");

        coder.Suggestion!.Enabled.Should().BeFalse();
        coder.Recommended.Should().BeFalse();
        coder.Reason.Should().Contain("does not serve it until it is loaded");
    }

    [Fact]
    public void Names_are_unique_and_models_already_in_the_catalog_are_marked()
    {
        ModelDefinition[] catalog =
        [
            new() { Name = "lmstudio-qwen3-8b", Connector = "other", Model = "something-else", Tier = 1 },
            new() { Name = "my-coder", Connector = "LMStudio", Model = "qwen/qwen3-coder-30b", Tier = 2 },
        ];

        IReadOnlyList<ModelLineupEntry> lineup = ModelLineup.Suggest(
            "lmstudio", LmStudio, [new DiscoveredModel("qwen/qwen3-8b"), new DiscoveredModel("qwen/qwen3-coder-30b"), new DiscoveredModel("other/qwen3-8b")], ModelLoading.Unknown, catalog);

        lineup.Select(e => e.Suggestion!.Name).Should().Equal("lmstudio-qwen3-8b-2", "lmstudio-qwen3-coder-30b", "lmstudio-qwen3-8b-3");
        lineup[1].Should().Match<ModelLineupEntry>(e => e.InCatalog == "my-coder" && e.InCatalogTier == 2 && !e.Recommended);
        lineup[0].InCatalog.Should().BeNull();
    }

    [Theory]
    [InlineData("lmstudio", "qwen/qwen3-8b", "lmstudio-qwen3-8b")]
    [InlineData("ollama", "llama3.2:latest", "ollama-llama3.2")]
    [InlineData("ollama", "qwen3:30b-a3b", "ollama-qwen3-30b-a3b")]
    [InlineData("claude-cli", "sonnet", "claude-sonnet")]
    [InlineData("claude-cli", "claude-fable-5-1", "claude-fable-5-1")]
    [InlineData("anthropic", "claude-opus-5-5", "anthropic-claude-opus-5-5")]
    [InlineData("openrouter", "Vendor/Model_X", "openrouter-model-x")]
    public void Names_combine_the_connector_and_the_model_id(string connector, string id, string name) =>
        ModelLineup.NameFor(connector, id).Should().Be(name);

    [Fact]
    public async Task The_dashboard_discover_endpoint_returns_the_lineup_in_configuration_shape()
    {
        LocalHttp = LocalServers.LmStudio(justInTime: true, chatId: "qwen3-8b");
        HttpClient client = await StartAsync(withCli: true);

        JsonElement lmstudio = JsonDocument.Parse(await (await client.PostAsync("/router/api/connectors/lmstudio/discover", null)).Content.ReadAsStringAsync()).RootElement;
        JsonElement claude = JsonDocument.Parse(await (await client.PostAsync("/router/api/connectors/claude/discover", null)).Content.ReadAsStringAsync()).RootElement;
        HttpResponseMessage missing = await client.PostAsync("/router/api/connectors/nope/discover", null);

        lmstudio.GetProperty("server").GetString().Should().Be("LM Studio");
        lmstudio.GetProperty("loading").GetString().Should().Be("OnDemand");
        lmstudio.GetProperty("models").EnumerateArray().Select(m => m.GetString()).Should().Contain(["qwen3-8b", "qwen/qwen3-coder-30b"]);
        JsonElement[] lineup = [.. lmstudio.GetProperty("lineup").EnumerateArray()];
        JsonElement existing = lineup.Single(e => e.GetProperty("id").GetString() == "qwen3-8b");
        existing.GetProperty("inCatalog").GetString().Should().Be("local", "the harness catalog serves qwen3-8b from lmstudio as 'local'");
        existing.GetProperty("recommended").GetBoolean().Should().BeFalse();
        JsonElement coder = lineup.Single(e => e.GetProperty("id").GetString() == "qwen/qwen3-coder-30b").GetProperty("suggestion");
        coder.GetProperty("Name").GetString().Should().Be("lmstudio-qwen3-coder-30b", "suggestions use the configuration document's property names");
        coder.GetProperty("Tier").GetInt32().Should().Be(2);
        coder.GetProperty("Priority").GetInt32().Should().Be(1);
        coder.GetProperty("Connector").GetString().Should().Be("lmstudio");
        lineup.Single(e => e.GetProperty("kind").GetString() == "Embedding").GetProperty("suggestion").ValueKind.Should().Be(JsonValueKind.Null);

        claude.GetProperty("lineup").EnumerateArray().Select(e => (e.GetProperty("id").GetString(), e.GetProperty("suggestion").GetProperty("Tier").GetInt32()))
            .Should().Equal(("haiku", 2), ("sonnet", 3), ("opus", 4), ("claude-fable-5-1", 5));
        claude.GetProperty("lineup")[1].GetProperty("inCatalog").GetString().Should().Be("claude-cli");
        claude.GetProperty("note").GetString().Should().Contain("no model list");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
