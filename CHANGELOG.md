# Changelog

User-visible changes to SemanticRouter.NET: the NuGet packages, the `semantic-router` tool, the container image and the desktop app. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/); before 1.0, a minor version can change behaviour. Each pull request with a user-visible change adds its line under **Unreleased** ([CONTRIBUTING.md](CONTRIBUTING.md#changelog)).

## [Unreleased]

### Security

- Machine-wide policy that the user's configuration cannot override, read from `HKLM\Software\Policies\SemanticRouter`, macOS managed preferences or `/etc/semantic-router/policy.json`: turn off connector types and CLI presets, limit endpoints and the hosts that count as local, require local-only, cap prompt storage and retention, make the dashboard read-only or turn it off, control desktop updates and turn features off. A policy that cannot be read stops the router. See [docs/enterprise.md](docs/enterprise.md). (#142)
- `init` does not add, or ask about, CLIs that the machine policy turns off. (#146)
- Offline mode (`Network:Offline` with `Network:AllowedHosts`, or `Offline` in the machine policy): connectors, embeddings and webhooks reach only this machine and the allowed hosts, CLI and Copilot connectors are off, and the ONNX model is never downloaded. (#147)
- Remote gateway clients need a key: with no `Middleware:GatewayKeys` they get 401 (`AllowUnauthenticatedRemoteClients` restores the old behaviour), and a key reference that does not resolve no longer opens the gateway. The container image reads `ROUTER_GATEWAY_KEY`. (#129)
- Default limits for remote clients (`Middleware:RemoteRequestsPerMinute`, 120) and CLI queues (`MaxQueue`, 16), and `ForwardedHeaders:KnownProxies`/`KnownNetworks` to trust `X-Forwarded-For` only from named proxies. (#129)
- Web pages cannot use the gateway through DNS rebinding or a foreign `Origin`; `/v1/models`, `count_tokens` and `/semantic-router/classify` follow the same gateway-key rule as the chat endpoints. (#114)
- Dashboard responses forbid framing (`frame-ancestors 'none'`, `X-Frame-Options: DENY`) and send `nosniff` and `no-referrer`; the dashboard bundles its fonts and sends a strict Content-Security-Policy. (#114, #129)
- The admin API cannot change what a CLI runs (program, arguments, environment, home directory) or which paths memory sources read, unless `Dashboard:AllowProcessSettings` is on. (#114, #144)
- The read-only dashboard role can no longer trigger calls that cost money or scan the disk (evaluate, classify, embeddings test, discover, check, memory refresh); dashboard sessions expire after `Dashboard:SessionLifetime` (12 hours), and wrong tokens are throttled per address. (#144)
- Dashboard identity from an authenticating reverse proxy (`Dashboard:TrustedIdentityHeader`, believed only from `KnownProxies`), the real actor on every configuration change, and an append-only audit log (`GET /router/api/audit`, an Audit log page, `State:Retention:AuditDays`). (#150)
- Local-only routing holds in more cases: a failed classification stays local while any route is local-only, a conversation kept local stays local (including named cloud models), local-only prompts stay off remote embedding endpoints (`Embeddings:IsLocal`), and the judge respects routing profiles. (#114)
- Secrets: CLI environment variables and more header names (auth, cookie, password, credential, session, signature) are masked; masked or stored keys are never sent to a connector that now points somewhere else; the configuration history keeps no secrets; keyed requests do not follow redirects; webhook URLs are not logged. (#114, #116)
- Keys written into addresses (`user:key@`, key-named query values) are masked in the admin API, the configuration history and every address the dashboard and `doctor` show. (#117, #129)
- CLIs run in a working directory only the user can write to and start by full path; a Custom CLI's `{prompt}` cannot become an option; the router's files are readable by their owner only. (#114)
- The data-protection key ring is encrypted at rest: to a certificate (`DataProtection:Certificate`) on any OS, with DPAPI for `SEMANTIC_ROUTER_KEYS_DIR` on Windows; otherwise owner-only permissions and a start-up warning. `doctor` says how the keys are protected. (#149)
- Stored prompts: `State:StorePrompts` (`Redacted` by default, masking credentials in previews; `On`; `Off`), `State:Retention`, and a purge (`DELETE /router/api/activity`, "Delete all activity") that leaves nothing readable in the database files. (#129)
- Webhooks carry no prompt or reply text unless `Notifications:IncludeDetails` is on, and the stored project label is a hash of the client's `user_id`, never the id. (#144)
- Prompted tool calls are harder to forge: a random fence label per run that never reaches the client, defused tool results, `PromptedToolParsing: Strict` (opt-in), calls copied from a tool result kept as text in both modes, and a prompted-call count in the activity log. (#129, #152)
- No outbound traffic you did not configure: no web fonts from other hosts, Claude Code started without its auto-updater and non-essential traffic, Codex with update checks and analytics off. `doctor` lists every host the router will contact. (#129, #143)
- Desktop updates: `Updates:Publisher` requires an update to be code-signed by a given publisher before it is applied, and the updater refuses feeds over plain HTTP other than loopback. (#142, #151)
- Verifiable releases: build provenance attestations for every asset, `SHA256SUMS`, CycloneDX SBOMs, a keyless cosign signature and attestations on the container image, and `THIRD-PARTY-NOTICES.txt`. See [docs/releases.md](docs/releases.md). (#145)
- Release pipeline: a tag must be on `main`, tests pass before anything publishes, the desktop app is built in jobs without secrets, and the image is built and scanned (Trivy) before the registry login. CodeQL and OpenSSF Scorecard run on the repository. (#129)
- The container runs on a chiseled ASP.NET Core image with no shell, with both base images pinned by digest. (#129)
- NuGet restores are locked to committed `packages.lock.json` files in CI, the release and the container build. (#148)

### Added

- A "Cut a release" workflow (Actions > Cut a release): the repository owner enters a version, it checks CI, code signing and NuGet publishing, tags `main`, and the release workflow publishes everything with the CHANGELOG section as the release notes. Only the owner can release, by this workflow or a pushed tag. See [docs/releases.md](docs/releases.md#cutting-a-release).
- Cache-aware stickiness: a conversation stays on the model with the warm prompt cache when that is cheaper than moving, and returns to it after a failover (`Middleware:Conversation:CacheLifetime`). (#137)
- Prompt-cache markers on Anthropic requests translated from OpenAI or Responses clients (`Connectors:{name}:PromptCaching`, `CacheTtl`). (#139)
- Cache reads and writes per activity entry, and a cache hit rate on the Usage page. (#113)
- Endpoints card on the Overview (host, state, health and latency per connector, with a Check button) and a Service card (where the router runs, the URLs for clients, its health). (#117, #121)
- `Network:CaBundle`, a PEM file trusted in addition to the system store, for TLS-inspecting proxies. (#143)
- Desktop app: notify-only updates (`Updates:Apply: Notify` in the machine policy, or "Install updates when quitting" in the tray), and managed deployment docs. (#151)
- `PromptRouterMiddlewareOptions.InjectedTags`; the Copilot app's injected context blocks are ignored when classifying. (#118)
- Model names `semantic-router`, `claude-semantic-router`, `-cheapest` and `-best`; `/v1/models` entries have a `display_name`. (#156)
- `tool-calls` benchmark for prompted tool calling. (#152)

### Changed

- Targets .NET 10 (LTS) instead of .NET 9, whose support ends on 10 November 2026: the packages and the tool need the .NET 10 runtime, and the container runs on ASP.NET Core 10. Building needs a 10.0.4xx SDK. (#115, #148)
- `init` asks before enabling a subscription CLI connector (Claude Code, Codex); declined or non-interactive runs add it disabled. `--yes` restores the old behaviour and `--no-cli` leaves CLIs out. (#141)
- Prompts go to Claude Code and Copilot CLIs on standard input; `PromptInput: "Argument"` restores the old behaviour. (#114)
- Model comparisons count each model's prompt-cache prices, from the share of input it reads from cache (`Selection:CacheShareWindow`, `CacheShareSamples`). (#138)
- Only the prose of a prompt is embedded, without pasted code or data (`EmbedProseOnly`, on by default); local-only routes are still checked against the whole prompt. (#157)
- The `init` template routes Claude Code's `*opus*` as `auto`; `opus-pinned` pins Opus, and an exact alias wins over a wildcard. (#156)
- Routing profile `Projects` globs match the hashed `user:…` project label, and dashboard sessions sign in once more after the upgrade. (#144)
- Dependency updates, among them GitHub.Copilot.SDK 1.0.22. (#134, #135, #136)

### Fixed

- Cached-token counts on streamed and translated replies, from the CLIs and in "saved by routing". (#113)
- After a failover, the serving model is timed from its own attempt, so latency ranking and endpoint health no longer count the failed attempts against it. (#154)
- Claude Code: a conversation keeps its key across turns (with `Middleware:Conversation:SessionHeader`), the session-title request is housekeeping, and a JSON `metadata.user_id` is not used as the project. (#156)
- A user message that holds only injected context (such as the Copilot app's `<system_reminder>`) no longer hides the person's message from the classifier. (#118)
- "What can you help me with?" goes to smalltalk; classify and Try a prompt apply callouts; computed properties are no longer written into `semantic-router.json`. (#157)
- Dashboard layout: steady Live rows, no sideways scrolling on Live, long tokens on Activity. (#155)

## [0.2.1] and earlier

See [GitHub Releases](https://github.com/siebe41/SemanticRouter.NET/releases).

[Unreleased]: https://github.com/siebe41/SemanticRouter.NET/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/siebe41/SemanticRouter.NET/releases/tag/v0.2.1
