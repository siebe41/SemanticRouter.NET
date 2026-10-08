using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SemanticRouter.Host;

/// <summary>
/// <c>semantic-router service install|uninstall|start|stop|restart|status</c>: runs the router in the background for the
/// signed-in user, so no terminal has to stay open. It is a per-user service on purpose: the subscription CLI connectors
/// use this user's own sign-in and PATH, which a system service would not have. A systemd user unit on Linux, a launchd
/// agent on macOS, and a sign-in entry under <c>HKCU\…\Run</c> on Windows; none of them needs administrator rights.
/// </summary>
internal static partial class ServiceCommand
{
    /// <summary>How long <c>install</c>, <c>start</c> and <c>restart</c> wait for the router to listen.</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        HostSettings settings, TextWriter output, ServicePlatform? platform = null, ServiceDefinition? definition = null, TimeSpan? startTimeout = null)
    {
        platform ??= ServicePlatform.ForThisMachine(settings);
        if (platform is null)
        {
            await output.WriteLineAsync("Background services are supported on Linux (systemd), macOS (launchd) and Windows. Start `semantic-router` with your own supervisor here.").ConfigureAwait(false);
            return 1;
        }

        definition ??= ServiceDefinition.ForThisTool(settings);
        TimeSpan timeout = startTimeout ?? DefaultStartTimeout;
        switch (settings.ServiceAction)
        {
            case "install":
                return await InstallAsync(settings, output, platform, definition, timeout).ConfigureAwait(false);

            case "uninstall":
                if (!platform.IsInstalled())
                {
                    await output.WriteLineAsync($"Nothing to remove: {platform.Location} does not exist.").ConfigureAwait(false);
                    return 0;
                }

                return await ReportAsync(output, await platform.UninstallAsync().ConfigureAwait(false),
                    $"Stopped the router and removed {platform.Location}. It no longer starts when you sign in.").ConfigureAwait(false);

            case "status":
                return await StatusAsync(settings, output, platform).ConfigureAwait(false);

            default:
                if (!platform.IsInstalled())
                {
                    await output.WriteLineAsync("The service is not installed. Run `semantic-router service install` first.").ConfigureAwait(false);
                    return 1;
                }

                DateTimeOffset requested = DateTimeOffset.UtcNow;
                ServiceOutcome outcome = settings.ServiceAction switch
                {
                    "start" => await platform.StartAsync().ConfigureAwait(false),
                    "stop" => await platform.StopAsync().ConfigureAwait(false),
                    _ => await platform.RestartAsync().ConfigureAwait(false),
                };
                if (settings.ServiceAction == "stop")
                {
                    return await ReportAsync(output, outcome, $"Stopped. {platform.StartsAgain}").ConfigureAwait(false);
                }

                if (await ReportAsync(output, outcome, null).ConfigureAwait(false) != 0)
                {
                    return 1;
                }

                return settings.NoWait ? 0 : await WaitForRouterAsync(settings, output, platform, requested, timeout).ConfigureAwait(false);
        }
    }

    private static async Task<int> InstallAsync(HostSettings settings, TextWriter output, ServicePlatform platform, ServiceDefinition definition, TimeSpan timeout)
    {
        if (settings.DryRun)
        {
            await output.WriteLineAsync($"# {platform.Kind}: {platform.Location}").ConfigureAwait(false);
            await output.WriteLineAsync(platform.Render(definition)).ConfigureAwait(false);
            return 0;
        }

        DateTimeOffset requested = DateTimeOffset.UtcNow;
        ServiceOutcome outcome = await platform.InstallAsync(definition).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await output.WriteLineAsync($"Could not install the service: {outcome.Detail}").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"Installed a {platform.Kind}: {platform.Location}").ConfigureAwait(false);
        await output.WriteLineAsync($"It runs `{definition.CommandLine}` whenever you sign in, and restarts it if it fails.").ConfigureAwait(false);
        foreach (string note in platform.InstallNotes)
        {
            await output.WriteLineAsync(note).ConfigureAwait(false);
        }

        IReadOnlyList<string> variables = EnvironmentReferences(settings.ConfigurationDirectory);
        if (variables.Count > 0)
        {
            await output.WriteLineAsync(
                $"Your configuration reads {string.Join(", ", variables)} from the environment. {platform.EnvironmentAdvice} " +
                "Typing a key on the dashboard instead stores it encrypted, and the service reads it from there.").ConfigureAwait(false);
        }

