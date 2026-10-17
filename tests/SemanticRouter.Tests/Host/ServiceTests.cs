using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary><c>semantic-router service</c>: the definitions it writes, the commands it runs and the running instance.</summary>
public sealed class ServiceTests : IDisposable
{
    private static readonly string FakeCli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-cli.exe" : "fake-cli");

    private readonly string _directory = Directory.CreateTempSubdirectory("router-service").FullName;

    public void Dispose()
    {
        // Windows can hold a closed database file for a moment longer (the last connection, a virus scanner): try again.
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

    private string ConfigurationDirectory => Path.Combine(_directory, "config dir");

    private HostSettings Settings(params string[] args) =>
        HostSettings.Parse(["service", .. args, "--config-dir", ConfigurationDirectory], _ => null);

    private ServiceDefinition Definition(params string[] hostArguments) => new()
    {
        Program = "/home/ada/.dotnet/tools/semantic-router",
        ConfigurationDirectory = ConfigurationDirectory,
        HostArguments = hostArguments,
        Environment = new Dictionary<string, string> { ["PATH"] = "/home/ada/.local/bin:/usr/bin", ["DOTNET_ROOT"] = "/home/ada/.dotnet" },
    };

    [Fact]
    public void Command_line_takes_a_service_action_and_keeps_host_options_for_the_service()
    {
        HostSettings install = HostSettings.Parse(["service", "install", "--urls", "http://localhost:6000", "--dry-run"], _ => null);
        install.Command.Should().Be("service");
        install.ServiceAction.Should().Be("install");
        install.DryRun.Should().BeTrue();
        install.HostArguments.Should().Equal("--urls", "http://localhost:6000");
        install.Error.Should().BeNull();

        HostSettings.Parse(["service", "start", "--no-wait"], _ => null).NoWait.Should().BeTrue();
        HostSettings.Parse(["service"], _ => null).Error.Should().Contain("service needs an action: install, uninstall, start, stop, restart, status");
        HostSettings.Parse(["service", "enable"], _ => null).Error.Should().Contain("Unknown service action 'enable'");
        HostSettings.Parse(["--dry-run"], _ => null).HostArguments.Should().Equal("--dry-run");
    }

    [Fact]
    public void The_definition_runs_this_tool_on_the_same_directory_and_carries_path_and_the_runtime_location()
    {
        HostSettings settings = HostSettings.Parse(["service", "install", "--config-dir", ConfigurationDirectory, "--urls", "http://localhost:6000"], _ => null);
        Func<string, string?> environment = name => name switch
        {
            "PATH" => "/home/ada/.local/bin:/usr/bin",
            "DOTNET_ROOT" => "/home/ada/.dotnet",
            "OPENAI_API_KEY" => "sk-not-copied",
            _ => null,
        };

        ServiceDefinition tool = ServiceDefinition.ForTool(settings, "/home/ada/.dotnet/tools/semantic-router", "/store/semantic-router.dll", environment);
        tool.Arguments.Should().Equal("run", "--config-dir", Path.GetFullPath(ConfigurationDirectory), "--urls", "http://localhost:6000");
        tool.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["PATH"] = "/home/ada/.local/bin:/usr/bin", ["DOTNET_ROOT"] = "/home/ada/.dotnet" });

