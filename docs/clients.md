# Pointing your tools at the router

The router has two front doors, so almost any tool can use it without changing how you work:

| Front door | Path | Who speaks it |
|---|---|---|
| OpenAI chat completions | `POST /v1/chat/completions`, `GET /v1/models` | OpenAI SDKs, Codex CLI, Aider, Continue, Cline, Open WebUI, LibreChat, LM Studio clients, … |
| Anthropic Messages | `POST /v1/messages`, `POST /v1/messages/count_tokens` | **Claude Code**, the Anthropic SDKs, anything built on them |

> **What has been tested.** Claude Code 2.1.293 (a streaming, tool-using session through a stub OpenAI-compatible backend, and through the real `claude` CLI as a prompted-tools backend) and curl. The other tools below speak standard OpenAI chat completions and should work, but have not been exercised individually. [docs/roadmap.md](roadmap.md) tracks the gaps.

Whatever the client speaks, every backend can answer: the router translates requests and replies between the two protocols, including streaming, images and tool calls, and passes bodies through untouched when the backend speaks the client's protocol.

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
| its model name (`claude-sonnet-…`, `/model opus`, `haiku` for side tasks) | maps it through `Middleware:ModelAliases` (sample: `*haiku*` → `auto:cheapest`, `*opus*` → `auto:best`, otherwise `auto`). Unknown names route as `auto`. |
| a turn **with tools** (every main-loop turn) | requires the `tools` capability. Candidates: HTTP models that declare `tools` (a local coder model, an API-key model) and CLI connectors with `ToolCalling: Prompted` (below). |
| **tool results** (the loop continuing) | pins the request to the model that issued the tool calls (`X-Prompt-Route-Reason: ConversationAffinity`), so one turn is never split across models. If that model is cooling down, it is classified normally. |
| `<system-reminder>` blocks inside user messages | ignores them when classifying (`Middleware:StripSystemReminders`). |
| side requests without tools (titles, summaries, command descriptions) | classifies them like any prompt; the sample's `agent-housekeeping` route sends them to tier 1. |
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

Every response carries routing headers (`X-Prompt-Route`, `-Tier`, `-Reason`, `-Model`, `-Connector`, `-Attempt`, `-Degraded`), the console logs each decision, and the dashboard's **Activity** page lists every routed turn with its prompt preview, tier, route and the model that answered.

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

> **Untested.** This section describes the chat-completions setup. Depending on its version, Codex may speak only the OpenAI Responses API, which the router does not accept yet ([#10](https://github.com/siebe41/SemanticRouter.NET/issues/10)). If Codex fails with a "not found" or wire-format error, that is the cause. Check your version's provider options (`wire_api`) in Codex's own documentation.

Codex can use an OpenAI-compatible provider:

```toml
# ~/.codex/config.toml
model_provider = "semantic-router"
model = "auto"

[model_providers.semantic-router]
name = "Semantic Router"
base_url = "http://localhost:5080/v1"
env_key = "OPENAI_API_KEY"       # export OPENAI_API_KEY=semantic-router
```

Codex's turns carry tools too, so the same rules as for Claude Code apply.

## Aider, Continue, Cline, Open WebUI, LibreChat

Set the OpenAI base URL to `http://localhost:5080/v1`, any API key, and pick `auto` (or any catalog model) from the model list.

## curl

```bash
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

Subscription CLI connectors only answer loopback clients (`AllowRemoteClients: false`), and the dashboard's admin API does too (`RouterDashboardOptions.RequireLocalClient`). Bind the router to `localhost` when it fronts your personal subscriptions; see [TERMS.md](TERMS.md).
