namespace SemanticRouter.Host;

/// <summary>What the command line asked for.</summary>
internal sealed record HostSettings
{
    public const string HomeVariable = "SEMANTIC_ROUTER_HOME";
    public const string KeysVariable = "SEMANTIC_ROUTER_KEYS_DIR";
    public const string ConfigurationFileName = "semantic-router.json";
    public const string StateFileName = "semantic-router.db";
    public const string DefaultUrl = "http://localhost:5080";

    /// <summary>What <c>semantic-router service</c> can do.</summary>
    public static readonly IReadOnlyList<string> ServiceActions = ["install", "uninstall", "start", "stop", "restart", "status"];

    /// <summary><c>run</c> (default), <c>init</c>, <c>doctor</c>, <c>models</c>, <c>service</c>, <c>help</c> or <c>version</c>.</summary>
    public string Command { get; init; } = "run";

    /// <summary>Where <c>semantic-router.json</c>, its secrets file and an optional <c>appsettings.json</c> live.</summary>
    public required string ConfigurationDirectory { get; init; }

    /// <summary><c>init</c>: overwrite an existing configuration file.</summary>
    public bool Force { get; init; }

    /// <summary><c>init</c>: skip probing for local servers and CLIs.</summary>
    public bool NoDetect { get; init; }

    /// <summary><c>init</c>: enable the subscription CLIs it finds without asking.</summary>
    public bool Yes { get; init; }

    /// <summary><c>init</c>: add no subscription CLI connectors.</summary>
    public bool NoCli { get; init; }

    /// <summary><c>models</c>: the one connector to list; <see langword="null"/> lists them all.</summary>
    public string? Connector { get; init; }

    /// <summary><c>service</c>: one of <see cref="ServiceActions"/>.</summary>
    public string? ServiceAction { get; init; }

    /// <summary><c>service install</c>: print the service definition instead of installing it.</summary>
    public bool DryRun { get; init; }

    /// <summary><c>service start</c>: return as soon as the router is started, without waiting for it to listen.</summary>
    public bool NoWait { get; init; }

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

        return new HostSettings
        {
            Command = command,
            ConfigurationDirectory = Path.GetFullPath(directory ?? DefaultConfigurationDirectory(environment, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS())),
            Force = force,
            NoDetect = noDetect,
            Yes = yes,
            NoCli = noCli,
            Connector = connector,
            ServiceAction = serviceAction,
            DryRun = dryRun,
            NoWait = noWait,
            HostArguments = hostArguments,
            Error = error,
        };
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
