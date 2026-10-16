using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Per-client rate limits (#74): a client over its requests or tokens per minute gets a 429 with Retry-After in its own
/// protocol while other clients are served, the request is recorded in the activity log, and the dashboard shows the
/// limits and the hits.
/// </summary>
public sealed class RateLimitTests : RouterHostTest
{
    private const string LaptopKey = "key-laptop-0123456789";
    private const string CiKey = "key-ci-0123456789";

    private readonly ManualTimeProvider _time = new();

    private Task<HttpClient> StartLimitedAsync(Action<PromptRouterMiddlewareOptions> limits) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.ResponsesPathPrefixes.Add("/v1/responses");
                o.GatewayKeys["laptop"] = LaptopKey;
                o.GatewayKeys["ci"] = CiKey;
                limits(o);
            },
            services: s => s.AddSingleton<TimeProvider>(_time));

    private static HttpRequestMessage Request(string path, string json, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {key}");
        }

        return request;
    }

    private static HttpRequestMessage OpenAI(string? key) => Request("/v1/chat/completions", ChatJson, key);

    private static HttpRequestMessage Anthropic(string? key) =>
        Request("/v1/messages", """{"model":"auto","max_tokens":64,"messages":[{"role":"user","content":"hello there"}]}""", key);

    private static HttpRequestMessage Responses(string? key) => Request("/v1/responses", """{"model":"auto","input":"hello there"}""", key);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_client_over_its_request_limit_gets_429_in_every_protocol_while_others_are_served()
    {
        HttpClient client = await StartLimitedAsync(o => o.RateLimits["ci"] = new RateLimitOptions { RequestsPerMinute = 2 });

        (await client.SendAsync(OpenAI(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        _time.Advance(TimeSpan.FromSeconds(20));
        (await client.SendAsync(Anthropic(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        _time.Advance(TimeSpan.FromSeconds(10));

        HttpResponseMessage openAI = await client.SendAsync(OpenAI(CiKey));
        HttpResponseMessage anthropic = await client.SendAsync(Anthropic(CiKey));
        HttpResponseMessage responses = await client.SendAsync(Responses(CiKey));
        HttpResponseMessage laptop = await client.SendAsync(Anthropic(LaptopKey));

        foreach (HttpResponseMessage limited in new[] { openAI, anthropic, responses })
        {
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30), "the oldest of the two requests leaves the window in 30 s");
        }

        JsonElement problem = await JsonOf(openAI);
        (problem.GetProperty("status").GetInt32(), problem.GetProperty("title").GetString()).Should().Be((429, "Rate limit reached"));
        problem.GetProperty("detail").GetString().Should().Be("Client 'ci' is over its limit of 2 requests per minute; retry in 30 s.");
        JsonElement anthropicError = await JsonOf(anthropic);
        (anthropicError.GetProperty("type").GetString(), anthropicError.GetProperty("error").GetProperty("type").GetString()).Should().Be(("error", "rate_limit_error"));
        (await JsonOf(responses)).GetProperty("error").GetProperty("type").GetString().Should().Be("rate_limit_exceeded");
        laptop.StatusCode.Should().Be(HttpStatusCode.OK, "another client is served");

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(10).First(e => e.Outcome == "rate-limited");
        (entry.Client, entry.Reason, entry.ServedBy).Should().Be(("ci", RoutingReason.RateLimited, null));
        entry.Detail.Should().Contain("2 requests per minute");
        Services.GetRequiredService<RoutingActivityLog>().Recent(10).Count(e => e.Outcome == "rate-limited").Should().Be(3);
        LocalHttp.Requests.Should().HaveCount(3, "a turned-away request reaches no model");

        _time.Advance(TimeSpan.FromSeconds(30));
        (await client.SendAsync(OpenAI(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK, "the first request left the window");
    }

    [Fact]
    public async Task Tokens_are_charged_from_the_estimate_and_the_real_usage()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":700,"completion_tokens":400}}""");
        HttpClient client = await StartLimitedAsync(o => o.RateLimits["ci"] = new RateLimitOptions { TokensPerMinute = 1000 });

        (await client.SendAsync(OpenAI(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        _time.Advance(TimeSpan.FromSeconds(15));
        HttpResponseMessage limited = await client.SendAsync(OpenAI(CiKey));

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the reply used 1,100 tokens, over the limit of 1,000");
        limited.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(45));
        (await JsonOf(limited)).GetProperty("detail").GetString().Should().Contain("its limit of 1,000 tokens per minute");
    }

    [Fact]
    public async Task The_default_limit_covers_clients_without_their_own_and_clients_without_a_key_by_address()
    {
        HttpClient client = await StartLimitedAsync(o =>
        {
            o.RateLimits["*"] = new RateLimitOptions { RequestsPerMinute = 1 };
            o.RateLimits["ci"] = new RateLimitOptions { RequestsPerMinute = 3 };
        });

        (await client.SendAsync(OpenAI(LaptopKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(OpenAI(LaptopKey))).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "laptop has no limit of its own, so * applies");
        (await client.SendAsync(OpenAI(key: null))).StatusCode.Should().Be(HttpStatusCode.OK, "a local client without a key is counted on its own");
        HttpResponseMessage keyless = await client.SendAsync(OpenAI(key: null));
        keyless.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await JsonOf(keyless)).GetProperty("detail").GetString().Should().StartWith("This client (");
        (await client.SendAsync(OpenAI(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(OpenAI(CiKey))).StatusCode.Should().Be(HttpStatusCode.OK, "ci has its own, higher limit");
    }

    [Fact]
    public async Task The_dashboard_shows_the_limits_and_the_hits()
    {
        HttpClient client = await StartLimitedAsync(o => o.RateLimits["ci"] = new RateLimitOptions { RequestsPerMinute = 1, TokensPerMinute = 50_000 });
        await client.SendAsync(OpenAI(CiKey));
        await client.SendAsync(OpenAI(CiKey));
        await client.SendAsync(OpenAI(LaptopKey));

        JsonElement limits = (await JsonOf(await client.GetAsync("/router/api/status"))).GetProperty("gateway").GetProperty("rateLimits");

        JsonElement ci = limits.EnumerateArray().Should().ContainSingle("laptop has no limit").Subject;
        ci.GetProperty("client").GetString().Should().Be("ci");
        ci.GetProperty("limit").GetString().Should().Be("ci");
        (ci.GetProperty("requestsPerMinute").GetInt32(), ci.GetProperty("tokensPerMinute").GetInt32()).Should().Be((1, 50_000));
        (ci.GetProperty("requests").GetInt32(), ci.GetProperty("limited").GetInt64()).Should().Be((1, 1));
        ci.GetProperty("tokens").GetInt64().Should().BePositive();
        ci.GetProperty("lastLimited").GetDateTimeOffset().Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public void Rate_limits_are_validated()
    {
        var document = new RouterConfiguration();
        document.Middleware.GatewayKeys["laptop"] = LaptopKey;
        document.Middleware.RateLimits["laptop"] = new RateLimitOptions { RequestsPerMinute = 0 };
        document.Middleware.RateLimits["printer"] = new RateLimitOptions { RequestsPerMinute = 10 };
        document.Middleware.RateLimits["*"] = new RateLimitOptions();

        RouterRuntimeProvider.Validate(document).Should().Contain(
        [
            "Middleware:RateLimits:laptop limits must be at least 1.",
            "Middleware:RateLimits:printer names no gateway client in Middleware:GatewayKeys; use \"*\" for every client without its own limit.",
            "Middleware:RateLimits:* needs RequestsPerMinute, TokensPerMinute or both.",
        ]);
    }

    [Fact]
    public void The_limiter_waits_for_enough_of_the_window_to_pass()
    {
        var time = new ManualTimeProvider();
        var limiter = new ClientRateLimiter(time);
        var limit = new RateLimitOptions { RequestsPerMinute = 3, TokensPerMinute = 100 };

        limiter.TryAdmit("a", limit, 10).Admitted.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10));
        limiter.TryAdmit("a", limit, 10).Admitted.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10));
        limiter.TryAdmit("a", limit, 10).Admitted.Should().BeTrue();

        RateLimitDecision requests = limiter.TryAdmit("a", limit, 10);
        (requests.Admitted, requests.RetryAfter, requests.Detail).Should().Be((false, TimeSpan.FromSeconds(40), "its limit of 3 requests per minute"));
        limiter.TryAdmit("b", limit, 10).Admitted.Should().BeTrue("each client has its own window");

        limiter.Charge("b", 200);
        RateLimitDecision tokens = limiter.TryAdmit("b", limit, 10);
        (tokens.Admitted, tokens.RetryAfter, tokens.Detail).Should().Be((false, TimeSpan.FromSeconds(60), "its limit of 100 tokens per minute"));
        limiter.Charge("b", -150);
        limiter.TryAdmit("b", limit, 10).Admitted.Should().BeTrue("the reply used less than estimated");

        time.Advance(TimeSpan.FromSeconds(40));
        limiter.TryAdmit("a", limit, 10).Admitted.Should().BeTrue();
    }

    [Fact]
    public void Idle_clients_without_a_key_are_forgotten_once_there_are_many()
    {
        var time = new ManualTimeProvider();
        var limiter = new ClientRateLimiter(time);
        var limit = new RateLimitOptions { RequestsPerMinute = 1 };
        var everyone = new PromptRouterMiddlewareOptions();
        everyone.RateLimits["*"] = limit;
        for (int i = 0; i < 1000; i++)
        {
            limiter.TryAdmit($"10.0.{i / 256}.{i % 256}", limit, 1);
        }

        time.Advance(TimeSpan.FromMinutes(30));
        limiter.TryAdmit("10.9.9.9", limit, 1);
        limiter.TryAdmit("10.9.9.9", limit, 1).Admitted.Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(31));
        limiter.TryAdmit("10.9.9.10", limit, 1);

        limiter.Status(everyone).Select(s => s.Client).Should().BeEquivalentTo(["10.9.9.9", "10.9.9.10"], "the clients idle for over an hour are forgotten");
    }

    [Fact]
    public async Task Remote_clients_are_limited_by_default_and_local_ones_are_not()
    {
        bool remote = true;
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.GatewayKeys["laptop"] = LaptopKey;
                o.RemoteRequestsPerMinute = 2;
            },
            services: s => s.AddSingleton<TimeProvider>(_time),
            beforeRouter: app => app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = remote ? IPAddress.Parse("203.0.113.7") : IPAddress.Loopback;
                return next(context);
            }));

        (await client.SendAsync(Request("/v1/chat/completions", ChatJson, LaptopKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Request("/v1/chat/completions", ChatJson, LaptopKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage third = await client.SendAsync(Request("/v1/chat/completions", ChatJson, LaptopKey));

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await third.Content.ReadAsStringAsync()).Should().Contain("2 requests per minute");

        remote = false;
        for (int i = 0; i < 3; i++)
        {
            (await client.SendAsync(Request("/v1/chat/completions", ChatJson, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Remote_clients_get_a_default_limit_that_configured_limits_replace()
    {
        var options = new PromptRouterMiddlewareOptions();

        ClientRateLimiter.LimitFor(options, null, remote: true)!.Value.Should()
            .Match<(string Name, RateLimitOptions Limit)>(l => l.Name == ClientRateLimiter.RemoteDefault && l.Limit.RequestsPerMinute == 120);
        ClientRateLimiter.LimitFor(options, null, remote: false).Should().BeNull("clients on this machine are not limited by default");

        options.RateLimits["*"] = new RateLimitOptions { RequestsPerMinute = 600 };
        ClientRateLimiter.LimitFor(options, null, remote: true)!.Value.Name.Should().Be("*");

        options.RateLimits.Clear();
        options.RemoteRequestsPerMinute = 0;
        ClientRateLimiter.LimitFor(options, null, remote: true).Should().BeNull();
    }
}
