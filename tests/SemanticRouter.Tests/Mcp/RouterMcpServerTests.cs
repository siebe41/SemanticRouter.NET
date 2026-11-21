using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SemanticRouter.AspNetCore;
using SemanticRouter.Mcp;
using SemanticRouter.Tests.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Mcp;

/// <summary>The router's MCP server (#376): an MCP client against the test host, signed in as the dashboard signs callers in.</summary>
public sealed class RouterMcpServerTests : RouterHostTest, IAsyncDisposable
{
    private const string AdminToken = "admin-token-0123456789abcdef";
    private const string ReadOnlyToken = "read-only-token-0123456789ab";
    private const string MetricsToken = "metrics-token-0123456789abcd";

    private static readonly string[] ReadOnlyTools = [RouterMcpTools.ExplainRequest, RouterMcpTools.Health, RouterMcpTools.Usage];

    private readonly List<IAsyncDisposable> _clients = [];

    private HttpClient? _http;

    /// <summary>A small talk route (tier 1) and a reasoning route (tier 4), with a low default threshold.</summary>
    private static void TwoRoutes(SemanticRouterOptions o)
    {
        o.DefaultSimilarityThreshold = 0.3f;
        o.Routes.Add(Route("smalltalk", 1, "hello how are you", "good morning", "hi there"));
        o.Routes.Add(Route("reasoning", 4, "design a distributed system", "architect a scalable platform"));
    }

    /// <summary>Starts the router with the MCP server registered (and enabled unless <paramref name="enabled"/> is false).</summary>
    private async Task<HttpClient> StartMcpAsync(
        bool enabled = true, bool proposals = false, bool tokens = false, bool realRouting = false)
    {
        _http = await StartAsync(
            mockClassifier: !realRouting,
            routing: realRouting ? TwoRoutes : null,
            services: s =>
            {
                new SemanticRouterBuilder(s).AddRouterMcpServer(o =>
                {
                    o.Enabled = enabled;
                    o.AllowProposals = proposals;
                });
                if (tokens)
                {
                    s.Configure<RouterDashboardOptions>(o =>
                    {
                        o.AdminToken = AdminToken;
                        o.ReadOnlyToken = ReadOnlyToken;
                        o.MetricsToken = MetricsToken;
                        o.RequireTokenForLocalClients = true;
                    });
                }
            },
            endpoints: e => e.MapRouterMcp());
        return _http;
    }

    private async Task<McpClient> ConnectAsync(string? token = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_http!.BaseAddress!, "/router/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        };
        if (token is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        }

        var transport = new HttpClientTransport(options, _http!);
        McpClient client = await McpClient.CreateAsync(transport);
        _clients.Add(client);
        _clients.Add(transport);
        return client;
    }

