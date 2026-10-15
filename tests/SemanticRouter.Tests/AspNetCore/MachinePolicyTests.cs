using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Desktop;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A machine-wide policy the user's configuration cannot override (#100): read from where administrators write it,
/// enforced at start-up, and refused in configuration changes.
/// </summary>
public sealed class MachinePolicyTests : RouterHostTest
{
    private static StringContent Body(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private Task<HttpClient> StartWithAsync(string policy, bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s => s.AddSingleton(MachinePolicy.FromJson(policy, "test policy")),
            withCli: withCli);

    [Fact]
    public void A_policy_document_is_read_strictly()
    {
        MachinePolicy policy = MachinePolicy.FromJson("""
            {
              // Comments and trailing commas are fine; a misspelt setting is not.
              "DisabledConnectorTypes": ["Cli", "copilot"],
              "DisabledPresets": ["Codex"],
              "AllowedEndpoints": ["api.anthropic.com", "*.corp.example", "10.0.0.5:8443"],
              "Features": { "Judge": 0, "Webhooks": "false" },
              "Updates": { "Mode": "Feed", "Feed": "https://updates.corp.example/router" },
              "MaxRetentionDays": "7",
            }
            """, "/etc/semantic-router/policy.json");

        policy.DisabledConnectorTypes.Should().Equal(ConnectorType.Cli, ConnectorType.Copilot);
        policy.DisabledPresets.Should().Equal(CliPreset.Codex);
        (policy.Features.Judge, policy.Features.Webhooks, policy.Features.Memory).Should().Be((false, false, true));
        (policy.Updates.Mode, policy.MaxRetentionDays, policy.Source).Should().Be((UpdateMode.Feed, 7, "/etc/semantic-router/policy.json"));
        policy.IsEmpty.Should().BeFalse();
        MachinePolicy.FromJson("{}").IsEmpty.Should().BeTrue();

        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"DisableConnectorTypes":["Cli"]}""", "p.json"))
            .Should().Throw<InvalidDataException>().WithMessage("*p.json*", "a typo must not silently allow everything");
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"AllowedEndpoints":["https://x/"]}"""))
            .Should().Throw<InvalidDataException>().WithMessage("*not a host name*");
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"Updates":{"Mode":"Feed"}}"""))
            .Should().Throw<InvalidDataException>().WithMessage("*Updates:Feed*");
    }

    [Fact]
    public void Registry_values_become_the_same_document()
    {
        // HKLM\Software\Policies\SemanticRouter: a multi-string, DWORDs, a string holding JSON, and a subkey.
        JsonObject root = MachinePolicyLoader.FromValues(new Dictionary<string, object?>
        {
            ["DisabledConnectorTypes"] = new[] { "Cli", "Copilot", "" },
            ["RequireLocalOnly"] = 1,
            ["Dashboard"] = "ReadOnly",
            ["LocalHosts"] = """["gpu.corp.example"]""",
        });
        root["Features"] = MachinePolicyLoader.FromValues(new Dictionary<string, object?> { ["TuningApply"] = 0 });

        MachinePolicy policy = MachinePolicy.FromJson(root.ToJsonString(), @"HKLM\Software\Policies\SemanticRouter");

        policy.DisabledConnectorTypes.Should().Equal(ConnectorType.Cli, ConnectorType.Copilot);
        (policy.RequireLocalOnly, policy.Dashboard, policy.Features.TuningApply).Should().Be((true, DashboardPolicy.ReadOnly, false));
        policy.LocalHosts.Should().Equal("gpu.corp.example");
    }

    [Fact]
    public void A_missing_file_is_no_policy_and_a_broken_one_stops_the_router()
    {
        string directory = Directory.CreateTempSubdirectory("policy").FullName;
        try
        {
            MachinePolicyLoader.FromFile(Path.Combine(directory, "policy.json")).Should().BeSameAs(MachinePolicy.None);
            File.WriteAllText(Path.Combine(directory, "policy.json"), "{ not json");
            FluentActions.Invoking(() => MachinePolicyLoader.FromFile(Path.Combine(directory, "policy.json"))).Should().Throw<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Enforcing_turns_off_what_breaks_it_and_says_so()
    {
        var configuration = new RouterConfiguration();
        configuration.Connectors["claude"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };
        configuration.Connectors["gateway"] = new ConnectorOptions { BaseAddress = new Uri("https://llm.corp.example:8443") };
        configuration.Connectors["elsewhere"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com") };
        configuration.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        configuration.Models.Add(Model("claude-cli", 4, connector: "claude"));
        configuration.Models.Add(Model("corp", 3, local: true, connector: "gateway"));
        configuration.Models.Add(Model("cloud", 4, connector: "elsewhere"));
        configuration.Models.Add(Model("local", 1, local: true, connector: "lmstudio"));
        configuration.Middleware.Judge.Enabled = true;
        configuration.State.StorePrompts = PromptStorage.On;
        MachinePolicy policy = MachinePolicy.FromJson("""
            {
              "DisabledConnectorTypes": ["Cli"],
              "AllowedEndpoints": ["*.corp.example"],
              "LocalHosts": [],
              "StorePrompts": "Redacted",
              "MaxRetentionDays": 7,
              "Features": { "Judge": false, "TuningApply": false }
            }
            """);

        IReadOnlyList<string> changes = policy.Enforce(configuration);

        configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("corp", "local");
        configuration.Models.Single(m => m.Name == "corp").IsLocal.Should().BeFalse("a corporate gateway is not this machine unless the policy says so");
        configuration.Models.Single(m => m.Name == "local").IsLocal.Should().BeTrue("loopback is always local");
        (configuration.State.StorePrompts, configuration.State.Retention.ActivityDays).Should().Be((PromptStorage.Redacted, 7));
        (configuration.Middleware.Judge.Enabled, configuration.Tuning.Mode).Should().Be((false, TuningMode.Suggest));
        changes.Should().Contain(c => c.Contains("'claude-cli' is turned off", StringComparison.Ordinal) && c.Contains("Cli connectors", StringComparison.Ordinal))
            .And.Contain(c => c.Contains("'cloud' is turned off", StringComparison.Ordinal) && c.Contains("api.example.com", StringComparison.Ordinal));
        policy.Violations(configuration).Should().BeEmpty("enforcing it once is enough");
        MachinePolicy.None.Enforce(new RouterConfiguration()).Should().BeEmpty();
    }

    [Fact]
    public void Local_only_turns_off_every_model_that_is_not_local_and_remote_embeddings()
    {
        var configuration = new RouterConfiguration();
        configuration.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        configuration.Connectors["cloud"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com") };
        configuration.Models.Add(Model("local", 1, local: true, connector: "lmstudio"));
        configuration.Models.Add(Model("cloud", 4, connector: "cloud"));
        configuration.Embeddings.Provider = EmbeddingProviderKind.OpenAICompatible;
        configuration.Embeddings.Endpoint = new Uri("https://api.example.com");

        MachinePolicy.FromJson("""{"RequireLocalOnly": true}""").Enforce(configuration);

        configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("local");
        configuration.Embeddings.Provider.Should().Be(EmbeddingProviderKind.Hashing);
    }

    [Fact]
    public async Task Disabled_CLI_connectors_are_never_started_and_cannot_be_turned_back_on()
    {
        HttpClient client = await StartWithAsync("""{"DisabledConnectorTypes":["Cli"]}""", withCli: true);

        RouterRuntime runtime = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        runtime.Configuration.Models.Single(m => m.Name == "claude-cli").Enabled.Should().BeFalse();
        runtime.Warnings.Should().Contain(w => w.Contains("'claude-cli' is turned off", StringComparison.Ordinal));
        runtime.Connectors.TryGet("claude", out IChatConnector connector).Should().BeTrue();
        connector.Should().BeOfType<UnavailableConnector>();

        (await client.PostAsync("/v1/chat/completions", Json("""{"model":"claude-cli","messages":[{"role":"user","content":"hello there"}]}""")))
            .Headers.GetValues("X-Prompt-Route-Model").Should().NotContain("claude-cli");
        Cli.Invocations.Should().BeEmpty();

        JsonNode config = (await ReadAsync(await client.GetAsync("/router/api/config")))["config"]!;
        config["Models"]!.AsArray().Single(m => (string?)m!["Name"] == "claude-cli")!["Enabled"] = true;
        HttpResponseMessage validate = await client.PostAsync("/router/api/config/validate", Body(config));
        HttpResponseMessage apply = await client.PutAsync("/router/api/config?persist=false", Body(config));

        (await ReadAsync(validate))["valid"]!.GetValue<bool>().Should().BeFalse();
        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apply.Content.ReadAsStringAsync()).Should().Contain("Not allowed").And.Contain("claude-cli");
    }

    [Fact]
    public async Task Allowed_endpoints_hold_at_start_up_and_on_every_change()
    {
        // The test catalog's cloud connector is https://api.example.com; the local one is loopback.
        HttpClient client = await StartWithAsync("""{"AllowedEndpoints":["api.anthropic.com"]}""");
        RouterRuntime runtime = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        runtime.Configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("local");

        JsonNode config = (await ReadAsync(await client.GetAsync("/router/api/config")))["config"]!;
        config["Connectors"]!["other"] = new JsonObject { ["BaseAddress"] = "https://llm.example.net" };
        config["Models"]!.AsArray().Add(new JsonObject { ["Name"] = "other", ["Connector"] = "other", ["Tier"] = 2 });
        HttpResponseMessage refused = await client.PutAsync("/router/api/config?persist=false", Body(config));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("llm.example.net is not an allowed endpoint");

        config["Connectors"]!["other"]!["BaseAddress"] = "https://api.anthropic.com";
        (await client.PutAsync("/router/api/config?persist=false", Body(config))).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonNode policy = await ReadAsync(await client.GetAsync("/router/api/policy"));
        ((string?)policy["source"], (string?)policy["policy"]!["allowedEndpoints"]![0]).Should().Be(("test policy", "api.anthropic.com"));
    }

    [Theory]
    [InlineData("ReadOnly", HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    [InlineData("Off", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("Default", HttpStatusCode.OK, HttpStatusCode.OK)]
    public async Task The_dashboard_can_be_made_read_only_or_turned_off(string mode, HttpStatusCode read, HttpStatusCode write)
    {
        HttpClient client = await StartWithAsync($$"""{"Dashboard":"{{mode}}"}""");

        HttpResponseMessage get = await client.GetAsync("/router/api/config");
        get.StatusCode.Should().Be(read);
        HttpResponseMessage reset = await client.PostAsync("/router/api/config/reset", null);
        reset.StatusCode.Should().Be(write);
        if (mode != "Default")
        {
            (await reset.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("your organization's policy");
        }
    }

    private static RouterConfiguration Reachable()
    {
        var configuration = new RouterConfiguration();
        configuration.Connectors["claude"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };
        configuration.Connectors["gateway"] = new ConnectorOptions { BaseAddress = new Uri("https://llm.corp.example") };
        configuration.Connectors["cloud"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com") };
        configuration.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        configuration.Models.Add(Model("claude-cli", 4, connector: "claude"));
        configuration.Models.Add(Model("corp", 3, connector: "gateway"));
        configuration.Models.Add(Model("cloud", 4, connector: "cloud"));
        configuration.Models.Add(Model("local", 1, local: true, connector: "lmstudio"));
        configuration.Embeddings.Provider = EmbeddingProviderKind.OpenAICompatible;
        configuration.Embeddings.Endpoint = new Uri("https://api.example.com");
        configuration.Notifications.Webhooks["slack"] = new WebhookOptions { Url = "https://hooks.example.com/T0/B0/x" };
        return configuration;
    }

    [Fact]
    public void Offline_mode_reaches_only_this_machine_and_the_allowed_hosts()
    {
        var network = new NetworkOptions { Offline = true };
        network.AllowedHosts.Add("*.corp.example");
        MachinePolicy offline = MachinePolicy.Effective(MachinePolicy.None, network);
        RouterConfiguration configuration = Reachable();

        IReadOnlyList<string> changes = offline.Enforce(configuration);

        configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("corp", "local");
        configuration.Embeddings.Provider.Should().Be(EmbeddingProviderKind.Hashing);
        configuration.Notifications.Webhooks.Should().BeEmpty();
        changes.Should().Contain(c => c.Contains("'claude-cli'", StringComparison.Ordinal) && c.Contains("reach their vendor, so they are off in offline mode (Network:Offline)", StringComparison.Ordinal))
            .And.Contain(c => c.Contains("'cloud'", StringComparison.Ordinal) && c.Contains("api.example.com is not reachable in offline mode", StringComparison.Ordinal))
            .And.Contain(c => c.StartsWith("Webhook 'slack'", StringComparison.Ordinal));
        MachinePolicy.Effective(MachinePolicy.None, new NetworkOptions()).Should().BeSameAs(MachinePolicy.None, "without offline mode nothing changes");
    }

    [Fact]
    public void Offline_mode_set_by_the_machine_policy_is_not_widened_by_the_users_hosts()
    {
        var network = new NetworkOptions();
        network.AllowedHosts.Add("*.corp.example");
        MachinePolicy policy = MachinePolicy.Effective(MachinePolicy.FromJson("""{"Offline": true}"""), network);
        RouterConfiguration configuration = Reachable();

        IReadOnlyList<string> changes = policy.Enforce(configuration);

        configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("local");
        changes.Should().Contain(c => c.Contains("llm.corp.example is not reachable in offline mode (your organization's policy)", StringComparison.Ordinal));

        MachinePolicy listed = MachinePolicy.FromJson("""{"Offline": true, "AllowedEndpoints": ["*.corp.example"]}""");
        listed.AllowsEndpoint(new Uri("https://llm.corp.example")).Should().BeTrue("the policy's own endpoints are reachable offline");
    }

    [Fact]
    public async Task Offline_mode_holds_at_start_up_and_on_every_change()
    {
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s => s.Configure<NetworkOptions>(n => n.Offline = true));

        RouterRuntime runtime = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        runtime.Configuration.Models.Where(m => m.Enabled).Select(m => m.Name).Should().Equal("local");
        runtime.Warnings.Should().Contain(w => w.Contains("api.example.com is not reachable in offline mode (Network:Offline)", StringComparison.Ordinal));

        JsonNode config = (await ReadAsync(await client.GetAsync("/router/api/config")))["config"]!;
        config["Models"]!.AsArray().Single(m => (string?)m!["Name"] == "cloud")!["Enabled"] = true;
        HttpResponseMessage apply = await client.PutAsync("/router/api/config?persist=false", Body(config));
        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apply.Content.ReadAsStringAsync()).Should().Contain("offline mode");

        new NetworkOptionsValidator().Validate(null, new NetworkOptions { AllowedHosts = { "https://x/" } }).Failed.Should().BeTrue();
    }

    [Fact]
    public void Desktop_updates_follow_the_policy_and_never_come_over_plain_HTTP()
    {
        static string? Environment(string name) => name == "SEMANTICROUTER_UPDATE_SOURCE" ? "https://example.com/feed" : null;

        DesktopUpdater.Feed(new UpdatePolicy(), _ => null).Should().Be(((string?)null, (string?)null), "GitHub Releases");
        DesktopUpdater.Feed(new UpdatePolicy(), Environment).Should().Be(("https://example.com/feed", (string?)null));
        DesktopUpdater.Feed(new UpdatePolicy { Mode = UpdateMode.Off }, Environment).Off.Should().Contain("organization's policy");
        DesktopUpdater.Feed(new UpdatePolicy { Mode = UpdateMode.Feed, Feed = "https://mirror.corp.example/router" }, Environment)
            .Should().Be(("https://mirror.corp.example/router", (string?)null), "the policy's feed wins over the variable");
        DesktopUpdater.Feed(new UpdatePolicy(), _ => "http://mirror.corp.example/router").Off.Should().Contain("not HTTPS");
        DesktopUpdater.Feed(new UpdatePolicy(), _ => "http://localhost:8080/").Should().Be(("http://localhost:8080/", (string?)null));
    }
}
