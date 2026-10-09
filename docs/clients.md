# Pointing your tools at the router

The router has three front doors, so almost any tool can use it without changing how you work:

| Front door | Path | Who speaks it |
|---|---|---|
| OpenAI chat completions | `POST /v1/chat/completions`, `GET /v1/models` | OpenAI SDKs, Aider, Continue, Cline, Open WebUI, LibreChat, LM Studio clients, … |
| Anthropic Messages | `POST /v1/messages`, `POST /v1/messages/count_tokens` | **Claude Code**, the Anthropic SDKs, anything built on them |
| OpenAI Responses | `POST /v1/responses` | **Codex CLI**, the OpenAI Agents SDK, `client.responses.create` |

> **What has been tested.** Claude Code 2.1.293 (a streaming, tool-using session through a stub OpenAI-compatible backend, and through the real `claude` CLI as a prompted-tools backend), Codex CLI 0.161.0 (a streaming session that ran a shell command and a namespaced tool through a stub OpenAI-compatible backend) and curl. The other tools below speak standard OpenAI chat completions and should work, but have not been exercised individually. [docs/roadmap.md](roadmap.md) tracks the gaps.

Whatever the client speaks, every backend can answer: the router translates requests and replies between the protocols, including streaming, images, tool calls, reasoning and structured output, and passes bodies through untouched when the backend speaks the client's protocol. What a backend cannot carry is listed in the `X-Prompt-Route-Dropped` response header (see the [compatibility table](providers.md#what-survives-translation)).

The dashboard's **Connect clients** page generates these snippets with your router's actual address.

## Claude Code

Claude Code reads `ANTHROPIC_BASE_URL` and sends the Messages API there, with its own system prompt, its tools and its full conversation. The router routes each turn on the latest human message, so a conversation can move between a local model and a frontier model as the asks change.

```bash
export ANTHROPIC_BASE_URL=http://localhost:5080
export ANTHROPIC_AUTH_TOKEN=semantic-router   # any value: the router never reads or forwards it
claude
```

`ANTHROPIC_AUTH_TOKEN` matters: with it set, Claude Code sends that placeholder as its bearer token instead of your real session token, so your login never leaves the `claude` process. The router ignores the `Authorization` and `x-api-key` headers of every request and uses the credentials from its own configuration.

What happens to the different kinds of requests Claude Code sends:

