# Security policy

## Supported versions

Security fixes go to `main` and into the next release. Only the latest release is supported; there are no back-ported fixes for older versions. If you build from source, use the latest `main`.

| Version | Supported |
|---|---|
| `main` | yes |
| Latest release (NuGet packages, `semantic-router` tool, container image, desktop app) | yes |
| Anything older | no |

## Verifying a release

Every release asset has a build provenance attestation and a line in `SHA256SUMS`; the container image is signed with keyless cosign and has provenance and SBOM attestations. CycloneDX SBOMs and `THIRD-PARTY-NOTICES.txt` are attached to each release. [docs/releases.md](docs/releases.md) has the commands (`gh attestation verify`, `sha256sum -c SHA256SUMS`, `cosign verify`). A file or image that fails these checks did not come from this repository's release workflow; please report where you got it.

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for a security problem.

Report it privately through GitHub: on the repository's **Security** tab, choose **Report a vulnerability**. This opens a private advisory that only you and the maintainers can see. Include:

- what is affected (package or component, version or commit);
- how to reproduce it, with the configuration involved (replace real keys with placeholders);
- what an attacker gains, and from where (another process on the same machine, a web page in a browser, another machine on the network).

Reports are handled on a best-effort basis; there is no guaranteed response time. Replies come in the advisory. Once a fix is released, the advisory is published, with credit to the reporter unless you ask otherwise.

## Scope

The router sits between your clients and models you pay for, holds the keys for them, and can start your signed-in vendor CLIs. Reports about these parts are in scope:

- **Credential handling.** Connector API keys, header values, embedding keys, CLI environment variables and dashboard tokens: a key that reaches the browser (it should be masked as `***`), a log, an activity entry, an error detail, a relayed upstream error or a backend it was not configured for.
- **Secret references and the secret file.** `${env:…}` and `${secret:…}` resolution, and `UseSecretFile()` (values encrypted with ASP.NET Core Data Protection into `{config name}.secrets.json`): a typed key written to the configuration file in plain text while a secret store is registered, or a secret file that can be decrypted without the user's data-protection keys.
- **Dashboard and admin API access control.** Loopback-only by default, the `Host` header check that guards against DNS rebinding, `AdminToken` and `ReadOnlyToken` (including the session cookie), and the same-origin check on writes: any way to read the configuration, change routing or apply a configuration without the access these settings require. See [docs/dashboard.md](docs/dashboard.md#access-and-security).
- **Gateway keys.** `Middleware:GatewayKeys`: a remote request served without a valid key, a key that is logged or forwarded to a backend, or a timing difference that reveals a key.
- **CLI connector process spawning.** Subscription CLI connectors start `claude`, `copilot` or `codex` as child processes with an argument list, a scrubbed environment and an empty working directory in a parent only the user can write to, and refuse `.cmd` shims. Prompt text that becomes extra arguments or shell commands, an API key or other variable leaking into the child, or a remote client reaching a CLI connector while `AllowRemoteClients` is off.
- **The LocalOnly guarantee.** A route, routing profile or `LocalOnlyWhen` signal marked local-only must only be served by models marked `IsLocal`, including on failover. A local-only prompt that reaches a non-local model is a security issue.
- **Stored prompts.** The activity log and the SQLite state database (`semantic-router.db`) hold prompt previews, with credentials masked unless `State:StorePrompts` is `On`; the evaluation set export leaves local-only prompts out unless asked. A way to read these without dashboard access, prompt text stored with `StorePrompts: Off`, or purged activity that can still be read from the database files is in scope.

Out of scope:

- Vulnerabilities in the vendor CLIs, model servers or cloud APIs themselves; report those to the vendor.
- What a model says, or a prompt injection that only changes a model's answer.
- Setups the documentation describes as trusted, such as `AllowUnauthenticatedRemoteClients: true` without an authenticating proxy, or keys written in plain text to `appsettings.json`.
- Questions about whether using a subscription CLI through the router fits a vendor's terms. These are not security issues; see [docs/TERMS.md](docs/TERMS.md) and open a normal issue if something there is wrong or out of date.

## If a key was exposed

Revoke it at the provider first. Removing a key from a file, or from the router's configuration, does not remove it from git history, logs or backups.
