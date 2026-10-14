# Subscription CLI connectors and vendor terms

SemanticRouter can answer prompts with **Claude Code**, **GitHub Copilot CLI** and **OpenAI Codex CLI**, using the subscription you are already signed in to. This page explains why the connectors are built the way they are, how that design lines up with each vendor's published terms, and where the limits are.

> **This is not legal advice.** It is the project's reading of the vendors' public documentation as of **7 October 2026**. Terms change, and enforcement is at each vendor's discretion. Read the current terms yourself. If your use is anything other than one person on their own machine, ask the vendor. Every quotation below links to its source.

## Summary

| Connector | Billing | Intended use | Fits the vendor's terms when… |
|---|---|---|---|
| Claude Code CLI (`ClaudeCode` preset) | your Claude Pro/Max/Team plan | **you**, on **your** machine | you run the unmodified `claude` binary under your own login, for ordinary individual use, and don't offer it to anyone else |
| GitHub Copilot CLI (`GitHubCopilot` preset) | your Copilot plan's AI credits | **you**, on **your** machine | you script your own Copilot CLI and don't share, resell or bulk-automate access |
| OpenAI Codex CLI (`Codex` preset) | your ChatGPT plan | **you**, on **one** trusted machine | you run Codex under your own ChatGPT login in a private environment and don't make your account available to others |
| Gemini CLI | – | **not provided** | Google ended Google-login access for consumer accounts and prohibits piggybacking on the CLI's OAuth (details below) |
| Any API-key connector (OpenAI, Anthropic, Gemini, Azure, OpenRouter, …) | per token, on your key | apps, teams, products | you follow that API's normal terms |

If you want to serve **other people** (a team, customers, a hosted product), use **API-key connectors**. Subscription logins are personal.

## How the connectors are designed

Each mechanism below exists because of a specific line in a vendor's terms. Those lines are quoted in the vendor sections.

