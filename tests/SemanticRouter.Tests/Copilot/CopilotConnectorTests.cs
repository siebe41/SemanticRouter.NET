using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;
using SemanticRouter.Tests.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Copilot;

/// <summary>The Copilot SDK connector at the gateway, with the SDK client faked.</summary>
public sealed class CopilotConnectorTests : RouterHostTest
{
    private const string CloudReply = """{"choices":[{"message":{"role":"assistant","content":"from cloud"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""";

    private readonly FakeCopilot _copilot = new();

    public CopilotConnectorTests()
    {
        ClassifyAs(tier: 4);
        CloudHttp = StubHttpMessageHandler.Json(CloudReply);
    }

    private Task<HttpClient> StartCopilotAsync(bool withFactory = true, Action<ConnectorOptions>? connector = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            o =>
            {
                o.Models.Add(Model("copilot-gpt", 4, connector: "copilot", capabilities: "tools"));
                o.Models.Add(Model("copilot-claude", 4, connector: "copilot", capabilities: "tools"));
                o.Models[^2].Model = "gpt-5";
                o.Models[^1].Model = "claude-sonnet-4.5";
                o.Models[^1].Priority = 1;
            },
            s =>
            {
                s.Configure<SemanticRouterConnectorsOptions>(o =>
                {
                    var options = new ConnectorOptions { Type = CopilotConnector.Type };
                    connector?.Invoke(options);
                    o.Connectors["copilot"] = options;
                });
                if (withFactory)
                {
                    s.AddSingleton<IChatConnectorFactory>(new CopilotConnectorFactory(NullLoggerFactory.Instance, _ => _copilot));
                }
            });

    private static string Esc(string text) => JsonSerializer.Serialize(text);

    [Fact]
    public async Task A_catalog_model_streams_a_chat_reply_through_the_sdk()
    {
        _copilot.Script(new CopilotUpdate.Text("Hello"), new CopilotUpdate.Text(" from Copilot."), new CopilotUpdate.Usage(40, 6, 10));
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(
            """{"model":"auto","stream":true,"messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"say hello"}]}"""));

        string sse = await response.Content.ReadAsStringAsync();
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("copilot-gpt");
        sse.Should().Contain("\"content\":\"Hello\"").And.Contain("\"content\":\" from Copilot.\"").And.Contain("[DONE]");

        CopilotTurn turn = _copilot.Turns.Single();
        turn.Model.Should().Be("gpt-5");
        turn.SystemPrompt.Should().Be("Be brief.");
        turn.Prompt.Should().Be("say hello");
        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Should().Match<RoutingActivityEntry>(e => e.InputTokens == 40 && e.CacheReadTokens == 10 && e.OutputTokens == 6);
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_calls_come_back_to_the_client_as_native_calls()
    {
        _copilot.Script(new CopilotUpdate.Text("Reading it."), new CopilotUpdate.ToolCall("call_7", "Read", """{"path":"README.md"}"""));
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", new StringContent("""
            {"model":"claude-sonnet-9","max_tokens":256,
             "tools":[{"name":"Read","description":"Reads a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
             "messages":[{"role":"user","content":"summarize the readme"}]}
            """, Encoding.UTF8, "application/json"));

        JsonElement json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("stop_reason").GetString().Should().Be("tool_use");
        json.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Reading it.");
        json.GetProperty("content")[1].GetProperty("id").GetString().Should().Be("call_7");
        json.GetProperty("content")[1].GetProperty("input").GetProperty("path").GetString().Should().Be("README.md");

        CopilotTurn turn = _copilot.Turns.Single();
        turn.Tools.Select(t => t.Name).Should().Equal("Read");
        (turn.SystemPrompt ?? string.Empty).Should().NotContain("tool_call", "tools are declared natively, not described in the prompt");
    }

    [Fact]
    public async Task The_follow_up_turn_carries_the_tool_result_in_its_transcript()
    {
        _copilot.Script(new CopilotUpdate.Text("It is a router."));
        HttpClient client = await StartCopilotAsync();

        await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","messages":[
              {"role":"user","content":"summarize the readme"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_7","type":"function","function":{"name":"Read","arguments":"{\"path\":\"README.md\"}"}}]},
              {"role":"tool","tool_call_id":"call_7","content":"SemanticRouter routes prompts."}],
             "tools":[{"type":"function","function":{"name":"Read","parameters":{"type":"object"}}}]}
            """));

        CopilotTurn turn = _copilot.Turns.Single();
        turn.Prompt.Should().Contain("summarize the readme").And.Contain("SemanticRouter routes prompts.").And.Contain("\"Read\"");
    }

