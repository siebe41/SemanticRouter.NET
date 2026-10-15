# Network traffic

What the router contacts, when, and how to turn it off. The router has no telemetry, crash reporting or analytics. Every outbound connection is one of the destinations below, and most of them only exist because you configured them.

## Destinations

| What | Destination | When | How to turn it off |
|---|---|---|---|
| **Connectors** | The `BaseAddress` of each HTTP connector (`api.anthropic.com`, `api.openai.com`, a local model server, …) | A request is routed to one of its models, the dashboard checks or discovers models, or `doctor` runs | Disable or remove the connector, or its models |
| **Embeddings** | `Embeddings:Endpoint` when the provider is `OpenAICompatible` | Every classified prompt, and route warm-up | Use `Onnx`, or a server on this machine |
| **ONNX model download** | `huggingface.co` and its download hosts | Once, on the first start with `Embeddings:Provider: Onnx` and no model in `models/{name}` | Copy a `models/{name}` directory into the configuration directory (or set `ModelPath`); the download is then skipped. The files are pinned to a revision and checked by SHA-256 |
| **The answer judge** | One of your connectors (`Middleware:Judge:Model`) | A sampled share of cheap answers, when `Judge:Enabled` is on (off by default) | Leave `Judge:Enabled` off |
| **Webhooks** | Each `Notifications:Webhooks` URL | A notice appears or clears, and the weekly digest | Remove the webhook |
| **Subscription CLIs** (`claude`, `codex`, `copilot`) and the Copilot SDK connector | Their vendor's API, from the vendor's own process | A request is routed to one of their models, or `doctor` runs | Disable the connector. The router starts Claude Code with `DISABLE_AUTOUPDATER=1` and `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` and Copilot with `--no-auto-update`, so a run makes no update, telemetry or error-reporting requests. Codex is started with `-c check_for_update_on_startup=false -c analytics.enabled=false -c otel.metrics_exporter="none"` (and `--ignore-user-config`), so it checks for no update and sends OpenAI no analytics or metrics |
| **Desktop app updates** | `api.github.com` and GitHub's release download hosts | At start and every 12 hours, when installed with the installer | Untick **Check for updates** in the tray menu; for a whole machine, `Updates:Mode` `Off` or an internal feed in the [machine policy](enterprise.md) |
| **`init`** | `localhost:1234` (LM Studio), `localhost:11434` (Ollama) | When you run `semantic-router init` | Nothing leaves this machine |

Nothing else is contacted:

- **The dashboard** loads its scripts, styles and fonts from the router itself and sends `Content-Security-Policy: default-src 'self'; …`, so the browser refuses requests to any other host. The link to the project on GitHub is only followed when you click it.
- **The desktop app** shows the dashboard from the router it runs, and reads the weekly digest from it for the tray.
- **Building from source** sends no Avalonia build telemetry: `Directory.Build.targets` replaces its telemetry target. The .NET SDK has its own; set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to turn it off (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

## Proxies and certificates

HTTP connectors, embeddings, webhooks and the ONNX download use .NET's `HttpClient`. It honours `HTTPS_PROXY`, `HTTP_PROXY` and `NO_PROXY` (and the system proxy on Windows and macOS), and trusts the operating system's certificate store.

**A corporate root certificate.** A proxy that inspects TLS signs every site with its own root. When that root is not in the operating system's store (common in containers), point `SemanticRouter:Network:CaBundle` at a PEM file with it:

```json
{ "SemanticRouter": { "Network": { "CaBundle": "/etc/semantic-router/corporate-root.pem" } } }
```

- The certificates in the file are trusted **in addition to** the system store, for the router's own connections only: connectors, the embeddings endpoint, webhooks and the ONNX download. A certificate must still match the host name and chain to a trusted root; nothing turns validation off.
- It is a host setting, read at start-up: put it in `appsettings.json` in the configuration directory, the environment (`SemanticRouter__Network__CaBundle`) or the command line (`--SemanticRouter:Network:CaBundle=…`). A relative path is resolved against the configuration directory.
- A file that is missing or holds no certificate stops the router at start-up, and `doctor` names it. `doctor` also prints how many certificates it added.
- The desktop app's updater does not use it; it trusts the system store.

The container image is Ubuntu chiseled, with no shell or package manager, so `update-ca-certificates` cannot run in it. Mount the PEM file and set the option:

```bash
docker run -v ./corporate-root.pem:/etc/semantic-router/corporate-root.pem:ro \
  -e SemanticRouter__Network__CaBundle=/etc/semantic-router/corporate-root.pem … semantic-router
```

On Linux, .NET also reads `SSL_CERT_FILE` (a bundle) and `SSL_CERT_DIR`, but these **replace** the system store instead of adding to it: a file with only the corporate root then fails every site the proxy does not inspect. Use `CaBundle`, or give `SSL_CERT_FILE` a file with both the system's roots and yours.

**The CLIs** are separate programs: `CaBundle` does not reach them. They inherit the router's environment (minus the variables the preset removes), so the same proxy variables reach them, and so do their own certificate settings: `NODE_EXTRA_CA_CERTS` for Claude Code and the Copilot CLI (Node.js programs; it adds to their built-in roots) and `CODEX_CA_CERTIFICATE` for Codex. Set them in the router's environment, or per connector in `EnvironmentVariables`, e.g. `"EnvironmentVariables": { "NODE_EXTRA_CA_CERTS": "/etc/semantic-router/corporate-root.pem" }`.

## Allowing it through a firewall

For an egress allowlist, list:

1. the hosts of the connectors you enabled;
2. `huggingface.co` for the first start with ONNX embeddings, or pre-seed `models/` and leave it out;
3. your webhook hosts;
4. for subscription CLIs, the hosts their vendors document;
5. for desktop updates, `api.github.com`, `github.com` and `objects.githubusercontent.com`.

`semantic-router doctor` prints this list for the current configuration, one `egress` line per destination:

```
[ OK ] egress: connector lmstudio: localhost:1234 (this machine).
[ OK ] egress: connector openai: api.openai.com.
[ OK ] egress: connector claude: CLI: claude, which contacts Anthropic.
[ OK ] egress: embeddings: huggingface.co, once, to download ONNX model all-MiniLM-L6-v2 into /data/models/all-MiniLM-L6-v2 (copy it there to skip the download).
[ OK ] egress: webhook ops: hooks.slack.com.
[ OK ] egress: desktop app updates: api.github.com, github.com and objects.githubusercontent.com (only the installed desktop app; untick Check for updates in its tray menu to turn this off).
```

Connectors and webhooks show their host and port only: a webhook URL usually carries its token, so its path and query are never printed. The embeddings line names the download host only while the model is not in the models directory yet. Then `doctor` checks each enabled connector and the embeddings, so it fails on any host the allowlist is missing.
