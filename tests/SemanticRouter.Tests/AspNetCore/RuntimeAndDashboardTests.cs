using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Hot reload through the admin API, the configuration store and the dashboard assets.</summary>
public sealed class RuntimeAndDashboardTests : RouterHostTest
{
    private Task<HttpClient> StartDispatchAsync(Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: services);

    private static async Task<JsonNode> ReadNodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Config_endpoint_masks_secrets_and_reports_the_source()
    {
        HttpClient client = await StartDispatchAsync();

        JsonNode envelope = await ReadNodeAsync(await client.GetAsync("/router/api/config"));

        envelope["source"]!.GetValue<string>().Should().Be("Configuration");
        envelope["version"]!.GetValue<int>().Should().Be(1);
        envelope["config"]!["Connectors"]!["anthropic"]!["ApiKey"]!.GetValue<string>().Should().Be("***");
        envelope["config"]!["Connectors"]!["anthropic"]!["Headers"]!["x-tenant"]!.GetValue<string>().Should().Be("acme", "only secret-looking headers are masked");
        envelope["config"]!["Models"]!.AsArray().Should().HaveCount(2);
        envelope["config"]!["Middleware"]!["Mode"]!.GetValue<string>().Should().Be("Dispatch");
    }

    [Fact]
    public async Task Applying_a_document_reloads_the_router_and_keeps_masked_secrets()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode envelope = await ReadNodeAsync(await client.GetAsync("/router/api/config"));
        JsonNode config = envelope["config"]!;
        config["Models"]!.AsArray().Add(new JsonObject
        {
            ["Name"] = "cloud-2", ["Connector"] = "anthropic", ["Model"] = "claude-opus-5-5", ["Tier"] = 5, ["Capabilities"] = new JsonArray("chat"),
        });
        config["Selection"]!["Strategy"] = "MostCapable";

