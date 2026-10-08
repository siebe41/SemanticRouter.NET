# The `semantic-router` tool and container

`SemanticRouter.Host` is the router as an application: the OpenAI and Anthropic front doors, the dashboard, hot reload and the configuration file, with nothing to clone or compile. It ships as a .NET tool and as a container image.

## Install and start

Prerequisite: the [.NET 9 runtime or SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet tool install -g SemanticRouter.Host
semantic-router init      # write a configuration with the models found on this machine
semantic-router doctor    # check every connector, model and the embeddings
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
| `semantic-router init [--force] [--no-detect]` | Writes a starting configuration. It probes LM Studio (`localhost:1234`) and Ollama (`localhost:11434`) for chat and embedding models and looks for `claude`, `codex` and `copilot` on `PATH`. It refuses to overwrite an existing file without `--force`. |
| `semantic-router doctor` | Loads the configuration exactly as `run` would and checks it without calling a model. It flags unresolved `${env:…}` references, tests the embeddings, and checks each HTTP connector (reachable, credentials accepted, configured model ids offered) and each CLI connector (installed, starts and, for Claude Code, signed in). Every failure names the setting to change. Exit code 1 when anything failed. |
| `semantic-router --version`, `--help` | |

Options: `--config-dir <dir>`, `--urls <urls>`, and any setting as `--SemanticRouter:<key>=<value>` (the same keys as the `SemanticRouter__<key>` environment variables).

## What `init` adds

| Found | Added |
|---|---|
| LM Studio or Ollama with chat models | A connector; a tier-1 local model (the first non-coder chat model) and, when one is loaded, a tier-2 `-coder` model. |
| An embedding model on either server | `Embeddings` switched to it, with a starting similarity threshold for that model family. |
| A server that answers `401` | The connector, plus a note to set its `ApiKey` (for example `"${env:LM_API_TOKEN}"`). |
| `claude` on PATH | `claude-cli` with prompted tool calling, `claude` (sonnet, tier 4) and `claude-opus` (tier 5). |
| `codex` on PATH | `codex-cli` (concurrency 1) and a tier-4 `codex` model. |
| `copilot` on PATH | `copilot-cli` and a tier-4 `copilot` model, **disabled**: it spends your plan's premium requests, so you turn it on. |

On Windows only the native `.exe` counts. An npm `.cmd` shim is reported and not used, because the router never starts a CLI through `cmd.exe`. Everything else (API-key providers, tiers, routes) is edited on the dashboard.

## Where things live

| | Default |
|---|---|
| Configuration directory | `SEMANTIC_ROUTER_HOME` if set; otherwise `%APPDATA%\semantic-router` (Windows), `~/Library/Application Support/semantic-router` (macOS) or `$XDG_CONFIG_HOME/semantic-router`, default `~/.config/semantic-router` (Linux). |
| `semantic-router.json` | The configuration. The dashboard's **Apply & save** writes it. |
| `semantic-router.secrets.json` | Keys typed into the dashboard, encrypted with ASP.NET Core Data Protection. See [dashboard.md](dashboard.md#keeping-secrets-out-of-the-file). |
| `appsettings.json` (optional) | Host settings: logging, Kestrel, `SemanticRouter:Dashboard`, overrides. |

Precedence, lowest first: the built-in template, `appsettings.json` in the configuration directory, `SemanticRouter__…` environment variables, command-line settings. A saved `semantic-router.json` then replaces the routing document as a whole. Secrets it leaves empty are still inherited from the layers below, so `SemanticRouter__Connectors__openai__ApiKey` keeps working.

## Listening beyond this machine

The tool binds to `http://localhost:5080`. With `--urls http://0.0.0.0:5080` (or any non-loopback address) it prints what that changes:

- **Subscription CLI connectors still answer only local requests.** Remote callers fall through to API-key or local models ([TERMS.md](TERMS.md)).
- **The dashboard refuses remote callers** until you set `SemanticRouter:Dashboard:AdminToken` and `RequireLocalClient: false` ([dashboard.md](dashboard.md#access-and-security)).
- **Clients cannot be told apart** until you set `SemanticRouter:Middleware:GatewayKeys`.

`semantic-router doctor` reports the same.

## Container

The image runs the same host for API-key connectors and local model servers.

```bash
ROUTER_ADMIN_TOKEN=$(openssl rand -hex 24) OPENAI_API_KEY=sk-... docker compose -f deploy/compose.yaml up
```

- It listens on port `8080` and keeps the configuration, the encrypted secrets and the data-protection keys on the `/data` volume.
- The compose example publishes the port on `127.0.0.1` only.
- Requests that come in through the port mapping arrive from the container network, so the dashboard treats them as remote. It asks for `ROUTER_ADMIN_TOKEN`, and without one it refuses everyone.
- Keep API keys in the environment and refer to them from the configuration: `"ApiKey": "${env:OPENAI_API_KEY}"`.
- A model server on the host machine is `http://host.docker.internal:1234` (LM Studio) or `:11434` (Ollama).

**Subscription CLI connectors do not work in a container.** The image has no vendor CLIs, and the CLIs' logins on your machine must not be copied or mounted into one: the router never handles vendor credentials ([TERMS.md](TERMS.md), rule 2). Run the dotnet tool on the machine itself for those.

Images are published as `ghcr.io/siebe41/semantic-router:<version>` when a version tag is pushed. To build one yourself: `docker build -f src/SemanticRouter.Host/Dockerfile -t semantic-router .`