    [Fact]
    public async Task A_quota_error_fails_over_and_cools_the_model_down_on_the_dashboard()
    {
        _copilot.Script(new CopilotUpdate.Error("quota", "quota_exceeded", 402, "You have used your premium requests.", TimeSpan.FromHours(3)));
        _copilot.Script(new CopilotUpdate.Error("quota", "quota_exceeded", 402, "You have used your premium requests."));
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        JsonElement status = JsonDocument.Parse(await client.GetStringAsync("/router/api/status")).RootElement;
        JsonElement gpt = status.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "copilot-gpt");
        gpt.GetProperty("coolingDownUntil").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(3), TimeSpan.FromMinutes(1));
        gpt.GetProperty("connectorType").GetString().Should().Be("Copilot");
        _copilot.Turns.Should().HaveCount(2, "a premium-request quota is per model: the account's other model was tried next");
    }

    [Fact]
    public async Task An_account_wide_rate_limit_cools_every_copilot_model()
    {
        _copilot.Script(new CopilotUpdate.Error("rate_limit", "user_weekly_rate_limited", 429, "Weekly limit reached."));
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        _copilot.Turns.Should().ContainSingle("the second Copilot model was cooling down too");
    }

    [Theory]
    [InlineData("authentication", 401, "Not signed in.")]
    [InlineData("query", null, "Execution failed: InvalidArg, No GitHub OAuth token or Copilot HMAC key provided")] // what runtime 1.0.93 sends
    public async Task Sign_in_problems_fail_over_with_a_hint(string type, int? status, string message)
    {
        _copilot.Script(new CopilotUpdate.Error(type, null, status, message));
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Detail.Should().Contain("sign in to Copilot");
    }

    [Fact]
    public async Task Discover_lists_the_accounts_models()
    {
        _copilot.Models = ["gpt-5", "claude-sonnet-4.5", "gpt-4.1"];
        HttpClient client = await StartCopilotAsync();

        JsonElement found = JsonDocument.Parse(await (await client.PostAsync("/router/api/connectors/copilot/discover", null)).Content.ReadAsStringAsync()).RootElement;

        found.GetProperty("models").EnumerateArray().Select(m => m.GetString()).Should().Equal("gpt-5", "claude-sonnet-4.5", "gpt-4.1");
    }

    [Fact]
    public async Task Doctor_checks_the_sign_in_and_the_configured_models()
    {
        _copilot.Models = ["gpt-5", "gpt-4.1"];
        await StartCopilotAsync();

        RouterCheck check = (await RouterDoctor.CheckAsync(Services)).Single(c => c.Subject == "connector copilot");

        check.Status.Should().Be(RouterCheckStatus.Warning);
        check.Message.Should().Contain("copilot-claude (claude-sonnet-4.5)").And.Contain("Available: gpt-5, gpt-4.1");
    }

    [Fact]
    public async Task A_lowercase_type_reaches_the_dashboard_in_the_package_spelling()
    {
        // The dashboard compares type names exactly ('Copilot'), as the old enum binding let it.
        HttpClient client = await StartCopilotAsync(connector: c => c.Type = "copilot");

        JsonElement config = JsonDocument.Parse(await client.GetStringAsync("/router/api/config")).RootElement.GetProperty("config");

        config.GetProperty("Connectors").GetProperty("copilot").GetProperty("Type").GetString().Should().Be(CopilotConnector.Type);
    }

    [Fact]
    public async Task Without_the_package_copilot_models_are_skipped_with_the_reason()
    {
        HttpClient client = await StartCopilotAsync(withFactory: false);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Detail.Should().Contain("no installed connector package provides connector type 'Copilot'");
    }

    [Fact]
    public async Task A_runtime_that_cannot_start_is_unavailable_not_fatal()
    {
        _copilot.Throw = new FileNotFoundException("'copilot' was not found on PATH.");
        HttpClient client = await StartCopilotAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
    }

    [Fact]
    public async Task Configuration_reloads_reuse_one_copilot_runtime()
    {
        int created = 0;
        var factory = new CopilotConnectorFactory(NullLoggerFactory.Instance, _ => { created++; return _copilot; });

        factory.Create("a", new ConnectorOptions { Type = CopilotConnector.Type });
        factory.Create("a", new ConnectorOptions { Type = CopilotConnector.Type });
        factory.Create("b", new ConnectorOptions { Type = CopilotConnector.Type, HomeDirectory = "/elsewhere" });
        await factory.DisposeAsync();

        created.Should().Be(2);
    }

    [Fact]
    public void The_setup_offers_copilot_through_the_sdk_once_the_package_is_added()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(_ => { }).AddCopilotConnector().AddCopilotConnector();
        using ServiceProvider provider = services.BuildServiceProvider();

        SetupApi.SetupPresetReplacement[] replacements = [.. provider.GetServices<SetupApi.SetupPresetReplacement>()];

        replacements.Should().ContainSingle("adding the package twice offers it once");
        SetupApi.SetupPreset copilot = SetupApi.Presets(replacements).Single(p => p.Id == "copilot");
        (copilot.Name, copilot.Connector.Type).Should().Be(("copilot", CopilotConnector.Type));
        SetupApi.Presets().Single(p => p.Id == "copilot").Connector.Preset.Should().Be(CliPreset.GitHubCopilot, "without the package, the CLI preset");
    }

    /// <summary>Plays back one scripted turn per call and records what it was asked.</summary>
    private sealed class FakeCopilot : ICopilotBackend
    {
        private readonly Queue<CopilotUpdate[]> _scripts = new();

        public List<CopilotTurn> Turns { get; } = [];

        public IReadOnlyList<string> Models { get; set; } = [];

        public Exception? Throw { get; set; }

        public void Script(params CopilotUpdate[] updates) => _scripts.Enqueue(updates);

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(Models);

        public async IAsyncEnumerable<CopilotUpdate> RunAsync(CopilotTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            if (Throw is { } failure)
            {
                throw failure;
            }

            foreach (CopilotUpdate update in _scripts.Count > 0 ? _scripts.Dequeue() : [])
            {
                await Task.Yield();
                yield return update;
            }
        }
    }
}
