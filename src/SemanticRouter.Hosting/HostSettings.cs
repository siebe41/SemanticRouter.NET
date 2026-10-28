namespace SemanticRouter.Host;

/// <summary>What the command line asked for.</summary>
internal sealed record HostSettings
{
    public const string HomeVariable = "SEMANTIC_ROUTER_HOME";
    public const string KeysVariable = "SEMANTIC_ROUTER_KEYS_DIR";

    /// <summary>
    /// <c>SEMANTIC_ROUTER_SEED_API_KEYS</c>: while the configuration is the untouched template, add a connector for
    /// <c>OPENAI_API_KEY</c> and <c>ANTHROPIC_API_KEY</c> when they are set (<see cref="FirstRun"/>). The container image
    /// turns it on; the tool leaves it off, since a key in someone's shell is no reason to spend it.
    /// </summary>
    public const string SeedVariable = "SEMANTIC_ROUTER_SEED_API_KEYS";
    public const string ConfigurationFileName = "semantic-router.json";
    public const string StateFileName = "semantic-router.db";
    public const string DefaultUrl = "http://localhost:5080";

    /// <summary>The subscription CLIs <c>init</c> looks for, by executable name: what <c>--enable-cli</c> takes.</summary>
    public static readonly IReadOnlyList<string> CliNames = ["claude", "codex", "copilot"];

    /// <summary>What <c>semantic-router service</c> can do.</summary>
    public static readonly IReadOnlyList<string> ServiceActions = ["install", "uninstall", "start", "stop", "restart", "status"];

    /// <summary><c>run</c> (default), <c>init</c>, <c>doctor</c>, <c>models</c>, <c>service</c>, <c>help</c> or <c>version</c>.</summary>
    public string Command { get; init; } = "run";

    /// <summary>Where <c>semantic-router.json</c>, its secrets file and an optional <c>appsettings.json</c> live.</summary>
    public required string ConfigurationDirectory { get; init; }

    /// <summary>
    /// Where what belongs to this machine lives (<see cref="LocalData"/>): the state database, downloaded models, logs and
    /// the desktop window's WebView2 profile. The configuration directory, except on Windows when neither
    /// <c>--config-dir</c> nor <c>SEMANTIC_ROUTER_HOME</c> chose it: then <c>%LOCALAPPDATA%\semantic-router</c>, since the
    /// default configuration directory is in the roaming profile.
    /// </summary>
    public string LocalDataDirectory
    {
        get => _localDataDirectory ?? ConfigurationDirectory;
        init => _localDataDirectory = value;
    }

    private readonly string? _localDataDirectory;

    /// <summary><c>init</c>: overwrite an existing configuration file.</summary>
    public bool Force { get; init; }

    /// <summary><c>init</c>: skip probing for local servers and CLIs.</summary>
    public bool NoDetect { get; init; }

    /// <summary><c>init</c>: enable the subscription CLIs it finds without asking.</summary>
    public bool Yes { get; init; }

    /// <summary><c>init</c>: add no subscription CLI connectors.</summary>
    public bool NoCli { get; init; }

    /// <summary>
    /// <c>init --enable-cli claude,codex</c>: enable just these of the CLIs it finds, without asking (names from
    /// <see cref="CliNames"/>); <see langword="null"/> when not given.
    /// </summary>
    public IReadOnlySet<string>? EnableCli { get; init; }

    /// <summary><c>models</c>: the one connector to list; <see langword="null"/> lists them all.</summary>
    public string? Connector { get; init; }

    /// <summary><c>service</c>: one of <see cref="ServiceActions"/>.</summary>
    public string? ServiceAction { get; init; }

    /// <summary><c>service install</c>: print the service definition instead of installing it.</summary>
    public bool DryRun { get; init; }

    /// <summary><c>service start</c>: return as soon as the router is started, without waiting for it to listen.</summary>
    public bool NoWait { get; init; }

    /// <summary>
    /// <c>SEMANTIC_ROUTER_KEYS_DIR</c>: the folder for the data-protection keys that decrypt the secrets file;
    /// <see langword="null"/> for ASP.NET Core's default (see <see cref="KeyRing"/>).
    /// </summary>
    public string? KeysDirectory { get; init; }

    /// <summary>Arguments for the web host (<c>--urls</c>, <c>--SemanticRouter:…=…</c>).</summary>
    public IReadOnlyList<string> HostArguments { get; init; } = [];

    /// <summary>An unknown command or a missing option value.</summary>
    public string? Error { get; init; }

    public string ConfigurationFile => Path.Combine(ConfigurationDirectory, ConfigurationFileName);

