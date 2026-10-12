using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Shared TestServer harness: two HTTP connectors with stub handlers and a mocked classifier.</summary>
public abstract class RouterHostTest : IAsyncDisposable
{
    protected const string ChatJson = """{"model":"auto","messages":[{"role":"user","content":"hello there"}]}""";

    private IHost? _host;

    protected Mock<IPromptClassifier> Classifier { get; } = new();

    /// <summary>The running host's services (after <see cref="StartAsync"/>).</summary>
    protected IServiceProvider Services => _host!.Services;

    private protected StubHttpMessageHandler LocalHttp { get; set; } = StubHttpMessageHandler.Json("""{"from":"local"}""");

    private protected StubHttpMessageHandler CloudHttp { get; set; } = StubHttpMessageHandler.Json("""{"from":"cloud"}""");

    private protected FakeCliProcessRunner Cli { get; set; } = FakeCliProcessRunner.Replying(ClaudeStream.Delta("Hi"), ClaudeStream.Success("Hi"));

    /// <summary>Stub for the optional native Anthropic connector ("anthropic-native").</summary>
    private protected StubHttpMessageHandler AnthropicHttp { get; set; } = StubHttpMessageHandler.Json("""{"type":"message","role":"assistant","content":[{"type":"text","text":"from anthropic"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":2}}""");

    protected RouterHostTest() => ClassifyAs(tier: 1);

    protected void ClassifyAs(int tier, bool localOnly = false, string? route = "smalltalk", RoutingReason reason = RoutingReason.SemanticMatch) =>
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = tier,
                Reason = reason,
                RouteName = route,
                LocalOnly = localOnly,
                Similarity = 0.91234f,
                Complexity = new ComplexityAnalysis { Score = 0.1234, EstimatedTokens = 3 },
            });

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Catalog: "local" (tier 1, local, HTTP), "cloud" (tier 4, HTTP, tools) and optionally a Claude Code CLI model.</summary>
    protected async Task<HttpClient> StartAsync(
        Action<PromptRouterMiddlewareOptions>? middleware = null,
        Action<SemanticRouterOptions>? routing = null,
        Action<IServiceCollection>? services = null,
        Action<IApplicationBuilder>? beforeRouter = null,
        bool mockClassifier = true,
        bool withCli = false)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    SemanticRouterBuilder router = s.AddSemanticPromptRouting(o =>
                    {
                        ModelDefinition local = Model("local", 1, local: true, connector: "lmstudio");
                        local.Model = "qwen3-8b";
                        ModelDefinition cloud = Model("cloud", 4, costPerMillion: 3m, connector: "anthropic", capabilities: "tools");
                        o.Models.Add(local);
                        o.Models.Add(cloud);
                        if (withCli)
                        {
                            ModelDefinition cli = Model("claude-cli", 4, connector: "claude");
                            cli.Model = "sonnet";
                            o.Models.Add(cli);
                        }

                        routing?.Invoke(o);
                    })
                    .AddConnector("lmstudio", c => c.BaseAddress = new Uri("http://localhost:1234"))
                    .AddConnector("anthropic", c =>
                    {
                        c.BaseAddress = new Uri("https://api.example.com");
                        c.ApiKey = "sk-test";
                        c.Headers["x-tenant"] = "acme";
                    });

                    if (withCli)
                    {
                        router.AddConnector("claude", c =>
                        {
                            c.Type = ConnectorType.Cli;
                            c.Preset = CliPreset.ClaudeCode;
                        });
                    }

                    if (middleware is not null)
                    {
                        router.ConfigureMiddleware(middleware);
                    }

                    s.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio").ConfigurePrimaryHttpMessageHandler(() => LocalHttp);
                    s.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "anthropic").ConfigurePrimaryHttpMessageHandler(() => CloudHttp);
                    s.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "anthropic-native").ConfigurePrimaryHttpMessageHandler(() => AnthropicHttp);
                    s.AddSingleton<ICliProcessRunner>(Cli);

                    if (mockClassifier)
                    {
                        s.AddSingleton(Classifier.Object);
                    }

                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    beforeRouter?.Invoke(app);
                    app.UseRouting();
                    app.UsePromptRouter();
                    app.UseEndpoints(e =>
                    {
                        e.MapPromptClassification();
                        e.MapRouterModels();
                        e.MapAnthropicTokenCount();
                        e.MapRouterDashboard();
                        e.MapFallback(EchoAsync);
                    });
                }))
            .StartAsync();

        return _host.GetTestClient();
    }

    // Downstream endpoint: proves the body is still readable and exposes the routing plan.
    private static async Task EchoAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        RoutingPlan? plan = context.GetPromptRouting();
        await context.Response.WriteAsJsonAsync(new EchoResponse(body, plan?.Classification.RequiredTier, plan is { Candidates.Count: > 0 } ? plan.Candidates[0].Model.Name : null));
    }

    protected sealed record EchoResponse(string Body, int? Tier, string? Model);

    protected static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    protected void VerifyNotClassified() =>
        Classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}

