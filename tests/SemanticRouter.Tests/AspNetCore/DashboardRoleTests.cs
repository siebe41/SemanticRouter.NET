using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The metrics role (figures only, never prompt text, on every dashboard endpoint) and sessions that signing out ends on
/// the server.
/// </summary>
public sealed class DashboardRoleTests : RouterHostTest
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReadOnlyToken = "viewer-token-0123456789";
    private const string MetricsToken = "metrics-token-0123456789";
    private const string GatewayKey = "gateway-key-0123456789";

    /// <summary>Every piece of text the metrics role must never see starts with this.</summary>
    private const string Secret = "SECRET";

    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    private readonly ManualTimeProvider _time = new();

    public DashboardRoleTests() => _time.Advance(DateTimeOffset.UtcNow - _time.GetUtcNow());

    private Task<HttpClient> StartAsync(Action<RouterDashboardOptions>? dashboard = null, Action<IServiceCollection>? services = null, bool behindProxy = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.GatewayKeys["laptop"] = GatewayKey;
            },
            routing: o => o.Routes.Add(Route("private", 2, Secret + "-UTTERANCE")),
            services: s =>
            {
                s.AddSingleton<TimeProvider>(_time);
                s.Configure<RouterDashboardOptions>(o =>
                {
                    o.RequireLocalClient = false;
                    o.AdminToken = AdminToken;
                    o.ReadOnlyToken = ReadOnlyToken;
                    o.MetricsToken = MetricsToken;
                    dashboard?.Invoke(o);
                });
                services?.Invoke(s);
            },
            beforeRouter: app =>
            {
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = Remote;
                    return next(context);
                });
                if (behindProxy)
                {
                    app.UseRouterForwardedHeaders();
                }
            });

    private static HttpRequestMessage Request(HttpMethod method, string path, string? bearer = null, string? cookie = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
    }

    private IReadOnlyList<AuditEntry> Audit() => Services.GetRequiredService<IAuditLog>().Query(null, null, 1000);

    /// <summary>Prompt text, a reply's error, an utterance, a learned utterance, an evaluation prompt and a connector's error, all marked <see cref="Secret"/>.</summary>
    private void SeedText()
    {
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        var entry = new RoutingActivityEntry
        {
            Timestamp = _time.GetUtcNow(),
            PromptPreview = Secret + "-PROMPT",
            Tier = 1,
            Route = "private",
            PlannedModel = "local",
            Reason = RoutingReason.SemanticMatch,
        };
        entry.SentTo.Add("local");
        entry.SentTo.Add("cloud");
        activity.Add(entry);
        entry.Outcome = "served";
        entry.ServedBy = "cloud";
        entry.Connector = "anthropic";
        entry.Detail = $"local: {Secret}-REPLY-ERROR";
        entry.GradeNote = Secret + "-JUDGE";
        entry.TotalMilliseconds = 120;
        activity.Complete(entry);

        Services.GetRequiredService<ITuningStore>().Save(new TuningChange
        {
            Id = 1,
            Timestamp = _time.GetUtcNow(),
            Kind = TuningChangeKind.Utterance,
            Route = "private",
            Utterance = Secret + "-LEARNED",
            Examples = [Secret + "-EXAMPLE"],
            State = TuningChangeState.Applied,
        });
        Services.GetRequiredService<IEvaluationSetStore>().Save([new EvaluationCase(Secret + "-EVALUATION", 1)]);

        // A connector down for over an hour: the notice's detail quotes what it said.
        Services.GetRequiredService<ConnectorIncidents>().Failed("lmstudio", ModelFailureKind.Unavailable, Secret + "-INCIDENT");
        _time.Advance(TimeSpan.FromMinutes(61));
    }

    /// <summary>A request path for a route pattern, with each parameter filled in.</summary>
    private static string PathOf(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart { Name: "id" } => "1",
            RoutePatternParameterPart { Name: "file" } => "core.js",
            RoutePatternParameterPart { Name: "name" } => "local",
            RoutePatternParameterPart parameter => parameter.Name,
            RoutePatternSeparatorPart separator => separator.Content,
            _ => string.Empty,
        }))));

    [Fact]
    public async Task The_metrics_role_gets_figures_or_403_from_every_dashboard_endpoint_and_never_text()
    {
        HttpClient client = await StartAsync();
        SeedText();

        // The admin token sees the text, so its absence below means something.
        string adminView = string.Concat(await Task.WhenAll(
            new[] { "/router/api/activity", "/router/api/config", "/router/api/status", "/router/api/digest", "/router/api/evaluation/set", "/router/api/tuning" }
                .Select(async path => await (await client.SendAsync(Request(HttpMethod.Get, path, AdminToken))).Content.ReadAsStringAsync())));
        foreach (string text in new[] { "PROMPT", "REPLY-ERROR", "UTTERANCE", "LEARNED", "EVALUATION", "INCIDENT", "JUDGE" })
        {
            adminView.Should().Contain($"{Secret}-{text}");
        }

        RouteEndpoint[] endpoints = [.. Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/router", StringComparison.Ordinal))];
        endpoints.Length.Should().BeGreaterThan(50, "every dashboard and admin endpoint is enumerated");

        var allowed = new List<string>();
        foreach (RouteEndpoint endpoint in endpoints)
        {
            foreach (string method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            {
                string path = PathOf(endpoint.RoutePattern);
                if (path == "/router/api/session" && method == HttpMethods.Post)
                {
                    continue; // signing in: below
                }

                HttpRequestMessage request = Request(new HttpMethod(method), path, MetricsToken,
                    content: method == HttpMethods.Get ? null : new StringContent("{}", Encoding.UTF8, "application/json"));
                HttpResponseMessage response = await client.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                string what = $"{method} {endpoint.RoutePattern.RawText}";

                body.Should().NotContain(Secret, what);
                bool marked = endpoint.Metadata.GetMetadata<DashboardMetricsMetadata>() is not null;
                bool anonymous = endpoint.Metadata.GetMetadata<DashboardAnonymousMetadata>() is not null;
                if (marked || anonymous)
                {
                    response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, what).And.NotBe(HttpStatusCode.Unauthorized, what);
                    endpoint.Metadata.GetMetadata<DashboardWriteMetadata>().Should().BeNull($"{what} is open to the metrics role, so it must change nothing");
                    if (marked)
                    {
                        allowed.Add(what);
                    }
                }
                else
                {
                    response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{what} is not marked for the metrics role");
                }
            }
        }

        allowed.Should().BeEquivalentTo(
            "GET /router/api/themes", "GET /router/api/status", "GET /router/api/usage", "GET /router/api/activity/series",
            "GET /router/api/feedback/summary", "GET /router/api/reliability", "GET /router/api/digest");

        HttpResponseMessage signIn = await client.PostAsJsonAsync("/router/api/session", new { token = MetricsToken });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode session = JsonNode.Parse(await signIn.Content.ReadAsStringAsync())!;
        ((string?)session["role"], (string?)session["identity"]).Should().Be(("Metrics", "metrics token"));
        signIn.Headers.GetValues("Set-Cookie").Single().Should().StartWith("semantic-router-session=m.");

        // The figures are there: the digest counts the learned change, the status names the failing connector.
        JsonNode digest = JsonNode.Parse(await (await client.SendAsync(Request(HttpMethod.Get, "/router/api/digest", MetricsToken))).Content.ReadAsStringAsync())!;
        digest["learned"]!.AsArray().Single()!["summary"]!.GetValue<string>().Should().Be("utterance for private");
        digest["notices"]!.AsArray().Should().ContainSingle(n => (string?)n!["key"] == "down:lmstudio");
        JsonNode status = JsonNode.Parse(await (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", MetricsToken))).Content.ReadAsStringAsync())!;
        status["endpoints"]!.AsArray().Single(e => (string?)e!["connector"] == "lmstudio")!["failures"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData(null, 403)]
    [InlineData("metrics", 200)]
    [InlineData("anonymous", 200)]
    [InlineData("write", 403)]
    public async Task An_endpoint_not_marked_for_the_metrics_role_is_refused_to_it(string? marker, int expected)
    {
        var options = new RouterDashboardOptions { RequireLocalClient = false };
        var access = new DashboardAccess(options, AdminToken, ReadOnlyToken, metricsToken: MetricsToken);
        var metadata = new List<object> { new HttpMethodMetadata(["GET"]) };
        metadata.AddRange(marker switch
        {
            "metrics" => [DashboardMetricsMetadata.Instance],
            "anonymous" => [DashboardAnonymousMetadata.Instance],
            "write" => [DashboardMetricsMetadata.Instance, DashboardWriteMetadata.Instance],
            _ => [],
        });
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Remote;
        context.Request.Method = "GET";
        context.Request.Headers.Authorization = "Bearer " + MetricsToken;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "a new endpoint"));

        object? result = await access.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context), _ => ValueTask.FromResult<object?>(Results.Ok()));

        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task The_metrics_token_opens_nothing_on_the_gateway()
    {
        HttpClient client = await StartAsync();

        (await client.SendAsync(Request(HttpMethod.Post, "/v1/chat/completions", MetricsToken, content: Json(ChatJson)))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Post, "/semantic-router/classify", MetricsToken, content: Json("""{"prompt":"hello"}""")))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Get, "/v1/models", MetricsToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/classify", MetricsToken, content: Json("""{"prompt":"hello"}""")))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Request(HttpMethod.Get, "/v1/models", GatewayKey))).StatusCode.Should().Be(HttpStatusCode.OK, "the gateway key still works");
        VerifyNotClassified();
    }

    [Fact]
    public async Task A_proxy_user_mapped_to_metrics_stays_metrics_under_a_read_only_policy()
    {
        HttpClient client = await StartAsync(
            o =>
            {
                o.TrustedIdentityHeader = "X-Forwarded-User";
                o.TrustedGroupsHeader = "X-Forwarded-Groups";
                o.TrustedIdentityRoles["finance"] = RouterDashboardRole.Metrics;
                o.TrustedIdentityRoles["staff"] = RouterDashboardRole.ReadOnly;
            },
            s =>
            {
                s.AddSingleton(MachinePolicy.FromJson("""{"Dashboard":"ReadOnly"}""", "test policy"));
                s.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
                {
                    o.KnownProxies.Clear();
                    o.KnownIPNetworks.Clear();
                    o.KnownProxies.Add(Remote);
                });
            },
            behindProxy: true);

        HttpRequestMessage AsUser(string path, string groups)
        {
            HttpRequestMessage request = Request(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-User", "bob@corp.example");
            request.Headers.Add("X-Forwarded-Groups", groups);
            return request;
        }

        JsonNode session = JsonNode.Parse(await (await client.SendAsync(AsUser("/router/api/session", "finance"))).Content.ReadAsStringAsync())!;
        ((string?)session["role"]).Should().Be("Metrics");
        (await client.SendAsync(AsUser("/router/api/usage", "finance"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(AsUser("/router/api/activity", "finance"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(AsUser("/router/api/activity", "finance, staff"))).StatusCode.Should().Be(HttpStatusCode.OK, "the highest role among the groups applies");

        // A proxy that appends its own groups header to the one the client sent: the client's copy must not count.
        HttpRequestMessage appended = AsUser("/router/api/activity", "finance");
        appended.Headers.Add("X-Forwarded-Groups", "staff");
        HttpResponseMessage refused = await client.SendAsync(appended);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("appears more than once");
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/config", MetricsToken))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the policy does not raise it");
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", MetricsToken))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_remote_admin_cannot_send_an_environment_variable_or_another_settings_secret_to_an_address_it_chooses()
    {
        HttpClient client = await StartAsync();
        JsonNode config = JsonNode.Parse(await (await client.SendAsync(Request(HttpMethod.Get, "/router/api/config", AdminToken))).Content.ReadAsStringAsync())!["config"]!;
        config["Connectors"]!["collector"] = new JsonObject
        {
            ["BaseAddress"] = "https://collector.example",
            ["ApiKey"] = "${env:ROUTER_ADMIN_TOKEN}",
            ["Headers"] = new JsonObject { ["X-Org"] = "${secret:connectors/anthropic/ApiKey}" },
        };

        HttpResponseMessage applied = await client.SendAsync(Request(
            HttpMethod.Put, "/router/api/config?persist=false", AdminToken, content: new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json")));

        applied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string[] errors = [.. JsonNode.Parse(await applied.Content.ReadAsStringAsync())!["errors"]!.AsArray().Select(e => (string)e!)];
        errors.Should().Contain(e => e.StartsWith("Connectors:collector:ApiKey: only this machine can name an environment variable", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("Connectors:collector:Headers:X-Org: a stored-secret reference may only name this setting's own secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_token_must_be_its_own()
    {
        Func<Task> start = () => StartAsync(o => o.MetricsToken = ReadOnlyToken);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ReadOnlyToken and MetricsToken must differ*");
    }

    [Fact]
    public void The_metrics_token_and_role_bind_from_configuration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SemanticRouter:Dashboard:MetricsToken"] = "${env:ROUTER_METRICS_TOKEN}",
            ["SemanticRouter:Dashboard:TrustedIdentityRoles:finance"] = "Metrics",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(configuration.GetSection("SemanticRouter"));
        using ServiceProvider provider = services.BuildServiceProvider();

        RouterDashboardOptions options = provider.GetRequiredService<IOptions<RouterDashboardOptions>>().Value;

        options.MetricsToken.Should().Be("${env:ROUTER_METRICS_TOKEN}");
        options.TrustedIdentityRoles["finance"].Should().Be(RouterDashboardRole.Metrics);
    }

    private static async Task<string> SignInAsync(HttpClient client, string token)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/router/api/session", new { token });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static async Task<HttpStatusCode> StatusWithAsync(HttpClient client, string cookie) =>
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", cookie: cookie))).StatusCode;

    [Fact]
    public async Task Signing_out_ends_the_session_on_the_server_so_a_replayed_cookie_is_refused()
    {
        HttpClient client = await StartAsync();
        string first = await SignInAsync(client, ReadOnlyToken);
        string second = await SignInAsync(client, ReadOnlyToken);
        first.Should().NotBe(second, "each sign-in is its own session");

        HttpResponseMessage signOut = await client.SendAsync(Request(HttpMethod.Delete, "/router/api/session", cookie: first));

        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);
        signOut.Headers.GetValues("Set-Cookie").Single().Should().StartWith("semantic-router-session=;");
        (await StatusWithAsync(client, first)).Should().Be(HttpStatusCode.Unauthorized, "the cookie was revoked, even though it has not lapsed");
        (await StatusWithAsync(client, second)).Should().Be(HttpStatusCode.OK, "other sessions are unaffected");
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", ReadOnlyToken))).StatusCode.Should().Be(HttpStatusCode.OK, "the token still works");
        Audit()[0].Should().Match<AuditEntry>(e => e.Actor == "read-only token" && e.Method == "DELETE" && e.Status == 204 && e.Summary == "signed out: session revoked");

        // The replay is refused before the sign-out endpoint sees it: nothing more to revoke, nothing more recorded as the user.
        (await client.SendAsync(Request(HttpMethod.Delete, "/router/api/session", cookie: first))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Audit().Count(e => e.Summary.StartsWith("signed out", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task An_admin_can_sign_out_every_session()
    {
        HttpClient client = await StartAsync();
        string admin = await SignInAsync(client, AdminToken);
        string viewer = await SignInAsync(client, ReadOnlyToken);
        string metrics = await SignInAsync(client, MetricsToken);

        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/session/revoke-all", cookie: viewer))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/session/revoke-all", cookie: metrics))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusWithAsync(client, viewer)).Should().Be(HttpStatusCode.OK);

        _time.Advance(TimeSpan.FromMilliseconds(5));
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/session/revoke-all", cookie: admin))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        foreach (string cookie in new[] { admin, viewer, metrics })
        {
            (await StatusWithAsync(client, cookie)).Should().Be(HttpStatusCode.Unauthorized);
        }

        _time.Advance(TimeSpan.FromMilliseconds(5));
        (await StatusWithAsync(client, await SignInAsync(client, ReadOnlyToken))).Should().Be(HttpStatusCode.OK, "signing in again works");
        Audit().Should().Contain(e => e.Actor == "admin token" && e.Status == 204 && e.Summary == "signed out every session");
        Audit().Should().Contain(e => e.Actor == "read-only token" && e.Status == 403 && e.Summary == "signed out every session");
    }

    [Fact]
    public async Task A_cookie_from_before_session_ids_is_refused()
    {
        HttpClient client = await StartAsync();
        string stamp = _time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(ReadOnlyToken), Encoding.UTF8.GetBytes($"semantic-router-dashboard-session-v2\nr\n{stamp}"));
        string old = $"semantic-router-session=r.{stamp}." + Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        (await StatusWithAsync(client, old)).Should().Be(HttpStatusCode.Unauthorized, "it carries no id, so it could not be signed out");
    }

    [Fact]
    public async Task A_session_id_cannot_be_swapped()
    {
        HttpClient client = await StartAsync();
        string first = await SignInAsync(client, ReadOnlyToken);
        string second = await SignInAsync(client, ReadOnlyToken);
        (await client.SendAsync(Request(HttpMethod.Delete, "/router/api/session", cookie: first))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        string[] revoked = first.Split('.');
        string[] live = second.Split('.');
        string swapped = $"{revoked[0]}.{revoked[1]}.{live[2]}.{revoked[3]}";

        (await StatusWithAsync(client, swapped)).Should().Be(HttpStatusCode.Unauthorized, "the id is signed with the rest");
    }
}

/// <summary>Revoked dashboard sessions: kept until they would have lapsed, in memory or in the state database.</summary>
public sealed class DashboardSessionsTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly string _directory = Directory.CreateTempSubdirectory("router-sessions").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private SqliteStateValues OpenStore()
    {
        using var factory = LoggerFactory.Create(_ => { });
        return new SqliteStateValues(new SqliteRouterDatabase(
            Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = Path.Combine(_directory, "state.db") }), factory.CreateLogger<SqliteRouterDatabase>(), _time));
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    [Fact]
    public void Revocations_survive_a_restart_with_the_state_database_and_are_pruned_once_the_session_would_have_lapsed()
    {
        var sessions = new DashboardSessions(OpenStore(), _time, Lifetime);
        long issued = Now;
        sessions.Revoke("first", issued);
        _time.Advance(TimeSpan.FromHours(6));
        sessions.Revoke("second", Now);

        var restarted = new DashboardSessions(OpenStore(), _time, Lifetime);
        restarted.IsRevoked("first", issued).Should().BeTrue();
        restarted.IsRevoked("second", Now).Should().BeTrue();
        restarted.IsRevoked("third", Now).Should().BeFalse();

        _time.Advance(TimeSpan.FromHours(6));
        restarted.Count.Should().Be(1, "the first session would have lapsed by now");
        restarted.Revoke("third", Now);
        OpenStore().Load(DashboardSessions.StoreKey).Should().NotContain("first", "the stored set is pruned too").And.Contain("second").And.Contain("third");
    }

    [Fact]
    public void Another_instance_sharing_the_state_database_sees_a_sign_out_within_a_second()
    {
        var one = new DashboardSessions(OpenStore(), _time, Lifetime);
        var other = new DashboardSessions(OpenStore(), _time, Lifetime);
        other.IsRevoked("shared", Now).Should().BeFalse();

        one.Revoke("shared", Now);
        _time.Advance(TimeSpan.FromSeconds(1));

        other.IsRevoked("shared", Now).Should().BeTrue();
    }

    [Fact]
    public void Signing_out_everywhere_ends_every_session_issued_until_then()
    {
        var sessions = new DashboardSessions(OpenStore(), _time, Lifetime);
        long before = Now;
        _time.Advance(TimeSpan.FromMinutes(1));
        sessions.RevokeAll();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        sessions.IsRevoked("any", before).Should().BeTrue();
        sessions.IsRevoked("later", Now).Should().BeFalse();
        new DashboardSessions(OpenStore(), _time, Lifetime).IsRevoked("any", before).Should().BeTrue("it survives a restart");

        _time.Advance(Lifetime);
        sessions.IsRevoked("any", before).Should().BeFalse("forgotten: a session that old is refused as lapsed anyway");
    }

    [Fact]
    public void Past_the_limit_the_oldest_revocations_become_a_sign_out_of_everything_issued_until_then()
    {
        var sessions = new DashboardSessions(null, _time, Lifetime);
        var issued = new List<long>();
        for (int i = 0; i <= DashboardSessions.MaxRevoked; i++)
        {
            issued.Add(Now);
            sessions.Revoke("id-" + i, Now);
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        sessions.Count.Should().Be(0);
        sessions.IsRevoked("id-0", issued[0]).Should().BeTrue("a revocation is never dropped early");
        sessions.IsRevoked($"id-{DashboardSessions.MaxRevoked}", issued[^1]).Should().BeTrue();
        sessions.IsRevoked("new", Now).Should().BeFalse();
    }
}