| # | Design rule | How the code enforces it |
|---|---|---|
| 1 | **Run the vendor's own, unmodified CLI.** The router does exactly what you could do in a terminal. It never calls a vendor's private endpoints and never imitates its client. | `CliChatConnector` starts the real executable (`claude`, `copilot`, `codex`) with `ProcessStartInfo.ArgumentList`, using documented flags only. |
| 2 | **Never touch credentials.** The router does not read, copy, store, forward or proxy OAuth tokens or session files. Sign-in happens only through the vendor's own CLI flow. | The connector has no credential settings. The CLI authenticates itself from its own store. |
| 3 | **Make sure your subscription is what gets used.** Some CLIs silently switch to API-key billing when a key is present in the environment. | The presets remove those variables from the child process: `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` for Claude Code, and `CODEX_API_KEY` for Codex. Removing `ANTHROPIC_BASE_URL` also stops a loop if you pointed Claude Code at this router. |
| 4 | **Personal, local use only.** A subscription login must not be turned into a service for other people. | `AllowRemoteClients` defaults to **false**. CLI connectors answer only requests from the loopback interface, and remote callers fall through to the next (API-key) model. The sample listens on `localhost`. |
| 5 | **Respect usage limits; never get around them.** When a vendor says you have hit a limit, stop asking. | Usage-limit messages put the model, or every model on that account, into a **cooldown** (`QuotaCooldown`, 15 minutes by default) instead of retrying. The router never rotates accounts or tokens. |
| 6 | **Ordinary volumes.** | CLI connectors have a concurrency limit (`MaxConcurrency`, default 2; the sample uses 1 for Codex, per OpenAI's "one machine or serialized job stream" guidance). With `CheapestCapable`, easy prompts go to free local models first. |
| 7 | **Least privilege for the CLI.** This is a safety measure, not a terms requirement. | Each run gets a fresh empty working directory, with built-in tools disabled or denied and MCP servers and custom instructions turned off (flags per preset). The prompt is never passed through a shell. With `ToolCalling: Prompted`, tools are only *described* to the CLI; the client that asked runs them. |
| 8 | **Never relay a client's credentials.** When Claude Code or another tool is pointed at the router, its `Authorization`/`x-api-key` headers are ignored and never forwarded or stored. | HTTP connectors always use the key from configuration. The recommended client setup sends a placeholder token (`ANTHROPIC_AUTH_TOKEN=semantic-router`), so the real session token never leaves the client. When `Middleware:GatewayKeys` is set, that header carries a key the router itself issued instead; it is compared in constant time and then dropped, never logged, stored or forwarded. |

## Anthropic: Claude Code

**What Anthropic says.** All quotations are from [Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance) unless another source is linked.

- **Who OAuth is for:** "OAuth authentication is intended exclusively for purchasers of Claude Free, Pro, Max, Team, and Enterprise subscription plans and is designed to support ordinary use of Claude Code and other native Anthropic applications."
- **What third-party developers may not do:** "Anthropic does not permit third-party developers to offer Claude.ai login into their own applications, or to route requests through Free, Pro, or Max plan credentials on behalf of their users. Moreover, developers may not collect, store, or intermediate Claude.ai credentials or session tokens — sign-in to a Claude account must complete through Anthropic's own flow."
- **What is explicitly not restricted:** "Nor does it prevent an end user from signing in to the unmodified Claude Code binary with their own Claude subscription…"
- **Usage limits:** "Advertised usage limits for Pro and Max plans assume ordinary, individual usage of Claude Code and the Agent SDK."
- **Driving the CLI from another language** ([Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)): "To drive the same agent loop from a language other than Python or TypeScript, run the CLI as a subprocess with the `-p` flag…"
- **Scripts on a subscription** ([Authentication](https://code.claude.com/docs/en/authentication)): `claude setup-token` exists for "CI pipelines and scripts", and its token "authenticates with your Claude subscription".
- **Products** ([Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)): "Unless previously approved, Anthropic does not allow third party developers to offer claude.ai login or rate limits for their products, including agents built on the Claude Agent SDK."

**How the connector lines up with this:**

- *Unmodified binary, own subscription.* The connector runs the published `claude` binary that you installed and signed in to (rules 1 and 2). That is the case the terms say is not restricted.
- *Subprocess with `-p`.* This is the integration path Anthropic documents for other languages.
- *No collecting, storing or intermediating credentials.* The router never sees a token (rule 2). This is also why there is **no** "pass Claude Code's subscription traffic through a gateway" feature: that would mean relaying a session token.
- *Not on behalf of other users, not in a product.* Loopback-only by default (rule 4). Turning on `AllowRemoteClients` to serve other people would be exactly what the third-party-developer and product clauses rule out.
- *Ordinary, individual usage.* Cooldowns, concurrency limits, and local models absorbing easy prompts (rules 5 and 6) all keep usage within normal individual patterns.

**Claude Code as the client.** Pointing Claude Code at the router (`ANTHROPIC_BASE_URL`) changes nothing above: the router does not touch the client's session token (rule 8), and when a turn lands on the Claude Code *connector* the router runs the unmodified `claude` binary on your own login exactly as before. With `ToolCalling: Prompted` the inner `claude` answers with text describing a tool call and your outer Claude Code runs the tool: the inner process still never runs tools, and the subscription still only serves you.

**Grey areas:**

- *"Ordinary, individual usage" is not defined.* A router that only sends *you* your own hard prompts is ordinary use by any reasonable reading. Feeding it unattended batch jobs around the clock is not. Prompted tool calling re-sends the full agent context on every turn (no prompt cache), so an agentic session through the router uses more of your quota than Claude Code talking to Anthropic directly; keep easy turns on local models.
- *Billing for third-party harnesses.* Press reports from 2026 say Anthropic began billing third-party *harnesses* that use subscription logins as pay-as-you-go "extra usage". This connector runs the official binary rather than reimplementing it, but whether a given setup counts as a harness is Anthropic's call. If your usage meter shows extra usage you did not expect, that is the signal; contact Anthropic.

## GitHub: Copilot CLI

**What GitHub says:**

- **Scripting is a documented use** ([run the CLI programmatically](https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically)): programmatic mode "allows you to use the CLI programmatically in scripts, CI/CD pipelines, and automation workflows."
- **Which terms apply** ([GitHub Terms for Additional Products and Features](https://docs.github.com/en/site-policy/github-terms/github-terms-for-additional-products-and-features)): for individual users, Copilot is governed by Section J (AI Features) of the GitHub Terms of Service. Business and Enterprise use is governed by the Generative AI Services Terms.
- **[Acceptable Use Policies](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies):**
  - §4 prohibits "excessive automated bulk activity" and placing "undue burden on our servers through automated means".
  - §6: "You will not reproduce, duplicate, copy, sell, resell or exploit any portion of the Service, use of the Service, or access to the Service without our express written permission."
- **Terms of Service §H:** "You may not share API tokens to exceed GitHub's rate limitations."
- **Billing** ([models and pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing)): usage draws AI credits from your plan's allowance, per token.

**How the connector lines up with this:**
- The preset runs the official `copilot` binary in its documented programmatic mode (prompt piped on stdin, `-s`), with tools denied (`--deny-tool=…`, `--no-ask-user`, `--disable-builtin-mcps`, `--no-custom-instructions`).
- It never calls Copilot's internal APIs and never shares a token (rules 1 and 2).
- Loopback-only, cooldowns and concurrency limits keep it personal and away from "bulk activity" or resale (rules 4–6).

## GitHub Copilot SDK

GitHub's official **Copilot SDK** (NuGet `GitHub.Copilot.SDK`, generally available June 2026) is the supported way for an application to use a person's Copilot subscription. The `Copilot` connector type is built on it (package `SemanticRouter.Connectors.Copilot`, included in the tool).

**What it does better than the CLI preset:**
- It uses the vendor's programmatic interface, not a CLI's text output. Rule 1 holds by construction: the SDK starts the official runtime and speaks its documented protocol.
- Sign-in is the runtime's own (the CLI's stored login, `gh auth`, or a token in the runtime's environment). The router passes no credentials and reads none (rule 2).
- Least privilege is enforced by the SDK rather than by flags: in `Empty` mode Copilot has no built-in tools at all. The client's tools are declared without handlers, so nothing runs inside Copilot (rule 7).
- Limits come back as structured errors (`quota`, `rate_limit`, with a retry-after time). The router turns them into cooldowns and declines the SDK's automatic model switch (rule 5).
- Sessions are deleted after each turn, so no conversation is kept by the runtime.

**What still applies:** the same personal-use rules. `AllowRemoteClients` defaults to false for this connector too, `MaxConcurrency` and `MaxQueue` limit volume, and GitHub's Acceptable Use Policies quoted above apply unchanged: a Copilot seat must not become a service for other people (rules 4 and 6). For anything shared, use API-key connectors.

## OpenAI: Codex CLI

**What OpenAI says:**

- **Default auth for `codex exec`** ([non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode)): "codex exec reuses saved CLI authentication by default."
- **[CI/CD auth guide](https://learn.chatgpt.com/docs/auth/ci-cd-auth):**
  - "API keys are the right default for automation because they are simpler to provision and rotate. Use this path only if you specifically need to run as your Codex account."
  - ChatGPT-login automation is "an advanced workflow for enterprise and other trusted private automation".
  - A given `auth.json` copy should be used by "only one machine or serialized job stream".
- **[Authentication](https://learn.chatgpt.com/docs/auth):** "Don't expose Codex execution in untrusted or public environments."
- **OpenAI Terms of Use:**
  - "You may not share your account credentials or make your account available to anyone else."
  - You may not "circumvent any rate limits or restrictions".
  - These were quoted from an archived copy; check the [live terms](https://openai.com/policies/terms-of-use).

**How the connector lines up with this:**
- It runs the official `codex` binary under your saved login, on your machine.
- The sandbox is read-only, there is no shell tool, web search is off, and it runs in an empty directory (rules 1, 2 and 7).
- `CODEX_API_KEY` is removed, so your ChatGPT plan is billed rather than an API key you happened to have set (rule 3).
- Loopback-only means the account is not "made available to anyone else" (rule 4). The sample's `MaxConcurrency: 1` keeps to a single serialized job stream. Usage-limit messages trigger a cooldown instead of retries (rule 5).

## Google: Gemini CLI (deliberately not included)

- Google's [deprecation notice](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals) says that from **18 June 2026**, consumer accounts can no longer use "Login with Google" for the IDE extensions or Gemini CLI. That covers Code Assist for individuals, Google AI Pro and Ultra.
- The Gemini CLI [terms page](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/tos-privacy.md) says: "Directly accessing the services powering Gemini CLI … using third-party software, tools, or services … is a violation of applicable terms and policies."
- The [FAQ](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/faq.md) says: "If you would like to use a third-party coding agent with Gemini, the supported and secure method is to use a Vertex AI or Google AI Studio API key."

Use the **Gemini API** with a key through the `OpenAICompatible` connector instead (see [providers.md](providers.md#google-gemini-api)).

## Session reuse and long-lived processes

Every prompted turn starts a new CLI process and re-sends the whole transcript, which is the slowest and most quota-hungry part of using a subscription CLI for agentic work. Two ways to avoid that were evaluated against the rules above. Neither is enabled.

- **Resuming a stored session** (Claude Code's `--resume`/`--continue`, and the equivalents in the other CLIs). The CLI keeps the conversation in its own session store under your home directory, keyed by working directory. A resumed turn would send only the new messages. That breaks rule 7: runs would need a persistent working directory instead of a fresh empty one, and every conversation that passed through the router would be stored on disk by the CLI, whatever profile or local-only policy the router applied. The router would also need to map each client conversation to a CLI session and to detect when a client rewrote its history (agents compact and edit their transcripts), or a resumed session would answer from a different context than the client sent. A wrong mapping fails silently.
- **A long-lived process per connector** (`--input-format stream-json`). This removes start-up time, but the process holds one conversation. Turns from different conversations, clients or profiles would share context unless each gets its own process. Per-conversation processes need idle timeouts, memory limits and cleanup, and they make `MaxConcurrency` mean "conversations" instead of "requests".

What the router does instead: it streams text as it arrives (most of the waiting a person notices), repairs a broken call with a short separate run rather than a retried turn, and measures time to first byte per connector. Either option above could be added later as an opt-in per connector. It would have to keep sessions inside a router-owned directory deleted on idle, never shared between profiles, and documented here first. For lower cost and latency with a Copilot subscription, the [Copilot SDK connector](providers.md#github-copilot-sdk) is the supported route.

## API-key connectors

OpenAI, Anthropic, Gemini, Azure OpenAI, OpenRouter and similar APIs are used with your own API keys under each provider's normal API terms. These are the right choice for anything shared: team gateways, internal tools, products. `AllowRemoteClients` does not apply to them.

## Your checklist

- [ ] The router listens on `localhost`. CLI connectors keep `AllowRemoteClients: false`.
- [ ] Only you use the subscription-backed models. Anyone else goes through API-key models.
- [ ] You signed in to each CLI yourself, through the vendor's own login flow.
- [ ] You are not feeding unattended bulk jobs through a subscription.
- [ ] You re-read the vendor terms linked above when you upgrade a CLI or change how you use the router.

## Sources

Anthropic:
- [Claude Code: Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Claude Code: Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)
- [Claude Code: Authentication](https://code.claude.com/docs/en/authentication)
- [Claude Code: Headless mode](https://code.claude.com/docs/en/headless)
- [Claude Code: Errors](https://code.claude.com/docs/en/errors)

GitHub:
- [Copilot CLI: run programmatically](https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically)
- [Copilot CLI: command reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
- [GitHub Terms for Additional Products and Features](https://docs.github.com/en/site-policy/github-terms/github-terms-for-additional-products-and-features)
- [GitHub Acceptable Use Policies](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies)
- [Copilot models and pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing)
- [Copilot SDK is generally available](https://github.blog/changelog/2026-06-02-copilot-sdk-is-now-generally-available/)
- [GitHub Models is retired](https://github.blog/changelog/2026-07-30-github-models-is-now-retired/)

OpenAI:
- [Codex: non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode)
- [Codex: CI/CD auth](https://learn.chatgpt.com/docs/auth/ci-cd-auth)
- [Codex: authentication](https://learn.chatgpt.com/docs/auth)
- [OpenAI Terms of Use](https://openai.com/policies/terms-of-use)

Google:
- [Code Assist for individuals deprecation](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals)
- [Gemini CLI terms and privacy](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/tos-privacy.md)
- [Gemini CLI FAQ](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/faq.md)