public sealed class AnnotateModeTests : RouterHostTest
{
    [Fact]
    public async Task Annotates_the_request_and_preserves_the_body()
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route").Should().Equal("smalltalk");
        response.Headers.GetValues("X-Prompt-Route-Tier").Should().Equal("1");
        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("SemanticMatch");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Similarity").Should().Equal("0.9123");
        response.Headers.GetValues("X-Prompt-Route-Complexity").Should().Equal("0.123");
        (await response.Content.ReadFromJsonAsync<EchoResponse>()).Should().Be(new EchoResponse(ChatJson, 1, "local"));
        Classifier.Verify(c => c.ClassifyAsync("hello there", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ignores_non_post_and_non_json_requests()
    {
        HttpClient client = await StartAsync();

        await client.GetAsync("/v1/chat/completions");
        await client.PostAsync("/v1/chat/completions", new StringContent("hello there", Encoding.UTF8, "text/plain"));

        VerifyNotClassified();
    }

    [Fact]
    public async Task Only_intercepts_configured_path_prefixes()
    {
        HttpClient client = await StartAsync(o => o.PathPrefixes.Add("/v1/chat"));

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Headers.Contains("X-Prompt-Route").Should().BeTrue();
        (await client.PostAsync("/api/orders", Json(ChatJson))).Headers.Contains("X-Prompt-Route").Should().BeFalse();
        (await client.PostAsync("/v1/chatty", Json(ChatJson))).Headers.Contains("X-Prompt-Route").Should().BeFalse("prefix matching is segment-aware");
    }

    [Fact]
    public async Task Rejects_bodies_over_the_limit()
    {
        HttpClient client = await StartAsync(o => o.MaxRequestBodyBytes = 16);

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        VerifyNotClassified();
    }

    [Theory]
    [InlineData("""{"model":"x"}""")]
    [InlineData("{ not json")]
    public async Task Passes_through_when_no_prompt_can_be_extracted(string body)
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/anything", Json(body));

        (await response.Content.ReadFromJsonAsync<EchoResponse>())!.Body.Should().Be(body);
        VerifyNotClassified();
    }

    [Fact]
    public async Task Fails_open_to_the_fallback_tier_when_classification_throws()
    {
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("embeddings down"));
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("ClassifierFailure");
        response.Headers.GetValues("X-Prompt-Route-Tier").Should().Equal("3");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
    }

    [Fact]
    public async Task Propagates_classification_failures_when_fail_open_is_disabled()
    {
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("embeddings down"));
        HttpClient client = await StartAsync(o => o.FailOpen = false);

        Func<Task> act = () => client.PostAsync("/v1/chat/completions", Json(ChatJson));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Response_headers_can_be_disabled()
    {
        HttpClient client = await StartAsync(o => o.EmitResponseHeaders = false);

        (await client.PostAsync("/x", Json(ChatJson))).Headers.Should().NotContain(h => h.Key.StartsWith("X-Prompt"));
    }

    [Fact]
    public async Task Classification_endpoint_explains_the_decision_with_the_real_classifier()
    {
        HttpClient client = await StartAsync(
            mockClassifier: false,
            routing: o =>
            {
                o.DefaultSimilarityThreshold = 0.3f;
                o.Routes.Add(Route("smalltalk", 1, "hello how are you", "good morning"));
                o.Routes.Add(Route("reasoning", 4, "design a distributed system"));
            });

        HttpResponseMessage response = await client.PostAsJsonAsync("/semantic-router/classify", new { prompt = "hello, how are you today?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredTier").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("route").GetString().Should().Be("smalltalk");
        json.RootElement.GetProperty("routes").GetArrayLength().Should().Be(2);
        json.RootElement.GetProperty("models")[0].GetProperty("model").GetString().Should().Be("local");
    }

    [Fact]
    public async Task Models_endpoint_lists_aliases_and_catalog()
    {
        HttpClient client = await StartAsync();

        using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));

