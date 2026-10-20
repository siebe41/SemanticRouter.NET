# Security policy

<!-- Maintainers: this policy and the "Report a security vulnerability" link in .github/ISSUE_TEMPLATE/config.yml only work while private vulnerability reporting is switched on (Settings > Code security > Private vulnerability reporting). It is a repository setting, so nothing in the code keeps it on. Check it with `gh api repos/siebe41/SemanticRouter.NET/private-vulnerability-reporting`, which should answer {"enabled":true}. -->

## Supported versions

<!-- owner: confirm the support policy -->
Only the latest minor release gets security fixes. A fix goes to `main` and ships in a new release: a patch release of the latest minor version when `main` has nothing else ready, otherwise the next minor version. Older minor versions get no fixes and nothing is back-ported, so upgrade to the release that has the fix. If you build from source, use the latest `main`.

| Version | Supported |
|---|---|
| `main` | yes |
| The latest minor release, newest patch (NuGet packages, `semantic-router` tool, container image, desktop app) | yes |
| Older minor releases, and older patches of the latest one | no |

The security fixes in each release are listed under **Security** in the [changelog](CHANGELOG.md).

### Before and after 1.0

Versions follow [Semantic Versioning](https://semver.org/). Until 1.0.0 they are 0.x, and a minor version can break the .NET API of the packages, the configuration, the HTTP endpoints or behaviour; its changelog lines say what broke. From 1.0.0 on, a break waits for the next major version. The .NET API of the library packages is checked against the latest 1.x release on every pull request ([Public API and package validation](CONTRIBUTING.md#public-api-and-package-validation)); the configuration, the HTTP endpoints and behaviour are held to the same rule in review. <!-- owner: confirm the exception --> The exception is a security fix that can only close a hole by changing a default or refusing something that used to work: it ships in the next minor or patch release, and its line under **Security** in the changelog says what to change.

## Verifying a release

Every release asset has a build provenance attestation and a line in `SHA256SUMS`; the container image is signed with keyless cosign and has provenance and SBOM attestations. CycloneDX SBOMs and `THIRD-PARTY-NOTICES.txt` are attached to each release. [Verifying a release](docs/releases.md) has the commands (`gh attestation verify`, `sha256sum -c SHA256SUMS`, `cosign verify`). A file or image that fails these checks did not come from this repository's release workflow; please report where you got it.

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for a security problem.

Report it privately through GitHub's private vulnerability reporting: on the repository's **Security** tab, choose **Report a vulnerability**. This opens a private advisory that only you and the maintainers can see. If you cannot use GitHub, email `<<ANDREW: address>>` instead. <!-- owner: replace the marker with a contact address, or delete this sentence --> If neither works for you, open a public issue that only asks for a private contact, with nothing about the problem in it: the maintainer opens a private advisory and adds you to it. Include:

- what is affected (package or component, version or commit);
- how to reproduce it, with the configuration involved (replace real keys with placeholders);
- what an attacker gains, and from where (another process on the same machine, a web page in a browser, another machine on the network).

Replies come in the advisory. Once a fix is released, the advisory is published, with credit to the reporter unless you ask otherwise.

## Response targets

<!-- owner: confirm these numbers -->
The project has one maintainer ([Governance](GOVERNANCE.md)), so these are targets, not guarantees. Business days are Monday to Friday, excluding public holidays.

| Step | Target |
|---|---|
| Acknowledge the report | within 5 business days |
| Initial assessment: whether it is in scope, and how severe it is | within 10 business days |
| Fix or mitigation released, critical or high severity | within 30 days of the assessment |
| Fix or mitigation released, medium or low severity | in the next planned release |

Severity follows [CVSS](https://www.first.org/cvss/). When a fix takes longer than its target, the advisory says why and when to expect it, and gives a workaround when there is one, such as a setting or a machine policy that closes the hole. The advisory is published when the fix is released; if you plan to disclose on your own, please give the fix time to ship first.

## Scope

The router sits between your clients and models you pay for, holds the keys for them, and can start your signed-in vendor CLIs. Reports about these parts are in scope:

- **Credential handling.** Connector API keys, header values, embedding keys, CLI environment variables and dashboard tokens: a key that reaches the browser (it should be masked as `***`), a log, an activity entry, an error detail, a relayed upstream error or a backend it was not configured for.
- **Secret references and the secret file.** `${env:…}` and `${secret:…}` resolution, and `UseSecretFile()` (values encrypted with ASP.NET Core Data Protection into `{config name}.secrets.json`): a typed key written to the configuration file in plain text while a secret store is registered, a secret file that can be decrypted without the user's data-protection keys, or keys written unencrypted while DPAPI or `DataProtection:Certificate` applies ([The key ring](docs/host.md#the-key-ring) says when they are not encrypted).
- **Dashboard and admin API access control.** Loopback-only by default (and only for the account the router runs as, unless `Network:TrustOtherLocalAccounts`), the `Host` header check that guards against DNS rebinding, `AdminToken`, `ReadOnlyToken` and `MetricsToken` (including the session cookie, and signing out, which must end the session on the server), the same-origin check on writes, and `TrustedIdentityHeader` (which must only be believed from a known proxy): any way to read the configuration, change routing or apply a configuration without the access these settings require, to act under another user's name, or for the `Metrics` role to see prompt or reply text, utterances, the configuration or memory. See [Access and security](docs/dashboard.md#access-and-security).
- **The audit log.** A change through the dashboard or admin API that is not recorded, a record naming the wrong actor, or a request body, token or prompt text that ends up in it.
- **Gateway keys.** `Middleware:GatewayKeys`: a remote request served without a valid key, a key that is logged or forwarded to a backend, or a timing difference that reveals a key.
- **CLI connector process spawning.** Subscription CLI connectors start `claude`, `copilot` or `codex` as child processes with an argument list, a scrubbed environment and an empty working directory in a parent only the user can write to, and refuse `.cmd` shims. Prompt text that becomes extra arguments or shell commands, an API key or other variable leaking into the child, or a remote client reaching a CLI connector while `AllowRemoteClients` is off.
- **Prompted tool calls.** With `ToolCalling: Prompted` a CLI connector reads tool calls out of the model's text, and the client runs them. Text in a tool result that becomes a call by being repeated (word for word or reformatted), a tool result that can pose as a turn of the transcript, a run's fence token that reaches the client or can be predicted, or, with `PromptedToolParsing: Strict`, a call accepted without the run's label, after which the reply goes on, or to a tool the request does not have. See [Forged tool calls](docs/providers.md#forged-tool-calls) for what each mode accepts.
- **The LocalOnly guarantee.** A route, routing profile or `LocalOnlyWhen` signal marked local-only must only be served by models marked `IsLocal`, including on failover. A local-only prompt that reaches a non-local model is a security issue.
- **Stored prompts.** The activity log and the SQLite state database (`semantic-router.db`) hold prompt previews, with credentials masked unless `State:StorePrompts` is `On`; the evaluation set export leaves local-only prompts out unless asked. A way to read these without dashboard access, prompt text stored with `StorePrompts: Off`, or purged activity that can still be read from the database files is in scope.

Out of scope:

- Vulnerabilities in the vendor CLIs, model servers or cloud APIs themselves; report those to the vendor.
- What a model says, or a prompt injection that only changes a model's answer. This includes a model that an injected instruction talks into making a tool call of its own: the client's permission settings decide whether it runs.
- Setups the documentation describes as trusted, such as `AllowUnauthenticatedRemoteClients: true` without an authenticating proxy, or keys written in plain text to `appsettings.json`.
- Questions about whether using a subscription CLI through the router fits a vendor's terms. These are not security issues; see [Subscription CLI terms](docs/TERMS.md) and open a normal issue if something there is wrong or out of date.

## If a key was exposed

Revoke it at the provider first. Removing a key from a file, or from the router's configuration, does not remove it from git history, logs or backups.
