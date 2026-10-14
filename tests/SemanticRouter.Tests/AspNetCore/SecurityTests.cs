using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Secret references and the secret store through the running router, and redaction of secrets in output.</summary>
public sealed class SecretHandlingTests : RouterHostTest
{
    private readonly CapturingLoggerProvider _logs = new();

    private Task<HttpClient> StartDispatchAsync(Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                s.AddSingleton<ILoggerProvider>(_logs);
                services?.Invoke(s);
            });

    private static async Task<JsonNode> ReadNodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static StringContent JsonBody(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Connector_keys_can_be_environment_references()
    {
        string variable = "SR_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "sk-from-environment");
        try
        {
            ClassifyAs(tier: 4);
            HttpClient client = await StartDispatchAsync(s =>
                s.PostConfigure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic"].ApiKey = "${env:" + variable + "}"));

            await client.PostAsync("/v1/chat/completions", Json(ChatJson));
            JsonNode config = await ReadNodeAsync(await client.GetAsync("/router/api/config"));
            JsonNode status = await ReadNodeAsync(await client.GetAsync("/router/api/status"));

            CloudHttp.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer sk-from-environment");
            config["config"]!["Connectors"]!["anthropic"]!["ApiKey"]!.GetValue<string>().Should().Be("${env:" + variable + "}");
            status["warnings"]!.AsArray().Should().BeEmpty();
            status["connectors"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "anthropic")!["hasApiKey"]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task A_key_in_a_connector_address_is_not_shown_in_status_or_check_messages()
    {
        HttpClient client = await StartDispatchAsync(s =>
            s.PostConfigure<SemanticRouterConnectorsOptions>(o =>
                o.Connectors["anthropic"].BaseAddress = new Uri("https://user:sk-in-userinfo@api.anthropic.example/v1?api-key=sk-in-query#sk-in-fragment")));

        string status = await client.GetStringAsync("/router/api/status");
        string check = await (await client.PostAsync("/router/api/connectors/anthropic/check", null)).Content.ReadAsStringAsync();

        foreach (string text in new[] { status, check })
        {
            text.Should().NotContain("sk-in-userinfo").And.NotContain("sk-in-query").And.NotContain("sk-in-fragment");
        }

        JsonNode node = JsonNode.Parse(status)!;
        node["connectors"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "anthropic")!["target"]!.GetValue<string>()
            .Should().Be("https://api.anthropic.example/v1");
        node["endpoints"]!.AsArray().Single(c => c!["connector"]!.GetValue<string>() == "anthropic")!["host"]!.GetValue<string>()
            .Should().Be("api.anthropic.example");
        JsonNode.Parse(check)!["messages"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_unset_reference_is_reported_in_status_and_the_log()
    {
        HttpClient client = await StartDispatchAsync(s =>
            s.PostConfigure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic"].ApiKey = "${env:SR_TEST_SURELY_UNSET}"));

        JsonNode status = await ReadNodeAsync(await client.GetAsync("/router/api/status"));

        status["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().ContainSingle()
            .Which.Should().Contain("Connectors:anthropic:ApiKey").And.Contain("SR_TEST_SURELY_UNSET");
        _logs.All.Should().Contain("SR_TEST_SURELY_UNSET");
    }

    [Fact]
    public async Task Saving_from_the_dashboard_moves_typed_keys_into_the_secret_store()
    {
        string directory = Directory.CreateTempSubdirectory("router-store").FullName;
        try
        {
            string path = Path.Combine(directory, "semantic-router.json");
            var secrets = new SecretsTests.MemorySecretStore();
            ClassifyAs(tier: 4);
            HttpClient client = await StartDispatchAsync(s =>
            {
                s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(path));
                s.AddSingleton<IRouterSecretStore>(secrets);
            });

            JsonNode envelope = await ReadNodeAsync(await client.GetAsync("/router/api/config"));
            envelope["secretStore"]!.GetValue<string>().Should().Be("memory");
            JsonNode config = envelope["config"]!;
            config["Connectors"]!["anthropic"]!["ApiKey"] = "sk-typed-in-dashboard";
            HttpResponseMessage saved = await client.PutAsync("/router/api/config", JsonBody(config));

            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadNodeAsync(saved))["warning"]!.GetValue<string>().Should().Contain("Moved").And.Contain("Connectors:anthropic:ApiKey");
            string file = await File.ReadAllTextAsync(path);
            file.Should().Contain("${secret:connectors/anthropic/ApiKey}").And.NotContain("sk-typed-in-dashboard");
            secrets.Values["connectors/anthropic/ApiKey"].Should().Be("sk-typed-in-dashboard");

            await client.PostAsync("/v1/chat/completions", Json(ChatJson));
            CloudHttp.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer sk-typed-in-dashboard");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Saving_a_typed_key_without_a_secret_store_warns_without_logging_the_key()
    {
        string directory = Directory.CreateTempSubdirectory("router-store").FullName;
        try
        {
            string path = Path.Combine(directory, "semantic-router.json");
            HttpClient client = await StartDispatchAsync(s => s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(path)));

            JsonNode config = (await ReadNodeAsync(await client.GetAsync("/router/api/config")))["config"]!;
            config["Connectors"]!["anthropic"]!["ApiKey"] = "sk-typed-plain-text";
            JsonNode saved = await ReadNodeAsync(await client.PutAsync("/router/api/config", JsonBody(config)));

            saved["warning"]!.GetValue<string>().Should().Contain("plain text").And.Contain("${env:VARIABLE}").And.NotContain("sk-typed-plain-text");
            _logs.All.Should().Contain("Saving secrets in plain text").And.NotContain("sk-typed-plain-text");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Keys_echoed_by_failing_backends_never_reach_clients_activity_or_logs()
    {
        // The local model fails with a message that quotes the cloud key; the cloud model rejects its credentials.
        LocalHttp = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("proxy refused key sk-test"));
        CloudHttp = StubHttpMessageHandler.Json("""{"error":{"message":"Incorrect API key provided: sk-test"}}""", HttpStatusCode.Unauthorized);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        string body = await response.Content.ReadAsStringAsync();
        string activity = await client.GetStringAsync("/router/api/activity");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("rejected the credentials").And.Contain("SemanticRouter:Connectors:anthropic:ApiKey").And.NotContain("sk-test");
        activity.Should().Contain("rejected the credentials").And.NotContain("sk-test");
        _logs.All.Should().Contain("rejected the credentials").And.NotContain("sk-test");
    }

    [Fact]
    public async Task Relayed_upstream_errors_are_redacted_in_both_protocols()
    {
        ClassifyAs(tier: 4);
        CloudHttp = StubHttpMessageHandler.Json("""{"error":{"message":"Bad request for key sk-test"}}""", HttpStatusCode.BadRequest);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage openAi = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage anthropic = await client.PostAsync(
            "/v1/messages", Json("""{"model":"claude-sonnet-5-5","max_tokens":10,"messages":[{"role":"user","content":"hello there"}]}"""));

        openAi.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await openAi.Content.ReadAsStringAsync()).Should().Contain("Bad request for key ***");
        anthropic.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anthropic.Content.ReadAsStringAsync()).Should().Contain("invalid_request_error").And.Contain("Bad request for key ***");
    }
}

/// <summary>Who may use the dashboard and admin API, and the gateway keys.</summary>
public sealed class DashboardAccessTests : RouterHostTest
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReadOnlyToken = "viewer-token-0123456789";
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    private readonly CapturingLoggerProvider _logs = new();

    private Task<HttpClient> StartAsync(
        Action<RouterDashboardOptions>? dashboard = null,
        bool remote = true,
        Action<PromptRouterMiddlewareOptions>? middleware = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                middleware?.Invoke(o);
            },
            services: s =>
            {
                s.AddSingleton<ILoggerProvider>(_logs);
                if (dashboard is not null)
                {
                    s.Configure(dashboard);
                }
            },
            beforeRouter: remote
                ? app => app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = Remote;
                    return next(context);
                })
                : null);

    private static HttpRequestMessage Get(string path, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    private static void WithTokens(RouterDashboardOptions o)
    {
        o.RequireLocalClient = false;
        o.AdminToken = AdminToken;
        o.ReadOnlyToken = ReadOnlyToken;
    }

    [Fact]
    public async Task Remote_clients_are_refused_when_no_token_is_configured()
    {
        HttpClient client = await StartAsync(o => o.RequireLocalClient = false);

        HttpResponseMessage response = await client.GetAsync("/router/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("needs a token").And.Contain("AdminToken");
        _logs.All.Should().Contain("remote requests to /router will be refused");
    }

    [Fact]
    public async Task Remote_clients_need_a_valid_token_but_can_load_the_sign_in_page()
    {
        HttpClient client = await StartAsync(WithTokens);

        (await client.GetAsync("/router")).StatusCode.Should().Be(HttpStatusCode.OK, "the page shows the sign-in form");
        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/router/api/status", "not-the-token-at-all"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/router/api/status", AdminToken))).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonNode session = JsonNode.Parse(await client.GetStringAsync("/router/api/session"))!;
        session["role"].Should().BeNull();
        session["tokenRequired"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task The_read_only_token_can_look_but_not_change()
    {
        HttpClient client = await StartAsync(WithTokens);
        string config = JsonNode.Parse(await (await client.SendAsync(Get("/router/api/config", ReadOnlyToken))).Content.ReadAsStringAsync())!["config"]!.ToJsonString();

        var put = new HttpRequestMessage(HttpMethod.Put, "/router/api/config?persist=false") { Content = new StringContent(config, Encoding.UTF8, "application/json") };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReadOnlyToken);
        var reset = new HttpRequestMessage(HttpMethod.Post, "/router/api/health/reset");
        reset.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReadOnlyToken);
        var classify = new HttpRequestMessage(HttpMethod.Post, "/router/api/classify") { Content = JsonContent.Create(new { prompt = "hello" }) };
        classify.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReadOnlyToken);

        (await client.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(reset)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(classify)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get("/router/api/activity", ReadOnlyToken))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signing_in_sets_a_session_cookie_that_does_not_carry_the_token()
    {
        HttpClient client = await StartAsync(WithTokens);

        HttpResponseMessage wrong = await client.PostAsJsonAsync("/router/api/session", new { token = "guess-guess-guess-guess" });
        HttpResponseMessage signIn = await client.PostAsJsonAsync("/router/api/session", new { token = AdminToken });

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        string cookie = signIn.Headers.GetValues("Set-Cookie").Single();
        cookie.Should().StartWith("semantic-router-session=a.").And.Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/router");
        cookie.Should().NotContain(AdminToken);

        var request = Get("/router/api/session");
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        JsonNode session = JsonNode.Parse(await (await client.SendAsync(request)).Content.ReadAsStringAsync())!;
        session["role"]!.GetValue<string>().Should().Be("Admin");
    }

    [Fact]
    public async Task Cross_origin_writes_and_foreign_host_names_are_refused_even_from_this_machine()
    {
        HttpClient client = await StartAsync(remote: false);
        string config = JsonNode.Parse(await client.GetStringAsync("/router/api/config"))!["config"]!.ToJsonString();

        HttpRequestMessage Put(string? origin, string? fetchSite = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "/router/api/config?persist=false") { Content = new StringContent(config, Encoding.UTF8, "application/json") };
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            if (fetchSite is not null)
            {
                request.Headers.Add("Sec-Fetch-Site", fetchSite);
            }

            return request;
        }

        (await client.SendAsync(Put("http://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Put(null, "cross-site"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Put("http://localhost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Put(null))).StatusCode.Should().Be(HttpStatusCode.OK, "scripts such as curl send no Origin");

        // DNS rebinding: a page on evil.example whose name now resolves to 127.0.0.1.
        HttpRequestMessage rebound = Get("/router/api/config");
        rebound.Headers.Host = "evil.example:5080";
        HttpResponseMessage response = await client.SendAsync(rebound);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("is not this machine");
    }

    [Fact]
    public async Task Web_pages_cannot_use_the_gateway_through_dns_rebinding_or_from_other_sites()
    {
        HttpClient client = await StartAsync(remote: false, middleware: o => o.GatewayKeys["laptop"] = "gw-laptop-key-123");

        HttpRequestMessage Chat(string? host = null, string? origin = null, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json(ChatJson) };
            if (host is not null)
            {
                request.Headers.Host = host;
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            if (key is not null)
            {
                request.Headers.Add("x-api-key", key);
            }

            return request;
        }

        // A page on evil.example whose name now resolves to 127.0.0.1 is same-origin with the gateway.
        HttpResponseMessage rebound = await client.SendAsync(Chat(host: "evil.example:5080", origin: "http://evil.example:5080"));
        rebound.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rebound.Content.ReadAsStringAsync()).Should().Contain("is not this machine");
        (await client.SendAsync(Chat(origin: "http://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Chat(origin: "null"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        LocalHttp.Requests.Should().BeEmpty("no refused request reaches a backend");

        (await client.SendAsync(Chat())).StatusCode.Should().Be(HttpStatusCode.OK, "SDKs and CLIs send no Origin");
        (await client.SendAsync(Chat(origin: "http://localhost"))).StatusCode.Should().Be(HttpStatusCode.OK, "the dashboard's prompt tester is same-origin");
        (await client.SendAsync(Chat(host: "router.internal:5080", key: "gw-laptop-key-123"))).StatusCode
            .Should().Be(HttpStatusCode.OK, "a page has no gateway key, so a request with one is not checked");

        var models = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        models.Headers.Host = "evil.example:5080";
        (await client.SendAsync(models)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var classify = new HttpRequestMessage(HttpMethod.Post, "/semantic-router/classify") { Content = JsonContent.Create(new { prompt = "hello" }) };
        classify.Headers.Host = "evil.example:5080";
        (await client.SendAsync(classify)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var count = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens")
        {
            Content = Json("""{"model":"x","messages":[{"role":"user","content":"hi"}]}"""),
        };
        count.Headers.Add("Origin", "http://evil.example");
        (await client.SendAsync(count)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/v1/models")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Classify_models_and_token_counts_need_a_gateway_key_from_remote_clients()
    {
        HttpClient client = await StartAsync(middleware: o => o.GatewayKeys["laptop"] = "gw-laptop-key-123");

        (await client.PostAsJsonAsync("/semantic-router/classify", new { prompt = "hello" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/v1/models")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        HttpResponseMessage count = await client.PostAsync(
            "/v1/messages/count_tokens", Json("""{"model":"x","messages":[{"role":"user","content":"hi"}]}"""));
        count.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await count.Content.ReadAsStringAsync()).Should().Contain("authentication_error");

        var withKey = new HttpRequestMessage(HttpMethod.Post, "/semantic-router/classify") { Content = JsonContent.Create(new { prompt = "hello" }) };
        withKey.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "gw-laptop-key-123");
        (await client.SendAsync(withKey)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_configuration_history_keeps_no_keys_and_rollback_takes_them_from_the_running_router()
    {
        HttpClient client = await StartAsync(remote: false);
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        _ = provider.Current;
        IRouterConfigurationHistory history = provider.History!;
        long startup = history.List(1).Single().Id;

        history.GetVersion(startup)!.ToJson().Should().NotContain("sk-test");
        HttpResponseMessage rollback = await client.PostAsync($"/router/api/config/history/{startup}/rollback", null);

        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        provider.Current.Configuration.Connectors["anthropic"].ApiKey.Should().Be("sk-test");
        history.List(1).Single().Id.Should().BeGreaterThan(startup);
        history.GetVersion(history.List(1).Single().Id)!.ToJson().Should().NotContain("sk-test");
    }

    [Fact]
    public async Task What_a_cli_runs_cannot_be_changed_over_the_admin_api_unless_allowed()
    {
        HttpClient client = await StartAsync(remote: false);

        async Task<HttpResponseMessage> PutWithAsync(HttpClient http, string name, string connector)
        {
            JsonNode config = JsonNode.Parse(await http.GetStringAsync("/router/api/config"))!["config"]!;
            config["Connectors"]![name] = JsonNode.Parse(connector);
            return await http.PutAsync("/router/api/config?persist=false", new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"));
        }

        HttpResponseMessage shell = await PutWithAsync(client, "evil", """{"Type":"Cli","Preset":"Custom","Executable":"/bin/sh","Arguments":["-c","id"]}""");
        HttpResponseMessage preload = await PutWithAsync(client, "claude", """{"Type":"Cli","Preset":"ClaudeCode","EnvironmentVariables":{"LD_PRELOAD":"/tmp/x.so"}}""");
        HttpResponseMessage preset = await PutWithAsync(client, "claude", """{"Type":"Cli","Preset":"ClaudeCode"}""");

        shell.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await shell.Content.ReadAsStringAsync()).Should().Contain("Connectors:evil").And.Contain("AllowProcessSettings");
        preload.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        preset.StatusCode.Should().Be(HttpStatusCode.OK, "a preset started from PATH runs the vendor's own CLI");
        await DisposeAsync();

        HttpClient allowed = await StartAsync(o => o.AllowProcessSettings = true, remote: false);
        (await PutWithAsync(allowed, "custom", """{"Type":"Cli","Preset":"Custom","Executable":"/usr/local/bin/llm"}""")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_other_site_may_frame_the_dashboard()
    {
        HttpClient client = await StartAsync(remote: false);

        foreach (string path in new[] { "/router", "/router/api/status", "/router/js/app.js" })
        {
            HttpResponseMessage response = await client.GetAsync(path);
            response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
            response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'none'");
            response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        }
    }

    [Fact]
    public async Task Unauthenticated_remote_access_must_be_asked_for_explicitly()
    {
        HttpClient client = await StartAsync(o =>
        {
            o.RequireLocalClient = false;
            o.AllowUnauthenticatedRemoteClients = true;
        });

        (await client.GetAsync("/router/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        _logs.All.Should().Contain("open to remote clients without authentication");
    }

    [Fact]
    public async Task Short_tokens_are_rejected_at_start_up()
    {
        Func<Task> act = () => StartAsync(o =>
        {
            o.RequireLocalClient = false;
            o.AdminToken = "short";
        });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("at least 16 characters");
    }

    [Fact]
    public async Task Gateway_keys_are_required_from_remote_clients_and_name_them_in_the_activity_log()
    {
        HttpClient client = await StartAsync(middleware: o => o.GatewayKeys["laptop"] = "gw-laptop-key-123");

        HttpResponseMessage missing = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage anthropicMissing = await client.PostAsync("/v1/messages", Json("""{"model":"x","max_tokens":5,"messages":[{"role":"user","content":"hi"}]}"""));
        var withKey = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json(ChatJson) };
        withKey.Headers.Add("x-api-key", "gw-laptop-key-123");
        HttpResponseMessage served = await client.SendAsync(withKey);

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anthropicMissing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anthropicMissing.Content.ReadAsStringAsync()).Should().Contain("authentication_error");
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        LocalHttp.Requests.Should().ContainSingle();
        LocalHttp.Requests.Single().Request.Headers.Contains("x-api-key").Should().BeFalse("gateway keys are never forwarded");

        RoutingActivityLog log = Services.GetRequiredService<RoutingActivityLog>();
        log.Recent().Single().Client.Should().Be("laptop");
        _logs.All.Should().NotContain("gw-laptop-key-123");
    }

    [Fact]
    public async Task Remote_clients_are_refused_while_no_gateway_key_is_configured()
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage chat = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage anthropic = await client.PostAsync("/v1/messages", Json("""{"model":"x","max_tokens":5,"messages":[{"role":"user","content":"hi"}]}"""));

        chat.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await chat.Content.ReadAsStringAsync()).Should().Contain("none is configured").And.Contain("GatewayKeys");
        anthropic.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anthropic.Content.ReadAsStringAsync()).Should().Contain("authentication_error");
        (await client.GetAsync("/v1/models")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/semantic-router/classify", new { prompt = "hello" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        LocalHttp.Requests.Should().BeEmpty();
        CloudHttp.Requests.Should().BeEmpty();
        _logs.All.Should().Contain("no valid gateway key");
        await DisposeAsync();

        HttpClient local = await StartAsync(remote: false);
        (await local.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK, "this machine needs no key");
    }

    [Fact]
    public async Task Unauthenticated_remote_gateway_clients_must_be_asked_for_explicitly()
    {
        HttpClient client = await StartAsync(middleware: o => o.AllowUnauthenticatedRemoteClients = true);

        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
        _logs.All.Should().Contain("serves other machines without a key");
        await DisposeAsync();

        HttpClient keyed = await StartAsync(middleware: o =>
        {
            o.AllowUnauthenticatedRemoteClients = true;
            o.GatewayKeys["laptop"] = "gw-laptop-key-123";
        });
        (await keyed.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "once a key is configured, every remote client needs one");
    }

    [Fact]
    public async Task A_gateway_key_whose_variable_is_unset_does_not_open_the_gateway()
    {
        string variable = "SR_TEST_GATEWAY_" + Guid.NewGuid().ToString("N");
        HttpClient client = await StartAsync(remote: false, middleware: o =>
        {
            o.GatewayKeys["laptop"] = "${env:" + variable + "}";
            o.RequireGatewayKeyForLocalClients = true;
        });

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("requires a gateway key");
    }

    [Fact]
    public async Task Local_clients_may_omit_the_gateway_key_unless_required()
    {
        HttpClient lenient = await StartAsync(remote: false, middleware: o => o.GatewayKeys["laptop"] = "gw-laptop-key-123");
        (await lenient.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode config = JsonNode.Parse(await lenient.GetStringAsync("/router/api/config"))!;
        config["config"]!["Middleware"]!["GatewayKeys"]!["laptop"]!.GetValue<string>().Should().Be("***");
        await DisposeAsync();

        HttpClient strict = await StartAsync(remote: false, middleware: o =>
        {
            o.GatewayKeys["laptop"] = "gw-laptop-key-123";
            o.RequireGatewayKeyForLocalClients = true;
        });
        (await strict.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>The HTTP clients that carry keys.</summary>
public sealed class KeyedHttpClientTests
{
    [Theory]
    [InlineData(OpenAICompatibleConnector.HttpClientNamePrefix + "openai")]
    [InlineData(WebhookNotifier.ClientName)]
    [InlineData("SemanticRouter.Embeddings")]
    public void Clients_that_carry_keys_do_not_follow_redirects(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(_ => { });
        using ServiceProvider provider = services.BuildServiceProvider();

        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        bool? follows = handler switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler client => client.AllowAutoRedirect,
            _ => null,
        };
        follows.Should().BeFalse("a redirect would carry x-api-key and other key headers to another host");
    }
}

