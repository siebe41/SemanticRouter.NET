# The `semantic-router` tool and container

`SemanticRouter.Host` is the router as an application: the OpenAI and Anthropic front doors, the dashboard, hot reload and the configuration file, with nothing to clone or compile. It ships as a .NET tool and as a container image.

## Install and start

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0): `dotnet tool install` is an SDK command, so the runtime alone is not enough to install it. No .NET at all? The [desktop app](desktop.md) brings its own.

```bash
dotnet tool install -g SemanticRouter.Host
semantic-router init      # write a configuration with the models found on this machine
semantic-router doctor    # check every connector, model and the embeddings
semantic-router models    # what each connector offers: loaded or not, suggested tiers
semantic-router           # start: http://localhost:5080, dashboard at /router
```

On start it says where it listens and how to try it:

```text
SemanticRouter 0.3.0 is listening on http://localhost:5080
  Configuration  /home/ada/.config/semantic-router/semantic-router.json
  Dashboard      http://localhost:5080/router

  Try it
    curl http://localhost:5080/v1/chat/completions -H 'Content-Type: application/json' -d '{"model":"auto","messages":[{"role":"user","content":"Say hello in five words"}]}'
    Watch it: http://localhost:5080/router#/live
  Claude Code
    ANTHROPIC_BASE_URL=http://localhost:5080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=131072 CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router
  OpenAI clients: base URL http://localhost:5080/v1, model "auto"
```

