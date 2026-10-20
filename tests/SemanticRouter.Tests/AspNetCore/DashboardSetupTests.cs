using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The admin API the dashboard's first run and draft safety rely on: setup, draft discovery, saved state, version
/// conflicts, field-level validation issues, and the gateway's answer when no model can serve a request.
/// </summary>
public sealed class DashboardSetupTests : RouterHostTest, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-setup-").FullName;

    private string File => Path.Combine(_directory, "router.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<HttpClient> StartDispatchAsync(Action<SemanticRouterOptions>? routing = null, Action<IServiceCollection>? services = null, bool store = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing,
            s =>
            {
                if (store)
                {
                    s.AddSemanticPromptRouting(_ => { }).UseConfigurationFile(File);
                }

                services?.Invoke(s);
            });

    private static async Task<JsonNode> NodeAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static StringContent Body(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonNode> ConfigAsync(HttpClient client) => (await NodeAsync(await client.GetAsync("/router/api/config")))["config"]!;

    private static async Task<bool> UnsavedAsync(HttpClient client) => (bool)(await NodeAsync(await client.GetAsync("/router/api/status")))["unsaved"]!;

    // ---- Contract 5: saved state, save and revert ----

    [Fact]
    public async Task A_configuration_applied_without_saving_is_reported_and_can_be_saved_or_reverted()
    {
        HttpClient client = await StartDispatchAsync(store: true);
        (await UnsavedAsync(client)).Should().BeFalse("nothing is stored yet, and a restart would load the same configuration");

        JsonNode config = await ConfigAsync(client);
        config["Selection"]!["Strategy"] = "MostCapable";
        JsonNode applied = await NodeAsync(await client.PutAsync("/router/api/config?persist=false", Body(config)));
        ((string?)applied["source"], (bool)applied["unsaved"]!).Should().Be(("Memory", true));
        (await UnsavedAsync(client)).Should().BeTrue();

        // Revert with nothing stored: the application's configuration again.
        JsonNode reverted = await NodeAsync(await client.PostAsync("/router/api/config/revert", null));
        ((string?)reverted["source"]).Should().Be("Configuration");
        ((string?)reverted["config"]!["Selection"]!["Strategy"]).Should().NotBe("MostCapable");
        (await UnsavedAsync(client)).Should().BeFalse();

        // Save: the running configuration goes to the file as it is.
        await client.PutAsync("/router/api/config?persist=false", Body(config));
        HttpResponseMessage save = await client.PostAsync("/router/api/config/save", null);
        save.StatusCode.Should().Be(HttpStatusCode.OK);
        ((string?)(await NodeAsync(save))["source"]).Should().Be("Store");
        (await UnsavedAsync(client)).Should().BeFalse();
        (await System.IO.File.ReadAllTextAsync(File)).Should().Contain("MostCapable");

        // Revert with something stored: what the file holds.
        config["Selection"]!["Strategy"] = "Priority";
        await client.PutAsync("/router/api/config?persist=false", Body(config));
        (await UnsavedAsync(client)).Should().BeTrue();
        JsonNode back = await NodeAsync(await client.PostAsync("/router/api/config/revert", null));
        ((string?)back["source"], (string?)back["config"]!["Selection"]!["Strategy"]).Should().Be(("Store", "MostCapable"));
        (await UnsavedAsync(client)).Should().BeFalse();

        string history = await client.GetStringAsync("/router/api/config/history");
        history.Should().Contain("\"source\":\"save\"").And.Contain("\"source\":\"revert\"");
        string audit = await client.GetStringAsync("/router/api/audit");
        audit.Should().Contain("saved the running configuration").And.Contain("went back to the saved configuration");
    }

    [Fact]
    public async Task Without_a_store_nothing_is_unsaved_and_save_says_why_it_cannot()
    {
        HttpClient client = await StartDispatchAsync();
        await client.PutAsync("/router/api/config?persist=false", Body(await ConfigAsync(client)));

        (await UnsavedAsync(client)).Should().BeFalse("there is no file a restart would load instead");
        HttpResponseMessage save = await client.PostAsync("/router/api/config/save", null);
        HttpResponseMessage revert = await client.PostAsync("/router/api/config/revert", null);
        save.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ((string?)(await NodeAsync(save))["detail"]).Should().Contain("no configuration file");
        revert.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Contract 6: no silent saves of unsaved changes ----

    [Fact]
    public async Task Adding_an_utterance_saves_only_when_the_running_configuration_is_saved()
    {
        HttpClient client = await StartDispatchAsync(store: true);
        await client.PostAsync("/router/api/config/save", null);

        JsonNode saved = await NodeAsync(await client.PostAsJsonAsync("/router/api/routes/greetings/utterances", new { utterance = "good morning", tier = 1 }));
        ((bool)saved["saved"]!).Should().BeTrue();
        (await System.IO.File.ReadAllTextAsync(File)).Should().Contain("good morning");

        JsonNode config = await ConfigAsync(client);
        config["Selection"]!["Strategy"] = "MostCapable";
        await client.PutAsync("/router/api/config?persist=false", Body(config));

        JsonNode notSaved = await NodeAsync(await client.PostAsJsonAsync("/router/api/routes/greetings/utterances", new { utterance = "good evening" }));
        ((bool)notSaved["saved"]!).Should().BeFalse("saving it would also save the strategy change nobody saved");
        string file = await System.IO.File.ReadAllTextAsync(File);
        file.Should().NotContain("good evening").And.NotContain("MostCapable");
        (await UnsavedAsync(client)).Should().BeTrue();
        (await client.GetStringAsync("/router/api/audit")).Should().Contain("not saved");
    }

    [Fact]
    public async Task Learned_changes_follow_the_running_configurations_saved_state()
    {
        await StartDispatchAsync(store: true);
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        provider.SaveRunning();

        provider.ApplyKeepingSaveState(c => Set(c, threshold: 0.33f), "auto-tune")!.Value.Saved.Should().BeTrue();
        provider.Unsaved.Should().BeFalse();

        RouterConfiguration next = provider.Current.Configuration.Clone();
        next.FallbackTier = 2;
        provider.Apply(next, persist: false);
        (RouterRuntime runtime, bool savedNow) = provider.ApplyKeepingSaveState(c => Set(c, threshold: 0.44f), "auto-tune")!.Value;

        savedNow.Should().BeFalse();
        runtime.Source.Should().Be(RouterConfigurationSource.Memory);
        provider.Store!.Load()!.DefaultSimilarityThreshold.Should().BeApproximately(0.33f, 0.0001f, "the file keeps the last saved version");
        provider.ApplyKeepingSaveState(_ => false, "auto-tune").Should().BeNull("a change that no longer applies applies nothing");
    }

    [Fact]
    public async Task A_change_on_top_of_the_running_configuration_is_made_again_when_another_version_comes_in()
    {
        await StartDispatchAsync(store: true);
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        provider.SaveRunning();

        int calls = 0;
        (RouterRuntime runtime, bool saved) = provider.ApplyKeepingSaveState(next =>
        {
            if (++calls == 1)
            {
                // Someone applies a version after this copy was made.
                RouterConfiguration other = provider.Current.Configuration.Clone();
                other.FallbackTier = 2;
                provider.Apply(other, persist: true);
            }

            return Set(next, threshold: 0.33f);
        }, "auto-tune")!.Value;

        calls.Should().Be(2);
        (runtime.Configuration.FallbackTier, runtime.Configuration.DefaultSimilarityThreshold).Should().Be((2, 0.33f), "the other change is kept, not undone");
        saved.Should().BeTrue();

        // A configuration that keeps changing: given up after a few tries.
        provider.Invoking(p => p.ApplyKeepingSaveState(next =>
            {
                p.Apply(p.Current.Configuration.Clone(), persist: false);
                return Set(next, threshold: 0.44f);
            }, "auto-tune"))
            .Should().Throw<RouterConfigurationConflictException>();
        provider.Current.Configuration.DefaultSimilarityThreshold.Should().Be(0.33f);
    }

    private static bool Set(RouterConfiguration configuration, float threshold)
    {
        configuration.DefaultSimilarityThreshold = threshold;
        return true;
    }

    // ---- Contract 3: version conflicts ----

    [Fact]
    public async Task A_change_based_on_an_old_version_is_refused_with_who_changed_it()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode config = await ConfigAsync(client);

        (await client.PutAsync("/router/api/config?persist=false&baseVersion=1", Body(config))).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage stale = await client.PutAsync("/router/api/config?persist=false&baseVersion=1", Body(config));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonNode conflict = await NodeAsync(stale);
        ((string?)conflict["title"]).Should().Be("The configuration changed since you loaded it.");
        ((int)conflict["currentVersion"]!).Should().Be(2);
        ((string?)conflict["actor"]).Should().Be("this machine");
        ((string?)conflict["source"]).Should().Be("admin API");
        DateTimeOffset.Parse((string)conflict["at"]!, System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        ((int)(await NodeAsync(await client.GetAsync("/router/api/config")))["version"]!).Should().Be(2, "nothing was applied");

        (await client.PutAsync("/router/api/config?persist=false", Body(config))).StatusCode.Should().Be(HttpStatusCode.OK, "without baseVersion nothing changes");
        Services.GetRequiredService<RouterRuntimeProvider>().Invoking(p => p.Apply(p.Current.Configuration.Clone(), persist: false, baseVersion: 1))
            .Should().Throw<RouterConfigurationConflictException>().Which.Current.Version.Should().Be(3);
    }

    [Fact]
    public async Task A_change_based_on_another_run_of_the_router_is_refused_even_with_the_same_version()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode envelope = await NodeAsync(await client.GetAsync("/router/api/config"));
        string instance = (string)envelope["instance"]!;
        JsonNode config = envelope["config"]!;

        instance.Should().MatchRegex("^[0-9a-f]{12}$");
        ((string?)(await NodeAsync(await client.GetAsync("/router/api/status")))["instance"]).Should().Be(instance);

        // Versions start at 1 on every start: version 1 of the run before a restart is not this one's.
        HttpResponseMessage restarted = await client.PutAsync("/router/api/config?persist=false&baseVersion=1&instance=0123456789ab", Body(config));
        restarted.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonNode conflict = await NodeAsync(restarted);
        ((string?)conflict["instance"], (int)conflict["currentVersion"]!).Should().Be((instance, 1));
        ((string?)conflict["detail"]).Should().StartWith("The router restarted since you loaded it.");
        (await client.PutAsync("/router/api/config?persist=false&instance=0123456789ab", Body(config))).StatusCode.Should().Be(HttpStatusCode.Conflict, "the instance alone is checked too");

        HttpResponseMessage same = await client.PutAsync($"/router/api/config?persist=false&baseVersion=1&instance={instance}", Body(config));
        same.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode applied = await NodeAsync(same);
        ((int)applied["version"]!, (string?)applied["instance"]).Should().Be((2, instance));
        ((string?)(await NodeAsync(await client.PutAsync("/router/api/config?persist=false&baseVersion=1", Body(config))))["instance"]).Should().Be(instance, "a version conflict names the instance too");
    }

    // ---- Contract 4: issues tied to fields ----

    [Fact]
    public async Task Validation_errors_name_the_field_they_are_about()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode config = await ConfigAsync(client);
        config["Models"]![1]!["Connector"] = "ghost";
        config["Routes"] = new JsonArray(
            new JsonObject { ["Name"] = "ok", ["Tier"] = 1, ["Utterances"] = new JsonArray("hi") },
            new JsonObject { ["Name"] = "", ["Tier"] = 1, ["Utterances"] = new JsonArray("x") });
        config["Connectors"]!["lmstudio"]!["MaxQueue"] = -1;

        HttpResponseMessage apply = await client.PutAsync("/router/api/config", Body(config));
        HttpResponseMessage validate = await client.PostAsync("/router/api/config/validate", Body(config));

        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode result = await NodeAsync(apply);
        result["errors"]!.AsArray().Should().HaveCount(result["issues"]!.AsArray().Count);
        var paths = result["issues"]!.AsArray().Select(i => (string?)i!["path"]).ToArray();
        paths.Should().Contain(["Models.1.Connector", "Routes.1.Name", "Connectors.lmstudio.MaxQueue"]);
        result["issues"]!.AsArray().Should().OnlyContain(i => !string.IsNullOrEmpty((string?)i!["message"]));
        (await NodeAsync(validate))["issues"]!.AsArray().Select(i => (string?)i!["path"]).Should().Contain("Routes.1.Name");
    }

    [Fact]
    public async Task A_value_that_does_not_fit_its_field_is_named_with_the_format_it_needs()
    {
        HttpClient client = await StartDispatchAsync();
        JsonNode config = await ConfigAsync(client);
        config["Connectors"]!["lmstudio"]!["Timeout"] = "5m";

        HttpResponseMessage apply = await client.PutAsync("/router/api/config", Body(config));

        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode result = await NodeAsync(apply);
        result["errors"]!.AsArray().Select(e => (string?)e).Should().Equal("Connectors.lmstudio.Timeout: \"5m\" is not a duration; use hh:mm:ss, e.g. 00:05:00");
        JsonNode issue = result["issues"]![0]!;
        ((string?)issue["path"], (string?)issue["message"]).Should().Be(("Connectors.lmstudio.Timeout", "\"5m\" is not a duration; use hh:mm:ss, e.g. 00:05:00"));

        config["Connectors"]!["lmstudio"]!["Timeout"] = "00:05:00";
        config["Connectors"]!["lmstudio"]!["Type"] = "Telepathy";
        JsonNode badEnum = await NodeAsync(await client.PostAsync("/router/api/config/validate", Body(config)));
        ((string?)badEnum["issues"]![0]!["path"]).Should().Be("Connectors.lmstudio.Type");
        ((string?)badEnum["issues"]![0]!["message"]).Should().Contain("no installed connector package provides connector type 'Telepathy'").And.Contain("OpenAICompatible");

        config["Connectors"]!["lmstudio"]!["Type"] = "OpenAICompatible";
        config["Models"]![0]!["Tier"] = "high";
        ((string?)(await NodeAsync(await client.PutAsync("/router/api/config", Body(config))))["errors"]![0]).Should().Be("Models.0.Tier: \"high\" is not a whole number");

        JsonNode broken = await NodeAsync(await client.PutAsync("/router/api/config", Body("{\"Models\": [")));
        ((string?)broken["errors"]![0]).Should().StartWith("The configuration is not valid JSON");
        broken["issues"]![0]!["path"].Should().BeNull();
    }

    [Theory]
    [InlineData("Connector 'local' MaxConcurrency must be at least 1.", "Connectors.local.MaxConcurrency")]
    [InlineData("Connector 'local' needs an absolute BaseAddress.", "Connectors.local.BaseAddress")]
    [InlineData("Connectors:local:BaseAddress: the address was changed, so enter the credentials in it (shown as ***) again.", "Connectors.local.BaseAddress")]
    [InlineData("Connectors:local:ApiKey: enter the secret again.", "Connectors.local.ApiKey")]
    [InlineData("Middleware:RateLimits:* limits must be at least 1.", "Middleware.RateLimits.*.RequestsPerMinute")]
    [InlineData("Middleware:RateLimits:* needs RequestsPerMinute, TokensPerMinute or both.", "Middleware.RateLimits.*.RequestsPerMinute")]
    [InlineData("Embeddings:Endpoint is required for the OpenAICompatible provider.", "Embeddings.Endpoint")]
    [InlineData("Usage:WarnAtPercent must be between 1 and 100.", "Usage.WarnAtPercent")]
    [InlineData("Budget 'daily' covers model 'ghost', which is not in the catalog.", "Usage.Budgets.0.Model")]
    [InlineData("Budget 'daily' needs MaxTokens or MaxCostUsd.", "Usage.Budgets.0.MaxTokens")]
    [InlineData("Routes[1] must have a Name.", "Routes.1.Name")]
    [InlineData("Route 'chat' must have at least one non-empty utterance.", "Routes.0.Utterances")]
    [InlineData("Route 'chat' SimilarityThreshold must be between -1 and 1.", "Routes.0.SimilarityThreshold")]
    [InlineData("Route name 'chat' is used more than once.", "Routes.1.Name")]
    [InlineData("Model 'big' uses connector 'ghost', which is not configured under Connectors.", "Models.1.Connector")]
    [InlineData("Model 'big' ActiveHours '25:00' is not HH:mm-HH:mm.", "Models.1.ActiveHours")]
    [InlineData("Profile 'work' names model 'ghost', which is not in the catalog.", "Profiles.work.DeniedModels")]
    [InlineData("Tier 'Fast' must not have a negative Level.", "Tiers.0.Level")]
    [InlineData("Memory source 'notes' needs a Path (the folder of notes).", "Memory.Sources.0.Path")]
    [InlineData("Memory:Sources:notes:Path: which folders and files the router reads is changed in the configuration file.", "Memory.Sources.0.Path")]
    [InlineData("Selection.MaxAttempts must be at least 1.", "Selection.MaxAttempts")]
    [InlineData("Complexity.CodeWeight must be between 0 and 1.", "Complexity.CodeWeight")]
    [InlineData("TokenSaturation must be positive.", "Complexity.TokenSaturation")]
    [InlineData("DefaultSimilarityThreshold must be between -1 and 1.", "DefaultSimilarityThreshold")]
    [InlineData("Selection cooldowns must not be negative.", "Selection.FailureCooldown")]
    [InlineData("Virtual model 'fast' names profile 'ghost', which is not configured.", "Middleware.VirtualModels.fast.Profile")]
    [InlineData("Not allowed: Model 'big' is turned off: connector 'local': Cli connectors are turned off by your organization's policy.", "Models.1.Enabled")]
    [InlineData("Not allowed: Prompt previews are stored as 'Redacted' (not 'Full'): your organization's policy.", "State.StorePrompts")]
    [InlineData("Tuning:Interval must be at least one minute.", "Tuning.Interval")]
    [InlineData("A route is LocalOnly but no enabled model is marked IsLocal, so it could never be served.", null)]
    public void Messages_map_to_the_dashboards_field_paths(string error, string? path)
    {
        var document = new RouterConfiguration();
        document.Routes.Add(TestEmbeddings.Route("chat", 1, "hi"));
        document.Routes.Add(TestEmbeddings.Route("chat", 2, "hello"));
        document.Models.Add(TestEmbeddings.Model("small", 1, connector: "local"));
        document.Models.Add(TestEmbeddings.Model("big", 3, connector: "local"));
        document.Tiers = [new TierDefinition { Level = -1, Name = "Fast" }];
        document.Profiles["work"] = new RoutingProfile { DeniedModels = { "ghost" } };
        document.Usage.Budgets.Add(new BudgetDefinition { Name = "daily", Model = "ghost" });
        document.Memory.Sources.Add(new MemorySourceOptions { Name = "notes" });
        document.Middleware.RateLimits["*"] = new RateLimitOptions { RequestsPerMinute = 0 };

        ConfigurationIssues.PathOf(error, document).Should().Be(path);
    }

    // ---- Contract 7: no model can serve this ----

    [Fact]
    public async Task With_no_model_enabled_the_gateway_answers_400_with_where_to_fix_it_in_each_protocol()
    {
        HttpClient client = await StartDispatchAsync(routing: o =>
        {
            foreach (ModelDefinition model in o.Models)
            {
                model.Enabled = false;
            }
        });

        HttpResponseMessage openAI = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage anthropic = await client.PostAsync(
            "/v1/messages", Json("""{"model":"auto","max_tokens":64,"messages":[{"role":"user","content":"hello there"}]}"""));

        const string message = "SemanticRouter has no model that can answer this (no model is enabled). Add one on the dashboard: http://localhost/router#/overview";
        openAI.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the SDKs and Claude Code retry 5xx, and retrying cannot help");
        JsonNode error = await NodeAsync(openAI);
        ((string?)error["error"]!["type"], (string?)error["error"]!["message"]).Should().Be(("invalid_request_error", message));
        anthropic.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode anthropicError = await NodeAsync(anthropic);
        ((string?)anthropicError["type"], (string?)anthropicError["error"]!["type"], (string?)anthropicError["error"]!["message"])
            .Should().Be(("error", "invalid_request_error", message));

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();
        (entry.Outcome, entry.Detail).Should().Be(("failed", message), "the activity log still records the request");
    }

    [Fact]
    public async Task When_no_enabled_model_fits_the_request_the_answer_says_what_it_needs()
    {
        ClassifyAs(tier: 1, localOnly: true, route: "pii");
        HttpClient client = await StartDispatchAsync(routing: o => o.Models.Single(m => m.Name == "local").Enabled = false);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((string?)(await NodeAsync(response))["error"]!["message"]).Should().Be(
            "SemanticRouter has no model that can answer this (no enabled model fits what it needs: tier 1, local only). " +
            "Enable or add one that fits on the dashboard: http://localhost/router#/models");
    }

    [Fact]
    public async Task When_the_prompt_could_not_be_classified_the_answer_stays_a_retryable_503()
    {
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("embeddings down"));
        HttpClient client = await StartDispatchAsync(routing: o =>
        {
            RouteDefinition customers = TestEmbeddings.Route("customers", 1, "look up the customer record");
            customers.LocalOnly = true;
            o.Routes.Add(customers);
        });

        // Unclassified, the fallback stays local (a local-only route might have covered it), and the local model has no tools.
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","messages":[{"role":"user","content":"hello there"}],
             "tools":[{"type":"function","function":{"name":"f","parameters":{"type":"object"}}}]}
            """));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "once the prompt can be classified, it may go to another model");
        (await response.Content.ReadAsStringAsync()).Should().Contain("could not classify this prompt").And.Contain("local only");
    }

    [Fact]
    public async Task When_the_only_models_that_fit_are_outside_their_active_hours_the_answer_is_a_retryable_503()
    {
        HttpClient client = await StartDispatchAsync(
            routing: o =>
            {
                o.Selection.TimeZone = "UTC";
                o.Models.ToList().ForEach(m => m.ActiveHours = "20:00-23:00");
            },
            services: s => s.AddSingleton<TimeProvider>(new ManualTimeProvider())); // 12:00 UTC

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the models come back at 20:00");
        (await response.Content.ReadAsStringAsync()).Should().Contain("no model is in its active hours right now").And.Contain("Try again later");
    }

    [Fact]
    public async Task Models_that_exist_but_failed_still_get_a_retryable_503()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        CloudHttp = StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError);
        HttpClient client = await StartDispatchAsync();

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ---- Contract 1: setup ----

    [Fact]
    public async Task Setup_lists_the_presets_and_what_the_detector_found_without_changing_anything()
    {
        var detector = new FixedDetector(
            new SetupDetection("claude-code", "Claude Code", "cli", "claude-cli",
                new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode, ToolCalling = CliToolCalling.Prompted })
            {
                Detail = "Signed in · claude 2.1.3 at /usr/local/bin/claude",
                Consent = SetupApi.Consent(CliPreset.ClaudeCode),
                Models = [new ModelDefinition { Name = "claude-sonnet", Connector = "claude-cli", Model = "sonnet", Tier = 3 }],
                Aliases = new Dictionary<string, string> { ["*opus*"] = "family:opus" },
            });
        HttpClient client = await StartDispatchAsync(services: s => s.AddSingleton<IRouterSetupDetector>(detector));

        JsonNode setup = await NodeAsync(await client.GetAsync("/router/api/setup"));
        await client.GetAsync("/router/api/setup");

        ((bool)setup["needed"]!).Should().BeFalse("the harness has enabled models");
        JsonNode found = setup["detected"]!.AsArray().Single()!;
        ((string?)found["id"], (string?)found["kind"], (string?)found["detail"]).Should().Be(("claude-code", "cli", "Signed in · claude 2.1.3 at /usr/local/bin/claude"));
        ((string?)found["consent"]).Should().StartWith("Uses your Claude subscription");
        ((string?)found["connector"]!["name"]).Should().Be("claude-cli");
        ((string?)found["connector"]!["settings"]!["Preset"], (string?)found["connector"]!["settings"]!["ToolCalling"]).Should().Be(("ClaudeCode", "Prompted"));
        ((string?)found["models"]![0]!["Model"], (string?)found["models"]![0]!["Connector"]).Should().Be(("sonnet", "claude-cli"));
        ((string?)found["aliases"]!["*opus*"]).Should().Be("family:opus");
        detector.Calls.Should().Be(1, "a second look within a short while reuses the first");

        JsonNode[] presets = [.. setup["presets"]!.AsArray().Select(p => p!)];
        presets.Select(p => (string?)p["id"]).Should().Equal("lmstudio", "ollama", "openai", "anthropic", "openrouter", "gemini", "claude-code", "codex", "copilot");
        JsonNode openai = presets.Single(p => (string?)p["id"] == "openai");
        ((bool)openai["needsKey"]!, (string?)openai["connector"]!["settings"]!["ApiKey"], (string?)openai["keyVariable"])
            .Should().Be((true, "${env:OPENAI_API_KEY}", "OPENAI_API_KEY"));
        ((bool)presets.Single(p => (string?)p["id"] == "lmstudio")["needsKey"]!).Should().BeFalse();
        ((string?)presets.Single(p => (string?)p["id"] == "lmstudio")["connector"]!["name"]).Should().Be("lmstudio", "the harness's lmstudio connector points at the same place");
        ((string?)presets.Single(p => (string?)p["id"] == "anthropic")["connector"]!["name"]).Should().Be("anthropic-2", "the harness's anthropic connector points elsewhere");
        JsonNode gemini = presets.Single(p => (string?)p["id"] == "gemini")["connector"]!["settings"]!;
        ((string?)gemini["BaseAddress"], (string?)gemini["RequestPath"]).Should().Be(("https://generativelanguage.googleapis.com", "/v1beta/openai/chat/completions"));
        ((string?)presets.Single(p => (string?)p["id"] == "copilot")["connector"]!["settings"]!["Preset"]).Should().Be("GitHubCopilot", "without the Copilot SDK package, the CLI preset");

        ((int)(await NodeAsync(await client.GetAsync("/router/api/config")))["version"]!).Should().Be(1, "setup only describes");
        (await client.GetStringAsync("/router/api/audit")).Should().Contain("looked for models on this machine");
    }

    [Fact]
    public async Task Setup_says_when_a_model_is_needed_and_leaves_out_what_the_policy_or_offline_mode_turns_off()
    {
        var detector = new FixedDetector(new SetupDetection("claude-code", "Claude Code", "cli", "claude-cli", new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode }));
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.ToList().ForEach(m => m.Enabled = false),
            services: s =>
            {
                s.AddSingleton<IRouterSetupDetector>(detector);
                s.AddSingleton(MachinePolicy.FromJson("""{ "DisabledConnectorTypes": [ "Cli" ] }""", "test policy"));
                s.Configure<NetworkOptions>(o => o.Offline = true);
            });

        JsonNode setup = await NodeAsync(await client.GetAsync("/router/api/setup"));

        ((bool)setup["needed"]!).Should().BeTrue();
        setup["detected"]!.AsArray().Should().BeEmpty("the policy turns CLI connectors off");
        setup["presets"]!.AsArray().Select(p => (string?)p!["id"]).Should().Equal(["lmstudio", "ollama"], "offline, only this machine's servers are reachable");
    }

    [Fact]
    public async Task Without_a_detector_setup_offers_the_presets()
    {
        HttpClient client = await StartDispatchAsync();

        JsonNode setup = await NodeAsync(await client.GetAsync("/router/api/setup"));

        setup["detected"]!.AsArray().Should().BeEmpty();
        setup["presets"]!.AsArray().Should().HaveCount(9);
    }

    [Fact]
    public async Task Setup_starts_nothing_for_another_site_and_a_refresh_reuses_a_look_younger_than_ten_seconds()
    {
        var time = new ManualTimeProvider();
        var detector = new FixedDetector();
        HttpClient client = await StartDispatchAsync(services: s =>
        {
            s.AddSingleton<IRouterSetupDetector>(detector);
            s.AddSingleton<TimeProvider>(time);
        });

        HttpRequestMessage Setup(string? fetchSite, string? origin = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/router/api/setup?refresh=true");
            if (fetchSite is not null)
            {
                request.Headers.Add("Sec-Fetch-Site", fetchSite);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return request;
        }

        // <img src="http://localhost:5080/router/api/setup?refresh=true"> on another site.
        (await client.SendAsync(Setup("cross-site"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Setup("same-site"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Setup(null, "http://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        detector.Calls.Should().Be(0, "nothing is started for a refused request");

        (await client.SendAsync(Setup("same-origin"))).StatusCode.Should().Be(HttpStatusCode.OK, "the dashboard's own fetch");
        (await client.SendAsync(Setup(null, "http://localhost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        detector.Calls.Should().Be(1, "a refresh within 10 seconds of the last look reuses it");

        time.Advance(TimeSpan.FromSeconds(11));
        (await client.SendAsync(Setup(null))).StatusCode.Should().Be(HttpStatusCode.OK, "scripts send no Origin");
        detector.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_detection_that_fails_still_answers_with_the_presets()
    {
        HttpClient client = await StartDispatchAsync(services: s =>
            s.AddSingleton<IRouterSetupDetector>(new ThrowingDetector(new System.ComponentModel.Win32Exception("An error occurred trying to start process 'claude'."))));

        HttpResponseMessage response = await client.GetAsync("/router/api/setup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode setup = await NodeAsync(response);
        ((string?)setup["detectionProblem"]).Should().Be("Could not look for models on this machine: An error occurred trying to start process 'claude'.");
        setup["presets"]!.AsArray().Should().HaveCount(9);
        setup["detected"]!.AsArray().Should().BeEmpty();
    }

    // ---- Contract 2: discovery for a draft connector ----

    [Fact]
    public async Task A_draft_connector_is_discovered_without_being_applied()
    {
        LocalHttp = LocalServers.LmStudio(justInTime: true, chatId: "qwen3-8b");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/router/api/connectors/discover", new
        {
            name = "lmstudio",
            settings = new { Type = "OpenAICompatible", BaseAddress = "http://localhost:1234", Timeout = "00:01:00" },
            models = Array.Empty<object>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode result = await NodeAsync(response);
        ((string?)result["server"]).Should().Be("LM Studio");
        JsonNode qwen = result["lineup"]!.AsArray().Single(e => (string?)e!["id"] == "qwen3-8b")!;
        qwen["inCatalog"].Should().BeNull("the draft's own (empty) catalog is used, not the running one");
        ((int)(await NodeAsync(await client.GetAsync("/router/api/config")))["version"]!).Should().Be(1, "nothing is applied");
        (await client.GetStringAsync("/router/api/audit")).Should().Contain("discovered a draft connector\\u0027s models: lmstudio (OpenAICompatible) at http://localhost:1234, not applied");

        HttpResponseMessage cli = await client.PostAsJsonAsync("/router/api/connectors/discover", new { name = "claude-new", settings = new { Type = "Cli", Preset = "ClaudeCode" } });
        (await NodeAsync(cli))["models"]!.AsArray().Select(m => (string?)m).Should().Contain(["haiku", "sonnet", "opus"]);
        Cli.Invocations.Should().BeEmpty("a CLI's names come from its preset, nothing is run");
    }

    [Theory]
    [InlineData("OpenAICompatible", "https://user:secret@api.example.com/v1/chat?key=sk-1", null, "https://api.example.com:443")]
    [InlineData("OpenAICompatible", "http://[::1]:1234/", null, "http://[::1]:1234")]
    [InlineData("Cli", null, "/opt/tools/claude", "claude")]
    [InlineData("Cli", null, null, "claude")]
    [InlineData("Copilot", null, null, "Copilot")]
    public void The_audit_names_where_a_draft_connector_was_asked_without_its_path_or_credentials(string type, string? address, string? executable, string expected)
    {
        var connector = new ConnectorOptions
        {
            Type = type,
            BaseAddress = address is null ? null : new Uri(address),
            Executable = executable,
            Preset = CliPreset.ClaudeCode,
        };

        RouterDashboardEndpoints.Destination(connector).Should().Be(expected);
    }

    [Fact]
    public async Task A_masked_key_is_used_only_where_it_was_configured()
    {
        CloudHttp = StubHttpMessageHandler.Json("""{"data":[{"id":"claude-sonnet-5-5"}]}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage same = await client.PostAsJsonAsync("/router/api/connectors/discover", new
        {
            name = "anthropic",
            settings = new { Type = "OpenAICompatible", BaseAddress = "https://api.example.com", ApiKey = "***" },
        });
        HttpResponseMessage moved = await client.PostAsJsonAsync("/router/api/connectors/discover", new
        {
            name = "anthropic",
            settings = new { Type = "OpenAICompatible", BaseAddress = "https://elsewhere.example.com", ApiKey = "***" },
        });

        same.StatusCode.Should().Be(HttpStatusCode.OK);
        CloudHttp.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");
        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode refused = await NodeAsync(moved);
        ((string?)refused["issues"]![0]!["path"]).Should().Be("Connectors.anthropic.ApiKey");
        CloudHttp.Requests.Should().HaveCount(1, "the key is not sent to the new address");
    }

    [Fact]
    public async Task A_draft_connector_meets_the_same_checks_as_applying_it()
    {
        HttpClient client = await StartDispatchAsync(services: s => s.AddSingleton(MachinePolicy.FromJson("""{ "DisabledPresets": [ "Codex" ] }""", "test policy")));

        HttpResponseMessage blocked = await client.PostAsJsonAsync("/router/api/connectors/discover", new { name = "codex-cli", settings = new { Type = "Cli", Preset = "Codex" } });
        HttpResponseMessage program = await client.PostAsJsonAsync("/router/api/connectors/discover", new { name = "mine", settings = new { Type = "Cli", Preset = "Custom", Executable = "/usr/bin/env" } });
        HttpResponseMessage parse = await client.PostAsJsonAsync("/router/api/connectors/discover", new { name = "draft", settings = new { BaseAddress = "http://localhost:9", Timeout = "5m" } });
        HttpResponseMessage shape = await client.PostAsJsonAsync("/router/api/connectors/discover", new { settings = new { } });

        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode policy = (await NodeAsync(blocked))["issues"]![0]!;
        ((string?)policy["path"], (string?)policy["message"]).Should().Be(("Connectors.codex-cli", "Connectors:codex-cli: not allowed: the Codex CLI is turned off by your organization's policy."));
        program.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((string?)(await NodeAsync(program))["errors"]![0]).Should().Contain("AllowProcessSettings");
        parse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((string?)(await NodeAsync(parse))["issues"]![0]!["path"]).Should().Be("Connectors.draft.Timeout");
        shape.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- The root redirect ----

    [Theory]
    [InlineData("GET", "/", HttpStatusCode.Redirect)]
    [InlineData("HEAD", "/", HttpStatusCode.Redirect)]
    [InlineData("POST", "/", HttpStatusCode.MethodNotAllowed)]
    [InlineData("GET", "/other", HttpStatusCode.NotFound)]
    public async Task The_site_root_sends_a_browser_to_the_dashboard(string method, string path, HttpStatusCode expected)
    {
        using IHost host = await RedirectHostAsync(own: false);

        HttpResponseMessage response = await host.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.Redirect)
        {
            response.Headers.Location!.ToString().Should().Be("/router/");
        }
    }

    [Fact]
    public async Task An_applications_own_root_endpoint_wins_over_the_redirect()
    {
        using IHost host = await RedirectHostAsync(own: true);

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("mine");
    }

    private static Task<IHost> RedirectHostAsync(bool own) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSemanticPromptRouting(_ => { });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapRouterDashboard();
                        e.MapRouterDashboardRedirect();
                        if (own)
                        {
                            e.MapGet("/", () => "mine");
                        }
                    });
                }))
            .StartAsync();

    private sealed class ThrowingDetector(Exception problem) : IRouterSetupDetector
    {
        public Task<IReadOnlyList<SetupDetection>> DetectAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<SetupDetection>>(problem);
    }

    private sealed class FixedDetector(params SetupDetection[] found) : IRouterSetupDetector
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SetupDetection>> DetectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SetupDetection>>(found);
        }
    }
}
