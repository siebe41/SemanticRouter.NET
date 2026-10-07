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

namespace SemanticRouter.Tests.AspNetCore;

public sealed class PromptRoutingMiddlewareTests : IAsyncDisposable
{
    private const string ChatJson = """{"model":"auto","messages":[{"role":"user","content":"hello there"}]}""";

    private static readonly PromptClassificationResult LocalResult = new()
    {
        Target = RouteTarget.Local,
        Reason = RoutingReason.SemanticMatch,
        RouteName = "smalltalk",
        Similarity = 0.91234f,
        Complexity = new ComplexityAnalysis { Score = 0.1234 },
    };

    private readonly Mock<IPromptClassifier> _classifier = new();
    private IHost? _host;

    public PromptRoutingMiddlewareTests()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalResult);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task<HttpClient> StartAsync(
        Action<PromptRouterMiddlewareOptions>? middleware = null,
        Action<IServiceCollection>? services = null,
        bool mockClassifier = true)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    SemanticRouterBuilder router = s.AddSemanticPromptRouting();
                    if (middleware is not null)
                    {
                        router.ConfigureMiddleware(middleware);
                    }

                    if (mockClassifier)
                    {
                        s.AddSingleton(_classifier.Object);
                    }

                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UsePromptRouter();
                    app.UseEndpoints(e =>
                    {
                        e.MapPromptClassification();
                        e.MapFallback(EchoAsync);
                    });
                }))
            .StartAsync();

        return _host.GetTestClient();
    }

    // Downstream endpoint: proves the body is still readable and exposes the routing feature.
    private static async Task EchoAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        PromptClassificationResult? routing = context.GetPromptRouting();
        await context.Response.WriteAsJsonAsync(new EchoResponse(body, routing?.Target.ToString(), routing?.RouteName));
    }

    private sealed record EchoResponse(string Body, string? Target, string? Route);

    // The hosted warm-up service legitimately calls WarmUpAsync, so assert on ClassifyAsync specifically.
    private void VerifyNotClassified() =>
        _classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Annotates_the_request_and_preserves_the_body_for_downstream_handlers()
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route").Should().Equal("smalltalk");
        response.Headers.GetValues("X-Prompt-Route-Target").Should().Equal("Local");
        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("SemanticMatch");
        response.Headers.GetValues("X-Prompt-Route-Similarity").Should().Equal("0.9123");
        response.Headers.GetValues("X-Prompt-Route-Complexity").Should().Equal("0.123");

        EchoResponse? echo = await response.Content.ReadFromJsonAsync<EchoResponse>();
        echo.Should().Be(new EchoResponse(ChatJson, "Local", "smalltalk"));
        _classifier.Verify(c => c.ClassifyAsync("hello there", It.IsAny<CancellationToken>()), Times.Once);
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

        HttpResponseMessage matched = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage other = await client.PostAsync("/api/orders", Json(ChatJson));
        HttpResponseMessage lookalike = await client.PostAsync("/v1/chatty", Json(ChatJson));

        matched.Headers.Contains("X-Prompt-Route").Should().BeTrue();
        other.Headers.Contains("X-Prompt-Route").Should().BeFalse();
        lookalike.Headers.Contains("X-Prompt-Route").Should().BeFalse("prefix matching is segment-aware");
    }

    [Fact]
    public async Task Rejects_bodies_over_the_limit()
    {
        HttpClient client = await StartAsync(o => o.MaxRequestBodyBytes = 16);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        VerifyNotClassified();
    }

    [Theory]
    [InlineData("""{"model":"x"}""")]
    [InlineData("{ not json")]
    public async Task Passes_through_when_no_prompt_can_be_extracted(string body)
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/anything", Json(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<EchoResponse>())!.Body.Should().Be(body);
        VerifyNotClassified();
    }

    [Fact]
    public async Task Fails_open_to_the_fallback_target_when_classification_throws()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("embeddings down"));
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route-Target").Should().Equal("Cloud");
        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("ClassifierFailure");
        response.Headers.GetValues("X-Prompt-Route").Should().Equal("none");
    }

    [Fact]
    public async Task Propagates_classification_failures_when_fail_open_is_disabled()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("embeddings down"));
        HttpClient client = await StartAsync(o => o.FailOpen = false);

        Func<Task> act = () => client.PostAsync("/v1/chat/completions", Json(ChatJson));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Response_headers_can_be_disabled_and_renamed()
    {
        HttpClient silent = await StartAsync(o => o.EmitResponseHeaders = false);
        (await silent.PostAsync("/x", Json(ChatJson))).Headers.Should().NotContain(h => h.Key.StartsWith("X-Prompt"));
        await DisposeAsync();

        HttpClient renamed = await StartAsync(o => o.HeaderPrefix = "X-LLM");
        (await renamed.PostAsync("/x", Json(ChatJson))).Headers.GetValues("X-LLM-Target").Should().Equal("Local");
    }

    [Fact]
    public async Task Classification_endpoint_explains_the_decision()
    {
        HttpClient client = await StartAsync(
            mockClassifier: false,
            services: s => s.Configure<SemanticRouterOptions>(o =>
            {
                o.DefaultSimilarityThreshold = 0.3f;
                o.Routes.Add(TestEmbeddings.Route("smalltalk", RouteTarget.Local, "hello how are you", "good morning"));
                o.Routes.Add(TestEmbeddings.Route("reasoning", RouteTarget.Cloud, "design a distributed system"));
            }));

        HttpResponseMessage response = await client.PostAsJsonAsync("/semantic-router/classify", new { prompt = "hello, how are you today?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("target").GetString().Should().Be("Local");
        json.RootElement.GetProperty("reason").GetString().Should().Be("SemanticMatch");
        json.RootElement.GetProperty("route").GetString().Should().Be("smalltalk");
        json.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(2);
        json.RootElement.GetProperty("complexity").GetProperty("estimatedTokens").GetInt32().Should().BePositive();
    }

    [Fact]
    public async Task Invalid_routing_options_fail_at_startup()
    {
        Func<Task> act = () => StartAsync(
            mockClassifier: false,
            services: s => s.Configure<SemanticRouterOptions>(o => o.Routes.Add(new RouteDefinition { Name = "empty" })));

        await act.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*'empty' must have at least one non-empty utterance*");
    }

    public sealed class Dispatch : IAsyncDisposable
    {
        private readonly PromptRoutingMiddlewareTests _fixture = new();
        private StubHttpMessageHandler _local = StubHttpMessageHandler.Json("""{"from":"local"}""");
        private readonly StubHttpMessageHandler _cloud = StubHttpMessageHandler.Json("""{"from":"cloud"}""");

        public ValueTask DisposeAsync() => _fixture.DisposeAsync();

        private Task<HttpClient> StartAsync(Action<PromptRouterMiddlewareOptions>? configure = null) =>
            _fixture.StartAsync(
                o =>
                {
                    o.Mode = PromptRouterMode.Dispatch;
                    o.PathPrefixes.Add("/v1/chat/completions");
                    configure?.Invoke(o);
                },
                s =>
                {
                    s.Configure<PromptDispatchOptions>(e =>
                    {
                        e.Local.BaseAddress = new Uri("http://ollama:11434");
                        e.Local.Model = "llama3.2";
                        e.Cloud.BaseAddress = new Uri("https://api.example.com");
                        e.Cloud.RequestPath = "/v1/chat/completions";
                        e.Cloud.Model = "frontier-large";
                        e.Cloud.ApiKey = "sk-test";
                        e.Cloud.Headers["x-tenant"] = "acme";
                    });
                    s.AddHttpClient(PromptDispatcher.LocalClientName).ConfigurePrimaryHttpMessageHandler(() => _local);
                    s.AddHttpClient(PromptDispatcher.CloudClientName).ConfigurePrimaryHttpMessageHandler(() => _cloud);
                });

        private void ClassifyAs(PromptClassificationResult result) =>
            _fixture._classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

        [Fact]
        public async Task Proxies_to_the_local_endpoint_rewriting_the_model()
        {
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions?trace=1", Json(ChatJson));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"local"}""");
            response.Headers.GetValues("X-Prompt-Route-Target").Should().Equal("Local");

            (HttpRequestMessage request, string body) = _local.Requests.Single();
            request.RequestUri.Should().Be(new Uri("http://ollama:11434/v1/chat/completions?trace=1"));
            request.Headers.Authorization.Should().BeNull();
            using JsonDocument json = JsonDocument.Parse(body);
            json.RootElement.GetProperty("model").GetString().Should().Be("llama3.2");
            json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hello there");
            _cloud.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task Proxies_to_the_cloud_endpoint_with_credentials()
        {
            ClassifyAs(LocalResult with { Target = RouteTarget.Cloud, RouteName = "reasoning" });
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

            (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
            (HttpRequestMessage request, string body) = _cloud.Requests.Single();
            request.RequestUri.Should().Be(new Uri("https://api.example.com/v1/chat/completions"));
            request.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");
            request.Headers.GetValues("x-tenant").Should().Equal("acme");
            body.Should().Contain("\"model\":\"frontier-large\"");
        }

        [Fact]
        public async Task Relays_upstream_status_codes_and_headers()
        {
            _local = new StubHttpMessageHandler((_, _) =>
            {
                var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
                reply.Headers.Add("Retry-After", "7");
                return reply;
            });
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            response.Headers.GetValues("Retry-After").Should().Equal("7");
            _cloud.Requests.Should().BeEmpty("4xx responses are the caller's problem, not an outage");
        }

        [Fact]
        public async Task Forwards_multi_token_server_header_as_a_single_value()
        {
            _local = new StubHttpMessageHandler((_, _) =>
            {
                var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                reply.Headers.TryAddWithoutValidation("Server", "BaseHTTP/0.6 Python/3.13");
                return reply;
            });
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

            response.Headers.NonValidated["Server"].Should().ContainSingle().Which.Should().Be("BaseHTTP/0.6 Python/3.13");
        }

        [Fact]
        public async Task Fails_over_to_the_cloud_when_the_local_endpoint_is_down()
        {
            _local = StubHttpMessageHandler.Throws();
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
            response.Headers.GetValues("X-Prompt-Route-Target").Should().Equal("Cloud");
            response.Headers.GetValues("X-Prompt-Route-Failover").Should().Equal("true");
        }

        [Fact]
        public async Task Fails_over_on_local_5xx()
        {
            _local = StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
            HttpClient client = await StartAsync();

            (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
            _cloud.Requests.Should().ContainSingle();
        }

        [Fact]
        public async Task Never_fails_over_pinned_routes()
        {
            _local = StubHttpMessageHandler.Throws();
            ClassifyAs(LocalResult with { IsPinned = true });
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            _cloud.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task Failover_can_be_disabled()
        {
            _local = StubHttpMessageHandler.Throws();
            HttpClient client = await StartAsync(o => o.FailoverToCloud = false);

            (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.BadGateway);
            _cloud.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task Rejects_requests_without_a_prompt()
        {
            HttpClient client = await StartAsync();

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"x"}"""));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _local.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task Requires_path_prefixes()
        {
            Func<Task> act = () => _fixture.StartAsync(o => o.Mode = PromptRouterMode.Dispatch);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PathPrefixes*");
        }

        [Fact]
        public async Task Requires_endpoint_addresses()
        {
            Func<Task> act = () => _fixture.StartAsync(o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1");
            });

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Endpoints:Local:BaseAddress*");
        }
    }
}