        json.RootElement.GetProperty("object").GetString().Should().Be("list");
        json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .Should().Equal("auto", "auto:cheapest", "auto:best", "auto:priority", "auto:fastest", "auto:balanced", "local", "cloud");
    }

    [Fact]
    public async Task Invalid_routing_options_fail_at_startup()
    {
        Func<Task> act = () => StartAsync(mockClassifier: false, routing: o => o.Routes.Add(new RouteDefinition { Name = "empty" }));

        await act.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*'empty' must have at least one non-empty utterance*");
    }
}

public sealed class DispatchModeTests : RouterHostTest
{
    private Task<HttpClient> StartDispatchAsync(
        Action<SemanticRouterOptions>? routing = null,
        Action<IApplicationBuilder>? beforeRouter = null,
        bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing,
            beforeRouter: beforeRouter,
            withCli: withCli);

    [Fact]
    public async Task Serves_easy_prompts_from_the_cheapest_capable_model()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"local"}""");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Connector").Should().Equal("lmstudio");
        response.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("1");

        (HttpRequestMessage request, string body) = LocalHttp.Requests.Single();
        request.RequestUri.Should().Be(new Uri("http://localhost:1234/v1/chat/completions"));
        request.Headers.Authorization.Should().BeNull();
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString().Should().Be("qwen3-8b");
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Serves_hard_prompts_from_a_capable_model_with_its_credentials()
    {
        ClassifyAs(tier: 4, route: "reasoning");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        (HttpRequestMessage request, string body) = CloudHttp.Requests.Single();
        request.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");
        request.Headers.GetValues("x-tenant").Should().Equal("acme");
        body.Should().Contain("\"model\":\"auto\"", "a model without an explicit provider id keeps the client's value");
    }

    [Fact]
    public async Task Relays_client_errors_without_failing_over()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"error":"bad request"}""", HttpStatusCode.BadRequest);
        HttpClient client = await StartDispatchAsync();

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fails_over_and_cools_down_an_unreachable_model()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage second = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        (await first.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        first.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("2");
        second.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal(["1"], "the failed model is cooling down");
        LocalHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Quota_errors_fall_through_to_the_next_model()
    {
        LocalHttp = StubHttpMessageHandler.Json("{}", HttpStatusCode.TooManyRequests);
        HttpClient client = await StartDispatchAsync();

        (await (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Content.ReadAsStringAsync())
            .Should().Be("""{"from":"cloud"}""");
    }

    [Fact]
    public async Task Local_only_prompts_never_leave_the_machine()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        ClassifyAs(tier: 1, localOnly: true, route: "pii");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("local: unreachable");
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_catalog_model_is_honoured()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"cloud","messages":[{"role":"user","content":"hello there"}]}"""));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("ModelOverride");
        VerifyNotClassified();
    }

    [Fact]
    public async Task Requests_with_tools_only_go_to_tool_capable_models()
    {
        HttpClient client = await StartDispatchAsync();

        await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","messages":[{"role":"user","content":"hello there"}],
             "tools":[{"type":"function","function":{"name":"f","parameters":{"type":"object"}}}]}
            """));

        LocalHttp.Requests.Should().BeEmpty();
        CloudHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Best_alias_picks_the_most_capable_model()
    {
        HttpClient client = await StartDispatchAsync();

        await client.PostAsync("/v1/chat/completions", Json("""{"model":"auto:best","messages":[{"role":"user","content":"hi"}]}"""));

        CloudHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_requests_without_a_prompt()
    {
        HttpClient client = await StartDispatchAsync();

        (await client.PostAsync("/v1/chat/completions", Json("""{"model":"x"}"""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Requires_path_prefixes_known_connectors_and_models()
    {
        Func<Task> noPrefixes = () => StartAsync(o => o.Mode = PromptRouterMode.Dispatch);
        await noPrefixes.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PathPrefixes*");
        await DisposeAsync();

        Func<Task> unknownConnector = () => StartDispatchAsync(o => o.Models.Add(Model("ghost", 1, connector: "nope")));
        await unknownConnector.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connector 'nope'*");
    }

    [Fact]
    public async Task Cli_connector_returns_an_openai_chat_completion()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Init, ClaudeStream.Delta("Hello"), ClaudeStream.Delta(" world"), ClaudeStream.Success("Hello world"));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("claude-cli");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("object").GetString().Should().Be("chat.completion");
        json.RootElement.GetProperty("model").GetString().Should().Be("claude-cli");
        json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("Hello world");
        json.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(3);

        CliInvocation invocation = Cli.Invocations.Single();
        invocation.Executable.Should().Be("claude");
        invocation.Arguments.Should().ContainInOrder("--model", "sonnet");
        invocation.RemoveEnvironment.Should().Contain("ANTHROPIC_API_KEY");
        Directory.Exists(invocation.WorkingDirectory).Should().BeFalse("the scratch directory is deleted after the run");
        CloudHttp.Requests.Should().BeEmpty("the flat-rate subscription is cheaper than the paid API at the same tier");
    }

    [Fact]
    public async Task Cli_connector_streams_server_sent_events()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Delta("Hel"), ClaudeStream.Delta("lo"), ClaudeStream.Success("Hello"));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"auto","stream":true,"messages":[{"role":"user","content":"hello there"}]}"""));

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        string[] events = (await response.Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e["data: ".Length..]).ToArray();
        events[^1].Should().Be("[DONE]");
        string text = string.Concat(events[..^1].Select(e => JsonDocument.Parse(e).RootElement.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _)).Select(d => d.GetProperty("content").GetString()));
        text.Should().Be("Hello");
        JsonDocument.Parse(events[^2]).RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
    }

    [Fact]
    public async Task Subscription_limit_falls_back_to_the_next_model_and_cools_down()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Error("You've hit your session limit · resets 3:45pm"));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage second = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        (await first.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        second.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("1");
        Cli.Invocations.Should().ContainSingle("the CLI model is in its quota cooldown for the second request");

        using JsonDocument models = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));
        models.RootElement.GetProperty("data").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "claude-cli")
            .GetProperty("coolingDownUntil").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Account_wide_limits_cool_down_every_model_on_the_connector()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Error("You've hit your weekly limit · resets Mon 12:00am"));
        HttpClient client = await StartDispatchAsync(withCli: true, routing: o => o.Models.Add(Model("claude-cli-opus", 5, connector: "claude")));

        (await (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Content.ReadAsStringAsync())
            .Should().Be("""{"from":"cloud"}""");

        Cli.Invocations.Should().ContainSingle("the second Claude model shares the exhausted account and is skipped");
    }

    [Fact]
    public async Task Model_specific_limits_only_cool_down_that_model()
    {
        ClassifyAs(tier: 5);
        Cli = new FakeCliProcessRunner(i => i.Arguments.Contains("opus")
            ? new FakeCliProcess([ClaudeStream.Error("You've hit your Opus limit · resets 3:45pm")])
            : new FakeCliProcess([ClaudeStream.Success("sonnet answer")]));
        HttpClient client = await StartDispatchAsync(withCli: true, routing: o =>
        {
            ModelDefinition opus = Model("claude-cli-opus", 5, connector: "claude");
            opus.Model = "opus";
            o.Models.Add(opus);
        });

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("claude-cli");
        Cli.Invocations.Should().HaveCount(2);
    }

    [Fact]
    public async Task Empty_cli_reply_falls_back_to_the_next_model()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Success(string.Empty));
        HttpClient client = await StartDispatchAsync(withCli: true);

        (await (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Content.ReadAsStringAsync())
            .Should().Be("""{"from":"cloud"}""");
    }

    [Fact]
    public async Task Below_tier_answers_are_flagged_as_degraded()
    {
        ClassifyAs(tier: 4);
        CloudHttp = StubHttpMessageHandler.Throws();
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Degraded").Should().Equal("true");
    }

    [Fact]
    public async Task Answers_at_the_required_tier_are_not_flagged()
    {
        HttpClient client = await StartDispatchAsync();

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Headers.Contains("X-Prompt-Route-Degraded").Should().BeFalse();
    }

    [Fact]
    public async Task Missing_cli_falls_back_to_the_next_model()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.NotInstalled();
        HttpClient client = await StartDispatchAsync(withCli: true);

        (await (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Content.ReadAsStringAsync())
            .Should().Be("""{"from":"cloud"}""");
    }

    [Fact]
    public async Task Subscription_cli_is_never_used_for_remote_clients()
    {
        ClassifyAs(tier: 4);
        HttpClient client = await StartDispatchAsync(
            withCli: true,
            beforeRouter: app => app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                return next(context);
            }));

        (await (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).Content.ReadAsStringAsync())
            .Should().Be("""{"from":"cloud"}""");
        Cli.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_503_when_every_candidate_fails()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        CloudHttp = StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("local: unreachable").And.Contain("cloud: HTTP 500");
    }
}
