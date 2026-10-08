using SemanticRouter.Host;

// semantic-router            run the router and its dashboard (http://localhost:5080)
// semantic-router init       write a starting configuration with the models found on this machine
// semantic-router doctor     check every connector, model and the embeddings without calling a model
Console.OutputEncoding = System.Text.Encoding.UTF8;
HostSettings settings = HostSettings.Parse(args);

switch (settings.Command)
{
    case "version":
        Console.WriteLine(RouterHost.Version);
        return 0;

    case "help":
        Console.WriteLine(Help());
        return 0;

    case "init":
    {
        using var http = new HttpClient();
        return await InitCommand.RunAsync(settings, InitProbes.ForThisMachine(http), Console.Out);
    }

    case "doctor":
        return await DoctorCommand.RunAsync(settings, Console.Out);

    default:
        if (settings.Error is not null)
        {
            Console.Error.WriteLine(settings.Error);
            Console.Error.WriteLine(Help());
            return 2;
        }

        return await RouterHost.RunAsync(settings, Console.Out);
}

static string Help() => $"""
    semantic-router {RouterHost.Version}: route each prompt to local models, subscription CLIs or paid APIs.

    Usage:
      semantic-router [run] [options]   Start the router and the dashboard (default http://localhost:5080).
      semantic-router init [--force]    Write a starting configuration with the models found on this machine
                                        (LM Studio, Ollama, and the claude / codex / copilot CLIs on PATH).
      semantic-router doctor            Check every connector, model and the embeddings. Exit code 1 on failure.
      semantic-router --version | --help

    Options:
      --config-dir <dir>   Configuration directory. Default: ${HostSettings.HomeVariable}, else
                           %APPDATA%\semantic-router, ~/Library/Application Support/semantic-router
                           or ~/.config/semantic-router.
      --urls <urls>        Where to listen, e.g. http://localhost:5080 (the default). Binding beyond this machine
                           needs a dashboard token for remote dashboard use; see docs/dashboard.md.
      --no-detect          init: write the template without probing this machine.
      --SemanticRouter:<key>=<value>
                           Override any setting, like the SemanticRouter__<key> environment variables.

    Files in the configuration directory:
      semantic-router.json           The configuration; the dashboard saves here.
      semantic-router.secrets.json   Keys typed into the dashboard, encrypted.
      semantic-router.db             Cooldowns, tool-call affinity, activity and configuration history.
      appsettings.json               Optional: host settings (logging, Kestrel, SemanticRouter overrides).
    """;
