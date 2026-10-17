using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// The dashboard's Service page in the tool: the saved listen addresses, what it may do where, handing over to the
/// background service, and restarting and stopping a real router in the same process.
/// </summary>
public sealed class LifecycleTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-lifecycle").FullName;

    public void Dispose()
    {
        for (int attempt = 1; ; attempt++)
        {
            SqliteFiles.Release(_directory);
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private HostSettings Settings(params string[] args) =>
        HostSettings.Parse(["--config-dir", _directory, "--SemanticRouter:Embeddings:Provider=Hashing", .. args], _ => null);

    private RouterLifecycle Lifecycle(ServicePlatform? platform, Func<string, string?>? environment = null, params string[] args) =>
        new(Settings(args), platform, s => new ServiceDefinition { Program = "/usr/bin/semantic-router", ConfigurationDirectory = s.ConfigurationDirectory, HostArguments = s.HostArguments },
            environment ?? (_ => null))
        {
            StartTimeout = TimeSpan.FromMilliseconds(500),
        };

    [Fact]
    public void The_saved_addresses_keep_the_rest_of_the_file()
    {
        HostListen.ReadSaved(_directory).Should().BeNull();
        File.WriteAllText(HostListen.FileIn(_directory), """{ "Logging": { "LogLevel": { "Default": "Debug" } }, "urls": "http://localhost:1" }""");

        HostListen.Save(_directory, HostListen.Urls("network", 6001));

        HostListen.ReadSaved(_directory).Should().Be("http://0.0.0.0:6001");
        JsonNode file = JsonNode.Parse(File.ReadAllText(HostListen.FileIn(_directory)))!;
        ((string?)file["Logging"]!["LogLevel"]!["Default"]).Should().Be("Debug");
        file.AsObject().Select(p => p.Key).Should().Equal("Logging", "Urls");
    }

    [Theory]
    [InlineData("http://localhost:5080", "local", 5080)]
    [InlineData("http://127.0.0.1:6000;http://[::1]:6000", "local", 6000)]
    [InlineData("http://0.0.0.0:7000", "network", 7000)]
    [InlineData("http://*:7000;http://[::]:7000", "network", 7000)]
    [InlineData("http://192.168.1.20:5080", "custom", 5080)]
    public void Addresses_read_as_this_machine_other_machines_or_custom(string urls, string scope, int port) =>
        HostListen.Describe(urls).Should().Be((scope, port));

    [Fact]
    public void Addresses_compare_by_what_they_reach()
    {
        HostListen.SameAddresses("http://localhost:5080", "http://127.0.0.1:5080/").Should().BeTrue();
        HostListen.SameAddresses("http://0.0.0.0:5080", "http://*:5080").Should().BeTrue();
        HostListen.SameAddresses("http://localhost:5080", "http://0.0.0.0:5080").Should().BeFalse();
        HostListen.SameAddresses("http://localhost:5080", "http://localhost:5081").Should().BeFalse();
    }

    [Fact]
    public void The_command_line_addresses_are_found_and_can_be_left_out()
    {
        HostListen.CommandLineUrls(["--urls", "http://0.0.0.0:1", "--x=y"]).Should().Be("http://0.0.0.0:1");
        HostListen.CommandLineUrls(["--urls=http://localhost:2"]).Should().Be("http://localhost:2");
        HostListen.CommandLineUrls(["--x=y"]).Should().BeNull();
        HostListen.WithoutUrls(["--urls", "http://0.0.0.0:1", "--a", "--urls=http://localhost:2", "--b"]).Should().Equal("--a", "--b");
    }

    [Fact]
    public void The_service_runs_without_urls_once_addresses_are_saved()
    {
        RouterLifecycle lifecycle = Lifecycle(new FakePlatform(), null, "--urls", "http://0.0.0.0:1");
        lifecycle.Definition().HostArguments.Should().Contain("--urls", "nothing saved: it runs as now");

        HostListen.Save(_directory, "http://localhost:6002");

        lifecycle.Definition().HostArguments.Should().NotContain("--urls").And.Contain("--SemanticRouter:Embeddings:Provider=Hashing");
    }

    [Fact]
    public void What_the_dashboard_may_do_depends_on_where_the_router_runs()
    {
        var platform = new FakePlatform();
        RouterLifecycle lifecycle = Lifecycle(platform);
        RouterServiceActions.All.Where(a => lifecycle.Refusal(a) is null).Should().Equal("restart", "stop", "install");
        lifecycle.RunsAs.Should().Be("foreground");

        platform.Installed = true;
        RouterServiceActions.All.Where(a => lifecycle.Refusal(a) is null).Should().Equal("restart", "stop", "reinstall", "uninstall");

        RouterLifecycle container = Lifecycle(platform, name => name == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null);
        container.RunsAs.Should().Be("container");
        RouterServiceActions.All.Where(a => container.Refusal(a) is null).Should().Equal("restart", "stop");
        container.Refusal("install").Should().Contain("container runtime");

        Lifecycle(null).Refusal("install").Should().Contain("no service manager");

        lifecycle.TryRequest("restart").Should().BeTrue();
        lifecycle.TryRequest("stop").Should().BeFalse();
        lifecycle.Refusal("stop").Should().Be("The router is already restarting.");
        lifecycle.TakePending().Should().Be("restart");
        lifecycle.Refusal("stop").Should().BeNull();
    }

    [Fact]
    public async Task Installing_hands_over_to_the_service_once_its_router_listens()
    {
        var platform = new FakePlatform { OnInstall = () => new RouterInstance(Environment.ProcessId, ["http://localhost:5080"], DateTimeOffset.UtcNow, "1.0.0").Write(_directory) };
        RouterLifecycle lifecycle = Lifecycle(platform);
        var output = new StringWriter();

        bool again = await lifecycle.CarryOutAsync("install", output);

        again.Should().BeFalse("the service's router took over");
        platform.Installed.Should().BeTrue();
        output.ToString().Should().Contain("Handed over to the fake service").And.Contain($"pid {Environment.ProcessId}");
    }

    [Fact]
    public async Task A_failed_install_starts_this_router_again_and_says_why()
    {
        var platform = new FakePlatform { InstallOutcome = ServiceOutcome.Failed("no systemd user session") };
        RouterLifecycle lifecycle = Lifecycle(platform);

        (await lifecycle.CarryOutAsync("install", TextWriter.Null)).Should().BeTrue();

        lifecycle.LastOutcome.Should().Match<RouterServiceOutcome>(o => !o.Succeeded && o.Action == "install" && o.Message.Contains("no systemd user session"));

        // Installed, but its router never listens: this one starts again too.
        var silent = new FakePlatform();
        RouterLifecycle waiting = Lifecycle(silent);
        (await waiting.CarryOutAsync("install", TextWriter.Null)).Should().BeTrue();
        waiting.LastOutcome!.Message.Should().Contain("was not listening").And.Contain("fake log");
    }

    [Fact]
    public async Task Removing_the_service_from_its_own_router_unregisters_it_and_stops()
    {
        var platform = new FakePlatform { Installed = true, Mine = true };
        RouterLifecycle service = Lifecycle(platform);
        service.RunsAs.Should().Be("service");

        (await service.CarryOutAsync("uninstall", TextWriter.Null)).Should().BeFalse("its router exits by itself");
        platform.Calls.Should().Equal("unregister");

        var other = new FakePlatform { Installed = true };
        RouterLifecycle foreground = Lifecycle(other);
        (await foreground.CarryOutAsync("uninstall", TextWriter.Null)).Should().BeTrue("a router started by hand keeps running");
        other.Calls.Should().Equal("uninstall");
        foreground.LastOutcome!.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Systemd_unregisters_without_stopping_and_knows_its_own_process()
    {
        var calls = new List<string>();
        string units = Path.Combine(_directory, "units");
        Directory.CreateDirectory(units);
        var systemd = new SystemdUserService(units, (file, arguments) =>
        {
            calls.Add(string.Join(' ', [file, .. arguments]));
            return Task.FromResult(new ProcessResult(0, string.Empty));
        });
        await File.WriteAllTextAsync(systemd.Location, "[Unit]");

        (await systemd.UnregisterAsync()).Succeeded.Should().BeTrue();

        calls.Should().Equal("systemctl --user disable semantic-router.service", "systemctl --user daemon-reload");
        File.Exists(systemd.Location).Should().BeFalse();
        SystemdUserService.InUnit("0::/user.slice/user-1000.slice/user@1000.service/app.slice/semantic-router.service\n").Should().BeTrue();
        SystemdUserService.InUnit("0::/user.slice/user-1000.slice/session-2.scope\n").Should().BeFalse();
        new LaunchAgentService(units, units, (_, _) => Task.FromResult(new ProcessResult(0, string.Empty)))
            .RunsThisProcess(name => name == "XPC_SERVICE_NAME" ? LaunchAgentService.Label : null).Should().BeTrue();
    }

    [Fact]
    public async Task The_dashboard_restarts_the_router_where_it_saved_and_stops_it()
    {
        int first = FreePort();
        HostListen.Save(_directory, $"http://127.0.0.1:{first}");
        RouterLifecycle lifecycle = Lifecycle(null);
        var output = new StringWriter();
        Task<int> run = RouterHost.RunAsync(lifecycle.Settings, TextWriter.Synchronized(output), b => b.Logging.ClearProviders(), lifecycle);
        RouterInstance before = await RunningAsync(i => i.Urls.Any(u => u.EndsWith($":{first}", StringComparison.Ordinal)));
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{first}") };

        JsonNode service = JsonNode.Parse(await client.GetStringAsync("/router/api/service"))!;
        ((bool)service["available"]!).Should().BeTrue();
        ((bool)service["service"]!["listen"]!["restartNeeded"]!).Should().BeFalse();

        int second = FreePort();
        HttpResponseMessage saved = await client.PutAsJsonAsync("/router/api/service/listen", new { scope = "local", port = second });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        JsonNode listen = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!["listen"]!;
        ((string?)listen["urls"], (bool)listen["restartNeeded"]!).Should().Be(($"http://localhost:{second}", true));

        (await client.PostAsync("/router/api/service/restart", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        RouterInstance after = await RunningAsync(i => i.StartedAt > before.StartedAt);
        after.ProcessId.Should().Be(Environment.ProcessId, "it restarts in the same process");
        after.Urls.Should().Contain(u => u.EndsWith($":{second}", StringComparison.Ordinal));

        using var moved = new HttpClient { BaseAddress = new Uri($"http://localhost:{second}") };
        JsonNode again = JsonNode.Parse(await moved.GetStringAsync("/router/api/service"))!;
        ((bool)again["service"]!["listen"]!["restartNeeded"]!).Should().BeFalse();
        ((string?)again["service"]!["lastOutcome"]!["message"]).Should().Be("Restarted.");

        (await moved.PostAsync("/router/api/service/stop", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
        RouterInstance.ReadRunning(_directory).Should().BeNull();
        output.ToString().Should().Contain("Restarting, as asked on the dashboard.").And.Contain("Stopped, as asked on the dashboard.");
    }

    [Fact]
    public async Task A_restart_onto_a_taken_address_keeps_the_old_one()
    {
        int first = FreePort();
        HostListen.Save(_directory, $"http://127.0.0.1:{first}");
        RouterLifecycle lifecycle = Lifecycle(null);
        Task<int> run = RouterHost.RunAsync(lifecycle.Settings, TextWriter.Null, b => b.Logging.ClearProviders(), lifecycle);
        RouterInstance before = await RunningAsync(i => i.Urls.Any(u => u.EndsWith($":{first}", StringComparison.Ordinal)));
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{first}") };

        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        int port = ((IPEndPoint)taken.LocalEndpoint).Port;
        (await client.PutAsJsonAsync("/router/api/service/listen", new { scope = "local", port })).EnsureSuccessStatusCode();
        (await client.PostAsync("/router/api/service/restart", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        RouterInstance after = await RunningAsync(i => i.StartedAt > before.StartedAt);
        after.Urls.Should().Contain(u => u.EndsWith($":{first}", StringComparison.Ordinal));
        JsonNode outcome = JsonNode.Parse(await client.GetStringAsync("/router/api/service"))!["service"]!["lastOutcome"]!;
        ((bool)outcome["succeeded"]!).Should().BeFalse();
        ((string?)outcome["message"]).Should().Contain("the address is in use");

        (await client.PostAsync("/router/api/service/stop", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
    }

    private async Task<RouterInstance> RunningAsync(Func<RouterInstance, bool> condition)
    {
        for (int i = 0; i < 300; i++)
        {
            if (RouterInstance.ReadRunning(_directory) is { } instance && condition(instance))
            {
                return instance;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The router did not start as expected.");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FakePlatform : ServicePlatform
    {
        public bool Installed { get; set; }

        public bool Mine { get; init; }

        public Action? OnInstall { get; init; }

        public ServiceOutcome InstallOutcome { get; init; } = ServiceOutcome.Ok();

        public List<string> Calls { get; } = [];

        public override string Kind => "fake service";

        public override string Location => "fake-location";

        public override string LogHint => "fake log";

        public override string EnvironmentAdvice => "fake advice";

        public override bool IsInstalled() => Installed;

        public override bool RunsThisProcess(Func<string, string?> environment) => Mine;

        public override string Render(ServiceDefinition definition) => "rendered";

        public override Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
        {
            Calls.Add("install");
            if (InstallOutcome.Succeeded)
            {
                Installed = true;
                OnInstall?.Invoke();
            }

            return Task.FromResult(InstallOutcome);
        }

        public override Task<ServiceOutcome> UninstallAsync()
        {
            Calls.Add("uninstall");
            Installed = false;
            return Task.FromResult(ServiceOutcome.Ok());
        }

        public override Task<ServiceOutcome> UnregisterAsync()
        {
            Calls.Add("unregister");
            Installed = false;
            return Task.FromResult(ServiceOutcome.Ok());
        }

        public override Task<ServiceOutcome> StartAsync() => Task.FromResult(ServiceOutcome.Ok());

        public override Task<ServiceOutcome> StopAsync() => Task.FromResult(ServiceOutcome.Ok());

        public override Task<ServiceOutcome> RestartAsync() => Task.FromResult(ServiceOutcome.Ok());

        public override Task<string> StatusAsync() => Task.FromResult("fake state");
    }
}
