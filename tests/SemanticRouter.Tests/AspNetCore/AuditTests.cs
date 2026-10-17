using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Dashboard identity from an authenticating reverse proxy (believed only from a known proxy), the real actor in the
/// configuration history, and the append-only audit log of dashboard and admin API actions.
/// </summary>
public sealed class AuditTests : RouterHostTest
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReadOnlyToken = "view-token-0123456789";
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Stranger = IPAddress.Parse("198.51.100.7");
    private const string Client = "203.0.113.9";

    private readonly CapturingLoggerProvider _logs = new();

    /// <summary>A router behind a proxy at 10.0.0.5 that names users in X-Forwarded-User; requests arrive from <paramref name="peer"/>.</summary>
    private Task<HttpClient> StartBehindProxyAsync(IPAddress? peer, Action<RouterDashboardOptions>? dashboard = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                s.AddSingleton<ILoggerProvider>(_logs);
                s.Configure<ForwardedHeadersOptions>(o =>
                {
                    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                    o.KnownProxies.Clear();
                    o.KnownIPNetworks.Clear();
                    o.KnownProxies.Add(Proxy);
                });
                s.Configure<RouterDashboardOptions>(o =>
                {
                    o.RequireLocalClient = false;
                    o.AdminToken = AdminToken;
                    o.ReadOnlyToken = ReadOnlyToken;
                    o.TrustedIdentityHeader = "X-Forwarded-User";
                    o.TrustedGroupsHeader = "X-Forwarded-Groups";
                    o.TrustedIdentityRoles["alice@corp.example"] = RouterDashboardRole.Admin;
                    o.TrustedIdentityRoles["router-admins"] = RouterDashboardRole.Admin;
                    o.TrustedIdentityRoles["staff"] = RouterDashboardRole.ReadOnly;
                    dashboard?.Invoke(o);
                });
            },
            beforeRouter: app =>
            {
                if (peer is not null)
                {
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = peer;
                        return next(context);
                    });
                }

                app.UseRouterForwardedHeaders();
            });

    private static HttpRequestMessage Request(HttpMethod method, string path, string? user = null, string? groups = null, string? bearer = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Forwarded-For", Client);
        if (user is not null)
        {
            request.Headers.Add("X-Forwarded-User", user);
        }

        if (groups is not null)
        {
            request.Headers.Add("X-Forwarded-Groups", groups);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    private IReadOnlyList<AuditEntry> Audit() => Services.GetRequiredService<IAuditLog>().Query(null, null, 1000);

    private static async Task<StringContent> CurrentConfigAsync(HttpClient client, HttpRequestMessage get)
    {
        HttpResponseMessage response = await client.SendAsync(get);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string config = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["config"]!.ToJsonString();
        return new StringContent(config, Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Behind_a_known_proxy_the_history_and_audit_log_name_the_signed_in_user()
    {
        HttpClient client = await StartBehindProxyAsync(Proxy);
        StringContent config = await CurrentConfigAsync(client, Request(HttpMethod.Get, "/router/api/config", "alice@corp.example"));

        HttpResponseMessage put = await client.SendAsync(Request(HttpMethod.Put, "/router/api/config?persist=false", "alice@corp.example", content: config));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        RouterConfigurationVersion latest = Services.GetRequiredService<RouterRuntimeProvider>().History!.List(1).Single();
        latest.Actor.Should().Be("alice@corp.example");
        AuditEntry entry = Audit().Should().ContainSingle().Which;
        entry.Actor.Should().Be("alice@corp.example");
        entry.Client.Should().Be(Client, "the address comes from the known proxy's X-Forwarded-For");
        entry.Method.Should().Be("PUT");
        entry.Path.Should().Be("/router/api/config");
        entry.Route.Should().Be("/router/api/config");
        entry.Status.Should().Be(200);
        entry.Summary.Should().Be($"applied the configuration: version {Services.GetRequiredService<RouterRuntimeProvider>().Current.Version}, not saved");
        _logs.All.Should().Contain("Audit: alice@corp.example from 203.0.113.9: PUT /router/api/config -> 200");

        JsonNode session = JsonNode.Parse(await (await client.SendAsync(Request(HttpMethod.Get, "/router/api/session", "alice@corp.example"))).Content.ReadAsStringAsync())!;
        session["role"]!.GetValue<string>().Should().Be("Admin");
        session["identity"]!.GetValue<string>().Should().Be("alice@corp.example");
        session["tokenRequired"]!.GetValue<bool>().Should().BeFalse("the proxy signs people in; there is nothing to sign out of here");
    }

    [Fact]
    public async Task Roles_come_from_the_user_then_their_groups_and_an_unmapped_user_is_refused()
    {
        HttpClient client = await StartBehindProxyAsync(Proxy);

        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "dave@corp.example", "staff, router-admins")))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the highest role among the groups applies");
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "erin@corp.example", "staff")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "staff is read-only");
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", "erin@corp.example", "staff"))).StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage unmapped = await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", "carol@corp.example"));
        unmapped.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unmapped.Content.ReadAsStringAsync()).Should().Contain("carol@corp.example").And.Contain("TrustedIdentityRoles");
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", "carol@corp.example", bearer: AdminToken)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "behind the proxy, the proxy decides who this is, not a token");

        Audit().Select(e => (e.Actor, e.Status, e.Summary)).Should().Equal(
            ("erin@corp.example", 403, "cleared every cooldown"),
            ("dave@corp.example", 200, "cleared every cooldown"));
    }

    [Fact]
    public async Task Everyone_gets_the_star_role_and_a_user_entry_overrides_their_groups()
    {
        HttpClient client = await StartBehindProxyAsync(Proxy, o =>
        {
            o.TrustedIdentityRoles["*"] = RouterDashboardRole.ReadOnly;
            o.TrustedIdentityRoles["mallory@corp.example"] = RouterDashboardRole.ReadOnly;
        });

        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", "carol@corp.example"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "carol@corp.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "mallory@corp.example", "router-admins")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the user's own entry wins over their groups");
    }

    [Fact]
    public async Task The_identity_header_from_anyone_but_a_known_proxy_is_ignored()
    {
        HttpClient client = await StartBehindProxyAsync(Stranger, o => o.TrustedIdentityRoles["*"] = RouterDashboardRole.Admin);

        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", "alice@corp.example"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "alice@corp.example"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", "alice@corp.example", bearer: ReadOnlyToken)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the read-only token decides, not the header");

        Audit().Should().ContainSingle().Which.Should().Match<AuditEntry>(e =>
            e.Actor == "read-only token" && e.Client == Stranger.ToString() && e.Status == 403, "the stranger's own address, not its X-Forwarded-For");
    }

    [Fact]
    public async Task Without_the_forwarded_headers_middleware_the_identity_header_is_ignored()
    {
        // MapRouterDashboard alone: no known proxy, so nobody may name a user.
        HttpClient client = await StartAsync(services: s => s.Configure<RouterDashboardOptions>(o =>
        {
            o.RequireTokenForLocalClients = true;
            o.AdminToken = AdminToken;
            o.TrustedIdentityHeader = "X-Forwarded-User";
            o.TrustedIdentityRoles["*"] = RouterDashboardRole.Admin;
        }));

        var request = new HttpRequestMessage(HttpMethod.Get, "/router/api/status");
        request.Headers.Add("X-Forwarded-User", "alice@corp.example");
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("alice@corp.example, mallory@corp.example")]
    [InlineData("*")]
    [InlineData("a-name-longer-than-two-hundred-and-fifty-six-characters-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx@corp.example")]
    public async Task An_identity_header_that_does_not_name_one_user_is_refused(string value)
    {
        HttpClient client = await StartBehindProxyAsync(Proxy, o => o.TrustedIdentityRoles["*"] = RouterDashboardRole.Admin);

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", value));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("does not name one user");
    }

    [Fact]
    public async Task The_identity_header_needs_a_host_naming_the_router_against_dns_rebinding()
    {
        HttpClient client = await StartBehindProxyAsync(Proxy, o => o.PublicOrigins.Add("https://router.corp.example"));
        HttpRequestMessage rebound = Request(HttpMethod.Get, "/router/api/status", "alice@corp.example");
        rebound.Headers.Host = "attacker.example";
        HttpRequestMessage proxied = Request(HttpMethod.Get, "/router/api/status", "alice@corp.example");
        proxied.Headers.Host = "router.corp.example";

        HttpResponseMessage refused = await client.SendAsync(rebound);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("PublicOrigins");
        (await client.SendAsync(proxied)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_repeated_identity_header_is_refused()
    {
        HttpClient client = await StartBehindProxyAsync(Proxy);
        HttpRequestMessage request = Request(HttpMethod.Get, "/router/api/status", "mallory@corp.example");
        request.Headers.Add("X-Forwarded-User", "alice@corp.example");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a proxy that appends to the client's header must not be believed");
    }

    [Fact]
    public async Task Sign_ins_sign_outs_and_wrong_tokens_are_recorded_with_the_token_role_as_actor()
    {
        HttpClient client = await StartBehindProxyAsync(Stranger);

        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/session", content: JsonContent.Create(new { token = "not-the-token-at-all" }))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        HttpResponseMessage signedIn = await client.SendAsync(Request(HttpMethod.Post, "/router/api/session", content: JsonContent.Create(new { token = ReadOnlyToken })));
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/status", bearer: "wrong-bearer-token-0123"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Delete, "/router/api/session", bearer: AdminToken))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        Audit().Select(e => (e.Actor, e.Method, e.Status, e.Summary)).Should().Equal(
            ("admin token", "DELETE", 204, "signed out"),
            ("anonymous", "GET", 401, "refused a wrong token"),
            ("read-only token", "POST", 200, "signed in"),
            ("anonymous", "POST", 401, "sign-in failed: wrong token"));
        string all = string.Join("\n", Audit().Select(e => e.ToString()));
        all.Should().NotContain(ReadOnlyToken).And.NotContain(AdminToken).And.NotContain("not-the-token-at-all").And.NotContain("wrong-bearer-token");
    }

    [Fact]
    public async Task Local_changes_are_by_this_machine_and_record_no_request_text()
    {
        HttpClient client = await StartAsync();

        (await client.DeleteAsync("/router/api/activity")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/router/api/classify", JsonContent.Create(new { prompt = "my very private prompt" }))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/router/api/routes/smalltalk/utterances", JsonContent.Create(new { utterance = "a learned private phrase", tier = 1 }))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/router/api/activity/export?prompts=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<AuditEntry> entries = Audit();
        entries.Select(e => e.Summary).Should().Equal(
            "exported activity: 1 entry, with prompt previews",
            $"added an utterance to a route: route smalltalk, version {Services.GetRequiredService<RouterRuntimeProvider>().Current.Version}",
            "classified a prompt",
            "deleted activity: 0 entries, everything");
        entries.Should().OnlyContain(e => e.Actor == "this machine" && e.Client == null);
        string all = string.Join("\n", entries.Select(e => e.ToString()));
        all.Should().NotContain("private");
        Services.GetRequiredService<RouterRuntimeProvider>().History!.List(1).Single().Actor.Should().Be("this machine");
    }

    [Fact]
    public async Task Requests_refused_before_anyone_signed_in_are_not_recorded()
    {
        HttpClient client = await StartBehindProxyAsync(Stranger);

        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Put, "/router/api/config", content: new StringContent("{}", Encoding.UTF8, "application/json"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        Audit().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_admin_role_reads_the_audit_log_and_it_filters_by_time()
    {
        HttpClient client = await StartBehindProxyAsync(Stranger);
        (await client.SendAsync(Request(HttpMethod.Post, "/router/api/health/reset", bearer: AdminToken))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.SendAsync(Request(HttpMethod.Get, "/router/api/audit", bearer: ReadOnlyToken))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/router/api/audit?limit=10", bearer: AdminToken));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode entry = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["entries"]!.AsArray().Single()!;
        entry["actor"]!.GetValue<string>().Should().Be("admin token");
        entry["summary"]!.GetValue<string>().Should().Be("cleared every cooldown");
        entry["route"]!.GetValue<string>().Should().Be("/router/api/health/reset");

        string future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
        JsonNode later = JsonNode.Parse(await (await client.SendAsync(Request(HttpMethod.Get, $"/router/api/audit?since={future}", bearer: AdminToken))).Content.ReadAsStringAsync())!;
        later["entries"]!.AsArray().Should().BeEmpty();

        HttpResponseMessage lines = await client.SendAsync(Request(HttpMethod.Get, "/router/api/audit?format=jsonl", bearer: AdminToken));
        lines.Content.Headers.ContentType!.MediaType.Should().Be("application/jsonl");
        (await lines.Content.ReadAsStringAsync()).Trim().Split('\n').Should().ContainSingle().Which.Should().Contain("\"summary\":\"cleared every cooldown\"");
        Audit().Should().ContainSingle("reading the audit log is not itself recorded");
    }

    [Fact]
    public void The_identity_settings_bind_from_configuration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SemanticRouter:Dashboard:TrustedIdentityHeader"] = "X-Auth-Request-Email",
            ["SemanticRouter:Dashboard:TrustedGroupsHeader"] = "X-Auth-Request-Groups",
            ["SemanticRouter:Dashboard:TrustedIdentityRoles:alice@corp.example"] = "Admin",
            ["SemanticRouter:Dashboard:TrustedIdentityRoles:*"] = "ReadOnly",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(configuration.GetSection("SemanticRouter"));
        using ServiceProvider provider = services.BuildServiceProvider();

        RouterDashboardOptions options = provider.GetRequiredService<IOptions<RouterDashboardOptions>>().Value;

        options.TrustedIdentityHeader.Should().Be("X-Auth-Request-Email");
        options.TrustedGroupsHeader.Should().Be("X-Auth-Request-Groups");
        options.TrustedIdentityRoles.Should().BeEquivalentTo(new Dictionary<string, RouterDashboardRole>
        {
            ["alice@corp.example"] = RouterDashboardRole.Admin,
            ["*"] = RouterDashboardRole.ReadOnly,
        });
        options.TrustedIdentityRoles["ALICE@corp.example"].Should().Be(RouterDashboardRole.Admin, "names are compared ignoring case");
    }

    [Fact]
    public void The_in_memory_log_keeps_the_latest_entries_newest_first()
    {
        var log = new InMemoryAuditLog(capacity: 3);
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        for (int i = 0; i < 5; i++)
        {
            log.Append(new AuditEntry(0, start.AddMinutes(i), "this machine", null, "POST", "/router/api/x", null, 200, "did " + i));
        }

        log.Query(null, null, 10).Select(e => (e.Id, e.Summary)).Should().Equal((5, "did 4"), (4, "did 3"), (3, "did 2"));
        log.Query(start.AddMinutes(3), start.AddMinutes(4), 10).Select(e => e.Id).Should().Equal(4);
    }
}

/// <summary>The audit log in the SQLite state database.</summary>
public sealed class SqliteAuditLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-audit").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private string DbPath => Path.Combine(_directory, "state.db");

    private SqliteRouterDatabase Open()
    {
        using var factory = LoggerFactory.Create(_ => { });
        return new SqliteRouterDatabase(Options.Create(new SqliteStateOptions { Path = DbPath }), factory.CreateLogger<SqliteRouterDatabase>(), _time);
    }

    private AuditEntry Entry(string summary) => new(0, _time.GetUtcNow(), "alice@corp.example", "203.0.113.9", "PUT", "/router/api/config", "/router/api/config", 200, summary);

    [Fact]
    public void Entries_survive_a_restart_and_are_pruned_only_past_the_retention()
    {
        SqliteRouterDatabase database = Open();
        var log = new SqliteAuditLog(database);
        AuditEntry first = log.Append(Entry("one"));
        _time.Advance(TimeSpan.FromDays(200));
        AuditEntry second = log.Append(Entry("two"));

        var reopened = new SqliteAuditLog(Open());
        reopened.Query(null, null, 10).Should().Equal(second, first);
        first.Id.Should().BeLessThan(second.Id);

        _time.Advance(TimeSpan.FromDays(200));
        reopened.Append(Entry("three"));
        reopened.Query(null, null, 10).Select(e => e.Summary).Should().Equal(new[] { "three", "two" }, "entries older than 365 days go as new ones are written");

        database.Apply(new StateRetentionOptions { AuditDays = 30 });
        log.Append(Entry("four"));
        log.Query(null, null, 10).Select(e => e.Summary).Should().Equal(new[] { "four", "three" }, "State:Retention:AuditDays sets the retention");
        log.Query(_time.GetUtcNow(), null, 10).Select(e => e.Summary).Should().Equal("four", "three");
        log.Query(null, _time.GetUtcNow(), 10).Should().BeEmpty();
    }

    [Fact]
    public void The_audit_table_is_added_to_an_existing_state_file()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta (key, value) VALUES ('schema_version', '2');";
            create.ExecuteNonQuery();
        }

        SqliteRouterDatabase database = Open();
        database.Available.Should().BeTrue();
        new SqliteAuditLog(database).Append(Entry("one")).Id.Should().Be(1);
    }

    [Fact]
    public void Audit_retention_must_be_at_least_a_day()
    {
        RouterConfiguration document = RouterConfiguration.FromJson("""{ "State": { "Retention": { "AuditDays": 0 } } }""");
        document.GetDocumentValidationErrors().Should().Contain(e => e.StartsWith("State:Retention", StringComparison.Ordinal));
        RouterConfiguration.FromJson("""{ "State": { "Retention": { "AuditDays": 730 } } }""").State.Retention.AuditDays.Should().Be(730);
    }
}
