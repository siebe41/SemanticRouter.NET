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
| **Subscription CLIs** (`claude`, `codex`, `copilot`) and the Copilot SDK connector | Their vendor's API, from the vendor's own process | A request is routed to one of their models, or `doctor` runs | Disable the connector. The router starts Claude Code with `DISABLE_AUTOUPDATER=1` and `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` and Copilot with `--no-auto-update`, so a run makes no update, telemetry or error-reporting requests. Codex is started as configured |
| **Desktop app updates** | `api.github.com` and GitHub's release download hosts | At start and every 12 hours, when installed with the installer | Untick **Check for updates** in the tray menu |
| **`init`** | `localhost:1234` (LM Studio), `localhost:11434` (Ollama) | When you run `semantic-router init` | Nothing leaves this machine |

Nothing else is contacted:

- **The dashboard** loads its scripts, styles and fonts from the router itself and sends `Content-Security-Policy: default-src 'self'; …`, so the browser refuses requests to any other host. The link to the project on GitHub is only followed when you click it.
- **The desktop app** shows the dashboard from the router it runs, and reads the weekly digest from it for the tray.
- **Building from source** sends no Avalonia build telemetry: `Directory.Build.targets` replaces its telemetry target. The .NET SDK has its own; set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to turn it off (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

## Proxies and certificates

HTTP connectors, embeddings, webhooks and the ONNX download use .NET's `HttpClient`. It honours `HTTPS_PROXY`, `HTTP_PROXY` and `NO_PROXY` (and the system proxy on Windows and macOS), and trusts the operating system's certificate store. In a container, add a corporate root certificate to the image's store (`/usr/local/share/ca-certificates`, then `update-ca-certificates`).

The CLIs inherit the router's environment (minus the variables the preset removes), so the same proxy variables reach them.

## Allowing it through a firewall

For an egress allowlist, list:

1. the hosts of the connectors you enabled;
2. `huggingface.co` for the first start with ONNX embeddings, or pre-seed `models/` and leave it out;
3. your webhook hosts;
4. for subscription CLIs, the hosts their vendors document;
5. for desktop updates, `api.github.com`, `github.com` and `objects.githubusercontent.com`.

`semantic-router doctor` checks each enabled connector and the embeddings, so it fails on any host the allowlist is missing.
