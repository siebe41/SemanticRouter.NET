using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The dashboard's Service card: the addresses clients point at and the router's own state.</summary>
public sealed class ServiceHealthTests
{
    [Fact]
    public void A_wildcard_bind_is_localhost_here_and_the_machine_name_and_network_addresses_elsewhere()
    {
        IReadOnlyList<ClientAddress> addresses = ServiceHealth.Addresses(["http://[::]:5080"], [], "desk", ["192.168.1.20"]);

        addresses.Should().Equal(
            new ClientAddress { Url = "http://localhost:5080", Local = true, Encrypted = true },
            new ClientAddress { Url = "http://desk:5080", Local = false, Encrypted = false },
            new ClientAddress { Url = "http://192.168.1.20:5080", Local = false, Encrypted = false });
    }

    [Fact]
    public void A_loopback_bind_is_this_machine_only()
    {
        ServiceHealth.Addresses(["http://127.0.0.1:5080", "http://localhost:5080"], [], "desk", ["192.168.1.20"])
            .Should().Equal(new ClientAddress { Url = "http://localhost:5080", Local = true, Encrypted = true });
    }

    [Fact]
    public void Public_origins_are_listed_after_the_bound_addresses_and_default_ports_are_left_out()
    {
        IReadOnlyList<ClientAddress> addresses = ServiceHealth.Addresses(["https://*:443"], ["https://router.home.lan", "not a url"], "desk", []);

        addresses.Select(a => a.Url).Should().Equal("https://localhost", "https://desk", "https://router.home.lan");
        addresses.Should().OnlyContain(a => a.Encrypted);
    }

    [Fact]
    public void Ok_when_the_index_is_ready_and_a_connector_answers()
    {
        (string state, string? note, IReadOnlyList<string> problems) = ServiceHealth.State(true, null, true, [], [Endpoint("lmstudio", "ok"), Endpoint("spare", "unused")]);

        state.Should().Be("ok");
        note.Should().BeNull();
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Starting_while_the_routes_are_embedded()
    {
        ServiceHealth.State(false, null, true, [], [Endpoint("lmstudio", "idle")]).State.Should().Be("starting");
        ServiceHealth.State(false, null, false, [], [Endpoint("lmstudio", "idle")]).State.Should().Be("ok", "without a warm-up the index is built on the first request");
    }

    [Fact]
    public void Degraded_when_some_connectors_fail_and_failing_when_all_do()
    {
        (string state, string? note, IReadOnlyList<string> problems) = ServiceHealth.State(true, null, true, [], [Endpoint("cloud", "failing", "The last check failed."), Endpoint("lmstudio", "ok")]);
        state.Should().Be("degraded");
        note.Should().Contain("1 of 2");
        problems.Should().Equal("cloud is failing: The last check failed.");

        ServiceHealth.State(true, null, true, [], [Endpoint("cloud", "failing"), Endpoint("lmstudio", "down")]).State.Should().Be("failing");
        ServiceHealth.State(true, null, true, [], [Endpoint("spare", "unused")]).State.Should().Be("failing", "no model is enabled");
    }

    [Fact]
    public void A_failed_warm_up_and_configuration_warnings_need_a_look()
    {
        (string state, _, IReadOnlyList<string> problems) = ServiceHealth.State(false, "connection refused", true, ["Model 'x' has no tier."], [Endpoint("lmstudio", "ok")]);

        state.Should().Be("degraded");
        problems.Should().Equal("The route index could not be embedded: connection refused", "Model 'x' has no tier.");
    }

    private static EndpointStatus Endpoint(string connector, string state, string? note = null) => new EndpointStatus
    {
        Connector = connector,
        Kind = "OpenAICompatible",
        Host = "localhost:1234",
        Scheme = "http",
        Local = true,
        Encrypted = true,
        State = state,
        StateNote = note,
        Models = state == "unused" ? 0 : 1,
        ModelsCoolingDown = 0,
        Attempts = 0,
        Failures = 0,
        LimitDays = 0,
    };
}