        HttpResponseMessage applied = await client.PutAsync("/router/api/config?persist=false", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"));

        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode result = await ReadNodeAsync(applied);
        result["version"]!.GetValue<int>().Should().Be(2);
        result["source"]!.GetValue<string>().Should().Be("Memory");
        result["warning"].Should().BeNull("nothing was asked to be saved");

        using JsonDocument models = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));
        models.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Contain("cloud-2");

        // MostCapable now picks cloud-2 (tier 5) for a tier-1 prompt, and the masked key still authenticates.
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud-2");
        CloudHttp.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");

        // Asking to save without a store applies in memory and says so.
        JsonNode saved = await ReadNodeAsync(await client.PutAsync("/router/api/config", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json")));
        saved["version"]!.GetValue<int>().Should().Be(3);
        saved["warning"]!.GetValue<string>().Should().Contain("no configuration store");
    }

    [Fact]
    public async Task Invalid_documents_are_rejected_and_leave_the_runtime_alone()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode config = (await ReadNodeAsync(await client.GetAsync("/router/api/config")))["config"]!;
        config["Models"]![0]!["Connector"] = "ghost";
        config["Routes"] = new JsonArray(new JsonObject { ["Name"] = "", ["Utterances"] = new JsonArray() });

        HttpResponseMessage validate = await client.PostAsync("/router/api/config/validate", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"));
        HttpResponseMessage apply = await client.PutAsync("/router/api/config", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"));

        validate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadNodeAsync(validate))["valid"]!.GetValue<bool>().Should().BeFalse();
        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string[] errors = (await ReadNodeAsync(apply))["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();
        errors.Should().Contain(e => e.Contains("connector 'ghost'")).And.Contain(e => e.Contains("must have a Name"));
        (await ReadNodeAsync(await client.GetAsync("/router/api/config")))["version"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Status_and_activity_reflect_routed_requests()
    {
        HttpClient client = await StartDispatchAsync();
        await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        JsonNode status = await ReadNodeAsync(await client.GetAsync("/router/api/status"));
        JsonNode activity = await ReadNodeAsync(await client.GetAsync("/router/api/activity?limit=10"));

        status["models"]!.AsArray().Should().HaveCount(2);
        status["tiers"]!.AsArray().Should().HaveCount(5, "the default ladder");
        status["activity"]!["served"]!.GetValue<int>().Should().Be(1);
        status["activity"]!["byModel"]!["local"]!.GetValue<int>().Should().Be(1);
        JsonNode entry = activity["entries"]!.AsArray().Single()!;
        entry["promptPreview"]!.GetValue<string>().Should().Be("hello there");
        entry["servedBy"]!.GetValue<string>().Should().Be("local");
        entry["outcome"]!.GetValue<string>().Should().Be("served");
        entry["protocol"]!.GetValue<string>().Should().Be("OpenAI");
    }

    [Fact]
    public async Task Dashboard_assets_are_served_with_the_prefix_injected()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage page = await client.GetAsync("/router");
        HttpResponseMessage script = await client.GetAsync("/router/app.js");
        HttpResponseMessage css = await client.GetAsync("/router/app.css");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        string html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("window.ROUTER_PREFIX = \"/router\"").And.Contain("data-theme=\"machine-dark\"").And.NotContain("__ROUTER_PREFIX__");
        script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        (await css.Content.ReadAsStringAsync()).Should().Contain("[data-theme=\"machine-light\"]");
    }

    [Fact]
    public async Task Admin_api_refuses_remote_clients_by_default()
    {
        HttpClient client = await StartAsync(
            o => { o.Mode = PromptRouterMode.Dispatch; o.PathPrefixes.Add("/v1/chat/completions"); },
            beforeRouter: app => app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                return next(context);
            }));

        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/router")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Classify_cooldown_reset_and_embeddings_probe_work_through_the_api()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        HttpClient client = await StartDispatchAsync();
        await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        (await ReadNodeAsync(await client.GetAsync("/router/api/status")))["models"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "local")!["coolingDownUntil"].Should().NotBeNull();

        (await client.PostAsync("/router/api/health/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadNodeAsync(await client.GetAsync("/router/api/status")))["models"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "local")!["coolingDownUntil"].Should().BeNull();

        JsonNode classified = await ReadNodeAsync(await client.PostAsJsonAsync("/router/api/classify", new { prompt = "hello there" }));
        classified["requiredTier"]!.GetValue<int>().Should().Be(1);
        classified["models"]!.AsArray().Should().HaveCountGreaterThan(0);

        JsonNode probe = await ReadNodeAsync(await client.PostAsJsonAsync("/router/api/embeddings/test", new { text = "x" }));
        probe["ok"]!.GetValue<bool>().Should().BeTrue();
        probe["dimensions"]!.GetValue<int>().Should().Be(HashingEmbeddingProvider.DefaultDimensions);
    }

    [Fact]
    public async Task Stored_documents_replace_the_baseline_at_startup_and_inherit_secrets()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("router-store").FullName, "semantic-router.json");
        try
        {
            var stored = new RouterConfiguration { DefaultSimilarityThreshold = 0.42f };
            stored.Routes.Add(Route("stored-route", 2, "something stored"));
            stored.Models.Add(Model("local", 1, local: true, connector: "lmstudio"));
            stored.Models.Add(Model("cloud", 4, costPerMillion: 3m, connector: "anthropic"));
            stored.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
            stored.Connectors["anthropic"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com") }; // no key: inherited
            stored.Middleware.Mode = PromptRouterMode.Dispatch;
            stored.Middleware.PathPrefixes.Add("/v1/chat/completions");
            new JsonFileRouterConfigurationStore(path).Save(stored);

            HttpClient client = await StartDispatchAsync(services: s =>
                s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(path)));

            JsonNode envelope = await ReadNodeAsync(await client.GetAsync("/router/api/config"));
            envelope["source"]!.GetValue<string>().Should().Be("Store");
            envelope["store"]!.GetValue<string>().Should().Be(path);
            envelope["config"]!["DefaultSimilarityThreshold"]!.GetValue<float>().Should().Be(0.42f);
            envelope["config"]!["Routes"]![0]!["Name"]!.GetValue<string>().Should().Be("stored-route");
            envelope["config"]!["Connectors"]!["anthropic"]!["ApiKey"]!.GetValue<string>().Should().Be("***", "inherited from the application's configuration");

            // Saving through the API persists to the same file.
            JsonNode config = envelope["config"]!;
            config["FallbackTier"] = 2;
            (await client.PutAsync("/router/api/config", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.OK);
            RouterConfiguration reloaded = new JsonFileRouterConfigurationStore(path).Load()!;
            reloaded.FallbackTier.Should().Be(2);
            reloaded.Connectors["anthropic"].ApiKey.Should().Be("sk-test", "the real value is stored, not the mask");

            // Reset goes back to the application's own configuration without touching the file.
            JsonNode reset = await ReadNodeAsync(await client.PostAsync("/router/api/config/reset", null));
            reset["source"]!.GetValue<string>().Should().Be("Configuration");
            reset["config"]!["Routes"]!.AsArray().Should().BeEmpty();
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Documents_round_trip_through_json_and_validate_connectors()
    {
        var document = new RouterConfiguration();
        document.Tiers = [new TierDefinition { Level = 1, Name = "One" }, new TierDefinition { Level = 1, Name = "Dup" }];
        document.Models.Add(Model("m", 1, connector: "cli"));
        document.Connectors["cli"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.Custom, ToolCalling = CliToolCalling.Prompted, Timeout = TimeSpan.FromSeconds(90) };
        document.Connectors["cli"].EnvironmentVariables["A"] = "b";
        document.Embeddings.Provider = EmbeddingProviderKind.OpenAICompatible;

        RouterConfiguration copy = RouterConfiguration.FromJson(document.ToJson());
        IReadOnlyList<string> errors = copy.GetDocumentValidationErrors();

        copy.Connectors["cli"].Should().Match<ConnectorOptions>(c => c.ToolCalling == CliToolCalling.Prompted && c.Timeout == TimeSpan.FromSeconds(90));
        copy.Connectors["cli"].EnvironmentVariables.Should().Contain("A", "b");
        copy.Tiers.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains("Tier level 1 is defined more than once"))
            .And.Contain(e => e.Contains("Custom CLI and needs an Executable"))
            .And.Contain(e => e.Contains("Embeddings:Endpoint is required"));
    }

    [Fact]
    public async Task Hot_reload_disposes_the_previous_runtime_and_builds_connectors_fresh()
    {
        await StartDispatchAsync();
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        RouterRuntime first = provider.Current;

        RouterConfiguration next = first.Configuration.Clone();
        next.Connectors.Remove("anthropic");
        next.Models.Remove(next.Models.First(m => m.Name == "cloud"));
        RouterRuntime second = provider.Apply(next, persist: false);

        second.Should().NotBeSameAs(first);
        second.Version.Should().Be(first.Version + 1);
        second.Connectors.Names.Should().BeEquivalentTo("lmstudio");
        provider.Current.Should().BeSameAs(second);
    }
}
