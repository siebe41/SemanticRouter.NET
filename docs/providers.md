# Provider setup

Each provider needs two things:

- a **connector** under `SemanticRouter:Connectors:{name}`, which says how to reach it;
- one or more **models** under `SemanticRouter:Models`, which say what each model can do and what it costs.

The snippets below are fragments of the `SemanticRouter` section. Model IDs and prices change often; check each provider's current list. Keep API keys out of `appsettings.json`:

```bash
dotnet user-secrets set "SemanticRouter:Connectors:openai:ApiKey" "<key>"
# or: export SemanticRouter__Connectors__openai__ApiKey=<key>
```

- [Choosing tiers, capabilities and costs](#choosing-tiers-capabilities-and-costs)
- Local: [LM Studio](#lm-studio) · [Ollama](#ollama) · [vLLM / llama.cpp / others](#vllm-llamacpp-and-other-openai-compatible-servers)
- Subscription CLIs: [Claude Code](#claude-code-cli) · [GitHub Copilot CLI](#github-copilot-cli) · [OpenAI Codex CLI](#openai-codex-cli) · [Any other CLI](#any-other-cli)
- APIs: [OpenAI](#openai-api) · [Anthropic](#anthropic-api) · [Google Gemini](#google-gemini-api) · [Azure OpenAI](#azure-openai) · [OpenRouter](#openrouter)
- [Tool calling and agentic clients](#tool-calling-and-agentic-clients)
- [Writing your own connector](#writing-your-own-connector)

Everything on this page can also be edited in the [dashboard](dashboard.md), with hot reload.

## Choosing tiers, capabilities and costs

- **Tier** is ordinal; only the comparison with route tiers matters. A workable scale:

  | Tier | Typical models | Typical routes |
  |---|---|---|
  | 1 | small local models (≤ 8B) | chit-chat, rewrite, summarize, translate |
  | 2 | mid-size local coder models, "mini" API models | small self-contained code tasks, extraction |
  | 3 | strong general models | everyday reasoning, unmatched prompts (`FallbackTier`) |
  | 4 | frontier models | architecture, debugging, multi-step analysis |
  | 5 | frontier with extended reasoning | the hardest problems |

- **Capabilities** are free-form tags matched against routes' `RequiredCapabilities`. The router also adds two automatically: `tools` when a request defines tools, and `vision` when it contains images. Only declare `tools` and `vision` for models whose **connector** can carry them. HTTP connectors (OpenAI-compatible and Anthropic) pass both through. CLI connectors carry neither by default; with `ToolCalling: Prompted` they carry tools (see [below](#tool-calling-and-agentic-clients)), never images.
- **Tiers** can be named under `SemanticRouter:Tiers` (`{ "Level": 4, "Name": "Frontier", "Description": "…" }`). Names are for people and the dashboard; routing compares the numbers.
- **Costs** are *marginal* USD per million tokens:
  - Local models and flat-rate subscriptions are `0`. The router learns about quota exhaustion from the error messages and cools those models down.
  - With `CheapestCapable`, `0`-cost models win whenever they qualify, and ties go to the lowest sufficient tier.
  - To prefer one free option over another, use `Priority` (lower wins).
- **ContextWindow** stops a request from going to a model that cannot hold it. Set it for local models in particular; it should match the context length you loaded the model with.
- **IsLocal** marks models whose prompts stay on your machine. Only these can serve `LocalOnly` routes.

## LM Studio

Start the server (the Developer tab, or `lms server start --port 1234`). Load your chat models and an embedding model, then copy their exact IDs from `curl http://localhost:1234/v1/models`.

**If "Require Authentication" is on** in LM Studio's server settings, every request needs the server's API token, sent as `Authorization: Bearer <token>` (which is what the connector and the embedding provider send when `ApiKey` is set). Give the router the token without writing it into a file:

```bash
dotnet user-secrets set "SemanticRouter:Connectors:lmstudio:ApiKey" "<token>"
dotnet user-secrets set "SemanticRouter:Embeddings:ApiKey" "<token>"     # when LM Studio also serves embeddings
# or: export SemanticRouter__Connectors__lmstudio__ApiKey=<token>
# or reference LM Studio's own variable: "ApiKey": "${env:LM_API_TOKEN}"
```

Without the token, LM Studio answers `401`. The router reports that as "the server requires authentication; set SemanticRouter:Connectors:lmstudio:ApiKey" (in the log, the 503 detail and the dashboard's **List models**), fails over to the next model and cools `lmstudio` down for the short unavailable cooldown. The PoC sample's preflight reads `LM_API_TOKEN` when no key is configured.

```jsonc
"Connectors": {
  "lmstudio": { "Type": "OpenAICompatible", "BaseAddress": "http://localhost:1234", "Timeout": "00:02:00" }
},
"Models": [
  { "Name": "lmstudio-small", "Connector": "lmstudio", "Model": "qwen/qwen3-8b", "Tier": 1, "IsLocal": true, "ContextWindow": 32768 },
  { "Name": "lmstudio-coder", "Connector": "lmstudio", "Model": "qwen/qwen3-coder-30b", "Tier": 2, "IsLocal": true,
    "ContextWindow": 65536, "Capabilities": [ "code", "tools" ] }
],
"Embeddings": {
  "Provider": "OpenAICompatible", "Endpoint": "http://localhost:1234", "Model": "text-embedding-nomic-embed-text-v1.5"
}
```

## Ollama

```jsonc
"Connectors": { "ollama": { "Type": "OpenAICompatible", "BaseAddress": "http://localhost:11434" } },
"Models": [ { "Name": "llama-local", "Connector": "ollama", "Model": "llama3.2", "Tier": 1, "IsLocal": true, "ContextWindow": 8192 } ],
"Embeddings": { "Provider": "OpenAICompatible", "Endpoint": "http://localhost:11434", "Model": "nomic-embed-text" }
```

Pull the models first: `ollama pull llama3.2 && ollama pull nomic-embed-text`.

## vLLM, llama.cpp and other OpenAI-compatible servers

Any server with `POST /v1/chat/completions` works the same way. Set `BaseAddress`, and `RequestPath` if the server uses a different path.

## Claude Code CLI

Uses your own Claude subscription. **Read [TERMS.md](TERMS.md#anthropic-claude-code) first.** In short: personal, local use, with the unmodified binary and your own login.

1. Install Claude Code. On Windows use the native installer, so that `claude.exe` is on `PATH`.
2. Run `claude` once and sign in with `/login`.
3. Configure:

```jsonc
"Connectors": {
  "claude-cli": { "Type": "Cli", "Preset": "ClaudeCode", "MaxConcurrency": 2, "Timeout": "00:03:00" }
},
"Models": [
  { "Name": "claude-code", "Connector": "claude-cli", "Model": "sonnet", "Tier": 4, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "reasoning" ] },
  { "Name": "claude-code-opus", "Connector": "claude-cli", "Model": "opus", "Tier": 5, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "reasoning" ], "Priority": 1 }
]
```

What the preset does:
- **Command line.** It runs `claude -p <prompt> --output-format stream-json --verbose --include-partial-messages --tools "" --disallowedTools "mcp__*" --strict-mcp-config --no-session-persistence --permission-mode dontAsk [--model …] [--system-prompt …]` in an empty temporary directory. These flags were checked against Claude Code 2.1.293.
- **Tools.** Add `"ToolCalling": "Prompted"` (and `tools` to the models' capabilities) to let agentic clients such as Claude Code use this connector for tool-bearing turns; see [Tool calling and agentic clients](#tool-calling-and-agentic-clients).
- **Long prompts.** Prompts longer than `MaxArgumentLength` go on stdin.
- **System messages.** Client system messages replace Claude Code's default system prompt.
- **Environment.** It removes `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` from the child environment. In print mode an API key would otherwise be used instead of your subscription. Set `RemoveEnvironmentVariables` yourself to change this.
- **Limits.** Usage-limit messages ("You've hit your session limit", "weekly limit", …) are quota errors. Account-wide limits cool down every model on the connector; "You've hit your Opus limit" cools down only the Opus model.
- **Extra flags** go in `Arguments`, e.g. `[ "--effort", "low" ]`.

Your user-level Claude settings (for example `~/.claude/CLAUDE.md`) still apply.

## GitHub Copilot CLI

Uses your Copilot plan's AI credits. See [TERMS.md](TERMS.md#github-copilot-cli).

1. Install the Copilot CLI (`npm install -g @github/copilot`, or a native package; on Windows make sure `copilot.exe` is on `PATH` or set `Executable`).
2. Sign in once with `copilot login`.

```jsonc
"Connectors": { "copilot-cli": { "Type": "Cli", "Preset": "GitHubCopilot", "MaxConcurrency": 2 } },
"Models": [
  { "Name": "copilot", "Connector": "copilot-cli", "Model": "claude-sonnet-5.5", "Tier": 4,
    "Capabilities": [ "chat", "code" ], "Priority": 2 }
]
```

The preset runs `copilot --prompt=<prompt> -s --no-color --stream off --no-ask-user --deny-tool=shell --deny-tool=write --deny-tool=url --deny-tool=memory --disable-builtin-mcps --no-custom-instructions --no-auto-update [--model=…]`.
- Copilot's valid model IDs change often; check `/model` in an interactive session. Copilot Free and Student plans can only use automatic model selection, so omit `Model` there.
- Copilot is billed per token in AI credits. Cost `0` treats your monthly allowance as free; give it a higher `Priority` than other free models if you want to save credits.

## OpenAI Codex CLI

Uses your ChatGPT plan. See [TERMS.md](TERMS.md#openai-codex-cli).

1. Install Codex (`npm install -g @openai/codex`).
2. Sign in with `codex login`, choosing "Sign in with ChatGPT".

```jsonc
"Connectors": { "codex-cli": { "Type": "Cli", "Preset": "Codex", "MaxConcurrency": 1 } },
"Models": [
  { "Name": "codex", "Connector": "codex-cli", "Model": "gpt-6.1-sol", "Tier": 4,
    "Capabilities": [ "chat", "code", "reasoning" ], "Priority": 2 }
]
```

- **Command line.** The preset runs `codex exec --json --sandbox read-only --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules --color never -c features.shell_tool=false -c web_search="disabled" [-m …] -`, with the prompt on stdin.
- **Billing.** It removes `CODEX_API_KEY`, which would otherwise take precedence and bill the API instead of your plan.
- **Limits.** "You've hit your usage limit" is treated as a quota error.
- **Concurrency.** Keep `MaxConcurrency` at 1. OpenAI's guidance for ChatGPT-login automation is one machine or a serialized job stream per login.

## Any other CLI

The `Custom` preset runs any command:
- standard output is the reply;
- a non-zero exit code is a failure;
- `QuotaErrorPatterns` marks quota errors.

```jsonc
"Connectors": {
  "llm": {
    "Type": "Cli", "Preset": "Custom", "Executable": "llm",
    "Arguments": [ "-m", "{model}" ],          // "{model}" is dropped when the model has no Model id
    "PromptInput": "Stdin",                    // or "Argument" with a "{prompt}" placeholder
    "QuotaErrorPatterns": [ "rate limit", "insufficient_quota" ]
  }
}
```

The executable is started directly, never through a shell, so prompts cannot inject commands.

## OpenAI API

```jsonc
"Connectors": { "openai": { "Type": "OpenAICompatible", "BaseAddress": "https://api.openai.com" } },
"Models": [
  { "Name": "openai-mini", "Connector": "openai", "Model": "<model id>", "Tier": 2, "ContextWindow": 128000,
    "Capabilities": [ "chat", "code", "tools", "vision" ], "InputCostPerMillionTokens": 0.4, "OutputCostPerMillionTokens": 1.6 }
]
```

Set `SemanticRouter:Connectors:openai:ApiKey`. If you need an organization header, add it under `Headers`, e.g. `"Headers": { "OpenAI-Organization": "org_…" }`.

## Anthropic API

The `Anthropic` connector speaks the Messages API (`POST /v1/messages`) with your API key, billed per token and separate from any Claude subscription. Requests from Anthropic-protocol clients (Claude Code, the Anthropic SDKs) pass through untouched, prompt caching and all; OpenAI-protocol requests are translated, including tools and streaming.

```jsonc
"Connectors": { "anthropic": { "Type": "Anthropic", "BaseAddress": "https://api.anthropic.com" } },
"Models": [
  { "Name": "anthropic-opus", "Connector": "anthropic", "Model": "claude-opus-5-5", "Tier": 5, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "tools", "vision", "reasoning" ], "InputCostPerMillionTokens": 15, "OutputCostPerMillionTokens": 75 }
]
```

Set `ApiKey`. `AnthropicVersion` (default `2023-06-01`) is sent as `anthropic-version`; add client headers you want relayed, such as `anthropic-beta`, to `ForwardHeaders`. The client's own `Authorization`/`x-api-key` headers are never forwarded. (Anthropic's OpenAI-compatible endpoint still works through an `OpenAICompatible` connector if you prefer it.)

## Google Gemini API

```jsonc
"Connectors": {
  "gemini": { "Type": "OpenAICompatible", "BaseAddress": "https://generativelanguage.googleapis.com",
              "RequestPath": "/v1beta/openai/chat/completions" }
},
"Models": [
  { "Name": "gemini-flash", "Connector": "gemini", "Model": "<model id>", "Tier": 3, "ContextWindow": 1000000,
    "Capabilities": [ "chat", "code", "tools", "vision" ], "InputCostPerMillionTokens": 0.3, "OutputCostPerMillionTokens": 2.5 }
]
```

Put your AI Studio key in `ApiKey`. The Gemini **CLI** is not supported; [TERMS.md](TERMS.md#google-gemini-cli-deliberately-not-included) explains why.

## Azure OpenAI

Azure puts the deployment in the path and authenticates with an `api-key` header. Use the path and API version your resource documents, for example:

```jsonc
"Connectors": {
  "azure": {
    "Type": "OpenAICompatible",
    "BaseAddress": "https://<resource>.openai.azure.com",
    "RequestPath": "/openai/deployments/<deployment>/chat/completions?api-version=<version>",
    "Headers": { "api-key": "<from secrets>" }
  }
}
```

## OpenRouter

```jsonc
"Connectors": {
  "openrouter": { "Type": "OpenAICompatible", "BaseAddress": "https://openrouter.ai", "RequestPath": "/api/v1/chat/completions" }
}
```

Set `ApiKey`, and use OpenRouter's `vendor/model` IDs as `Model`.

## Tool calling and agentic clients

Agentic clients (Claude Code, Codex CLI, agent frameworks) send every turn with tool definitions and expect tool calls back. How each connector type handles that:

| Connector | Tool-bearing requests |
|---|---|
| `OpenAICompatible` | Passed through (OpenAI clients) or translated from the Anthropic format: tool definitions, `tool_use`/`tool_calls` and tool results map 1:1, streaming included. Declare `tools` on models whose server supports function calling (LM Studio and Ollama with `qwen3-coder` or similar, vLLM, OpenRouter, OpenAI, Gemini). |
| `Anthropic` | Passed through (Anthropic clients) or translated from the OpenAI format. |
| `Cli` with `ToolCalling: None` (default) | Skipped; the request falls through to the next candidate. |
| `Cli` with `ToolCalling: Prompted` | The tools are described in the system prompt with a fixed call format; the CLI's reply is parsed for fenced `tool_call` blocks, which become structured tool calls for the client. Earlier tool calls and results are rendered into the transcript of the next run. Declare `tools` on the model to make it a candidate. |

Prompted tool calling keeps the CLI text-in/text-out (the vendor CLI's own tools stay disabled; the **client** runs the tools), so it fits the design rules in [TERMS.md](TERMS.md). It costs more than native tool use: every turn re-sends the whole context, there is no prompt caching, and it depends on the model following the format, which frontier models do and small ones often do not.

Across a tool-calling turn the router pins follow-up requests (those carrying tool results) to the model that issued the calls (`Middleware:PinToolCallingTurns`), so a turn never changes models halfway.

## Writing your own connector

Implement `IChatConnector` for anything that does not speak OpenAI chat-completions:

```csharp
public sealed class MyConnector(IHttpClientFactory http) : IChatConnector
{
    public string Name => "my-provider";

    public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken ct)
    {
        // request.Chat.Messages / request.Chat.Stream / request.Model.Model / request.RawBody
        // Either write a complete OpenAI-format response and return ConnectorResult.Completed,
        // or return ConnectorResult.Failed(ModelFailureKind.QuotaExceeded | Unavailable, "why")
        // *before writing anything*, so the router can fail over to the next model.
    }
}

builder.Services.AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .AddConnector<MyConnector>();
```

Models refer to it with `"Connector": "my-provider"`.