    private static async Task<string[]> ToolNamesAsync(McpClient client) =>
        [.. (await client.ListToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal)];

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        string text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> FailAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        result.IsError.Should().BeTrue();
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    /// <summary>An MCP initialize request, as a client sends it first.</summary>
    private static HttpRequestMessage Initialize(string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/router/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public new async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable client in _clients)
        {
            await client.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task Is_off_by_default()
    {
        HttpClient http = await StartMcpAsync(enabled: false);

        // Nothing is mapped: the request falls through to what else the application serves (here the test's echo).
        Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Should().NotContain(e => (e.RoutePattern.RawText ?? string.Empty).Contains("mcp", StringComparison.OrdinalIgnoreCase));
        string answer = await (await http.SendAsync(Initialize())).Content.ReadAsStringAsync();
        answer.Should().NotContain("serverInfo");
    }

    [Fact]
    public async Task Lists_the_read_only_tools_and_classify_but_not_propose_tuning_unless_it_is_allowed()
    {
        await StartMcpAsync();
        McpClient client = await ConnectAsync();

        (await ToolNamesAsync(client)).Should().Equal(RouterMcpTools.Classify, RouterMcpTools.ExplainRequest, RouterMcpTools.Health, RouterMcpTools.Usage);
        client.ServerInfo!.Name.Should().Be("semantic-router");
        Func<Task> propose = () => client.CallToolAsync(RouterMcpTools.ProposeTuning, new Dictionary<string, object?> { ["kind"] = "DefaultThreshold", ["threshold"] = 0.5, ["reason"] = "x" }).AsTask();
        await propose.Should().ThrowAsync<McpException>("a tool that is not offered is an unknown tool");
    }

    [Fact]
    public async Task Offers_propose_tuning_when_proposals_are_allowed()
    {
        await StartMcpAsync(proposals: true);
        McpClient client = await ConnectAsync();

        (await ToolNamesAsync(client)).Should().Contain(RouterMcpTools.ProposeTuning);
    }

    [Fact]
    public async Task Classify_answers_what_the_admin_api_answers_for_the_same_prompt()
    {
        HttpClient http = await StartMcpAsync(realRouting: true);
        McpClient client = await ConnectAsync();
        const string prompt = "design a distributed system for payments";

        HttpResponseMessage response = await http.PostAsJsonAsync("/router/api/classify", new { prompt });
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        JsonObject expected = JsonNode.Parse(body)!.AsObject();
        JsonElement result = await CallAsync(client, RouterMcpTools.Classify, new() { ["prompt"] = prompt });
        JsonObject actual = JsonNode.Parse(result.GetProperty("classification").GetRawText())!.AsObject();

        // The time each classification took is its own.
        expected.Remove("elapsedMilliseconds");
        actual.Remove("elapsedMilliseconds");
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"MCP: {actual.ToJsonString()}\nHTTP: {expected.ToJsonString()}");
        expected["route"]!.GetValue<string>().Should().Be("reasoning");
        result.GetProperty("decision").GetString().Should().Contain("Route: reasoning").And.Contain("Tier: 4");

        Services.GetRequiredService<IAuditLog>().Query(null, null, 50).Should().Contain(e => e.Summary == "MCP: classified a prompt" && e.Actor == "this machine");
    }

    [Fact]
    public async Task Classify_reports_a_bad_request_as_a_tool_error()
    {
        await StartMcpAsync();
        McpClient client = await ConnectAsync();

        (await FailAsync(client, RouterMcpTools.Classify, new() { ["prompt"] = "hi", ["profile"] = "nope" })).Should().Contain("Unknown routing profile 'nope'");
    }

