using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SemanticRouter.Desktop;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Desktop;

/// <summary>The desktop app's parts that need no display: options, preferences, sign-in entries, one instance, PATH, the router.</summary>
public sealed class DesktopTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-desktop").FullName;

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Options_take_the_host_options_plus_background_and_quit()
    {
        DesktopOptions options = DesktopOptions.Parse(["--background", "--config-dir", _directory, "--urls", "http://localhost:6000"], _ => null);

        options.Background.Should().BeTrue();
        options.Quit.Should().BeFalse();
        options.Host.ConfigurationDirectory.Should().Be(_directory);
        options.Host.HostArguments.Should().Equal("--urls", "http://localhost:6000");
        DesktopOptions.Parse(["--quit"], _ => null).Quit.Should().BeTrue();
        DesktopOptions.Parse(["init"], _ => null).Host.Error.Should().Contain("takes options, not the 'init' command");
    }

    [Theory]
    [InlineData("http://localhost:5080/router#/tiers", true)]
    [InlineData("http://LOCALHOST:5080/router/api/status", true)]
    [InlineData("http://127.0.0.1:5080/router#/models", true)]
    [InlineData("http://[::1]:5080/router#/live", true)]
    [InlineData("http://localhost:5081/router", false)]
    [InlineData("https://localhost:5080/router", false)]
    [InlineData("https://github.com/siebe41/SemanticRouter.NET", false)]
    [InlineData("http://192.168.1.20:5080/router", false)]
    public void The_dashboard_window_keeps_router_links_and_sends_the_rest_to_the_browser(string link, bool router)
    {
        var home = new Uri("http://localhost:5080/router#/overview");
        DashboardWindow.IsSameRouter(new Uri(link), home).Should().Be(router);
        DashboardWindow.IsSameRouter(new Uri("http://192.168.1.20:5080/router#/tiers"), new Uri("http://192.168.1.20:5080/router")).Should().BeTrue("a router reached by its network address keeps its own links too");
    }

    [Fact]
    public void Preferences_round_trip_and_fall_back_to_the_window()
    {
        DesktopPreferences.Load(_directory).OpenDashboardIn.Should().Be(DashboardSurface.Window);

        new DesktopPreferences { OpenDashboardIn = DashboardSurface.Browser }.Save(_directory);
        File.ReadAllText(Path.Combine(_directory, DesktopPreferences.FileName)).Should().Contain("\"openDashboardIn\": \"Browser\"");
        DesktopPreferences.Load(_directory).OpenDashboardIn.Should().Be(DashboardSurface.Browser);

        File.WriteAllText(Path.Combine(_directory, DesktopPreferences.FileName), "{ not json");
        DesktopPreferences.Load(_directory).OpenDashboardIn.Should().Be(DashboardSurface.Window);
    }

    [Fact]
    public void The_window_fits_the_screen_it_opens_on_title_bar_included()
    {
        // 1920x1080 at 150% with a 48 px taskbar: 1280x688 units of room, less than the preferred 1280x860.
        var scaled = new ScreenArea(0, 0, 1920, 1032, 1.5);
        WindowPlacement fitted = WindowPlacement.Choose(null, [scaled], scaled);
        fitted.Width.Should().BeApproximately(1152, 0.5);
        fitted.Height.Should().BeApproximately(619.2, 0.5);
        fitted.Y.Should().BeGreaterThanOrEqualTo(0, "the title bar, with minimize and maximize, stays on the screen");
        (fitted.Y + (fitted.Height * 1.5)).Should().BeLessThanOrEqualTo(1032);
        (fitted.X + (fitted.Width * 1.5)).Should().BeLessThanOrEqualTo(1920);

        // A large screen gets the preferred size, centred.
        var large = new ScreenArea(0, 0, 2560, 1400, 1);
        WindowPlacement roomy = WindowPlacement.Choose(null, [large], large);
        roomy.Should().Be(new WindowPlacement(640, 270, 1280, 860));

        // A tiny screen: the window shrinks below its usual minimum rather than overflow.
        var tiny = new ScreenArea(0, 0, 400, 400, 1);
        WindowPlacement squeezed = WindowPlacement.Choose(null, [tiny], tiny);
        squeezed.Width.Should().BeLessThanOrEqualTo(400);
        squeezed.Height.Should().BeLessThanOrEqualTo(400);
    }

    [Fact]
    public void A_saved_window_comes_back_where_it_was_unless_its_screen_is_gone()
    {
        var left = new ScreenArea(0, 0, 1920, 1040, 1);
        var right = new ScreenArea(1920, 0, 2560, 1400, 1);
        var saved = new WindowPlacement(2100, 100, 1400, 900, Maximized: true);

        WindowPlacement.Choose(saved, [left, right], left).Should().Be(saved);

        // The second monitor was unplugged: back to a fresh size on the primary screen, still maximized.
        WindowPlacement moved = WindowPlacement.Choose(saved, [left], left);
        moved.X.Should().BeLessThan(1920);
        moved.Maximized.Should().BeTrue();

        // Saved partly off the bottom right: pulled back so all of it shows.
        WindowPlacement pulled = WindowPlacement.Choose(new WindowPlacement(1500, 800, 1000, 700), [left], left);
        pulled.Should().Be(new WindowPlacement(920, 340, 1000, 700));

        // Saved larger than the screen (a smaller monitor now, or higher scaling): shrunk to fit.
        WindowPlacement.Choose(new WindowPlacement(0, 0, 3000, 2000), [left], left).Should().Be(new WindowPlacement(0, 0, 1920, 1040));
    }

    [Fact]
    public void The_window_placement_is_remembered_with_the_preferences()
    {
        new DesktopPreferences { Window = new WindowPlacement(10, 20, 900, 700, Maximized: true) }.Save(_directory);

        DesktopPreferences.Load(_directory).Window.Should().Be(new WindowPlacement(10, 20, 900, 700, Maximized: true));
    }

    [Fact]
    public void The_linux_autostart_entry_quotes_its_command_and_is_removed_again()
    {
        XdgAutostartFile.Quote("/opt/Semantic Router/SemanticRouter").Should().Be("\"/opt/Semantic Router/SemanticRouter\"");
        XdgAutostartFile.Quote("100% $HOME \"x\" `y` a\\b").Should().Be("\"100%% \\\\$HOME \\\\\"x\\\\\" \\\\`y\\\\` a\\\\\\\\b\"");
        XdgAutostartFile.DefaultDirectory(_ => null, "/home/ada").Should().Be(Path.Combine("/home/ada", ".config", "autostart"));

        var autostart = new XdgAutostartFile(Path.Combine(_directory, "autostart"));
        autostart.IsEnabled().Should().BeFalse();
        autostart.Enable("/opt/router/SemanticRouter", SignInLaunch.Arguments("/home/ada/.config/semantic-router"));

        string entry = File.ReadAllText(autostart.Location);
        entry.Should().StartWith("[Desktop Entry]").And.Contain("Type=Application").And.Contain("Terminal=false");
        entry.Should().Contain("Exec=\"/opt/router/SemanticRouter\" \"--background\" \"--config-dir\" \"/home/ada/.config/semantic-router\"");
        autostart.IsEnabled().Should().BeTrue();

        autostart.Disable();
        autostart.IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void Run_from_an_AppImage_the_autostart_icon_is_a_copy_that_outlasts_the_app()
    {
        string icons = Path.Combine(_directory, "icons");
        string image = Path.Combine(_directory, "SemanticRouter.AppImage");
        File.WriteAllText(image, string.Empty);
        var fromImage = new XdgAutostartFile(Path.Combine(_directory, "autostart"), icons, name => name == "APPIMAGE" ? image : null);

        fromImage.Enable(image, SignInLaunch.Arguments("/home/ada/.config/semantic-router"));

        string copy = Path.Combine(icons, "semantic-router.png");
        File.Exists(copy).Should().BeTrue();
        File.ReadAllText(fromImage.Location).Should().Contain("Icon=" + Escaped(copy));

        var installed = new XdgAutostartFile(Path.Combine(_directory, "autostart2"), Path.Combine(_directory, "icons2"), _ => null);
        installed.Enable("/opt/router/SemanticRouter", []);
        File.ReadAllText(installed.Location).Should().Contain("Icon=" + Escaped(Path.Combine(AppContext.BaseDirectory, "semantic-router.png")));
        Directory.Exists(Path.Combine(_directory, "icons2")).Should().BeFalse("an installed program's icon stays where it is");

        // The desktop entry escapes backslashes (key-file string rules), which only Windows paths have.
        static string Escaped(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    [Fact]
    public void The_macos_sign_in_agent_starts_the_app_once_and_respects_quit()
    {
        var agent = new LaunchAgentFile(Path.Combine(_directory, "LaunchAgents"));
        agent.Enable("/Applications/SemanticRouter.app/Contents/MacOS/SemanticRouter", SignInLaunch.Arguments("/Users/ada/Library/Application Support/semantic-router"));

        XDocument plist = XDocument.Parse(File.ReadAllText(agent.Location));
        plist.Descendants("array").Single().Elements("string").Select(e => e.Value).Should().Equal(
            "/Applications/SemanticRouter.app/Contents/MacOS/SemanticRouter", "--background", "--config-dir", "/Users/ada/Library/Application Support/semantic-router");
        string[] keys = [.. plist.Descendants("key").Select(k => k.Value)];
        keys.Should().Contain("RunAtLoad").And.NotContain("KeepAlive");
        Path.GetFileName(agent.Location).Should().Be("io.github.siebe41.semantic-router.desktop.plist");
    }

    [Fact]
    public void The_windows_sign_in_value_runs_the_app_in_the_background()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string key = $@"Software\SemanticRouter.Tests\{Guid.NewGuid():N}";
        var run = new WindowsRunValue(key, "SemanticRouter Desktop");
        try
        {
            run.Enable(@"C:\Program Files\SemanticRouter\SemanticRouter.exe", SignInLaunch.Arguments(@"C:\Users\Ada\AppData\Roaming\semantic-router"));
            run.Read().Should().Be(@"""C:\Program Files\SemanticRouter\SemanticRouter.exe"" --background --config-dir C:\Users\Ada\AppData\Roaming\semantic-router");
            run.IsEnabled().Should().BeTrue();
            run.Disable();
            run.IsEnabled().Should().BeFalse();
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SemanticRouter.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task A_second_launch_hands_its_command_to_the_first()
    {
        string directory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        using var first = new SingleInstance(directory);
        var other = new SingleInstance(directory);

        (await other.TrySendAsync(SingleInstance.Show, TimeSpan.FromMilliseconds(300))).Should().BeFalse("nothing listens yet");

        var received = new List<string>();
        var quit = new TaskCompletionSource();
        first.Listen(command =>
        {
            lock (received)
            {
                received.Add(command);
            }

            if (command == SingleInstance.Quit)
            {
                quit.TrySetResult();
            }
        });

        (await SendWhenListeningAsync(other, SingleInstance.Show)).Should().BeTrue();
        (await other.TrySendAsync(SingleInstance.Ping, TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await other.TrySendAsync(SingleInstance.Quit, TimeSpan.FromSeconds(5))).Should().BeTrue();
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (received)
        {
            received.Should().Equal(SingleInstance.Show, SingleInstance.Quit);
        }

        SingleInstance.PipeName(directory).Should().NotBe(SingleInstance.PipeName(directory + "2")).And.HaveLength("semantic-router-".Length + 16);
    }

    [Fact]
    public void The_login_shell_path_is_found_between_markers_and_merged_without_repeats()
    {
        ShellEnvironment.Extract("motd noise\n__SEMANTIC_ROUTER_PATH__/a:/b__SEMANTIC_ROUTER_PATH__ trailing").Should().Be("/a:/b");
        ShellEnvironment.Extract("no markers").Should().BeNull();

        string existing = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        ShellEnvironment.Merge("/usr/bin:/bin", "/home/ada/.local/bin:/usr/bin", [existing, Path.Combine(_directory, "missing")])
            .Should().Be($"/usr/bin:/bin:/home/ada/.local/bin:{existing}");

        if (!OperatingSystem.IsWindows())
        {
            ShellEnvironment.ReadLoginShellPath("/bin/sh").Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task The_app_hosts_the_router_or_uses_the_one_already_running()
    {
        string directory = Path.Combine(_directory, "config");
        HostSettings host = HostSettings.Parse(["--config-dir", directory, "--urls", $"http://127.0.0.1:{FreePort()}", "--SemanticRouter:Embeddings:Provider=Hashing", "--Logging:LogLevel:Default=Warning"], _ => null);

        await using (var hosting = new DesktopRouter(host, TextWriter.Null))
        {
            await hosting.StartAsync();
            hosting.State.Should().Be(RouterState.Hosting, hosting.Status);
            hosting.Url.Should().StartWith("http://127.0.0.1:");
            hosting.Status.Should().StartWith("Running on http://127.0.0.1:");

            await using var second = new DesktopRouter(host, TextWriter.Null);
            await second.StartAsync();
            second.State.Should().Be(RouterState.Attached);
            second.AttachedProcessId.Should().Be(Environment.ProcessId);
            second.Url.Should().Be(hosting.Url);

            await hosting.DisposeAsync();
            second.Refresh();
            second.State.Should().Be(RouterState.Stopped);
        }

        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        HostSettings busy = HostSettings.Parse(["--config-dir", directory, "--urls", $"http://127.0.0.1:{((IPEndPoint)taken.LocalEndpoint).Port}", "--SemanticRouter:Embeddings:Provider=Hashing", "--Logging:LogLevel:Default=Warning"], _ => null);
        await using var blocked = new DesktopRouter(busy, TextWriter.Null, attachWait: TimeSpan.FromMilliseconds(500));
        await blocked.StartAsync();
        blocked.State.Should().Be(RouterState.Failed);
        blocked.Status.Should().Contain("address is in use");
    }

    [Fact]
    public void Options_take_headless_and_the_service_actions()
    {
        DesktopOptions headless = DesktopOptions.Parse(["--headless", "--config-dir", _directory, "--urls", "http://localhost:6000"], _ => null);
        headless.Headless.Should().BeTrue();
        headless.Host.Error.Should().BeNull();
        headless.Host.HostArguments.Should().Equal("--urls", "http://localhost:6000");

        DesktopOptions.Parse(["--install-service"], _ => null).ServiceAction.Should().Be("install");
        DesktopOptions.Parse(["--uninstall-service"], _ => null).ServiceAction.Should().Be("uninstall");
        DesktopOptions.Parse([], _ => null).Should().Match<DesktopOptions>(o => !o.Headless && o.ServiceAction == null);
    }

    [Fact]
    public void The_service_runs_the_app_headless_and_windows_starts_it_directly()
    {
        HostSettings host = HostSettings.Parse(["--config-dir", _directory, "--urls", "http://localhost:6000"], _ => null);
        ServiceDefinition desktop = ServiceDefinition.ForDesktop(host, @"C:\Program Files\SemanticRouter\SemanticRouter.exe", n => n == "PATH" ? "/bin" : null);

        desktop.Windowless.Should().BeTrue();
        desktop.Arguments.Should().Equal("--headless", "--config-dir", _directory, "--urls", "http://localhost:6000");
        desktop.Environment.Should().ContainKey("PATH");

        // No console program in between, so no console window flashes at sign-in.
        WindowsSignInService.SignInCommand(desktop).Should().Be(
            WindowsCommandLine.Join([@"C:\Program Files\SemanticRouter\SemanticRouter.exe", "--headless", "--config-dir", _directory, "--urls", "http://localhost:6000"]));
        new SystemdUserService(_directory, (_, _) => Task.FromResult(new ProcessResult(0, string.Empty))).Render(desktop)
            .Should().Contain("\"--headless\" \"--config-dir\"");

        // The tool still goes through `service start`, which starts the router without a console and returns.
        ServiceDefinition tool = ServiceDefinition.ForTool(host, @"C:\tools\semantic-router.exe", "unused.dll", _ => null);
        tool.Windowless.Should().BeFalse();
        WindowsSignInService.SignInCommand(tool).Should().Contain("service start --no-wait");
    }

    [Fact]
    public async Task Waiting_for_a_start_ignores_a_router_from_before_and_gives_up_in_time()
    {
        string directory = Path.Combine(_directory, "wait");
        Directory.CreateDirectory(directory);
        DateTimeOffset requested = DateTimeOffset.UtcNow;
        new RouterInstance(Environment.ProcessId, ["http://localhost:6000"], requested.AddMinutes(-5), "1.0.0").Write(directory);

        (await RouterInstance.WaitForStartAsync(directory, requested, TimeSpan.FromMilliseconds(400))).Should().BeNull("that record is from before");

        Task<RouterInstance?> waiting = RouterInstance.WaitForStartAsync(directory, requested, TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        new RouterInstance(Environment.ProcessId, ["http://localhost:6001"], DateTimeOffset.UtcNow, "1.0.0").Write(directory);
        (await waiting)!.Urls.Should().Equal("http://localhost:6001");
    }

    [Fact]
    public async Task Turning_the_background_service_on_hands_the_port_to_it_and_off_takes_it_back()
    {
        string directory = Path.Combine(_directory, "service");
        HostSettings host = HostSettings.Parse(["--config-dir", directory, "--urls", $"http://127.0.0.1:{FreePort()}", "--SemanticRouter:Embeddings:Provider=Hashing", "--Logging:LogLevel:Default=Warning"], _ => null);
        await using var platform = new InProcessServicePlatform(host);
        var service = new RouterService(platform, host, "/opt/SemanticRouter/SemanticRouter", n => n == "PATH" ? "/usr/bin" : null) { StartTimeout = TimeSpan.FromSeconds(20) };
        await using var router = new DesktopRouter(host, TextWriter.Null, attachWait: TimeSpan.FromSeconds(2));

        await router.StartAsync();
        router.State.Should().Be(RouterState.Hosting, router.Status);
        service.IsOn.Should().BeFalse();

        (await service.TurnOnAsync(router)).Should().BeNull();
        service.IsOn.Should().BeTrue();
        router.State.Should().Be(RouterState.Attached, "the app now shows the service's router");
        platform.Installed!.Arguments.Should().StartWith(["--headless", "--config-dir"]);
        platform.Installed.Program.Should().Be("/opt/SemanticRouter/SemanticRouter");

        (await service.RestartAsync(router)).Should().BeNull();
        router.State.Should().Be(RouterState.Attached);

        (await service.TurnOffAsync(router)).Should().BeNull();
        service.IsOn.Should().BeFalse();
        router.State.Should().Be(RouterState.Hosting, "the app runs the router itself again");
    }

    [Fact]
    public async Task Headless_runs_only_the_router_logs_to_its_file_and_stops_cleanly()
    {
        string app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SemanticRouter.exe" : "SemanticRouter");
        string directory = Path.Combine(_directory, "headless");
        int port = FreePort();
        var start = new ProcessStartInfo(app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--headless", "--config-dir", directory, "--urls", $"http://127.0.0.1:{port}", "--SemanticRouter:Embeddings:Provider=Hashing" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        try
        {
            RouterInstance? instance = await RouterInstance.WaitForStartAsync(directory, DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromSeconds(60));
            instance.Should().NotBeNull("the headless router records itself once it listens");
            instance!.ProcessId.Should().Be(process.Id);

            using (var http = new HttpClient())
            {
                (await http.GetAsync(new Uri($"http://127.0.0.1:{port}/router/"))).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // The router still writes to its log: share it, as Windows refuses a reader that would lock writers out.
            using (var log = new StreamReader(new FileStream(RouterLog.FileIn(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                (await log.ReadToEndAsync()).Should().Contain($"is listening on http://127.0.0.1:{port}");
            }

            // The way a service manager stops it: SIGTERM, or on Windows the stop event.
            if (OperatingSystem.IsWindows())
            {
                instance.RequestStop().Should().BeTrue();
            }
            else
            {
                using Process kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!;
                await kill.WaitForExitAsync();
            }

            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(exited.Token);
            process.ExitCode.Should().Be(0);
            File.Exists(RouterInstance.PathIn(directory)).Should().BeFalse("a clean stop removes the record");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>A service manager that runs the router in this process, so the hand-over can be tested without one.</summary>
    private sealed class InProcessServicePlatform(HostSettings host) : ServicePlatform, IAsyncDisposable
    {
        private RunningRouter? _router;

        public ServiceDefinition? Installed { get; private set; }

        public override string Kind => "in-process service";

        public override string Location => "memory";

        public override string LogHint => "the test output";

        public override string EnvironmentAdvice => string.Empty;

        public override bool IsInstalled() => Installed is not null;

        public override string Render(ServiceDefinition definition) => string.Empty;

        public override async Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
        {
            Installed = definition;
            return await StartAsync();
        }

        public override async Task<ServiceOutcome> UninstallAsync()
        {
            await StopAsync();
            Installed = null;
            return ServiceOutcome.Ok();
        }

        public override async Task<ServiceOutcome> StartAsync()
        {
            _router ??= await RouterHost.StartAsync(host, TextWriter.Null, b => b.Logging.ClearProviders());
            return _router is null ? ServiceOutcome.Failed("address in use") : ServiceOutcome.Ok();
        }

        public override async Task<ServiceOutcome> StopAsync()
        {
            if (_router is { } router)
            {
                _router = null;
                await router.DisposeAsync();
            }

            return ServiceOutcome.Ok();
        }

        public override async Task<ServiceOutcome> RestartAsync()
        {
            await StopAsync();
            return await StartAsync();
        }

        public override Task<string> StatusAsync() => Task.FromResult("in process");

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private static async Task<bool> SendWhenListeningAsync(SingleInstance client, string command)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (await client.TrySendAsync(command, TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return false;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public void Notifications_start_their_tool_by_full_path()
    {
        string? tool = DesktopNotifier.Tool();

        if (tool is not null)
        {
            Path.IsPathFullyQualified(tool).Should().BeTrue("a bare name could start a file of that name in the current directory");
        }
    }

    [Fact]
    public void The_tray_says_when_the_router_runs_without_a_model()
    {
        TrayDigest.NoModelEnabled("""{"models":[],"gateway":{"mode":"Dispatch"}}""").Should().BeTrue();
        TrayDigest.NoModelEnabled("""{"models":[{"name":"a","enabled":false}],"gateway":{"mode":"Dispatch"}}""").Should().BeTrue();
        TrayDigest.NoModelEnabled("""{"models":[{"name":"a","enabled":false},{"name":"b","enabled":true}],"gateway":{"mode":"Dispatch"}}""").Should().BeFalse();
        TrayDigest.NoModelEnabled("""{"models":[],"gateway":{"mode":"Annotate"}}""").Should().BeFalse("an annotating router calls no model");
        TrayDigest.NoModelEnabled("""{"version":3}""").Should().BeFalse("an answer it does not understand is not reported as a problem");

        TrayDigest.StatusLine("Running on http://localhost:5080", noModel: true).Should().Be("Running on http://localhost:5080, but no model is enabled yet");
        TrayDigest.StatusLine("Running on http://localhost:5080", noModel: false).Should().Be("Running on http://localhost:5080");
    }

    [Fact]
    public async Task The_tray_reads_the_routers_own_status()
    {
        HostSettings host = HostSettings.Parse(
            ["--config-dir", Path.Combine(_directory, "status"), "--urls", "http://127.0.0.1:0", "--SemanticRouter:Embeddings:Provider=Hashing"], _ => null);
        await using RunningRouter? router = await RouterHost.StartAsync(host, TextWriter.Null, b => b.Logging.ClearProviders());
        using var client = new HttpClient();

        TrayDigest.NoModelEnabled(await client.GetStringAsync(new Uri(router!.LocalUrl + "/router/api/status"))).Should().BeTrue("the template has no model");
    }
}