        ServiceDefinition viaDotnet = ServiceDefinition.ForTool(settings, "/usr/share/dotnet/dotnet", "/store/semantic-router.dll", environment);
        viaDotnet.Arguments[0].Should().Be("/store/semantic-router.dll");
        viaDotnet.Command("service", "start").Should().Equal("/store/semantic-router.dll", "service", "start", "--config-dir", Path.GetFullPath(ConfigurationDirectory));
    }

    [Fact]
    public void The_systemd_unit_quotes_every_argument_and_restarts_only_what_a_restart_can_fix()
    {
        var systemd = new SystemdUserService(Path.Combine(_directory, "units"), new FakeRunner().RunAsync);

        string unit = systemd.Render(Definition("--urls", "http://localhost:6000"));

        unit.Should().Contain($"ExecStart=\"/home/ada/.dotnet/tools/semantic-router\" \"run\" \"--config-dir\" {SystemdUserService.QuoteArgument(ConfigurationDirectory)} \"--urls\" \"http://localhost:6000\"");
        unit.Should().Contain("Environment=\"PATH=/home/ada/.local/bin:/usr/bin\"").And.Contain("Environment=\"DOTNET_ROOT=/home/ada/.dotnet\"");
        unit.Should().Contain("Restart=on-failure").And.Contain("RestartPreventExitStatus=2 3").And.Contain("WantedBy=default.target");

        SystemdUserService.QuoteArgument("100% \"$HOME\" C:\\x").Should().Be("\"100%% \\\"$$HOME\\\" C:\\\\x\"");
        SystemdUserService.QuoteValue("KEY=$5%").Should().Be("\"KEY=$5%%\"");
        SystemdUserService.DefaultUnitDirectory(_ => null, "/home/ada").Should().Be(Path.Combine("/home/ada", ".config", "systemd", "user"));
        SystemdUserService.DefaultUnitDirectory(n => n == "XDG_CONFIG_HOME" ? "/xdg" : null, "/home/ada").Should().Be(Path.Combine("/xdg", "systemd", "user"));
    }

    [Fact]
    public async Task Systemd_install_writes_the_unit_then_reloads_enables_and_restarts_it()
    {
        var runner = new FakeRunner();
        var systemd = new SystemdUserService(Path.Combine(_directory, "units"), runner.RunAsync);

        (await systemd.InstallAsync(Definition())).Succeeded.Should().BeTrue();

        File.ReadAllText(systemd.Location).Should().Contain("ExecStart=");
        runner.Calls.Should().Equal(
            "systemctl --user show-environment",
            "systemctl --user daemon-reload",
            "systemctl --user enable semantic-router.service",
            "systemctl --user restart semantic-router.service");

        runner.Calls.Clear();
        (await systemd.UninstallAsync()).Succeeded.Should().BeTrue();
        systemd.IsInstalled().Should().BeFalse();
        runner.Calls.Should().Equal("systemctl --user disable --now semantic-router.service", "systemctl --user daemon-reload");
    }

    [Fact]
    public async Task Systemd_install_explains_a_machine_without_a_user_session_and_writes_nothing()
    {
        var runner = new FakeRunner { Fail = args => args.Contains("show-environment") ? "Failed to connect to bus: No medium found" : null };
        var systemd = new SystemdUserService(Path.Combine(_directory, "units"), runner.RunAsync);

        ServiceOutcome outcome = await systemd.InstallAsync(Definition());

        outcome.Succeeded.Should().BeFalse();
        outcome.Detail.Should().Contain("no systemd user session").And.Contain("No medium found");
        systemd.IsInstalled().Should().BeFalse();
    }

    [Fact]
    public async Task The_launchd_agent_is_valid_xml_logs_with_timestamps_and_is_bootstrapped_into_the_gui_domain()
    {
        var runner = new FakeRunner { Fail = args => args is ["print", ..] ? "Could not find service" : null };
        var launchd = new LaunchAgentService(Path.Combine(_directory, "LaunchAgents"), Path.Combine(_directory, "Logs"), runner.RunAsync);
        ServiceDefinition definition = Definition("--SemanticRouter:Label=<a & b>");

        XDocument plist = XDocument.Parse(launchd.Render(definition));
        string[] arguments = [.. plist.Descendants("array").Single().Elements("string").Select(e => e.Value)];
        arguments.Should().Equal("/home/ada/.dotnet/tools/semantic-router", "run", "--config-dir", ConfigurationDirectory, "--SemanticRouter:Label=<a & b>");
        string[] keys = [.. plist.Descendants("key").Select(k => k.Value)];
        keys.Should().Contain(["Label", "RunAtLoad", "KeepAlive", "SuccessfulExit", "PATH", "DOTNET_ROOT", "Logging__Console__FormatterOptions__TimestampFormat"]);

        (await launchd.InstallAsync(definition)).Succeeded.Should().BeTrue();
        launchd.IsInstalled().Should().BeTrue();
        Directory.Exists(Path.Combine(_directory, "Logs")).Should().BeTrue();
        runner.Calls.Should().Equal(
            "id -u",
            $"launchctl print gui/501/{LaunchAgentService.Label}",
            $"launchctl bootstrap gui/501 {launchd.Location}");

        runner.Calls.Clear();
        runner.Fail = _ => null;
        (await launchd.RestartAsync()).Succeeded.Should().BeTrue();
        runner.Calls.Should().EndWith($"launchctl kickstart -k gui/501/{LaunchAgentService.Label}");
    }

    [Fact]
    public void Windows_command_lines_round_trip_through_the_c_runtime_rules()
    {
        WindowsCommandLine.Quote("plain").Should().Be("plain");
        WindowsCommandLine.Quote(string.Empty).Should().Be("\"\"");
        WindowsCommandLine.Quote(@"C:\Users\Ada Lovelace\.dotnet\tools\semantic-router.exe").Should().Be(@"""C:\Users\Ada Lovelace\.dotnet\tools\semantic-router.exe""");
        WindowsCommandLine.Quote(@"C:\dir with space\").Should().Be(@"""C:\dir with space\\""");
        WindowsCommandLine.Quote("say \"hi\"").Should().Be(@"""say \""hi\""""");
        WindowsCommandLine.Quote(@"a\""b c").Should().Be(@"""a\\\""b c""");

        ServiceDefinition definition = Definition() with { Program = @"C:\Users\Ada Lovelace\.dotnet\tools\semantic-router.exe", ConfigurationDirectory = @"C:\Users\Ada Lovelace\AppData\Roaming\semantic-router" };
        WindowsSignInService.SignInCommand(definition).Should().Be(
            @"""C:\Users\Ada Lovelace\.dotnet\tools\semantic-router.exe"" service start --no-wait --config-dir ""C:\Users\Ada Lovelace\AppData\Roaming\semantic-router""");
    }

    [Fact]
    public async Task Windows_sign_in_entry_is_written_to_the_run_key_and_removed_again()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string key = $@"Software\SemanticRouter.Tests\{Guid.NewGuid():N}";
        var windows = new WindowsSignInService(ConfigurationDirectory, key, "SemanticRouter");
        int? started = null;
        try
        {
            // Installing starts the definition in the background. The stand-in CLI then waits for input and never
            // records itself as a router, so the test ends it: running in the configuration directory, it would
            // keep that directory from being deleted.
            ServiceOutcome installed = await windows.InstallAsync(Definition() with { Program = FakeCli });
            installed.Succeeded.Should().BeTrue(installed.Detail);
            Match pid = Regex.Match(installed.Detail ?? string.Empty, @"in the background \(pid (?<pid>\d+)\)");
            pid.Success.Should().BeTrue(installed.Detail);
            started = int.Parse(pid.Groups["pid"].Value, CultureInfo.InvariantCulture);

            windows.IsInstalled().Should().BeTrue();
            (await windows.StatusAsync()).Should().Contain("starts at sign-in").And.Contain("service start --no-wait");
            File.ReadAllText(windows.DefinitionFile).Should().Contain("\"run\"");

            await windows.UninstallAsync();
            windows.IsInstalled().Should().BeFalse();
            File.Exists(windows.DefinitionFile).Should().BeFalse();
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SemanticRouter.Tests", throwOnMissingSubKey: false);
            if (started is { } id)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(id);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }
        }
    }

    [Fact]
    public async Task Install_reports_where_the_router_listens_and_the_environment_variables_a_service_will_not_see()
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        File.WriteAllText(Path.Combine(ConfigurationDirectory, HostSettings.ConfigurationFileName),
            """{ "Connectors": { "openai": { "ApiKey": "${env:OPENAI_API_KEY}" }, "lm": { "ApiKey": "Bearer ${env: LM_API_TOKEN }" } } }""");
        var platform = new FakePlatform(onStart: () =>
            new RouterInstance(Environment.ProcessId, ["http://localhost:6000"], DateTimeOffset.UtcNow, "1.2.3").Write(ConfigurationDirectory));
        var output = new StringWriter();

        int exit = await ServiceCommand.RunAsync(Settings("install"), output, platform, Definition());

        string report = output.ToString();
        exit.Should().Be(0, report);
        report.Should().Contain("Installed a fake service: fake-location");
        report.Should().Contain("reads LM_API_TOKEN, OPENAI_API_KEY from the environment. fake advice");
        report.Should().Contain($"Router     running (pid {Environment.ProcessId}) on http://localhost:6000").And.Contain("Dashboard  http://localhost:6000/router");

        output = new StringWriter();
        (await ServiceCommand.RunAsync(Settings("status"), output, platform, Definition())).Should().Be(0);
        output.ToString().Should().Contain("State      fake state").And.Contain("Logs       fake log");
    }

    [Fact]
    public async Task Start_says_where_to_look_when_the_router_does_not_come_up()
    {
        var platform = new FakePlatform(onStart: () => { }) { Installed = true };
        var output = new StringWriter();

        int exit = await ServiceCommand.RunAsync(Settings("start"), output, platform, Definition(), TimeSpan.FromMilliseconds(300));

        exit.Should().Be(1);
        output.ToString().Should().Contain("The router is not listening after 0 seconds. See what it logged: fake log");

        output = new StringWriter();
        (await ServiceCommand.RunAsync(Settings("start"), output, new FakePlatform(() => { }), Definition())).Should().Be(1);
        output.ToString().Should().Contain("not installed");

        output = new StringWriter();
        (await ServiceCommand.RunAsync(Settings("install", "--dry-run"), output, platform, Definition())).Should().Be(0);
        output.ToString().Should().Contain("# fake service: fake-location").And.Contain("rendered");
    }

    [Fact]
    public async Task The_router_records_itself_while_it_runs_and_reports_an_address_in_use()
    {
        HostSettings settings = HostSettings.Parse(["--config-dir", ConfigurationDirectory, "--urls", $"http://127.0.0.1:{FreePort()}", "--SemanticRouter:Embeddings:Provider=Hashing"], _ => null);
        var lifetime = new TaskCompletionSource<IHostApplicationLifetime>();
        Task<int> run = RouterHost.RunAsync(settings, TextWriter.Null, builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.AddHostedService(sp => new LifetimeCapture(sp.GetRequiredService<IHostApplicationLifetime>(), lifetime));
        });

        RouterInstance? instance = null;
        for (int i = 0; i < 100 && instance is null; i++)
        {
            await Task.Delay(100);
            instance = RouterInstance.ReadRunning(ConfigurationDirectory);
        }

        instance.Should().NotBeNull();
        instance!.ProcessId.Should().Be(Environment.ProcessId);
        instance.Urls.Should().ContainSingle().Which.Should().StartWith("http://127.0.0.1:");

        using (var listener = new TcpListener(IPAddress.Loopback, FreePort()))
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var output = new StringWriter();
            HostSettings taken = HostSettings.Parse(["--config-dir", Path.Combine(_directory, "other"), "--urls", $"http://127.0.0.1:{port}", "--SemanticRouter:Embeddings:Provider=Hashing"], _ => null);
            (await RouterHost.RunAsync(taken, output, b => b.Logging.ClearProviders())).Should().Be(RouterHost.AddressInUseExitCode);
            output.ToString().Should().Contain("address already in use").And.Contain("semantic-router service status");
        }

        // Started again on the same directory and address: it is this router, so say so and where its dashboard is.
        var again = new StringWriter();
        (await RouterHost.RunAsync(settings, again, b => b.Logging.ClearProviders())).Should().Be(RouterHost.AddressInUseExitCode);
        again.ToString().Should().Contain($"already running for this configuration (process {Environment.ProcessId}")
            .And.Contain(RouterHost.LocalAddress(instance.Urls[0]) + "/router").And.NotContain("   at ");

        // Windows routers stop when asked through their event; elsewhere the service manager sends SIGTERM.
        if (OperatingSystem.IsWindows())
        {
            instance.RequestStop().Should().BeTrue();
        }
        else
        {
            (await lifetime.Task).StopApplication();
        }

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
        File.Exists(RouterInstance.PathIn(ConfigurationDirectory)).Should().BeFalse();
    }

    [Fact]
    public void A_stale_instance_file_is_not_a_running_router()
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        new RouterInstance(int.MaxValue, ["http://localhost:5080"], DateTimeOffset.UtcNow, "1.0.0").Write(ConfigurationDirectory);

        RouterInstance.Read(ConfigurationDirectory).Should().NotBeNull();
        RouterInstance.ReadRunning(ConfigurationDirectory).Should().BeNull();

        RouterInstance.Remove(ConfigurationDirectory, processId: 1);
        File.Exists(RouterInstance.PathIn(ConfigurationDirectory)).Should().BeTrue("another process's record is left alone");
        RouterInstance.Remove(ConfigurationDirectory, int.MaxValue);
        File.Exists(RouterInstance.PathIn(ConfigurationDirectory)).Should().BeFalse();
    }

    [Fact]
    public async Task Service_manager_commands_are_started_by_full_path_never_from_the_current_directory()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        string name = "sr-tool-" + Guid.NewGuid().ToString("N");
        string file = Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name); // Windows runs only a native .exe
        File.WriteAllText(file, string.Empty);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, bin);

        ServicePlatform.SystemTool(name, bin).Should().Be(file);
        if (!Path.IsPathRooted(relative))
        {
            ServicePlatform.SystemTool(name, relative).Should().BeNull("a relative PATH entry depends on the current directory");
        }

        if (!OperatingSystem.IsWindows())
        {
            ServicePlatform.SystemTool("sh", string.Empty).Should().BeOneOf("/usr/bin/sh", "/bin/sh");
        }

        ProcessResult missing = await ServicePlatform.RunProcessAsync(name, []);
        missing.Succeeded.Should().BeFalse();
        missing.Output.Should().Contain("not found");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FakeRunner
    {
        public List<string> Calls { get; } = [];

        /// <summary>Returns the failure output for a command that should fail, or <see langword="null"/>.</summary>
        public Func<IReadOnlyList<string>, string?> Fail { get; set; } = _ => null;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add(string.Join(' ', [fileName, .. arguments]));
            if (fileName == "id")
            {
                return Task.FromResult(new ProcessResult(0, "501\n"));
            }

            return Task.FromResult(Fail(arguments) is { } failure ? new ProcessResult(1, failure) : new ProcessResult(0, string.Empty));
        }
    }

    private sealed class FakePlatform(Action onStart) : ServicePlatform
    {
        public bool Installed { get; set; }

        public override string Kind => "fake service";

        public override string Location => "fake-location";

        public override string LogHint => "fake log";

        public override string EnvironmentAdvice => "fake advice";

        public override bool IsInstalled() => Installed;

        public override string Render(ServiceDefinition definition) => "rendered";

        public override Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
        {
            Installed = true;
            return StartAsync();
        }

        public override Task<ServiceOutcome> UninstallAsync() => Task.FromResult(ServiceOutcome.Ok());

        public override Task<ServiceOutcome> StartAsync()
        {
            onStart();
            return Task.FromResult(ServiceOutcome.Ok());
        }

        public override Task<ServiceOutcome> StopAsync() => Task.FromResult(ServiceOutcome.Ok());

        public override Task<ServiceOutcome> RestartAsync() => StartAsync();

        public override Task<string> StatusAsync() => Task.FromResult("fake state");
    }

    private sealed class LifetimeCapture(IHostApplicationLifetime lifetime, TaskCompletionSource<IHostApplicationLifetime> captured) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            captured.TrySetResult(lifetime);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