        return await WaitForRouterAsync(settings, output, platform, requested, timeout).ConfigureAwait(false);
    }

    private static async Task<int> StatusAsync(HostSettings settings, TextWriter output, ServicePlatform platform)
    {
        bool installed = platform.IsInstalled();
        await output.WriteLineAsync(installed
            ? $"Service    {platform.Kind}: {platform.Location}"
            : "Service    not installed (`semantic-router service install` starts the router when you sign in)").ConfigureAwait(false);
        if (installed)
        {
            await output.WriteLineAsync($"State      {await platform.StatusAsync().ConfigureAwait(false)}").ConfigureAwait(false);
        }

        RouterInstance? instance = RouterInstance.ReadRunning(settings.ConfigurationDirectory);
        if (instance is null)
        {
            await output.WriteLineAsync($"Router     not running for {settings.ConfigurationDirectory}").ConfigureAwait(false);
        }
        else
        {
            await WriteRunningAsync(output, instance).ConfigureAwait(false);
        }

        if (installed)
        {
            await output.WriteLineAsync($"Logs       {platform.LogHint}").ConfigureAwait(false);
        }

        return instance is null ? 1 : 0;
    }

    /// <summary>Waits until a router started after <paramref name="requested"/> records itself, then says where it listens.</summary>
    private static async Task<int> WaitForRouterAsync(
        HostSettings settings, TextWriter output, ServicePlatform platform, DateTimeOffset requested, TimeSpan timeout)
    {
        // The instance file stores whole milliseconds, and the clocks are the same machine's.
        DateTimeOffset since = requested.AddSeconds(-1);
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < timeout)
        {
            if (RouterInstance.ReadRunning(settings.ConfigurationDirectory) is { } instance && instance.StartedAt >= since)
            {
                await WriteRunningAsync(output, instance).ConfigureAwait(false);
                return 0;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"The router is not listening after {timeout.TotalSeconds:0} seconds. See what it logged: {platform.LogHint}").ConfigureAwait(false);
        return 1;
    }

    private static async Task WriteRunningAsync(TextWriter output, RouterInstance instance)
    {
        string url = instance.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? (instance.Urls.Count > 0 ? instance.Urls[0] : HostSettings.DefaultUrl);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Router     running (pid {instance.ProcessId}) on {string.Join(", ", instance.Urls)} since {instance.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}, version {instance.Version}")).ConfigureAwait(false);
        await output.WriteLineAsync($"Dashboard  {RouterHost.LocalAddress(url)}/router").ConfigureAwait(false);
    }

    private static async Task<int> ReportAsync(TextWriter output, ServiceOutcome outcome, string? success)
    {
        if (!outcome.Succeeded)
        {
            await output.WriteLineAsync($"Failed: {outcome.Detail}").ConfigureAwait(false);
            return 1;
        }

        string? message = success ?? outcome.Detail;
        if (message is not null)
        {
            await output.WriteLineAsync(message).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>The <c>${env:…}</c> variables the configuration files in <paramref name="directory"/> refer to.</summary>
    internal static IReadOnlyList<string> EnvironmentReferences(string directory)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in new[] { HostSettings.ConfigurationFileName, "appsettings.json" }.Select(f => Path.Combine(directory, f)).Where(File.Exists))
        {
            foreach (Match match in EnvironmentReference().Matches(File.ReadAllText(file)))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return [.. names];
    }

    [GeneratedRegex(@"\$\{env:\s*(?<name>[^}\s]+)\s*\}", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentReference();
}

/// <summary>What a background service runs: this tool, <c>run</c>, the configuration directory and the host options.</summary>
internal sealed record ServiceDefinition
{
    /// <summary>Variables a service would otherwise not have: where the CLIs are and where the .NET runtime is.</summary>
    public static readonly IReadOnlyList<string> CarriedVariables =
        ["PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", HostSettings.KeysVariable];

    /// <summary>The executable: the tool's own launcher, or <c>dotnet</c> when it runs as <c>dotnet semantic-router.dll</c>.</summary>
    public required string Program { get; init; }

    /// <summary>Arguments the executable needs before any command (the assembly, for <c>dotnet</c>).</summary>
    public IReadOnlyList<string> ProgramArguments { get; init; } = [];

    public required string ConfigurationDirectory { get; init; }

    /// <summary>Host options to run with (<c>--urls</c>, <c>--SemanticRouter:…=…</c>).</summary>
    public IReadOnlyList<string> HostArguments { get; init; } = [];

    /// <summary>The service's environment, taken from the shell that installed it.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Everything after <see cref="Program"/> to run the router.</summary>
    public IReadOnlyList<string> Arguments => [.. ProgramArguments, .. Command("run"), .. HostArguments];

    /// <summary>Everything after <see cref="Program"/> for another command on the same configuration directory.</summary>
    public IReadOnlyList<string> Command(params string[] command) =>
        [.. ProgramArguments, .. command, "--config-dir", ConfigurationDirectory];

    public string CommandLine => string.Join(' ', new[] { Program }.Concat(Arguments).Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    public static ServiceDefinition ForThisTool(HostSettings settings) =>
        ForTool(settings, System.Environment.ProcessPath, typeof(RouterHost).Assembly.Location, System.Environment.GetEnvironmentVariable);

    internal static ServiceDefinition ForTool(HostSettings settings, string? processPath, string assemblyPath, Func<string, string?> environment)
    {
        string program = processPath ?? throw new InvalidOperationException("The path of this executable is unknown.");
        bool viaDotnet = Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return new ServiceDefinition
        {
            Program = program,
            ProgramArguments = viaDotnet ? [assemblyPath] : [],
            ConfigurationDirectory = settings.ConfigurationDirectory,
            HostArguments = settings.HostArguments,
            Environment = CarriedVariables
                .Select(name => (name, value: environment(name)))
                .Where(v => !string.IsNullOrEmpty(v.value))
                .ToDictionary(v => v.name, v => v.value!, StringComparer.Ordinal),
        };
    }
}

/// <summary>What happened when a service manager was asked to do something.</summary>
internal sealed record ServiceOutcome(bool Succeeded, string? Detail = null)
{
    public static ServiceOutcome Ok(string? detail = null) => new(true, detail);

    public static ServiceOutcome Failed(string detail) => new(false, detail);
}

/// <summary>The exit code and the combined output of a finished process.</summary>
internal sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a service manager command (<c>systemctl</c>, <c>launchctl</c>); replaced in tests.</summary>
internal delegate Task<ProcessResult> ProcessRunner(string fileName, IReadOnlyList<string> arguments);

/// <summary>A per-user service manager: systemd, launchd or the Windows sign-in entries.</summary>
internal abstract class ServicePlatform
{
    /// <summary>A short name: "systemd user service", "launchd agent", "Windows sign-in entry".</summary>
    public abstract string Kind { get; }

    /// <summary>Where the definition lives: a unit file, a property list or a registry value.</summary>
    public abstract string Location { get; }

    /// <summary>Where to read what the router logged.</summary>
    public abstract string LogHint { get; }

    /// <summary>When a stopped service starts again.</summary>
    public virtual string StartsAgain => "It starts again when you next sign in, or with `semantic-router service start`.";

    /// <summary>How to give the service an environment variable.</summary>
    public abstract string EnvironmentAdvice { get; }

    /// <summary>Anything else worth knowing after an install.</summary>
    public virtual IEnumerable<string> InstallNotes => [];

    public abstract bool IsInstalled();

    /// <summary>The definition as it is written to <see cref="Location"/>.</summary>
    public abstract string Render(ServiceDefinition definition);

    /// <summary>Writes the definition (replacing an older one), enables it and (re)starts the router.</summary>
    public abstract Task<ServiceOutcome> InstallAsync(ServiceDefinition definition);

    public abstract Task<ServiceOutcome> UninstallAsync();

    public abstract Task<ServiceOutcome> StartAsync();

    public abstract Task<ServiceOutcome> StopAsync();

    public abstract Task<ServiceOutcome> RestartAsync();

    /// <summary>What the service manager says about the service, in a few words.</summary>
    public abstract Task<string> StatusAsync();

    public static ServicePlatform? ForThisMachine(HostSettings settings)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSignInService(settings.ConfigurationDirectory);
        }

        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgentService(
                Path.Combine(home, "Library", "LaunchAgents"), Path.Combine(home, "Library", "Logs", "semantic-router"), RunProcessAsync);
        }

        if (OperatingSystem.IsLinux())
        {
            return new SystemdUserService(SystemdUserService.DefaultUnitDirectory(System.Environment.GetEnvironmentVariable, home), RunProcessAsync);
        }

        return null;
    }

    /// <summary>Runs a command and collects its output; a missing executable is a failed result, not an exception.</summary>
    public static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, $"{fileName}: {ex.Message}");
        }

        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new ProcessResult(-1, $"{fileName} did not finish within a minute.");
            }

            return new ProcessResult(process.ExitCode, ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim());
        }
    }
}