    /// <summary>Parses <paramref name="args"/>. Options the host does not know are passed on to the web host.</summary>
    public static HostSettings Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string command = "run";
        string? directory = null, connector = null, serviceAction = null;
        bool force = false, noDetect = false, yes = false, noCli = false, dryRun = false, noWait = false;
        var hostArguments = new List<string>();
        string? error = null;
        HashSet<string>? enableCli = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "run" or "init" or "doctor" or "models" or "service" or "help" or "version" when i == 0:
                    command = arg;
                    break;
                case "-h" or "--help" or "-?":
                    command = "help";
                    break;
                case "--version":
                    command = "version";
                    break;
                case "--force":
                    force = true;
                    break;
                case "--no-detect":
                    noDetect = true;
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--no-cli":
                    noCli = true;
                    break;
                case "--enable-cli":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith('-'))
                    {
                        error = "--enable-cli needs the CLIs to enable, e.g. --enable-cli claude,codex.";
                    }
                    else
                    {
                        enableCli = ParseCliNames(args[++i], enableCli, ref error);
                    }

                    break;
                case "--dry-run" when command == "service":
                    dryRun = true;
                    break;
                case "--no-wait" when command == "service":
                    noWait = true;
                    break;
                case "--config-dir":
                    if (i + 1 >= args.Count)
                    {
                        error = "--config-dir needs a directory.";
                    }
                    else
                    {
                        directory = args[++i];
                    }

                    break;
                default:
                    if (arg.StartsWith("--config-dir=", StringComparison.Ordinal))
                    {
                        directory = arg["--config-dir=".Length..];
                    }
                    else if (arg.StartsWith("--enable-cli=", StringComparison.Ordinal))
                    {
                        enableCli = ParseCliNames(arg["--enable-cli=".Length..], enableCli, ref error);
                    }
                    else if (i == 0 && !arg.StartsWith('-'))
                    {
                        error = $"Unknown command '{arg}'.";
                    }
                    else if (command == "models" && connector is null && !arg.StartsWith('-'))
                    {
                        connector = arg;
                    }
                    else if (command == "service" && serviceAction is null && !arg.StartsWith('-'))
                    {
                        serviceAction = arg;
                    }
                    else
                    {
                        hostArguments.Add(arg);
                    }

                    break;
            }
        }

        if (command == "service" && error is null && (serviceAction is null || !ServiceActions.Contains(serviceAction)))
        {
            error = serviceAction is null
                ? $"service needs an action: {string.Join(", ", ServiceActions)}."
                : $"Unknown service action '{serviceAction}'. Use {string.Join(", ", ServiceActions)}.";
        }

        // "--config-dir=" or an empty argument names no directory (Path.GetFullPath would throw on it).
        if (directory is not null && string.IsNullOrWhiteSpace(directory))
        {
            error ??= "--config-dir needs a directory.";
            directory = null;
        }

        string configuration = Path.GetFullPath(directory ?? DefaultConfigurationDirectory(environment, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()));
        return new HostSettings
        {
            Command = command,
            ConfigurationDirectory = configuration,
            LocalDataDirectory = LocalDataDirectoryFor(configuration, environment, OperatingSystem.IsWindows()),
            Force = force,
            NoDetect = noDetect,
            Yes = yes,
            NoCli = noCli,
            EnableCli = enableCli,
            Connector = connector,
            ServiceAction = serviceAction,
            DryRun = dryRun,
            NoWait = noWait,
            KeysDirectory = environment(KeysVariable) is { Length: > 0 } keys ? keys : null,
            HostArguments = hostArguments,
            Error = error,
        };
    }

    /// <summary>
    /// The CLIs in a comma-separated <c>--enable-cli</c> value, added to <paramref name="names"/> (the option may be
    /// given more than once). Sets <paramref name="error"/> for an empty list or a name that is not in <see cref="CliNames"/>.
    /// </summary>
    private static HashSet<string> ParseCliNames(string value, HashSet<string>? names, ref string? error)
    {
        names ??= new HashSet<string>(StringComparer.Ordinal);
        string[] given = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (given.Length == 0)
        {
            error ??= "--enable-cli needs the CLIs to enable, e.g. --enable-cli claude,codex.";
        }

        foreach (string name in given)
        {
            if (CliNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                names.Add(known);
            }
            else
            {
                error ??= $"Unknown CLI '{name}' for --enable-cli. Use {string.Join(", ", CliNames)}.";
            }
        }

        return names;
    }

    /// <summary>
    /// The local data directory for <paramref name="configurationDirectory"/>: <c>%LOCALAPPDATA%\semantic-router</c> when
    /// it is Windows' default, roaming one (however it was given: the background service names it with
    /// <c>--config-dir</c>); otherwise the configuration directory itself.
    /// </summary>
    internal static string LocalDataDirectoryFor(string configurationDirectory, Func<string, string?> environment, bool windows)
    {
        if (!windows || environment(HomeVariable) is { Length: > 0 }
            || !string.Equals(Path.GetFullPath(configurationDirectory), Path.GetFullPath(DefaultConfigurationDirectory(environment, windows, macOS: false)), StringComparison.OrdinalIgnoreCase))
        {
            return configurationDirectory;
        }

        string localAppData = environment("LOCALAPPDATA") is { Length: > 0 } l ? l
            : Path.Combine(environment("USERPROFILE") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return Path.GetFullPath(Path.Combine(localAppData, "semantic-router"));
    }

    /// <summary>
    /// <c>SEMANTIC_ROUTER_HOME</c> when set; otherwise <c>%APPDATA%\semantic-router</c> on Windows,
    /// <c>~/Library/Application Support/semantic-router</c> on macOS and <c>$XDG_CONFIG_HOME/semantic-router</c>
    /// (default <c>~/.config/semantic-router</c>) elsewhere.
    /// </summary>
    internal static string DefaultConfigurationDirectory(Func<string, string?> environment, bool windows, bool macOS)
    {
        if (environment(HomeVariable) is { Length: > 0 } explicitHome)
        {
            return explicitHome;
        }

        string home = environment(windows ? "USERPROFILE" : "HOME") is { Length: > 0 } h
            ? h
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (windows)
        {
            string appData = environment("APPDATA") is { Length: > 0 } a ? a : Path.Combine(home, "AppData", "Roaming");
            return Path.Combine(appData, "semantic-router");
        }

        if (macOS)
        {
            return Path.Combine(home, "Library", "Application Support", "semantic-router");
        }

        string config = environment("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        return Path.Combine(config, "semantic-router");
    }
}
