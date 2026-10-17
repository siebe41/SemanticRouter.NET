using SemanticRouter.Desktop;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Desktop;

/// <summary>The installed app (#50): the uninstall hook's clean-up, the path services start, updates and their flags.</summary>
public sealed class InstallationTests
{
    private static readonly string App = Path.GetFullPath(Path.Combine("apps", "SemanticRouter", "SemanticRouter"));

    private static readonly string Tool = Path.GetFullPath(Path.Combine("home", ".dotnet", "tools", "semantic-router"));

    [Fact]
    public void Uninstalling_removes_the_background_service_and_the_sign_in_entry_but_not_the_configuration()
    {
        var platform = new RecordingPlatform { Installed = true };
        var signIn = new RecordingSignIn { Enabled = true };
        HostSettings settings = DesktopOptions.Parse([], _ => null).Host;

        Installation.BeforeUninstall(settings, _ => platform, signIn, App);

        platform.Calls.Should().Equal("uninstall");
        signIn.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Uninstalling_the_app_leaves_a_service_the_tool_installed()
    {
        var platform = new RecordingPlatform { Installed = true, Program = Tool };
        var signIn = new RecordingSignIn { Enabled = true };

        Installation.BeforeUninstall(DesktopOptions.Parse([], _ => null).Host, _ => platform, signIn, App);

        platform.Calls.Should().BeEmpty("the tool's router keeps working without the app");
        platform.Installed.Should().BeTrue();
        signIn.Enabled.Should().BeFalse("the app's own sign-in entry still goes");
    }

    [Fact]
    public async Task An_update_leaves_a_service_the_tool_installed_running_the_tool()
    {
        var updates = new UpdateControlTests.FakeUpdates();
        var platform = new RecordingPlatform { Installed = true, Program = Tool };
        var updater = new DesktopUpdater(updates, platform, TextWriter.Null, executable: App);

        (await updater.CheckAsync()).Should().Be("1.0.1");
        (await updater.ApplyOnQuitAsync()).Should().BeTrue();

        platform.Calls.Should().BeEmpty("the update does not replace the tool, so its service is neither stopped nor reinstalled");
        updates.Calls.Should().Equal("check", "download", "apply 1.0.1 restart=False");

        var own = new RecordingPlatform { Installed = true, Program = App };
        var ownUpdates = new UpdateControlTests.FakeUpdates();
        var updatesOwn = new DesktopUpdater(ownUpdates, own, TextWriter.Null, executable: App);
        await updatesOwn.CheckAsync();
        (await updatesOwn.ApplyOnQuitAsync()).Should().BeTrue();
        own.Calls.Should().Equal("stop");
        ownUpdates.Calls.Should().Equal(["check", "download", "apply 1.0.1 restart=True"], "the new version reinstalls and starts the app's own service");
    }

    [Fact]
    public void The_background_setting_names_a_service_that_runs_another_program()
    {
        RouterService.BackgroundLabel(null).Should().Be("Keep the router running in the background");
        RouterService.BackgroundLabel(Tool).Should().Be($"Keep the router running in the background (the service runs {Path.GetFileName(Tool)})");
        HostSettings settings = DesktopOptions.Parse([], _ => null).Host;
        new RouterService(new RecordingPlatform { Installed = true, Program = Tool }, settings, App, _ => null).OtherProgram.Should().Be(Tool);
        new RouterService(new RecordingPlatform { Installed = true, Program = App }, settings, App, _ => null).OtherProgram.Should().BeNull();
        new RouterService(new RecordingPlatform { Program = Tool }, settings, App, _ => null).OtherProgram.Should().BeNull("nothing is installed");
    }

    [Fact]
    public void Uninstalling_with_nothing_set_up_touches_nothing()
    {
        var platform = new RecordingPlatform();
        var signIn = new RecordingSignIn();

        Installation.BeforeUninstall(DesktopOptions.Parse([], _ => null).Host, _ => platform, signIn, App);
        Installation.BeforeUninstall(DesktopOptions.Parse([], _ => null).Host, _ => null, null, App);

        platform.Calls.Should().BeEmpty();
        signIn.Disabled.Should().BeFalse();
    }

    [Fact]
    public void Inside_an_AppImage_services_start_the_AppImage_not_its_temporary_mount()
    {
        string image = Path.GetTempFileName();
        try
        {
            Installation.Executable(name => name == "APPIMAGE" ? image : null).Should().Be(image);
            Installation.Executable(name => name == "APPIMAGE" ? image + ".gone" : null).Should().Be(Environment.ProcessPath);
            Installation.Executable(_ => null).Should().Be(Environment.ProcessPath);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void The_update_flags_are_parsed()
    {
        DesktopOptions.Parse(["--update"], _ => null).Update.Should().BeTrue();
        DesktopOptions.Parse([DesktopUpdater.ResumeService, "--background"], _ => null).Should().Match<DesktopOptions>(o => o.ResumeService && o.Background && o.Host.Error == null);
        DesktopOptions.Parse([], _ => null).Should().Match<DesktopOptions>(o => !o.Update && !o.ResumeService);
    }

    [Fact]
    public async Task A_copy_that_was_not_installed_does_not_update_or_stop_the_service()
    {
        var platform = new RecordingPlatform { Installed = true };
        var updater = new DesktopUpdater(null, platform, TextWriter.Null);

        updater.Installed.Should().BeFalse();
        (await updater.CheckAsync()).Should().BeNull();
        (await updater.ApplyNowAsync(restartApp: true)).Should().BeFalse();
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        updater.Ready.Should().BeNull();
        platform.Calls.Should().BeEmpty();
    }

    [Fact]
    public void After_an_update_the_service_and_the_app_come_back_with_the_same_settings()
    {
        HostSettings settings = DesktopOptions.Parse(["--config-dir", "/srv/router", "--urls", "http://localhost:6000"], _ => null).Host;
        string[] host = [.. DesktopUpdater.HostArgumentsOf(settings)];

        DesktopUpdater.RestartArguments(restartApp: true, serviceOn: true, host).Should().Equal([DesktopUpdater.ResumeService, .. host]);
        DesktopUpdater.RestartArguments(restartApp: true, serviceOn: false, host).Should().Equal(host);
        DesktopUpdater.RestartArguments(restartApp: false, serviceOn: true, host).Should().Equal(["--install-service", .. host]);
        DesktopUpdater.RestartArguments(restartApp: false, serviceOn: false, host).Should().BeEmpty("the app quit and no service runs: nothing starts");
        DesktopOptions restarted = DesktopOptions.Parse(DesktopUpdater.RestartArguments(true, true, host), _ => null);
        restarted.Should().Match<DesktopOptions>(o => o.ResumeService && o.Host.ConfigurationDirectory == settings.ConfigurationDirectory);
        restarted.Host.HostArguments.Should().Equal(settings.HostArguments);
    }

    [Fact]
    public void A_zip_download_run_from_the_build_output_is_not_an_installed_app()
    {
        DesktopUpdater.Create(DesktopOptions.Parse([], _ => null).Host, _ => null, TextWriter.Null).Installed.Should().BeFalse();
    }

    internal sealed class RecordingPlatform : ServicePlatform
    {
        public bool Installed { get; set; }

        public string? Program { get; init; }

        public List<string> Calls { get; } = [];

        public override string Kind => "recording";

        public override string? InstalledProgram() => Installed ? Program : null;

        public override string Location => "nowhere";

        public override string LogHint => "none";

        public override string EnvironmentAdvice => "none";

        public override bool IsInstalled() => Installed;

        public override string Render(ServiceDefinition definition) => string.Empty;

        public override Task<ServiceOutcome> InstallAsync(ServiceDefinition definition) => Record("install");

        public override Task<ServiceOutcome> UninstallAsync()
        {
            Installed = false;
            return Record("uninstall");
        }

        public override Task<ServiceOutcome> StartAsync() => Record("start");

        public override Task<ServiceOutcome> StopAsync() => Record("stop");

        public override Task<ServiceOutcome> RestartAsync() => Record("restart");

        public override Task<string> StatusAsync() => Task.FromResult("recording");

        private Task<ServiceOutcome> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(ServiceOutcome.Ok());
        }
    }

    private sealed class RecordingSignIn : SignInLaunch
    {
        public bool Enabled { get; set; }

        public bool Disabled { get; private set; }

        public override string Location => "nowhere";

        public override bool IsEnabled() => Enabled;

        public override void Enable(string program, IReadOnlyList<string> arguments) => Enabled = true;

        public override void Disable() => (Enabled, Disabled) = (false, true);
    }
}
