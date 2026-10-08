using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using SemanticRouter.Desktop;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Desktop;

/// <summary>The desktop app's parts that need no display: options, preferences, sign-in entries, one instance, PATH, the router.</summary>
public sealed class DesktopTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-desktop").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
}
