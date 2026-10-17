using SemanticRouter.Host;

// semantic-router            run the router and its dashboard (http://localhost:5080)
// semantic-router init       write a starting configuration with the models found on this machine
// semantic-router doctor     check every connector, model and the embeddings without calling a model
// semantic-router models     list what each connector offers, with suggested tiers and load state
// semantic-router service    run it in the background whenever you sign in (systemd, launchd or Windows sign-in)
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

    case "init" when settings.Error is null:
    {
        using var http = new HttpClient();
        // It asks before enabling a subscription CLI only at a terminal; scripted, it leaves them disabled.
        TextReader? terminal = Console.IsInputRedirected || Console.IsOutputRedirected ? null : Console.In;
        InitProbes probes;
        try
        {
            probes = InitProbes.ForThisMachine(http);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return await InitCommand.RunAsync(settings, probes, Console.Out, terminal);
    }

    case "doctor":
        return await DoctorCommand.RunAsync(settings, Console.Out);

    case "models":
        return await ModelsCommand.RunAsync(settings, Console.Out);

    case "service" when settings.Error is null:
        return await ServiceCommand.RunAsync(settings, Console.Out);

    default:
        if (settings.Error is not null)
        {
            Console.Error.WriteLine(settings.Error);
            Console.Error.WriteLine(Help());
            return 2;
        }

        return Environment.GetEnvironmentVariable(WindowsSignInService.DetachedVariable) == "1"
            ? await WindowsSignInService.RunDetachedAsync(settings)
            : await RouterHost.RunAsync(settings, Console.Out, lifecycle: RouterLifecycle.ForThisTool(settings));
}

static string Help() => $"""
    semantic-router {RouterHost.Version}: route each prompt to local models, subscription CLIs or paid APIs.

    Usage:
      semantic-router [run] [options]   Start the router and the dashboard (default http://localhost:5080).
      semantic-router init [--force] [--yes | --enable-cli <names> | --no-cli]
                                        Write a starting configuration with the models found on this machine
                                        (LM Studio, Ollama, and the claude / codex / copilot CLIs on PATH).
                                        It asks before enabling each subscription CLI, since that CLI answers
                                        on whatever account it is signed in to (at work, see
                                        {SiteLinks.WorkUse}); without a terminal they are added
                                        disabled unless --yes or --enable-cli says otherwise. A file that still
                                        holds the built-in template is replaced without --force.
      semantic-router doctor            Check every connector, model and the embeddings, and list every host the
                                        router will contact. Exit code 1 on failure.
      semantic-router models [name]     List the models each connector (or the one named) offers: loaded or not,
                                        context, size, which are in the catalog, and a suggested tier for the rest.
      semantic-router service <action> [options]
                                        Run the router in the background whenever you sign in, so no terminal
                                        stays open: a systemd user service (Linux), a launchd agent (macOS) or a
                                        sign-in entry (Windows). Per user, no administrator rights needed.
          install [--dry-run]           Install (or update) and start it with the options given here, e.g. --urls.
                                        --dry-run prints the definition instead.
          start | stop | restart        Control it. start --no-wait returns without waiting for it to listen.
          status                        Whether it is installed and running, where it listens and where it logs.
          uninstall                     Stop it and stop starting it at sign-in.
      semantic-router --version | --help

    The guides: {SiteLinks.Root}

    Options:
      --config-dir <dir>   Configuration directory. Default: ${HostSettings.HomeVariable}, else
                           %APPDATA%\semantic-router, ~/Library/Application Support/semantic-router
                           or ~/.config/semantic-router.
      --urls <urls>        Where to listen, e.g. http://localhost:5080 (the default). Binding beyond this machine
                           needs a dashboard token for remote dashboard use; see {SiteLinks.DashboardAccess}.
      --no-detect          init: write the template without probing this machine.
      --yes, -y            init: enable the claude and codex CLIs it finds without asking.
      --enable-cli <names> init: enable only the CLIs named, without asking, e.g. --enable-cli claude,codex
                           (claude, codex, copilot); the others it finds are added disabled.
      --no-cli             init: add no subscription CLI connectors (claude, codex, copilot). Wins over
                           --yes and --enable-cli.
      --SemanticRouter:<key>=<value>
                           Override any setting, like the SemanticRouter__<key> environment variables.

    Files in the configuration directory:
      semantic-router.json           The configuration; the dashboard saves here.
      semantic-router.secrets.json   Keys typed into the dashboard, encrypted.
      semantic-router.evaluation.jsonl  Labelled prompts for the dashboard's Evaluate page.
      semantic-router.db             Cooldowns, tool-call affinity, activity and configuration history.
      semantic-router.instance.json  The running router's process id and addresses, while it runs.
      appsettings.json               Optional: host settings (logging, Kestrel, SemanticRouter overrides).
      semantic-router.host.json      Where to listen, as the dashboard's Admin → Service page saves it; --urls wins.
    """;