    [Fact]
    public async Task Refuses_callers_the_dashboard_refuses()
    {
        HttpClient http = await StartMcpAsync(tokens: true);

        (await http.SendAsync(Initialize())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await http.SendAsync(Initialize("not-the-token-0123456789"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Func<Task> anonymous = () => ConnectAsync();
        await anonymous.Should().ThrowAsync<Exception>();

        // A browser page on another site cannot drive it either.
        HttpRequestMessage crossSite = Initialize(AdminToken);
        crossSite.Headers.Add("Origin", "https://evil.example");
        (await http.SendAsync(crossSite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Services.GetRequiredService<IAuditLog>().Query(null, null, 50).Should().Contain(e => e.Summary == "refused a wrong token");
    }

    [Fact]
    public async Task Offers_each_role_the_tools_its_admin_api_endpoints_allow()
    {
        await StartMcpAsync(proposals: true, tokens: true);

        (await ToolNamesAsync(await ConnectAsync(AdminToken))).Should().Equal(
            RouterMcpTools.Classify, RouterMcpTools.ExplainRequest, RouterMcpTools.Health, RouterMcpTools.ProposeTuning, RouterMcpTools.Usage);

        McpClient readOnly = await ConnectAsync(ReadOnlyToken);
        (await ToolNamesAsync(readOnly)).Should().Equal(ReadOnlyTools);
        Func<Task> classify = () => readOnly.CallToolAsync(RouterMcpTools.Classify, new Dictionary<string, object?> { ["prompt"] = "hello" }).AsTask();
        await classify.Should().ThrowAsync<McpException>("classifying spends an embedding call, which the read-only role may not");

        McpClient metrics = await ConnectAsync(MetricsToken);
        (await ToolNamesAsync(metrics)).Should().Equal(RouterMcpTools.Health, RouterMcpTools.Usage);
        JsonElement usage = await CallAsync(metrics, RouterMcpTools.Usage, new() { ["window"] = "day" });
        usage.GetProperty("totals").ValueKind.Should().Be(JsonValueKind.Object);
        usage.GetProperty("forecast").ValueKind.Should().Be(JsonValueKind.Null, "the quota forecast (#374) is not wired yet");
    }

    [Fact]
    public async Task Explain_request_returns_the_activity_pages_projection_and_nothing_more()
    {
        HttpClient http = await StartMcpAsync();
        McpClient client = await ConnectAsync();
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        RoutingActivityEntry entry = activity.Add(new RoutingActivityEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            PromptPreview = "my private prompt",
            Tier = 3,
            Reason = RoutingReason.SemanticMatch,
            Route = "code",
            Similarity = 0.81234f,
            PlannedModel = "cloud",
            Selection = "fastest of the tier",
            Project = "secret-project",
            Detail = "the backend said: private reply text",
        });
        entry.ServedBy = "cloud";
        entry.Outcome = "served";
        Services.GetRequiredService<RoutingFeedbackLog>().Add(new RoutingFeedback { EntryId = entry.Id, Kind = FeedbackKind.Retried, Timestamp = DateTimeOffset.UtcNow });

        JsonElement result = await CallAsync(client, RouterMcpTools.ExplainRequest, new() { ["id"] = entry.Id });

        JsonNode page = JsonNode.Parse(await http.GetStringAsync("/router/api/activity?limit=500"))!["entries"]!.AsArray().Single(e => e!["id"]!.GetValue<long>() == entry.Id)!;
        JsonNode.DeepEquals(JsonNode.Parse(result.GetProperty("entry").GetRawText()), page).Should().BeTrue("the entry is what GET api/activity gives the Activity page");
        result.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("entry", "decision", "selection", "learned", "feedback");
        string decision = result.GetProperty("decision").GetString()!;
        decision.Should().Contain("Route: code · similarity 0.812").And.Contain("Selection: fastest of the tier").And.Contain("Feedback: Retried").And.Contain($"Activity entry: #{entry.Id}");
        decision.Should().NotContain("private").And.NotContain("secret-project");
        result.GetProperty("feedback").EnumerateArray().Select(f => f.GetString()).Should().Equal("Retried");

        (await FailAsync(client, RouterMcpTools.ExplainRequest, new() { ["id"] = entry.Id + 1000 })).Should().Contain("not among the recent requests");
    }

    [Fact]
    public async Task Health_reports_connectors_and_cooldowns()
    {
        await StartMcpAsync(tokens: true);
        ModelDefinition cloud = Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Models.Single(m => m.Name == "cloud");
        Services.GetRequiredService<IModelHealth>().ReportFailure(cloud, ModelFailureKind.QuotaExceeded, DateTimeOffset.UtcNow.AddHours(2));

        JsonElement health = await CallAsync(await ConnectAsync(MetricsToken), RouterMcpTools.Health);

        health.GetProperty("endpoints").EnumerateArray().Select(e => e.GetProperty("connector").GetString()).Should().Contain(["anthropic", "lmstudio"]);
        health.GetProperty("endpoints").EnumerateArray().Should().OnlyContain(e => e.GetProperty("lastError").ValueKind == JsonValueKind.Null);
        JsonElement cooling = health.GetProperty("coolingDown").EnumerateArray().Single();
        cooling.GetProperty("model").GetString().Should().Be("cloud");
        cooling.GetProperty("until").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Propose_tuning_replays_the_change_and_records_a_suggestion_without_applying_it()
    {
        HttpClient http = await StartMcpAsync(proposals: true, realRouting: true);
        McpClient client = await ConnectAsync();
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        foreach (string prompt in new[] { "hello there, how are you", "design a distributed system for payments", "what is the capital of Australia" })
        {
            activity.Add(new RoutingActivityEntry { Timestamp = DateTimeOffset.UtcNow.AddHours(-1), PromptPreview = prompt, Tier = 1, Reason = RoutingReason.SemanticMatch });
        }

        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        int version = provider.Current.Version;

        JsonElement result = await CallAsync(client, RouterMcpTools.ProposeTuning, new()
        {
            ["kind"] = "DefaultThreshold",
            ["threshold"] = 0.95,
            ["reason"] = "weak matches go to the wrong tier",
        });

        provider.Current.Version.Should().Be(version, "a proposal never applies anything");
        provider.Current.Configuration.DefaultSimilarityThreshold.Should().Be(0.3f);
        JsonElement proposal = result.GetProperty("proposal");
        proposal.GetProperty("state").GetString().Should().Be("Suggested");
        proposal.GetProperty("why").GetString().Should().StartWith("Proposed over MCP by this machine: weak matches go to the wrong tier.");
        JsonElement replay = result.GetProperty("replay");
        replay.GetProperty("requests").GetProperty("replayed").GetInt32().Should().Be(3);
        replay.GetProperty("moved").GetInt32().Should().BeGreaterThan(0, "a 0.95 threshold leaves the matches unmatched");
        replay.GetProperty("moves").EnumerateArray().Select(m => m.EnumerateObject().Select(p => p.Name)).Should().OnlyContain(names => !names.Contains("prompt"));

        long id = proposal.GetProperty("id").GetInt64();
        TuningChange suggestion = Services.GetRequiredService<RouterTuner>().Changes().Single(c => c.Id == id);
        suggestion.State.Should().Be(TuningChangeState.Suggested);
        suggestion.OldValue.Should().Be(0.3f);
        suggestion.NewValue.Should().Be(0.95f);

        // A person applies it on the Router learned page.
        (await http.PostAsync($"/router/api/tuning/changes/{id}/apply", null)).EnsureSuccessStatusCode();
        provider.Current.Configuration.DefaultSimilarityThreshold.Should().Be(0.95f);
        Services.GetRequiredService<IAuditLog>().Query(null, null, 50).Should().Contain(e => e.Summary.StartsWith("MCP: proposed a tuning change: change " + id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proposed_route_model_is_applied_as_the_routes_preferred_model()
    {
        await StartMcpAsync(proposals: true, realRouting: true);
        McpClient client = await ConnectAsync();
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();

        JsonElement result = await CallAsync(client, RouterMcpTools.ProposeTuning, new()
        {
            ["kind"] = "RouteModel",
            ["route"] = "SMALLTALK",
            ["model"] = "cloud",
            ["reason"] = "the local model rambles",
            ["days"] = 1,
        });

        long id = result.GetProperty("proposal").GetProperty("id").GetInt64();
        result.GetProperty("proposal").GetProperty("route").GetString().Should().Be("smalltalk");
        provider.Current.Configuration.Routes.Single(r => r.Name == "smalltalk").PreferredModel.Should().BeNull();
        RouterTuner tuner = Services.GetRequiredService<RouterTuner>();
        tuner.ApplySuggestion(id, "a person");
        provider.Current.Configuration.Routes.Single(r => r.Name == "smalltalk").PreferredModel.Should().Be("cloud");
        tuner.Undo(id, "a person");
        provider.Current.Configuration.Routes.Single(r => r.Name == "smalltalk").PreferredModel.Should().BeNull();
    }

    [Theory]
    [InlineData("DefaultThreshold", null, "1.5", null, null, "from 0 to 1")]
    [InlineData("RouteThreshold", "nope", "0.5", null, null, "There is no route 'nope'")]
    [InlineData("Utterance", "smalltalk", null, "good morning", null, "has that utterance already")]
    [InlineData("RouteModel", "smalltalk", null, null, "no-such-model", "enabled model of the catalog")]
    public async Task Propose_tuning_refuses_a_change_that_does_not_fit(string kind, string? route, string? threshold, string? utterance, string? model, string problem)
    {
        await StartMcpAsync(proposals: true, realRouting: true);
        McpClient client = await ConnectAsync();
        var arguments = new Dictionary<string, object?> { ["kind"] = kind, ["reason"] = "because", ["route"] = route, ["utterance"] = utterance, ["model"] = model };
        if (threshold is not null)
        {
            arguments["threshold"] = double.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture);
        }

        (await FailAsync(client, RouterMcpTools.ProposeTuning, arguments)).Should().Contain(problem);
        Services.GetRequiredService<RouterTuner>().Changes().Should().BeEmpty();
    }
}
