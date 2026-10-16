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
        lineup.Select(e => (e.Suggestion!.ApiInputCostPerMillionTokens, e.Suggestion.ApiOutputCostPerMillionTokens)).Should().Equal(
            [(0.10m, 0.50m), (2m, 10m), (4m, 20m), (10m, 50m)], "a subscription's use is valued at the API price of the family's latest model");
        lineup.Select(e => e.Suggestion!.ApiCacheReadCostPerMillionTokens).Should().Equal(
            [0.01m, 0.20m, 0.20m, 0.25m], "cache reads are a tenth of input unless the price list says otherwise (Opus 5.5, Fable 5.1)");
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
        lineup.Select(e => (e.Suggestion!.CacheReadCostPerMillionTokens, e.Suggestion.CacheWriteCostPerMillionTokens)).Should().Equal(
            [(0.20m, 5m), (0.1m, 1.25m), (0.25m, 12.5m)], "cached input is priced apart from input, or a long agent session costs ten times what it should");
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

    [Fact]
    public async Task Known_families_get_their_table_tiers_and_capabilities_from_recorded_listings()
    {
        var ollama = new ConnectorOptions { BaseAddress = new Uri("http://localhost:11434") };
        ConnectorProbeResult fromOllama = await ConnectorProbe.DiscoverAsync("ollama", ollama, new HttpClient(LocalServers.Ollama()), TimeSpan.FromSeconds(5));
        ConnectorProbeResult fromLmStudio = await ConnectorProbe.DiscoverAsync("lmstudio", LmStudio, new HttpClient(LocalServers.LmStudio(justInTime: true)), TimeSpan.FromSeconds(5));
        IReadOnlyList<ModelLineupEntry> o = ModelLineup.Suggest("ollama", ollama, fromOllama.Details, fromOllama.Loading, []);
        IReadOnlyList<ModelLineupEntry> l = ModelLineup.Suggest("lmstudio", LmStudio, fromLmStudio.Details, fromLmStudio.Loading, []);

        ModelLineupEntry llama = o.Single(e => e.Model.Id == "llama3.2:latest");
        (llama.Family, llama.TierFrom, llama.Suggestion!.Tier).Should().Be(("Llama 3.2", "size", 1));
        llama.Suggestion.Capabilities.Should().NotContain("tools", "Ollama reports tools, but a 3B Llama's tool calls are unreliable");
        llama.Reason.Should().Contain("no tools: Llama 3.2 tool calls are unreliable below 7B");
        ModelLineupEntry cloud = o.Single(e => e.Model.Id == "gpt-oss:120b-cloud");
        (cloud.Family, cloud.TierFrom, cloud.Suggestion!.Tier).Should().Be(("gpt-oss", "family", 3));
        cloud.Suggestion.Capabilities.Should().Contain("reasoning");
        ModelLineupEntry coder = l.Single(e => e.Model.Id == "qwen/qwen3-coder-30b");
        (coder.Family, coder.TierFrom, coder.Suggestion!.Tier).Should().Be(("Qwen3 Coder", "family", 2));
        coder.Reason.Should().StartWith("Qwen3 Coder (model-family table), 30B parameters → tier 2");
        l.Single(e => e.Model.Id == "qwen/qwen3-8b").Suggestion!.Capabilities.Should().Contain(["tools", "reasoning"]);
        ModelLineupEntry gemma = l.Single(e => e.Model.Id == "google/gemma-3-12b");
        (gemma.Family, gemma.TierFrom).Should().Be(("Gemma 3", "size"));
        gemma.Suggestion!.Capabilities.Should().Contain("vision").And.NotContain("tools");
    }

    [Fact]
    public void Unknown_models_fall_back_to_the_size_and_name_rules()
    {
        IReadOnlyList<ModelLineupEntry> local = Suggest("lmstudio", LmStudio, "acme/widget-13b", "acme/widget-coder");
        IReadOnlyList<ModelLineupEntry> remote = Suggest("openrouter", new ConnectorOptions { BaseAddress = new Uri("https://openrouter.ai/api") }, "acme/omni-large", "acme/omni-mini");

        local.Select(e => (e.Family, e.TierFrom, e.Suggestion!.Tier)).Should().Equal(((string?)null, "size", 2), ((string?)null, "size", 2));
        remote.Select(e => (e.TierFrom, e.Suggestion!.Tier, e.Recommended)).Should().Equal(("name", 3, false), ("name", 2, false));
        remote[0].Reason.Should().Contain("family not recognised");
    }

    [Fact]
    public void Hosted_families_and_the_most_specific_pattern_win()
    {
        var gemini = new ConnectorOptions { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/openai") };
        IReadOnlyList<ModelLineupEntry> hosted = Suggest("gemini", gemini, "gemini-2.5-flash", "gemini-3-pro-preview");
        ModelLineupEntry distill = Suggest("lmstudio", LmStudio, "deepseek-r1-distill-qwen-7b").Single();

        hosted.Select(e => (e.Family, e.TierFrom, e.Suggestion!.Tier)).Should().Equal(("Gemini Flash", "family", 2), ("Gemini Pro", "family", 3));
        hosted[1].Suggestion!.Capabilities.Should().Contain(["vision", "reasoning", "tools"]);
        distill.Family.Should().Be("DeepSeek R1", "deepseek-r1 is longer than qwen");
        distill.Suggestion!.Capabilities.Should().Contain("reasoning").And.NotContain("tools");
    }

    [Fact]
    public void A_local_family_table_adds_and_replaces_families_and_a_broken_one_is_reported()
    {
        string directory = Directory.CreateTempSubdirectory("router-families-").FullName;
        try
        {
            string file = Path.Combine(directory, ModelFamilyTable.FileName);
            File.WriteAllText(file, """
                // Comments are allowed.
                { "families": [
                  { "family": "Qwen3 Coder", "match": ["qwen3-coder"], "bands": [{ "upTo": 9.5, "tier": 2 }, { "tier": 3 }], "capabilities": ["code", "tools"] },
                  { "family": "Acme Widget", "match": ["acme/widget*"], "tier": 4, "capabilities": ["code"], "notes": "Our own fine-tune." },
                  { "family": "", "match": ["x"] }
                ] }
                """);

            ModelFamilyTable table = ModelFamilyTable.Load(file);
            IReadOnlyList<ModelLineupEntry> lineup = ModelLineup.Suggest(
                "lmstudio", LmStudio, [new DiscoveredModel("qwen/qwen3-coder-30b"), new DiscoveredModel("acme/widget-13b")], ModelLoading.Unknown, [], table);

            lineup.Select(e => (e.Family, e.Suggestion!.Tier)).Should().Equal(("Qwen3 Coder", 3), ("Acme Widget", 4));
            lineup[1].Reason.Should().Contain("Our own fine-tune");
            table.Families.Count(f => f.Family == "Qwen3 Coder").Should().Be(1, "the local entry replaces the built-in one");
            table.Families.Should().Contain(f => f.Family == "Llama 3.2", "built-in families stay");
            table.Source.Should().Contain(file).And.Contain("2 families");
            table.Warning.Should().Contain("needs a name");
            ModelFamilyTable.Load(Path.Combine(directory, "missing.json")).Should().BeSameAs(ModelFamilyTable.BuiltIn);

            File.WriteAllText(file, "{ not json");
            ModelFamilyTable broken = ModelFamilyTable.Load(file);
            broken.Warning.Should().Contain("could not be read").And.Contain("using the built-in table");
            broken.Find("qwen/qwen3-coder-30b")!.Bands.Should().HaveCount(3, "the built-in entry");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
