using System.Text.Json;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The dashboard's endpoints: where each connector sends requests, its state and figures from the activity log.</summary>
public sealed class EndpointHealthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Dictionary<string, DateTimeOffset> NoCooldowns = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Only_the_host_of_an_address_is_shown_never_its_credentials_path_or_query()
    {
        RouterConfiguration config = Config();
        config.Connectors["cloud"].BaseAddress = new Uri("https://user:sk-in-userinfo@api.example.com:8443/v1/deployments?api-key=sk-in-query");

        EndpointStatus cloud = Build(config).Single(e => e.Connector == "cloud");

        cloud.Host.Should().Be("api.example.com:8443");
        cloud.Scheme.Should().Be("https");
        cloud.Encrypted.Should().BeTrue();
        cloud.Local.Should().BeFalse();
        JsonSerializer.Serialize(Build(config)).Should().NotContain("sk-in-userinfo").And.NotContain("sk-in-query").And.NotContain("deployments");
    }

    [Fact]
    public void Loopback_hosts_are_local_and_plain_http_elsewhere_is_not_encrypted()
    {
        RouterConfiguration config = Config();
        config.Connectors["cloud"].BaseAddress = new Uri("http://models.internal.example");

        IReadOnlyList<EndpointStatus> endpoints = Build(config);

        endpoints.Single(e => e.Connector == "lmstudio").Should().Match<EndpointStatus>(e => e.Host == "localhost:1234" && e.Local && e.Encrypted);
        endpoints.Single(e => e.Connector == "cloud").Should().Match<EndpointStatus>(e => e.Host == "models.internal.example" && !e.Local && !e.Encrypted);
    }

    [Fact]
    public void A_cli_shows_its_program_and_whom_it_sends_to()
    {
        EndpointStatus claude = Build(Config()).Single(e => e.Connector == "claude");

        claude.Host.Should().BeNull();
        claude.Program.Should().Be("claude");
        claude.Via.Should().Be("Anthropic");
        claude.Kind.Should().Be("Cli/ClaudeCode");
        claude.Local.Should().BeFalse("Claude Code sends prompts to Anthropic");
    }

    [Fact]
    public void A_failed_attempt_that_fell_through_counts_against_its_endpoint_with_its_own_error()
    {
        RoutingActivityEntry failover = Entry(Now.AddMinutes(-1), "served", servedBy: "gpt", total: 1200, firstByte: 400, output: 80, "local-small", "gpt");
        failover.Detail = "local-small: HTTP 500 Internal Server Error; other: skipped (busy)";
        RoutingActivityEntry direct = Entry(Now.AddMinutes(-2), "served", servedBy: "local-small", total: 300, firstByte: 100, output: null, "local-small");

        IReadOnlyList<EndpointStatus> endpoints = Build(Config(), recent: [failover, direct]);

        EndpointStatus local = endpoints.Single(e => e.Connector == "lmstudio");
        local.Attempts.Should().Be(2);
        local.Failures.Should().Be(1);
        local.SuccessRate.Should().Be(0.5);
        local.LastError.Should().Be("HTTP 500 Internal Server Error");
        local.LastFailure.Should().Be(failover.Timestamp);
        local.LastSuccess.Should().Be(direct.Timestamp);
        local.TotalP50.Should().Be(300, "only answered requests count toward its speed");

        EndpointStatus cloud = endpoints.Single(e => e.Connector == "cloud");
        cloud.Should().Match<EndpointStatus>(e => e.Attempts == 1 && e.Failures == 0 && e.SuccessRate == 1 && e.State == "ok");
        cloud.FirstByteP50.Should().Be(400);
        cloud.TokensPerSecond.Should().Be(100, "80 tokens in the 800 ms after the first byte");
    }

    [Fact]
    public void A_request_every_model_failed_counts_against_each_endpoint_it_reached()
    {
        RoutingActivityEntry failed = Entry(Now, "failed", servedBy: null, total: null, firstByte: null, output: null, "local-small", "gpt");
        failed.Detail = "Every candidate model failed: local-small: unreachable; gpt: HTTP 401";

        IReadOnlyList<EndpointStatus> endpoints = Build(Config(), recent: [failed]);

        endpoints.Single(e => e.Connector == "lmstudio").LastError.Should().Be("unreachable");
        endpoints.Single(e => e.Connector == "cloud").Should().Match<EndpointStatus>(e => e.Failures == 1 && e.SuccessRate == 0 && e.LastError == "HTTP 401");
    }

    [Fact]
    public void The_state_says_whether_an_endpoint_is_up_cooling_down_failing_or_unused()
    {
        RouterConfiguration config = Config();
        config.Models.Add(new ModelDefinition { Name = "local-large", Connector = "lmstudio", Model = "qwen3-32b", IsLocal = true });
        config.Connectors["spare"] = new ConnectorOptions { BaseAddress = new Uri("https://spare.example.com") };
        var incidents = new ConnectorIncidents();
        incidents.Failed("cloud", ModelFailureKind.Unavailable, "HTTP 503");
        var cooldowns = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { ["local-small"] = Now.AddMinutes(5), ["claude-cli"] = Now.AddHours(3) };

        IReadOnlyList<EndpointStatus> endpoints = EndpointHealth.Build(config, cooldowns, [], incidents, null);

        endpoints.Single(e => e.Connector == "lmstudio").Should().Match<EndpointStatus>(e => e.State == "degraded" && e.ModelsCoolingDown == 1 && e.CooldownUntil == cooldowns["local-small"]);
        endpoints.Single(e => e.Connector == "claude").State.Should().Be("down", "its only model is cooling down");
        endpoints.Single(e => e.Connector == "cloud").Should().Match<EndpointStatus>(e => e.State == "failing" && e.FailingSince != null);
        endpoints.Single(e => e.Connector == "spare").State.Should().Be("unused");
        EndpointHealth.Build(Config(), NoCooldowns, [], null, null).Should().OnlyContain(e => e.State == "idle");
    }

    [Fact]
    public void Many_recent_failures_degrade_an_endpoint_that_still_answers_sometimes()
    {
        RoutingActivityEntry[] recent =
        [
            Entry(Now, "served", servedBy: "gpt", total: 100, firstByte: 50, output: null, "local-small", "gpt"),
            Entry(Now.AddSeconds(-1), "served", servedBy: "gpt", total: 100, firstByte: 50, output: null, "local-small", "gpt"),
            Entry(Now.AddSeconds(-2), "served", servedBy: "gpt", total: 100, firstByte: 50, output: null, "local-small", "gpt"),
            Entry(Now.AddSeconds(-3), "served", servedBy: "local-small", total: 100, firstByte: 50, output: null, "local-small"),
        ];

        Build(Config(), recent).Single(e => e.Connector == "lmstudio").Should().Match<EndpointStatus>(e => e.State == "degraded" && e.Failures == 3);
    }

    [Fact]
    public async Task A_check_is_kept_until_the_connector_points_somewhere_else()
    {
        RouterConfiguration config = Config();
        var checks = new EndpointChecks();
        var started = new TaskCompletionSource<IReadOnlyList<RouterCheck>>();
        int runs = 0;
        Task<IReadOnlyList<RouterCheck>> Probe()
        {
            Interlocked.Increment(ref runs);
            return started.Task;
        }

        string target = EndpointHealth.TargetKey(config, "lmstudio");
        Task<EndpointCheck> first = checks.RunAsync("lmstudio", target, Probe);
        Task<EndpointCheck> second = checks.RunAsync("lmstudio", target, Probe);
        started.SetResult([new RouterCheck("connector lmstudio", RouterCheckStatus.Failed, "http://localhost:1234/v1/models is not reachable (refused).")]);
        EndpointCheck result = await first;

        (await second).Should().BeSameAs(result, "a check already under way is joined, not started again");
        runs.Should().Be(1);
        result.Status.Should().Be("Failed");
        result.Messages.Should().Equal("http://localhost:1234/v1/models is not reachable (refused).");
        Build(config, checks: checks).Single(e => e.Connector == "lmstudio").Should().Match<EndpointStatus>(e => e.Check == result && e.State == "failing");

        config.Connectors["lmstudio"].BaseAddress = new Uri("http://localhost:11434");
        Build(config, checks: checks).Single(e => e.Connector == "lmstudio").Check.Should().BeNull("the check was of another address");
    }

    private static IReadOnlyList<EndpointStatus> Build(RouterConfiguration config, IReadOnlyList<RoutingActivityEntry>? recent = null, EndpointChecks? checks = null) =>
        EndpointHealth.Build(config, NoCooldowns, recent ?? [], null, checks);

    private static RouterConfiguration Config()
    {
        var config = new RouterConfiguration();
        config.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        config.Connectors["cloud"] = new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com") };
        config.Connectors["claude"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };
        config.Models.Add(new ModelDefinition { Name = "local-small", Connector = "lmstudio", Model = "qwen3-8b", IsLocal = true });
        config.Models.Add(new ModelDefinition { Name = "gpt", Connector = "cloud", Model = "gpt-5" });
        config.Models.Add(new ModelDefinition { Name = "claude-cli", Connector = "claude", Model = "sonnet" });
        return config;
    }

    private static RoutingActivityEntry Entry(
        DateTimeOffset at, string outcome, string? servedBy, double? total, double? firstByte, int? output, params string[] sentTo)
    {
        var entry = new RoutingActivityEntry
        {
            Timestamp = at,
            Outcome = outcome,
            ServedBy = servedBy,
            TotalMilliseconds = total,
            TimeToFirstByteMilliseconds = firstByte,
            OutputTokens = output,
        };
        foreach (string model in sentTo)
        {
            entry.SentTo.Add(model);
        }

        return entry;
    }
}