The Claude Code line sets its variables for that one command, so it changes nothing else in your shell. `CLAUDE_CODE_MAX_CONTEXT_TOKENS` is the smallest context window among your enabled tool-capable models; the line leaves it out when one of them has no `ContextWindow`. On Windows the lines use PowerShell (`Invoke-RestMethod`, and `$env:` variables for the session). [clients.md](clients.md#claude-code) explains each variable, and the Codex and SDK setups.

While no model is enabled, the banner says so instead, with a link to the setup on the dashboard's Overview (`http://localhost:5080/router#/overview`): it lists the CLIs and local servers it finds and the providers you can add, and adds them for you.

The console shows the router's own lines: one per request (the tier it needed, the route and the model it chose) and its warnings. ASP.NET Core's lines per request and its start-up notes are left out (`Microsoft.AspNetCore` and `Microsoft.Hosting.Lifetime` log from `Warning`, data protection from `Error`); set `Logging:LogLevel` in `appsettings.json`, or `--Logging:LogLevel:Microsoft.AspNetCore=Information`, to see them again.

**When the address is taken**, the router says so and stops (exit code 3). If the router listening there is the one for this configuration directory, it says that, with its process id and dashboard address; otherwise another program holds the port, and `--urls http://localhost:5081` picks another.

## Commands

| Command | What it does |
|---|---|
| `semantic-router` (or `run`) | Starts the router and the dashboard. Creates the configuration file from the built-in template on first run (no models yet). |
| `semantic-router init [--force] [--no-detect] [--yes \| --no-cli]` | Writes a starting configuration. It probes LM Studio (`localhost:1234`) and Ollama (`localhost:11434`) for chat and embedding models and looks for `claude`, `codex` and `copilot` on `PATH`, asking before it enables a subscription CLI (see [What `init` adds](#what-init-adds)). `--yes` enables them without asking; `--no-cli` adds no subscription CLI connector. A file that still holds the built-in template (as the router writes it on its first start, or an `init` that found nothing left it) is replaced; any other file only with `--force`, including a template whose embeddings someone changed. |
| `semantic-router doctor` | Loads the configuration exactly as `run` would and checks it without calling a model. It prints the [machine-wide policy](enterprise.md), if an administrator set one, flags unresolved `${env:…}` references, tests the embeddings, and checks each HTTP connector (reachable, credentials accepted, configured model ids offered) and each CLI connector (installed, starts and, for Claude Code and Codex, signed in). No enabled model is a failure. Every failure says what to do: start LM Studio's server, the setting to change and where it is on the dashboard (for example *Providers → Connectors → openai → API key*), how to sign in. It also lists every host the router will contact with this configuration, for a firewall allowlist ([network.md](network.md#allowing-it-through-a-firewall)). Exit code 1 when anything failed. |
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
| An embedding model on either server | `Embeddings` switched to it, with a starting similarity threshold for that model family (calibrate it later on the **Evaluate** page). |
| No embedding server | `Embeddings` set to `Onnx` with `all-MiniLM-L6-v2`: a 23 MB model that runs inside the router. It is downloaded on first start and checksum-verified. Threshold 0.40. |
| A server that answers `401` | The connector, plus a note to set its `ApiKey` (for example `"${env:LM_API_TOKEN}"`). |
| `claude` on PATH | `claude-cli` with prompted tool calling and Claude's lineup, one tier per family: `claude-haiku` (tier 2), `claude-sonnet` (3), `claude-opus` (4) and `claude-fable-5-1` (5, **disabled** until you enable it, because not every plan includes it). |
| `codex` on PATH | `codex-cli` (concurrency 1) and a tier-4 `codex` model. |
| `copilot` on PATH | `copilot-cli` and a tier-4 `copilot` model, **disabled**: it spends your plan's premium requests, so you turn it on. |

On Windows only the native `.exe` counts. An npm `.cmd` shim is reported and not used, because the router never starts a CLI through `cmd.exe`. Everything else (API-key providers, tiers, routes) is edited on the dashboard.

**Subscription CLIs are enabled only on a yes.** A CLI connector answers with whatever account that CLI is signed in to, which on a work machine may be a personal plan under consumer terms (see [Using it at work](TERMS.md#using-it-at-work)). So `init` lists the `claude` and `codex` it found and asks about each one (`[y/N]`, no by default):

| How `init` runs | `claude-cli` and `codex-cli` |
|---|---|
| At a terminal | Asked about one by one. Yes: added enabled, as in the table above. No: added with every model disabled, so you can turn them on later on the dashboard's **Models** page. When nothing else was found (no LM Studio or Ollama with a model), the question says that without a yes the router has no model to use. |
| Without a terminal (input or output redirected: a script, CI, an installer) | Added with every model disabled, with a note saying how to enable them. |
| `--yes` | Added enabled without asking. |
| `--no-cli` | Not added. Neither is `copilot-cli`. |

`copilot-cli` is never asked about: it is always added disabled, as above.

After writing the file, `init` prints what it added, with a one-line legend of the tiers, and the next step: with a model enabled, `doctor` and the one-line Claude Code command (as in the banner); with none, the dashboard's setup (`http://localhost:5080/router#/overview`), or starting LM Studio's server or Ollama and running `init` again.

A CLI that the [machine-wide policy](enterprise.md) turns off (`DisabledConnectorTypes` or `DisabledPresets`) is not added or asked about, even with `--yes`; the notes say which.

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
| `semantic-router.db`, table `activity` | One entry per request: time, client name, project label (the `X-Router-Project` header, or else a hash of the client's user id, never the id; none for Claude Code's JSON user id), models, tiers, tokens, cost, notes, and a **prompt preview** (see below) | 30 days or 10,000 entries (`State:Retention:ActivityDays`, `ActivityEntries`) | **Gateway → Settings → Stored activity → Delete all activity**, or `DELETE /router/api/activity?since=…&until=…` |
| `semantic-router.db`, table `feedback` | Retries, corrections, labels and grades per request: tiers and short notes, no prompt text | As activity | With the activity it belongs to ageing out |
| `semantic-router.db`, table `usage` | Tokens and cost per request, by model, connector, route, client and profile; no prompt text | 400 days (`State:Retention:UsageDays`) | Delete the file |
| `semantic-router.db`, table `config_history` | Every applied configuration, with secrets masked, and who applied it | 200 versions (`State:Retention:ConfigurationVersions`) | Delete the file |
| `semantic-router.db`, table `audit` | The [audit log](dashboard.md#audit-log): each change made through the dashboard or admin API, sign-in and sign-out, with when, who, the client's address, the request path and status, and a short summary; no request bodies, tokens or prompt text | 365 days (`State:Retention:AuditDays`); never changed, and not deleted by an activity purge or capped by the machine policy's `MaxRetentionDays` | Delete the file |
| `semantic-router.db`, tables `cooldowns`, `affinity`, `warm_caches`, `tuning_changes` | Model cooldowns, which model issued each tool call (ids only), the size of each conversation's prompt cache per model (conversation hashes only), learned routing changes (which can include short utterances learned from prompt previews) | Until they expire, 24 hours, until they expire, until undone | Undo on **Router learned**, or delete the file |
| `semantic-router.evaluation.jsonl` | Prompts you labelled, in full | Until you remove them | Edit or delete the file (the **Evaluate** page) |
| `semantic-router.secrets.json` | Keys typed into the dashboard, encrypted | Until removed from the configuration | Remove the key on the dashboard |
| The data-protection key ring (see [The key ring](#the-key-ring)) | The keys that decrypt `semantic-router.secrets.json`; a new one every 90 days | Kept, so older secrets stay readable | Only with the secrets file: without its keys, the file cannot be read |
| Logs (console, `logs/` for the Windows service) | Routing decisions and errors, with keys redacted. No prompt text. A CLI run that fails logs the start of what it wrote, which can quote the model's reply, only as `State:StorePrompts` allows (see below) | As your log setup keeps them | Delete the files |

**Prompt previews.** `State:StorePrompts` decides how much of each prompt the activity log keeps:

- `Redacted` (default): the first 160 characters, with API keys, tokens and private keys masked as `***` (the patterns `LocalOnlyWhen: Secrets` looks for).
- `On`: the first 160 characters as they are.
- `Off`: none. The activity page, its exports and the live view show no prompt text, and automatic tuning learns no utterances from activity.

The same setting covers text a model wrote about the prompt, which can quote it:

- **A failed CLI run's output.** When a CLI exits with an error, the activity entry's failover notes, the log line and the attempt span keep the start of its reply and error output: 200 characters in the activity entry, 1,000 in the log. `Redacted` masks credentials in it as above, `On` keeps it as it is, and `Off` keeps only the exit code (and that it was a usage limit), as `exit code 1: output omitted (StorePrompts: Off)`.
- **The judge's reason.** The [judge](routing-quality.md#the-judge)'s one-line reason for its grade is kept with the grade, masked with `Redacted`. With `Off` only the grade and the judge's name are kept.

The setting applies to new requests; delete earlier activity to remove previews already stored. The database is opened with `secure_delete` and its write-ahead log is truncated after each prune, so deleted rows are overwritten rather than left in free pages. A purge also compacts the file (`VACUUM`). The evaluation set is not affected: it holds what you chose to label.

On Linux and macOS the configuration directory is kept at mode 0700 and the files the router writes in it at 0600, since they hold prompt previews and configuration; Windows profile folders are private already.

Precedence, lowest first: the built-in template, `appsettings.json` in the configuration directory, `SemanticRouter__…` environment variables, command-line settings. A saved `semantic-router.json` then replaces the routing document as a whole. Secrets it leaves empty are still inherited from the layers below, so `SemanticRouter__Connectors__openai__ApiKey` keeps working.

### The key ring

`semantic-router.secrets.json` is encrypted with ASP.NET Core Data Protection. The keys that decrypt it, the key ring, are kept outside the configuration directory, so a copy of that directory alone carries no usable key. How they are protected depends on the platform:

| | Where | Protected by |
|---|---|---|
| Windows | `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` | DPAPI, for your Windows account (ASP.NET Core's default) |
| Windows, `SEMANTIC_ROUTER_KEYS_DIR` set | That folder | DPAPI, for your Windows account. ASP.NET Core leaves a folder set this way unencrypted; the router asks for DPAPI. |
| macOS, Linux | `~/.aspnet/DataProtection-Keys` (shared with other ASP.NET Core apps you run) | File permissions only: the router keeps the folder at 0700 and the key files at 0600. |
| Container | `/data/keys` (`SEMANTIC_ROUTER_KEYS_DIR`), on the same volume as the secrets | File permissions only |
| Any of these, with `SemanticRouter:DataProtection:Certificate` | As above | Encryption to the certificate |

Where only file permissions protect the keys, anyone who can read both the key ring and the secrets file can decrypt the keys you typed into the dashboard: a backup of your whole home directory, or a copy of the container's volume. `semantic-router doctor` says which case applies (the `key ring` line). With file permissions only it is a `[WARN]`, and the router logs a warning at start-up, once the secrets file holds a key; while it holds none there is nothing to give away, and the line is an `[ OK ]` that says to encrypt the key ring before you type a key into the dashboard (or to keep keys in the environment as `${env:…}` references).

**Encrypting the key ring with a certificate.** Set `SemanticRouter:DataProtection:Certificate` to an RSA certificate with its private key:

- a PKCS#12 file (`.pfx`, `.p12`), with its password in `CertificatePassword`;
- a PEM file with the certificate and its private key, or the certificate alone with the key file in `CertificateKey` (as Kubernetes mounts a TLS secret: `tls.crt` and `tls.key`); an encrypted key takes `CertificatePassword`;
- or the thumbprint of a certificate in the `CurrentUser\My` store (on Windows, your personal certificate store).

```json
{ "SemanticRouter": { "DataProtection": { "Certificate": "/run/secrets/keyring.pfx", "CertificatePassword": "${env:KEYRING_PASSWORD}" } } }
```

It is a host setting, read at start-up: put it in `appsettings.json` in the configuration directory (which the tool, its background service and the desktop app all read), the environment (`SemanticRouter__DataProtection__Certificate`) or the command line. A relative path is resolved against the configuration directory, and `CertificatePassword` can be an `${env:…}` reference. A certificate that cannot be loaded, has no private key or is not RSA stops the router with the setting named; `doctor` shows it as `[FAIL] configuration`. In a container, mount the certificate from your secret store (read-only) next to the volume rather than on it.

- **Existing secrets keep working.** ASP.NET Core reads each key as it was written, encrypted or not. At the next start the router adds a key encrypted to the certificate (or with DPAPI, for `SEMANTIC_ROUTER_KEYS_DIR` on Windows) and uses it from then on, and it re-encrypts each saved secret with that key when it reads it. The older keys stay in the folder, unencrypted, and `doctor` lists them. Once the router has started with the certificate they protect nothing it needs, and can be deleted, unless another ASP.NET Core app on your account uses the same folder.
- **Keep the certificate.** Keys encrypted to it can be read only with its private key. Replace or lose it, and the secrets saved under those keys can no longer be decrypted: the router then treats them as missing, and you type them again on the dashboard. Its expiry date is not checked.
- **Not the macOS Keychain or the Linux Secret Service.** .NET has no built-in access to either, and the packages that add it bring native dependencies or are not maintained. On a desktop, full-disk encryption (FileVault, BitLocker, LUKS) covers the key ring at rest; use a certificate where that is not enough.

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

  Only these proxies are believed; the headers from anyone else are ignored. Without this, every request arrives from the proxy, so clients share one rate limit, and a proxy on this machine makes them all look local: then turn on `RequireGatewayKeyForLocalClients` (and `RequireTokenForLocalClients` for the dashboard). Add the proxy's public address to `Dashboard:PublicOrigins`. A proxy that signs people in can also name them to the dashboard (`Dashboard:TrustedIdentityHeader`, believed only from these proxies); see [Signed-in users from a reverse proxy](dashboard.md#signed-in-users-from-a-reverse-proxy). With Caddy:

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

[deploy/compose.yaml](../deploy/compose.yaml) runs the published image, `ghcr.io/siebe41/semantic-router:<version>` (`SEMANTIC_ROUTER_VERSION` picks the version); a commented-out `build:` block builds it from your checkout instead.

**API keys become connectors.** On the first start, the one that creates the configuration file from the built-in template, an `OPENAI_API_KEY` or `ANTHROPIC_API_KEY` in the container's environment gets a connector (`openai`, `anthropic`) that refers to it as `${env:OPENAI_API_KEY}`: the key stays in the environment, never in the file. Then open the dashboard's Overview (or **Providers → Connectors → Discover models**) to choose which of its models to use. The image turns this on with `SEMANTIC_ROUTER_SEED_API_KEYS=true`; the tool leaves it off, since a key in your shell is no reason to spend it. A key added after that start is not picked up, and a seeded connector you delete stays deleted: add connectors on the dashboard.

- It listens on port `8080` and keeps the configuration, the encrypted secrets, the data-protection keys and the state database on the `/data` volume. The keys are not encrypted unless you mount a certificate and set `SemanticRouter__DataProtection__Certificate` (see [The key ring](#the-key-ring)); otherwise protect the volume as you would the secrets themselves.
- The compose example publishes the port on `127.0.0.1` only.
- Requests that come in through the port mapping arrive from the container network, so the router treats them as remote. The dashboard asks for `ROUTER_ADMIN_TOKEN` and the gateway for `ROUTER_GATEWAY_KEY` (clients send it as `ANTHROPIC_AUTH_TOKEN` or `OPENAI_API_KEY`); without them, everyone is refused.
- With `docker run`, publish the port as `-p 127.0.0.1:5080:8080`. Plain `-p 5080:8080` opens it to every network the machine is on.
- The image is Ubuntu chiseled: no shell or package manager, so `docker exec … sh` does not work. Use `docker cp` to read or replace files on the volume, and `docker logs` for the log. Base images are pinned by digest and scanned before each release.
- Keep API keys in the environment and refer to them from the configuration: `"ApiKey": "${env:OPENAI_API_KEY}"`.
- A model server on the host machine is `http://host.docker.internal:1234` (LM Studio) or `:11434` (Ollama).
- Behind a proxy that inspects TLS, mount its root certificate and set `SemanticRouter__Network__CaBundle` to it; the image cannot run `update-ca-certificates`. See [network.md](network.md#proxies-and-certificates).

**Subscription CLI connectors do not work in a container.** The image has no vendor CLIs, and the CLIs' logins on your machine must not be copied or mounted into one: the router never handles vendor credentials ([TERMS.md](TERMS.md), rule 2). Run the dotnet tool on the machine itself for those.

Images are published as `ghcr.io/siebe41/semantic-router:<version>` when a version tag is pushed, signed with keyless cosign and with provenance and SBOM attestations: [releases.md](releases.md#check-the-container-image) shows how to verify one. To build one yourself: `docker build -f src/SemanticRouter.Host/Dockerfile -t semantic-router .`
