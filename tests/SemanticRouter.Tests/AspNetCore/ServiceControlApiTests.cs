using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The dashboard's Service page API: what the host's <see cref="IRouterServiceControl"/> describes, saving where the
/// router listens, and actions that start only once their answer has been sent.
/// </summary>
public sealed class ServiceControlApiTests : RouterHostTest
{
    private const string ReadOnlyToken = "view-token-0123456789";

    private readonly FakeControl _control = new();

    private Task<HttpClient> StartWithControlAsync(bool control = true, IPAddress? peer = null) =>
        StartAsync(
            services: s =>
            {
                if (control)
                {
                    s.AddSingleton<IRouterServiceControl>(_control);
                }

                s.Configure<RouterDashboardOptions>(o => o.ReadOnlyToken = ReadOnlyToken);
                if (peer is not null)
                {
                    s.Configure<RouterDashboardOptions>(o => o.RequireLocalClient = false);
                }
            },
            beforeRouter: app => app.Use((context, next) =>
            {
                if (peer is not null)
                {
                    context.Connection.RemoteIpAddress = peer;
                }

                return next(context);
            }));

    private static async Task<JsonNode> NodeAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Without_a_control_the_page_is_told_the_host_decides()
    {
        HttpClient client = await StartWithControlAsync(control: false);

        JsonNode service = await NodeAsync(await client.GetAsync("/router/api/service"));
        ((bool)service["available"]!).Should().BeFalse();
        ((bool)service["dashboard"]!["requireLocalClient"]!).Should().BeTrue();

        HttpResponseMessage restart = await client.PostAsync("/router/api/service/restart", null);
        restart.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await restart.Content.ReadAsStringAsync()).Should().Contain("cannot change it");
    }

    [Fact]
    public async Task The_description_comes_from_the_control()
    {
        HttpClient client = await StartWithControlAsync();

        JsonNode service = await NodeAsync(await client.GetAsync("/router/api/service"));

        ((bool)service["available"]!).Should().BeTrue();
        ((string?)service["service"]!["runsAs"]).Should().Be("foreground");
        ((string?)service["service"]!["listen"]!["urls"]).Should().Be("http://localhost:5080");
        ((string?)service["service"]!["background"]!["kind"]).Should().Be("fake service");
        service["service"]!["actions"]!.AsArray().Select(a => (string?)a).Should().Equal("restart", "stop", "install");
    }

    [Fact]
    public async Task Saving_where_it_listens_checks_the_value_and_is_audited()
    {
        HttpClient client = await StartWithControlAsync();

        HttpResponseMessage saved = await client.PutAsJsonAsync("/router/api/service/listen", new { scope = "network", port = 6000 });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        ((string?)(await NodeAsync(saved))["listen"]!["urls"]).Should().Be("http://0.0.0.0:6000");

        (await client.PutAsJsonAsync("/router/api/service/listen", new { scope = "everywhere", port = 6000 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _control.Locked = "the ASPNETCORE_URLS environment variable decides";
        (await client.PutAsJsonAsync("/router/api/service/listen", new { scope = "local", port = 6000 })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        AuditEntry entry = Services.GetRequiredService<IAuditLog>().Query(null, null, 10).First(e => e.Status == 200);
        entry.Summary.Should().Contain("changed where the router listens").And.Contain("other machines too, port 6000");
    }

    [Fact]
    public async Task An_action_starts_after_its_answer_and_names_itself_in_the_audit_log()
    {
        HttpClient client = await StartWithControlAsync();

        HttpResponseMessage accepted = await client.PostAsync("/router/api/service/restart", null);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        ((string?)(await NodeAsync(accepted))["action"]).Should().Be("restart");
        (await _control.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("restart");
        Services.GetRequiredService<IAuditLog>().Query(null, null, 10).Should().Contain(e => e.Summary.StartsWith("restarted the router", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_or_unknown_action_does_not_run()
    {
        HttpClient client = await StartWithControlAsync();

        HttpResponseMessage refused = await client.PostAsync("/router/api/service/uninstall", null);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("not installed");

        (await client.PostAsync("/router/api/service/explode", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _control.Ran.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task The_read_only_role_can_neither_see_nor_act()
    {
        HttpClient client = await StartWithControlAsync(peer: IPAddress.Parse("198.51.100.7"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadOnlyToken);

        (await client.GetAsync("/router/api/service")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/router/api/service/stop", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _control.Ran.Task.IsCompleted.Should().BeFalse();
    }

    private sealed class FakeControl : IRouterServiceControl
    {
        private string _urls = "http://localhost:5080";

        public TaskCompletionSource<string> Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Locked { get; set; }

        public Task<RouterServiceDescription> DescribeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RouterServiceDescription(
            new RouterListenDescription("local", 5080, _urls, null, "/config/semantic-router.host.json", null, Locked is null, false),
            new RouterBackgroundService("fake service", false, null, "/fake/unit", "fake log", []),
            "foreground",
            [.. RouterServiceActions.All.Where(a => Refusal(a) is null)],
            null));

        public Task<RouterServiceDescription> SetListenAsync(RouterListenChange change, CancellationToken cancellationToken = default)
        {
            if (change.Scope is not ("local" or "network"))
            {
                throw new ArgumentException("not a scope");
            }

            if (Locked is not null)
            {
                throw new InvalidOperationException(Locked);
            }

            _urls = $"http://{(change.Scope == "network" ? "0.0.0.0" : "localhost")}:{change.Port}";
            return DescribeAsync(cancellationToken);
        }

        public string? Refusal(string action) => action is RouterServiceActions.Reinstall or RouterServiceActions.Uninstall ? "The background service is not installed." : null;

        public void Run(string action) => Ran.TrySetResult(action);
    }
}
