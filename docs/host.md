# The `semantic-router` tool and container

`SemanticRouter.Host` is the router as an application: the OpenAI and Anthropic front doors, the dashboard, hot reload and the configuration file, with nothing to clone or compile. It ships as a .NET tool and as a container image.

## Install and start

Prerequisite: the [.NET 10 runtime or SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet tool install -g SemanticRouter.Host
semantic-router init      # write a configuration with the models found on this machine
semantic-router doctor    # check every connector, model and the embeddings
semantic-router models    # what each connector offers: loaded or not, suggested tiers
semantic-router           # start: http://localhost:5080, dashboard at /router
```

On start it prints the lines to paste into your shell:

```text
SemanticRouter 0.2.0 is listening on http://localhost:5080
  Configuration  /home/ada/.config/semantic-router/semantic-router.json
  Dashboard      http://localhost:5080/router
  Claude Code    export ANTHROPIC_BASE_URL=http://localhost:5080; export ANTHROPIC_AUTH_TOKEN=semantic-router
  OpenAI clients export OPENAI_BASE_URL=http://localhost:5080/v1; model "auto"
```

## Commands

| Command | What it does |
|---|---|
| `semantic-router` (or `run`) | Starts the router and the dashboard. Creates the configuration file from the built-in template on first run. |
| `semantic-router init [--force] [--no-detect] [--yes \| --no-cli]` | Writes a starting configuration. It probes LM Studio (`localhost:1234`) and Ollama (`localhost:11434`) for chat and embedding models and looks for `claude`, `codex` and `copilot` on `PATH`, asking before it enables a subscription CLI (see [What `init` adds](#what-init-adds)). `--yes` enables them without asking; `--no-cli` adds no subscription CLI connector. It refuses to overwrite an existing file without `--force`. |
| `semantic-router doctor` | Loads the configuration exactly as `run` would and checks it without calling a model. It prints the [machine-wide policy](enterprise.md), if an administrator set one, flags unresolved `${env:…}` references, tests the embeddings, and checks each HTTP connector (reachable, credentials accepted, configured model ids offered) and each CLI connector (installed, starts and, for Claude Code, signed in). Every failure names the setting to change. It also lists every host the router will contact with this configuration, for a firewall allowlist ([network.md](network.md#allowing-it-through-a-firewall)). Exit code 1 when anything failed. |
| `semantic-router models [connector]` | Asks every connector (or the one named) which models it offers, and prints them with their load state, context length and size, the catalog model that already uses each, and a suggested name, tier (and where it came from: the family table, the size or the name) and capabilities for the rest (see [Model lineups](providers.md#model-lineups-several-models-per-provider)). A `model-families.json` in the configuration directory adds or changes families. LM Studio and Ollama also say whether they load models on demand. Changes nothing; add models on the dashboard. |
| `semantic-router service install\|uninstall\|start\|stop\|restart\|status` | Runs the router in the background whenever you sign in. See [Run it in the background](#run-it-in-the-background). |
| `semantic-router --version`, `--help` | |

Options: `--config-dir <dir>`, `--urls <urls>`, and any setting as `--SemanticRouter:<key>=<value>` (the same keys as the `SemanticRouter__<key>` environment variables).

## Run it in the background

Keeping a terminal open for the router gets old. `service install` hands it to your operating system's own service manager instead: it starts when you sign in, restarts if it fails, and logs where that platform keeps logs.

```bash
semantic-router service install            # install, start, and wait until it listens
semantic-router service status             # installed? running? where it listens, where it logs
semantic-router service restart            # after `dotnet tool update`, or to pick up new host options
semantic-router service stop | start
semantic-router service uninstall          # stop it and stop starting it at sign-in
```

Options given to `install` are kept for the service: `semantic-router service install --urls http://localhost:6000` runs it on that port, and `--config-dir` picks the configuration directory (the service always passes the directory explicitly, so `SEMANTIC_ROUTER_HOME` in your shell does not matter later). `--dry-run` prints what would be written without installing anything. Running `install` again replaces the definition and restarts the router.

It is a **per-user service on purpose**. The subscription CLI connectors run `claude`, `codex` and `copilot` on your own sign-in, from your PATH, and a system service running as another account would have neither. Nothing needs administrator rights.

| | Linux | macOS | Windows |
|---|---|---|---|
| What is installed | systemd user unit `~/.config/systemd/user/semantic-router.service` | launchd agent `~/Library/LaunchAgents/io.github.siebe41.semantic-router.plist` | value `SemanticRouter` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (Task Manager → Startup apps), plus `semantic-router.service.json` in the configuration directory |
| Runs | while you are signed in; `loginctl enable-linger` keeps it running without a session | while you are signed in | from sign-in, without a console window |
| Restarts | on failure, every 5 s | on failure, at most every 30 s | not automatically |
| Logs | `journalctl --user -u semantic-router -f` | `~/Library/Logs/semantic-router/semantic-router.log` | `logs\semantic-router.log` in the configuration directory (rolled to `.1` at start once it passes 10 MB) |
| Stop | `systemctl --user stop` | `launchctl bootout` (a killed agent would be restarted) | a stop request the router listens for, then ending the process after 20 s |

Things to know:

- **PATH and the .NET runtime are copied from the shell you install from.** Services start with a minimal PATH, so the unit and the agent carry yours (plus `DOTNET_ROOT` if it is set, without which a .NET installed in your home directory is not found). Install a CLI somewhere new? Run `service install` again.
- **Other environment variables are not copied.** If your configuration uses `${env:OPENAI_API_KEY}`, `install` names the variables a service will not see. Type the key on the dashboard instead (it is stored encrypted), or give it to the service: `systemctl --user edit semantic-router.service` on Linux, a user environment variable (`setx`) on Windows.
- **Updating the tool:** on Linux and macOS run `service restart` after `dotnet tool update`. On Windows run `service stop` first, because a running program cannot be replaced.
- **Exit codes a restart cannot fix are not retried by systemd**: 2 (a command-line error) and 3 (the address is already in use, for example by a router you started in a terminal). The message names `service status`.
- **Windows shows a console window for a moment at sign-in**, while the sign-in entry starts the background router and returns. The same service set up by the [desktop app](desktop.md#the-app-and-the-background-service) runs `SemanticRouter --headless`, a GUI program, and starts without one. Either installs the same service, so `semantic-router service status`, `stop` and `start` work on both.
- **Without systemd** (some containers, WSL without systemd, OpenRC distributions) `install` says so and changes nothing. Run `semantic-router` under the supervisor you have.

While it runs, the router records its process id and addresses in `semantic-router.instance.json`; `service status` reads it.

## What `init` adds

| Found | Added |
|---|---|
| LM Studio or Ollama with chat models | A connector and **every chat model it serves**, each on the tier its family suggests ([model-family table](providers.md#the-model-family-table)) or else its size (≤ 9B: 1, ≤ 35B: 2, larger: 3), with `code`, `tools`, `vision` and `reasoning` from the server's report or the family table, and the context it is loaded with. Models that are not loaded get `Priority` 1, so loaded ones are preferred. The notes say whether the server loads models on demand. |
| An embedding model on either server | `Embeddings` switched to it, with a starting similarity threshold for that model family. |
| No embedding server | `Embeddings` set to `Onnx` with `all-MiniLM-L6-v2`: a 23 MB model that runs inside the router. It is downloaded on first start and checksum-verified. Threshold 0.40. |
| A server that answers `401` | The connector, plus a note to set its `ApiKey` (for example `"${env:LM_API_TOKEN}"`). |
| `claude` on PATH | `claude-cli` with prompted tool calling and Claude's lineup, one tier per family: `claude-haiku` (tier 2), `claude-sonnet` (3), `claude-opus` (4) and `claude-fable-5-1` (5, **disabled** until you enable it, because not every plan includes it). |
| `codex` on PATH | `codex-cli` (concurrency 1) and a tier-4 `codex` model. |
| `copilot` on PATH | `copilot-cli` and a tier-4 `copilot` model, **disabled**: it spends your plan's premium requests, so you turn it on. |

On Windows only the native `.exe` counts. An npm `.cmd` shim is reported and not used, because the router never starts a CLI through `cmd.exe`. Everything else (API-key providers, tiers, routes) is edited on the dashboard.

**Subscription CLIs are enabled only on a yes.** A CLI connector answers with whatever account that CLI is signed in to, which on a work machine may be a personal plan under consumer terms (see [Using it at work](TERMS.md#using-it-at-work)). So `init` lists the `claude` and `codex` it found and asks about each one (`[y/N]`, no by default):

| How `init` runs | `claude-cli` and `codex-cli` |
|---|---|
| At a terminal | Asked about one by one. Yes: added enabled, as in the table above. No: added with every model disabled, so you can turn them on later on the dashboard's **Models** page. |
| Without a terminal (input or output redirected: a script, CI, an installer) | Added with every model disabled, with a note saying how to enable them. |
| `--yes` | Added enabled without asking. |
| `--no-cli` | Not added. Neither is `copilot-cli`. |

`copilot-cli` is never asked about: it is always added disabled, as above.

## Where things live

| | Default |
|---|---|
| Configuration directory | `SEMANTIC_ROUTER_HOME` if set; otherwise `%APPDATA%\semantic-router` (Windows), `~/Library/Application Support/semantic-router` (macOS) or `$XDG_CONFIG_HOME/semantic-router`, default `~/.config/semantic-router` (Linux). |
| `semantic-router.json` | The configuration. The dashboard's **Apply & save** writes it. |
| `semantic-router.db` | Durable state (SQLite): model cooldowns, tool-call affinity, the activity log and every applied configuration, so a restart forgets nothing. See [What the router stores](#what-the-router-stores). |
| `semantic-router.secrets.json` | Keys typed into the dashboard, encrypted with ASP.NET Core Data Protection. See [dashboard.md](dashboard.md#keeping-secrets-out-of-the-file). |
| `semantic-router.evaluation.jsonl` | Labelled prompts for the **Evaluate** page. `init` writes a sample set for the starting routes. See [routing-quality.md](routing-quality.md). |
| `semantic-router.instance.json` | The running router's process id, addresses, start time and version. Removed when it stops. |
| `semantic-router.service.json`, `logs/` | Windows background service only: what the sign-in entry starts, and what it logs. |
| `models/` | Downloaded ONNX embedding models, one directory per model. Copy a `models/{name}` directory here to run without internet access. |
| `appsettings.json` (optional) | Host settings: logging, Kestrel, `SemanticRouter:Dashboard`, overrides. |

### What the router stores

| Where | What | Kept for | How to delete it |
|---|---|---|---|
| `semantic-router.db`, table `activity` | One entry per request: time, client name, project label (the `X-Router-Project` header, or else a hash of the client's user id, never the id), models, tiers, tokens, cost, notes, and a **prompt preview** (see below) | 30 days or 10,000 entries (`State:Retention:ActivityDays`, `ActivityEntries`) | **Gateway → Stored prompts → Delete all activity**, or `DELETE /router/api/activity?since=…&until=…` |
| `semantic-router.db`, table `feedback` | Retries, corrections, labels and grades per request: tiers and short notes, no prompt text | As activity | With the activity it belongs to ageing out |
| `semantic-router.db`, table `usage` | Tokens and cost per request, by model, connector, route, client and profile; no prompt text | 400 days (`State:Retention:UsageDays`) | Delete the file |
| `semantic-router.db`, table `config_history` | Every applied configuration, with secrets masked | 200 versions (`State:Retention:ConfigurationVersions`) | Delete the file |
| `semantic-router.db`, tables `cooldowns`, `affinity`, `warm_caches`, `tuning_changes` | Model cooldowns, which model issued each tool call (ids only), the size of each conversation's prompt cache per model (conversation hashes only), learned routing changes (which can include short utterances learned from prompt previews) | Until they expire, 24 hours, until they expire, until undone | Undo on **Router learned**, or delete the file |
| `semantic-router.evaluation.jsonl` | Prompts you labelled, in full | Until you remove them | Edit or delete the file (the **Evaluate** page) |
| `semantic-router.secrets.json` | Keys typed into the dashboard, encrypted | Until removed from the configuration | Remove the key on the dashboard |
| Logs (console, `logs/` for the Windows service) | Routing decisions and errors, with keys redacted. No prompt text, but a CLI run that fails logs the start of what it wrote, which can quote the model's reply | As your log setup keeps them | Delete the files |

**Prompt previews.** `State:StorePrompts` decides how much of each prompt the activity log keeps:

- `Redacted` (default): the first 160 characters, with API keys, tokens and private keys masked as `***` (the patterns `LocalOnlyWhen: Secrets` looks for).
- `On`: the first 160 characters as they are.
- `Off`: none. The activity page, its exports and the live view show no prompt text, and automatic tuning learns no utterances from activity.

The setting applies to new requests; delete earlier activity to remove previews already stored. The database is opened with `secure_delete` and its write-ahead log is truncated after each prune, so deleted rows are overwritten rather than left in free pages. A purge also compacts the file (`VACUUM`). The evaluation set is not affected: it holds what you chose to label.

On Linux and macOS the configuration directory is kept at mode 0700 and the files the router writes in it at 0600, since they hold prompt previews and configuration; Windows profile folders are private already.

Precedence, lowest first: the built-in template, `appsettings.json` in the configuration directory, `SemanticRouter__…` environment variables, command-line settings. A saved `semantic-router.json` then replaces the routing document as a whole. Secrets it leaves empty are still inherited from the layers below, so `SemanticRouter__Connectors__openai__ApiKey` keeps working.

## Listening beyond this machine

The tool binds to `http://localhost:5080`. With `--urls http://0.0.0.0:5080` (or any non-loopback address) it prints what that changes:

- **Subscription CLI connectors still answer only local requests.** Remote callers fall through to API-key or local models ([TERMS.md](TERMS.md)).
- **The dashboard refuses remote callers** until you set `SemanticRouter:Dashboard:AdminToken` and `RequireLocalClient: false` ([dashboard.md](dashboard.md#access-and-security)).
- **The gateway refuses other machines** with `401` until you set `SemanticRouter:Middleware:GatewayKeys`. Each client then sends its key as its API key (`ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY`), and its name shows in Activity. Without this, anyone who can reach the port could spend your API keys. Clients on this machine need no key unless `RequireGatewayKeyForLocalClients` is on.

`semantic-router doctor` reports the same.

`Middleware:AllowUnauthenticatedRemoteClients: true` serves other machines without a key while none is configured. Use it only behind a reverse proxy that authenticates every request; the router logs a warning at start-up. Once any gateway key is set, it has no effect.

### Serving over HTTPS

Gateway keys, dashboard tokens and prompts cross the network in clear text over `http://`. For anything beyond this machine, put HTTPS in front:

- **Kestrel directly.** Give it a certificate in `appsettings.json` in the configuration directory, and listen with `--urls https://0.0.0.0:5443`:

  ```json
  {
    "Kestrel": {
      "Certificates": {
        "Default": { "Path": "/etc/semantic-router/router.pfx", "Password": "${env:ROUTER_CERT_PASSWORD}" }
      }
    }
  }
  ```

  `Path` and `KeyPath` also take PEM files. See [Configure endpoints for Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints).

- **A reverse proxy** (Caddy, nginx, Traefik) that terminates TLS and forwards to the router. Name the proxy in `appsettings.json`, so the router takes the client's address and scheme from its `X-Forwarded-For` and `X-Forwarded-Proto` headers:

  ```json
  { "SemanticRouter": { "ForwardedHeaders": { "KnownProxies": [ "127.0.0.1" ], "KnownNetworks": [ "10.0.0.0/8" ] } } }
  ```

  Only these proxies are believed; the headers from anyone else are ignored. Without this, every request arrives from the proxy, so clients share one rate limit, and a proxy on this machine makes them all look local: then turn on `RequireGatewayKeyForLocalClients` (and `RequireTokenForLocalClients` for the dashboard). Add the proxy's public address to `Dashboard:PublicOrigins`. With Caddy:

  ```
  router.example.com {
      reverse_proxy localhost:5080
  }
  ```

## Container

The image runs the same host for API-key connectors and local model servers.

```bash
ROUTER_ADMIN_TOKEN=$(openssl rand -hex 24) ROUTER_GATEWAY_KEY=$(openssl rand -hex 24) OPENAI_API_KEY=sk-... \
  docker compose -f deploy/compose.yaml up
```

- It listens on port `8080` and keeps the configuration, the encrypted secrets, the data-protection keys and the state database on the `/data` volume.
- The compose example publishes the port on `127.0.0.1` only.
- Requests that come in through the port mapping arrive from the container network, so the router treats them as remote. The dashboard asks for `ROUTER_ADMIN_TOKEN` and the gateway for `ROUTER_GATEWAY_KEY` (clients send it as `ANTHROPIC_AUTH_TOKEN` or `OPENAI_API_KEY`); without them, everyone is refused.
- With `docker run`, publish the port as `-p 127.0.0.1:5080:8080`. Plain `-p 5080:8080` opens it to every network the machine is on.
- The image is Ubuntu chiseled: no shell or package manager, so `docker exec … sh` does not work. Use `docker cp` to read or replace files on the volume, and `docker logs` for the log. Base images are pinned by digest and scanned before each release.
- Keep API keys in the environment and refer to them from the configuration: `"ApiKey": "${env:OPENAI_API_KEY}"`.
- A model server on the host machine is `http://host.docker.internal:1234` (LM Studio) or `:11434` (Ollama).
- Behind a proxy that inspects TLS, mount its root certificate and set `SemanticRouter__Network__CaBundle` to it; the image cannot run `update-ca-certificates`. See [network.md](network.md#proxies-and-certificates).

**Subscription CLI connectors do not work in a container.** The image has no vendor CLIs, and the CLIs' logins on your machine must not be copied or mounted into one: the router never handles vendor credentials ([TERMS.md](TERMS.md), rule 2). Run the dotnet tool on the machine itself for those.

Images are published as `ghcr.io/siebe41/semantic-router:<version>` when a version tag is pushed. To build one yourself: `docker build -f src/SemanticRouter.Host/Dockerfile -t semantic-router .`