| Claude Code sends… | The router… |
|---|---|
| its model name (`claude-sonnet-…`, `/model opus`, `haiku` for side tasks) | maps it through `Middleware:ModelAliases` (sample: `*haiku*` → `auto:cheapest`, `*opus*` → `family:opus`, the catalog's Opus model or else the most capable, otherwise `auto`). Its default Sonnet name therefore means "router decides". Unknown names route as `auto`. |
| a follow-up such as "ok, go ahead" in the middle of a task | keeps the conversation's tier and model ([conversation routing](routing-quality.md#conversations)), so the task does not drop to a small model. |
| a message starting with `@opus`, `@local`, `@best`… | routes as the tag asks, for that turn and the rest of the conversation ([below](#telling-the-router-what-you-want)). |
| a turn **with tools** (every main-loop turn) | requires the `tools` capability. Candidates: HTTP models that declare `tools` (a local coder model, an API-key model) and CLI connectors with `ToolCalling: Prompted` (below). |
| **tool results** (the loop continuing) | pins the request to the model that issued the tool calls (`X-Prompt-Route-Reason: ConversationAffinity`), so one turn is never split across models. If that model is cooling down, it is classified normally. |
| `<system-reminder>` blocks inside user messages | ignores them when classifying (`Middleware:StripSystemReminders`). |
| its own side requests: session titles, topic checks, the compaction summary, `Warmup` pings, shell command checks | recognises them by their wording (`Middleware:Housekeeping`) and sends them to tier 1 without classifying them (`ClientHousekeeping`). They do not count as turns of the conversation, so a compaction in the middle of a tier-4 task is not held at tier 4. Other side requests are classified like any prompt; the sample's `agent-housekeeping` route sends them to tier 1. |
| a session resumed after compaction ("This session is being continued from a previous conversation…") | routes its first turn at `Housekeeping:ResumedTier` (4, the agentic tier) instead of classifying the summary (`ResumedSession`); the conversation keeps that tier as its floor. |
| "go ahead", "continue", "looks good, do it" | the sample's `continuation` route (tier 3) catches them on a first turn; in a conversation the [floor](routing-quality.md#conversations) keeps them at the task's tier. |
| `POST /v1/messages/count_tokens` | answers from its own estimator (map it with `app.MapAnthropicTokenCount()`). |
| `thinking`, `cache_control`, `metadata` | passes them through to an `Anthropic` connector; drops what other backends cannot use. |

### Where agentic turns can go

A tool-bearing turn needs a backend that can carry tools:

1. **An OpenAI-compatible model declaring `tools`** (LM Studio or Ollama with a tool-capable model such as `qwen3-coder`, vLLM, OpenRouter, OpenAI). Tool definitions, tool calls and tool results are translated 1:1. This is the free, local path.
2. **An `Anthropic` connector** (the Messages API with an API key, billed per token): Claude Code's requests pass through untouched, including prompt caching.
3. **A CLI connector with `ToolCalling: Prompted`** (your Claude subscription through the `claude` binary): the tools are described in the prompt, the model answers with a fenced `tool_call` block, and the router turns it into a real `tool_use` for Claude Code, which then runs the tool as usual. The next request carries the tool result; the router renders the whole exchange back into the prompt of a fresh `claude -p` run. Text in, text out: the inner `claude` never runs tools itself.

   Costs and limits of the prompted path: every turn re-sends the full context (no prompt cache, so it is slower and uses more of your quota than Claude Code talking to Anthropic directly), tool-call parsing relies on the model following the format (Sonnet and Opus do; small models often do not), and parallel tool calls are supported but images are not. Keep local models for the many easy turns and let `CheapestCapable` reserve the subscription for the hard ones.

Context size matters: a Claude Code turn is 15–30k tokens of system prompt, tool definitions and history before the conversation even starts. Set `ContextWindow` on local models honestly; the router skips models that cannot hold the request.

### Checking what happened

Every response carries routing headers (`X-Prompt-Route`, `-Tier`, `-Reason`, `-Model`, `-Connector`, `-Attempt`, `-Degraded`, and `-Dropped` when the backend could not carry a request feature), the console logs each decision, and the dashboard's **Activity** page lists every routed turn with its prompt preview, tier, route and the model that answered.

## Telling the router what you want

The router decides by itself; you only need this when you really want something specific. Start a message with a tag, in any client:

| Tag | Routes to |
|---|---|
| `@opus`, `@sonnet`, `@qwen`… | A model family: the most capable catalog model whose name or model id contains it. |
| `@claude-opus` (any catalog model name) | That model. |
| `@best`, `@cheap`, `@priority` | The most capable model, the cheapest that fits, or your priority order. |
| `@tier3` (`@tier1` to `@tier9`) | That tier, up or down, whatever the prompt looks like. |
| `@local` | Local models only: the conversation does not leave the machine. |
| `@auto` | Back to automatic routing. |

```text
@opus why does this deadlock when I call .Result?
```

- **The tag never reaches the model.** It is taken out of the request (from every user message, since clients resend the history) before it is forwarded.
- **It holds for the conversation.** The next turns without a tag stay on your choice until you write `@auto` or another tag. The header `X-Prompt-Route-Callout` and the Activity page show `@opus (earlier in the conversation)`.
- **Only a tag at the very start counts**, after anything the client injects (Claude Code's `<system-reminder>` blocks). A tag that is not one of these (`@src/Program.cs`, a file mention) is left in the message. A message that is nothing but a tag keeps it, so the model has something to answer.
- **Your own names:** `Middleware:Callouts:Names`, e.g. `"deep": "family:opus"`, `"fast": "lmstudio-small"`, `"private": "local"`; then write `@deep`.
- **Policy still applies.** A routing profile that does not allow the model wins (the header says "not allowed here; routed automatically"), and a profile or secret detection that keeps a conversation local still does. A model callout skips classification, so a local-only *route* does not apply to it: use `@local` for that.
- Switch it off with `Middleware:Callouts:Enabled: false`.

## OpenAI SDKs and everything with a base URL

```bash
export OPENAI_BASE_URL=http://localhost:5080/v1
export OPENAI_API_KEY=semantic-router        # any value
```

```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:5080/v1", api_key="semantic-router")
client.chat.completions.create(model="auto", messages=[{"role": "user", "content": "hello"}])
```

Model names: `auto` (configured strategy), `auto:cheapest`, `auto:best`, `auto:priority`, or a catalog model name to force it. `GET /v1/models` lists all of them, so model pickers in chat UIs work.

## OpenAI Codex CLI

Codex speaks the Responses API. Point a provider at the router with `wire_api = "responses"`:

```toml
# ~/.codex/config.toml
model_provider = "semantic-router"
model = "auto"

[model_providers.semantic-router]
name = "Semantic Router"
base_url = "http://localhost:5080/v1"
wire_api = "responses"
env_key = "OPENAI_API_KEY"       # export OPENAI_API_KEY=semantic-router
```

Any backend can answer. The router maps Codex's items and tools onto the backend's protocol and back:

- **Namespaced tools.** Codex groups tools in namespaces (`multi_agent_v1`, `collaboration`, …). Backends see them as `namespace__name`, and calls come back to Codex under the original name and namespace.
- **Custom tools.** Codex's free-form tools (such as `exec`) become a function with one `input` string, with the grammar quoted in the description. Calls come back as `custom_tool_call` items.
- **Reasoning.** `reasoning.effort` becomes `reasoning_effort` or an Anthropic `thinking` budget on models that declare `reasoning`. Backend reasoning comes back as a reasoning summary.
- **Hosted tools.** Codex declares `web_search` on most turns. Only a `Responses` connector (OpenAI's API) runs it. Other backends answer without it, and the response says `X-Prompt-Route-Dropped: web_search`.
- **Server-side state.** `previous_response_id`, encrypted reasoning items and `/v1/responses/{id}` lookups only work against OpenAI itself. Codex sends `store: false` and the full history on every turn, so it does not need them. Remote compaction (`/v1/responses/compact`) is not routed: Codex falls back to compacting locally. Its local compaction request (a "context checkpoint compaction" asking for a handoff summary) is recognised and goes to tier 1, like Claude Code's.

Codex's turns carry tools, so the same rules as for Claude Code apply: models need `tools`, and tool-result turns stay on the model that issued the calls. Without metadata for the model name `auto`, Codex prints "Model metadata for `auto` not found" and uses defaults. That is harmless. To silence it, alias a known OpenAI name in `ModelAliases` (`"gpt-*": "auto"`) and set `model` to that name.

If you route through a proxy (`HTTPS_PROXY`), exclude the router: `NO_PROXY=localhost,127.0.0.1`.

## Aider, Continue, Cline, Open WebUI, LibreChat

Set the OpenAI base URL to `http://localhost:5080/v1`, any API key, and pick `auto` (or any catalog model) from the model list.

## curl

```bash
# Responses shape
curl http://localhost:5080/v1/responses -H 'Content-Type: application/json' -d '{"model":"auto","input":"hello"}'

# OpenAI shape
curl -N http://localhost:5080/v1/chat/completions -H 'Content-Type: application/json' \
  -d '{"model":"auto","stream":true,"messages":[{"role":"user","content":"Compare event sourcing and CRUD"}]}'

# Anthropic shape
curl http://localhost:5080/v1/messages -H 'Content-Type: application/json' -H 'anthropic-version: 2023-06-01' \
  -d '{"model":"auto","max_tokens":256,"messages":[{"role":"user","content":"hello"}]}'

# Explain a decision without calling a model
curl -s http://localhost:5080/semantic-router/classify -H 'Content-Type: application/json' -d '{"prompt":"why does this deadlock?"}'
```

## Keep it local

Subscription CLI connectors only answer loopback clients (`AllowRemoteClients: false`), and the dashboard's admin API does too unless a dashboard token is configured (see [dashboard.md](dashboard.md#access-and-security)). Bind the router to `localhost` when it fronts your personal subscriptions; see [TERMS.md](TERMS.md).

If you do share the router (with API-key and local models only), set `Middleware:GatewayKeys` so each client presents its own key: `ANTHROPIC_AUTH_TOKEN=<key>` for Claude Code, `OPENAI_API_KEY=<key>` for OpenAI clients. The router compares the key and records the client's name in Activity; it never forwards it.
