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
- [Model lineups: several models per provider](#model-lineups-several-models-per-provider)
- Local: [LM Studio](#lm-studio) · [Ollama](#ollama) · [vLLM / llama.cpp / others](#vllm-llamacpp-and-other-openai-compatible-servers)
- Subscription CLIs: [Claude Code](#claude-code-cli) · [GitHub Copilot CLI](#github-copilot-cli) · [OpenAI Codex CLI](#openai-codex-cli) · [Any other CLI](#any-other-cli)
- APIs: [OpenAI](#openai-api) · [OpenAI Responses](#openai-responses-api) · [Anthropic](#anthropic-api) · [Google Gemini](#google-gemini-api) · [Azure OpenAI](#azure-openai) · [OpenRouter](#openrouter)
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

## Model lineups: several models per provider

A connector can serve any number of catalog models: Claude's four families through one Claude Code login, or every model your LM Studio has downloaded. Each is its own entry under `Models` with the same `Connector`, its own `Model` id and its own `Tier`, so the router sends easy prompts to the small ones and keeps the big ones for prompts that need them.

You don't have to type them in. **Discover models** on the dashboard's Models page, or `semantic-router models` on the command line, asks the connector what it offers and suggests an entry for each model:

| Provider | Asked with | Suggested tier |
|---|---|---|
| Claude (Claude Code CLI, Anthropic API, Copilot) | the preset's names (`haiku`, `sonnet`, `opus`, `claude-fable-5-1`), `GET /v1/models`, or the Copilot SDK | by family, one rung each: Haiku **2**, Sonnet **3**, Opus **4**, Fable **5** |
| LM Studio | `GET /v1/models` and `GET /api/v0/models` | from the [model-family table](#the-model-family-table) when it has a tier for the family, else by size: ≤ 9B **1**, ≤ 35B **2**, larger **3** |
| Ollama | `GET /v1/models`, `/api/tags`, `/api/ps`, `POST /api/show` | family table, else by size, as above |
| vLLM, llama.cpp, other OpenAI-compatible servers | `GET /v1/models` | family table, else by the size in the model id, as above |
| Other APIs (OpenAI, Gemini, OpenRouter, …) | `GET /v1/models` | family table (Gemini Flash **2**, Gemini Pro **3**, …), else 2 for `mini`/`nano`/`lite`/`small` models, otherwise 3. Not ticked by default: review the tier and set the prices. |

Each suggestion explains itself, and every field can be changed before it is added:
- **Where the tier came from.** `family` (the family table), `size` or `name`, shown next to each suggestion on the dashboard and after the tier in `semantic-router models` (`T2 (family)`).
- **Capabilities.** `code` for coder models; `tools`, `vision` and `reasoning` when the server reports them (LM Studio's `tool_use`, Ollama's `tools`, `vision` and `thinking`) or the family table lists them. The table can also take a capability away: Gemma 3 has no native tool calling, distilled DeepSeek R1 models do not call tools, and a 3B Llama 3.2 gets no `tools` even when Ollama reports them, because its tool calls are unreliable below 7B. `tools` is only claimed when the connector can carry tools: HTTP, Copilot, or a CLI with `ToolCalling: Prompted`.
- **Context.** For a local server that reports load state, the context the model is **loaded with**, not the most it supports: LM Studio and Ollama load models with their own default context unless told otherwise. A model that is not loaded gets `0` (unknown) and a note to set it. Claude Code models get 200K, the conservative assumption; raise it if your plan has 1M.
- **Prices.** Anthropic API models get the list price; local models and subscriptions are free. Check prices before relying on them.
- **Names.** The connector and the model id: `lmstudio-qwen3-8b`, `ollama-llama3.2`, `claude-sonnet`. Models already in the catalog are marked, not suggested again.

The Claude lineup through Claude Code, as `semantic-router init` writes it:

```jsonc
"Models": [
  { "Name": "claude-haiku",     "Connector": "claude-cli", "Model": "haiku",            "Tier": 2, "ContextWindow": 200000, "Capabilities": [ "chat", "code", "reasoning", "tools" ] },
  { "Name": "claude-sonnet",    "Connector": "claude-cli", "Model": "sonnet",           "Tier": 3, "ContextWindow": 200000, "Capabilities": [ "chat", "code", "reasoning", "tools" ] },
  { "Name": "claude-opus",      "Connector": "claude-cli", "Model": "opus",             "Tier": 4, "ContextWindow": 200000, "Capabilities": [ "chat", "code", "reasoning", "tools" ] },
  { "Name": "claude-fable-5-1", "Connector": "claude-cli", "Model": "claude-fable-5-1", "Tier": 5, "ContextWindow": 200000, "Capabilities": [ "chat", "code", "reasoning", "tools" ], "Enabled": false }
]
```

With the default ladder, unmatched prompts (tier 3) go to Sonnet, the architecture and debugging routes (tier 4) to Opus, and Fable is used only when a prompt needs tier 5: `auto:best`, a tier-5 route, or [retry escalation](routing-quality.md). Fable starts disabled because not every plan includes it. If Opus is scarce on your plan, put Sonnet on tier 4 as well and give Opus `Priority` 1.

### The model-family table

Size is a weak guide on its own: a recent coder can beat an older, larger general model on code, and tool calling, vision and reasoning differ by family. Discovery and `semantic-router init` therefore look a model up in a table of families before falling back to the size rule. The table ships with the router (Qwen3, Qwen3 Coder, Qwen2.5, QwQ, DeepSeek, gpt-oss, Llama 3.x and 2, Devstral, Codestral, Magistral, Mistral, Gemma 3, Phi-4, Granite, GLM-4.5, Gemini, …). It is a starting point, not a benchmark: the suggestions stay editable, and the [reliability tracking](routing-quality.md#model-reliability) catches a model that does badly on your work.

Add your own families, or change the built-in ones, in `model-families.json` in the configuration directory, next to `semantic-router.json` (`UseModelFamilyFile(path)` in your own host). It needs no restart: discovery reads it each time. A family in your file replaces the built-in family with the same name, and the others stay.

```jsonc
{
  "families": [
    // Matched against the model id in lower case, anywhere in it; * stands for anything. The longest match wins.
    { "family": "Qwen3 Coder", "match": ["qwen3-coder"],
      "bands": [ { "upTo": 9.5, "tier": 1 }, { "upTo": 40, "tier": 3 }, { "tier": 4 } ],  // tiers by size (billions of parameters)
      "capabilities": ["code", "tools"] },
    { "family": "Our fine-tune", "match": ["acme/widget*"], "tier": 3, "notes": "Trained on our codebase." },
    { "family": "Llama 2", "match": ["llama2", "llama-2"], "adjust": -1, "without": ["tools"] }
  ]
}
```

| Field | Meaning |
|---|---|
| `family` | Its name; shown with the suggestion. |
| `match` | Patterns for the model id (lower case, anywhere, `*` for anything). The longest matching pattern of all families wins, so `qwen3-coder` beats `qwen3`. |
| `tier` | A fixed tier, whatever the size (hosted models). |
| `bands` | Tiers by size, smallest first: `upTo` (billions of parameters; leave it out on the last band) and `tier`. Used when the size is known and there is no `tier`. |
| `adjust` | Added to the size rule's tier when there is neither `tier` nor `bands` (at least 1). |
| `capabilities` | Capabilities the family has. |
| `without` | Capabilities it does not have, even when the server reports them. |
| `toolsFrom` | The size (billions) from which its tool calls are reliable; smaller models get no `tools`. |
| `notes` | Shown with the suggestion. |

A family without `tier`, `bands` or `adjust` leaves the tier to the size rule and only says what it can do. An entry that cannot be read is skipped with a warning on the dashboard and in `semantic-router models`; a file that cannot be read at all leaves the built-in table in use.

### Loading models on demand

Local servers differ in whether a model has to be loaded before it can answer. Discovery tells you which kind you have:

| Server | Loading | What the router does |
|---|---|---|
| LM Studio, just-in-time loading **on** | **On demand**: `/v1/models` lists every downloaded model, and a request for one that is not loaded loads it. The request waits for the load, and LM Studio may unload another model to make room. | Suggests every model; those not loaded get `Priority` 1, so loaded models win ties. |
| LM Studio, just-in-time loading **off** | **Loaded only**: only loaded models are listed and answer. | Suggests models that are not loaded as **disabled**. `doctor` fails a catalog model that is downloaded but not loaded and says to `lms load` it or turn just-in-time loading on. |
| Ollama | **On demand**, always: a model loads on its first request and unloads after its keep-alive time (5 minutes by default). `OLLAMA_MAX_LOADED_MODELS` limits how many stay loaded. | As LM Studio with just-in-time loading on. Ollama's cloud models (`…-cloud`) run on ollama.com, so they are never suggested as local, and `init` leaves them out. |
| vLLM, llama.cpp, other servers | Unknown: the server serves what it lists. | Suggests what is listed, marked local but not ticked: a server on a local address can still be a proxy (LiteLLM, a gateway) that sends prompts to a cloud API, and `IsLocal` is what keeps `LocalOnly` prompts on your machine. |

The first request to a cold model can take long enough to hit the connector's `Timeout`; raise it (LM Studio's default in `init` is 2 minutes) if large models time out while loading. `semantic-router doctor` also warns when a model's `ContextWindow` is larger than the context it is loaded with, since requests between the two would be sent to it and fail.

## LM Studio

Start the server (the Developer tab, or `lms server start --port 1234`). Load your chat models and an embedding model, then copy their exact IDs from `curl http://localhost:1234/v1/models`.

**If "Require Authentication" is on** in LM Studio's server settings, every request needs the server's API token, sent as `Authorization: Bearer <token>` (which is what the connector and the embedding provider send when `ApiKey` is set). Give the router the token without writing it into a file:

```bash
dotnet user-secrets set "SemanticRouter:Connectors:lmstudio:ApiKey" "<token>"
dotnet user-secrets set "SemanticRouter:Embeddings:ApiKey" "<token>"     # when LM Studio also serves embeddings
# or: export SemanticRouter__Connectors__lmstudio__ApiKey=<token>
# or reference LM Studio's own variable: "ApiKey": "${env:LM_API_TOKEN}"
```

Without the token, LM Studio answers `401`. The router reports that as "the server requires authentication; set SemanticRouter:Connectors:lmstudio:ApiKey" (in the log, the 503 detail and the dashboard's **Discover models**), fails over to the next model and cools `lmstudio` down for the short unavailable cooldown. The PoC sample's preflight reads `LM_API_TOKEN` when no key is configured.

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
  { "Name": "claude-haiku", "Connector": "claude-cli", "Model": "haiku", "Tier": 2, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "reasoning" ] },
  { "Name": "claude-sonnet", "Connector": "claude-cli", "Model": "sonnet", "Tier": 3, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "reasoning" ] },
  { "Name": "claude-opus", "Connector": "claude-cli", "Model": "opus", "Tier": 4, "ContextWindow": 200000,
    "Capabilities": [ "chat", "code", "reasoning" ] }
]
```

The `haiku`, `sonnet` and `opus` aliases follow Anthropic's latest model of each family; add `claude-fable-5-1` on tier 5 if your plan includes it ([Model lineups](#model-lineups-several-models-per-provider)).

What the preset does:
- **Command line.** It runs `claude -p --output-format stream-json --verbose --include-partial-messages --tools "" --disallowedTools "mcp__*" --strict-mcp-config --no-session-persistence --permission-mode dontAsk [--model …] [--system-prompt …]` in an empty directory that only you can write to. These flags were checked against Claude Code 2.1.293.
- **Tools.** Add `"ToolCalling": "Prompted"` (and `tools` to the models' capabilities) to let agentic clients such as Claude Code use this connector for tool-bearing turns; see [Tool calling and agentic clients](#tool-calling-and-agentic-clients).
- **Prompt on stdin.** The conversation is written to the CLI's standard input, not passed as an argument, because other users of the machine can read a process's arguments (`ps`) and process auditing records them. `"PromptInput": "Argument"` puts it after `-p` instead, up to `MaxArgumentLength` characters.
- **System messages.** Client system messages replace Claude Code's default system prompt. They are passed with `--system-prompt`, so they are visible on the command line; one longer than `MaxArgumentLength` is moved into the conversation instead.
- **Environment.** It removes `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` from the child environment. In print mode an API key would otherwise be used instead of your subscription. Set `RemoveEnvironmentVariables` yourself to change this. It sets `DISABLE_AUTOUPDATER=1` and `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`, so a run contacts only Anthropic's API: no auto-update, telemetry or error reporting. `EnvironmentVariables` overrides them (for example `"CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "0"`).
- **Limits.** Usage-limit messages ("You've hit your session limit", "weekly limit", …) are quota errors. Account-wide limits cool down every model on the connector; "You've hit your Opus limit" cools down only the Opus model.
- **Extra flags** go in `Arguments`, e.g. `[ "--effort", "low" ]`.

Your user-level Claude settings (for example `~/.claude/CLAUDE.md`) still apply.

**Where CLIs run.** Every CLI run (and `doctor`'s checks) gets a fresh directory under a parent that only you can write to, because a CLI reads instructions, settings and hooks from its working directory, and Claude Code reads `CLAUDE.md` from every directory above it. On Linux the parent is `$XDG_RUNTIME_DIR/semantic-router`, or `~/.cache/semantic-router` (`$XDG_CACHE_HOME`) when there is no runtime directory; it is never under the shared `/tmp`. On macOS it is under your own `$TMPDIR`, and on Windows under `%TEMP%`. On Linux and macOS the parent is created with mode 0700, and a CLI request fails (with a message naming the directory) if it, or a directory above it, can be written to by other users.

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

The preset runs `copilot -s --no-color --stream off --no-ask-user --deny-tool=shell --deny-tool=write --deny-tool=url --deny-tool=memory --disable-builtin-mcps --no-custom-instructions --no-auto-update [--model=…]` with the prompt piped to it on stdin, so it is not visible on the command line. `"PromptInput": "Argument"` passes it as `--prompt=<prompt>` instead, up to `MaxArgumentLength` characters.
- Copilot's valid model IDs change often; check `/model` in an interactive session. Copilot Free and Student plans can only use automatic model selection, so omit `Model` there.
- Copilot is billed per token in AI credits. Cost `0` treats your monthly allowance as free; give it a higher `Priority` than other free models if you want to save credits.

## GitHub Copilot SDK

Uses your Copilot plan through GitHub's official [Copilot SDK](https://github.com/github/copilot-sdk) (`GitHub.Copilot.SDK`), the supported way for an application to use a person's Copilot subscription. Prefer it to the CLI preset above: replies stream, the client's tools are real tool calls instead of text in a prompt, usage is reported, and **Discover** lists the models your account can use. See [TERMS.md](TERMS.md#github-copilot-sdk).

1. Install the Copilot CLI (`npm install -g @github/copilot`). The SDK runs it as its runtime.
2. Sign in once with `copilot` and `/login`, or with `gh auth login`.
3. Run `semantic-router doctor`. It starts the runtime and lists your models, or says what is missing.

```jsonc
"Connectors": { "copilot": { "Type": "Copilot", "MaxConcurrency": 2 } },
"Models": [
  { "Name": "copilot-sonnet", "Connector": "copilot", "Model": "claude-sonnet-4.5", "Tier": 4,
    "Capabilities": [ "chat", "code", "tools" ], "Priority": 2 }
]
```

- **Package.** `SemanticRouter.Connectors.Copilot` (`.AddCopilotConnector()`), so the core libraries keep their dependencies. The `semantic-router` tool includes it. Without the package, a `Copilot` connector skips every request and says why.
- **Runtime.** `Executable` is the `copilot` CLI (default: `copilot` on `PATH`). `HomeDirectory` is its `COPILOT_HOME` (default `~/.copilot`, so the CLI's login is reused). One runtime process serves every request and starts on first use. Applying a new configuration keeps it.
- **What Copilot is allowed to do.** The SDK runs in its `Empty` mode, so none of Copilot's own tools exist (no shell, file edits or web access). Only the client's tools are declared, without handlers. When the model calls one, the call goes back to the client, which runs it and sends the result with its next request.
- **History.** The SDK cannot be given earlier assistant turns. Each request runs in a fresh session with the conversation rendered as a transcript, the same way the CLI connectors do it. The session is deleted when the turn ends.
- **Failures.** Quota and rate limits (`session.error` of type `quota` or `rate_limit`, HTTP 402/429) cool the model down. The runtime's retry-after time is honoured. An exhausted premium-request allowance cools only that model, because Copilot's included models keep working. Weekly and global rate limits cool every model on the connector. A missing sign-in fails over with a hint. The SDK's automatic model switching is declined, so the router's own failover decides.
- **Not carried.** Images, and features the [translation table](#what-survives-translation) lists for CLIs. Reasoning is streamed when the client asks for it.
- The CLI preset above keeps working and remains the fallback (for example on a machine where only the native `copilot` binary is installed and the SDK's protocol does not match its version).

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

The executable is started directly, never through a shell, so prompts cannot inject commands. With `"PromptInput": "Argument"`, a prompt that starts with `-` gets a leading space when `{prompt}` is a whole argument, so the CLI cannot read it as an option (inside a larger argument, such as `--prompt={prompt}`, it is left as is). A bare name (`llm`) is looked up on the absolute `PATH` entries only and started by its full path, never from the current directory; a relative path with a folder (`tools/llm`) is taken relative to the router's current directory.

## OpenAI API

```jsonc
"Connectors": { "openai": { "Type": "OpenAICompatible", "BaseAddress": "https://api.openai.com" } },
"Models": [
  { "Name": "openai-mini", "Connector": "openai", "Model": "<model id>", "Tier": 2, "ContextWindow": 128000,
    "Capabilities": [ "chat", "code", "tools", "vision" ], "InputCostPerMillionTokens": 0.4, "OutputCostPerMillionTokens": 1.6 }
]
```

Set `SemanticRouter:Connectors:openai:ApiKey`. If you need an organization header, add it under `Headers`, e.g. `"Headers": { "OpenAI-Organization": "org_…" }`.

## OpenAI Responses API

The `Responses` connector speaks `POST /v1/responses`. Use it for OpenAI's own API when your clients are Codex or the Agents SDK. Their requests then pass through untouched, so hosted tools (`web_search`, `file_search`, …), encrypted reasoning and `previous_response_id` keep working. Chat-completions and Anthropic clients are translated to it.

```jsonc
"Connectors": { "openai-responses": { "Type": "Responses", "BaseAddress": "https://api.openai.com" } },
"Models": [
  { "Name": "openai-frontier", "Connector": "openai-responses", "Model": "<model id>", "Tier": 5,
    "Capabilities": [ "chat", "code", "tools", "vision", "reasoning" ], "InputCostPerMillionTokens": 2, "OutputCostPerMillionTokens": 8 }
]
```

Set `ApiKey`. The router sends `store: false` on translated requests.

## Anthropic API

The `Anthropic` connector speaks the Messages API (`POST /v1/messages`) with your API key, billed per token and separate from any Claude subscription. Requests from Anthropic-protocol clients (Claude Code, the Anthropic SDKs) pass through untouched, prompt caching and all; OpenAI-protocol requests are translated, including tools, streaming and [cache markers](#prompt-caching-on-translated-requests).

```jsonc
"Connectors": { "anthropic": { "Type": "Anthropic", "BaseAddress": "https://api.anthropic.com" } },
"Models": [
  { "Name": "anthropic-sonnet", "Connector": "anthropic", "Model": "claude-sonnet-5-5", "Tier": 3, "ContextWindow": 1000000,
    "Capabilities": [ "chat", "code", "tools", "vision", "reasoning" ], "InputCostPerMillionTokens": 2, "OutputCostPerMillionTokens": 10 },
  { "Name": "anthropic-opus", "Connector": "anthropic", "Model": "claude-opus-5-5", "Tier": 4, "ContextWindow": 1000000,
    "Capabilities": [ "chat", "code", "tools", "vision", "reasoning" ], "InputCostPerMillionTokens": 4, "OutputCostPerMillionTokens": 20 }
]
```

Set `ApiKey`, then **Discover models** lists the models your key can use with their list prices and context windows. `AnthropicVersion` (default `2023-06-01`) is sent as `anthropic-version`; add client headers you want relayed, such as `anthropic-beta`, to `ForwardHeaders`. The client's own `Authorization`/`x-api-key` headers are never forwarded. (Anthropic's OpenAI-compatible endpoint still works through an `OpenAICompatible` connector if you prefer it.)

### Prompt caching on translated requests

Anthropic only caches what a request marks with `cache_control`. Claude Code marks its own requests, which pass through untouched. Requests the router translates (from OpenAI clients, Aider, Codex) carry no markers of their own, so the router adds them (`PromptCaching: Auto`, the default):

- **One breakpoint at the end of the system prompt.** Tools and system come first in Anthropic's prompt and stay the same every turn, so this caches both. Without a system prompt it goes on the last tool. The system text itself is not changed; it is sent as one text block so it can carry the marker.
- **Automatic caching for the conversation** (a top-level `cache_control`). Anthropic puts that breakpoint on the last block and moves it forward as the conversation grows, so each turn reads the previous turns from the cache.
- **Only above the model's cacheable minimum:** 4,096 estimated tokens on Opus 4.5 and 4.6 and Haiku 4.5, 512 on other models. A shorter prompt gets no markers.
- **`CacheTtl`:** `5m` (default) or `1h`, on both markers. Writing a one-hour entry costs twice the input price instead of 1.25 times, so it only pays off when turns are often more than five minutes apart.

The first turn above the minimum pays the cache-write price on its prompt; later turns read it. The activity entry and the Usage page show the reads and writes ([observability.md](observability.md)). A request from an Anthropic client that the router rebuilds keeps that client's own markers, and the router adds none. Set `PromptCaching: Off` for an Anthropic-compatible server that rejects `cache_control`.

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
| `Anthropic` | Passed through (Anthropic clients) or translated from the OpenAI and Responses formats. |
| `Responses` | Passed through (Responses clients) or translated from the chat-completions and Anthropic formats. |
| `Copilot` | Native: the tools are declared to the Copilot SDK without handlers, and each call the model makes is returned to the client as a structured tool call. Declare `tools` on the model. |
| `Cli` with `ToolCalling: None` (default) | Skipped; the request falls through to the next candidate. |
| `Cli` with `ToolCalling: Prompted` | The tools are described in the system prompt with a fixed call format; the CLI's reply is parsed for fenced `tool_call-<token>` blocks, which become structured tool calls for the client. Earlier tool calls and results are rendered into the transcript of the next run. Declare `tools` on the model to make it a candidate. |

Prompted tool calling keeps the CLI text-in/text-out (the vendor CLI's own tools stay disabled; the **client** runs the tools), so it fits the design rules in [TERMS.md](TERMS.md). It costs more than native tool use: every turn re-sends the whole context, there is no prompt caching, and it depends on the model following the format, which frontier models do and small ones often do not.

### Prompted tool calling in practice

- **Text streams.** The reply's text goes to the client as the CLI writes it. Only a line that opens a fence or a JSON object is held back until the router can tell whether it is a tool call; ordinary code blocks (` ```python `) stream like text. Calls are sent as structured tool calls at the end of the reply, after any text. `StreamPromptedReplies: false` buffers the whole reply instead. The trade-off: once text has been streamed, a run that then fails cannot fail over to another model.
- **Near misses count.** Besides the requested ` ```tool_call ` block, the router accepts:
  - another tool-ish label (`tool_use`, `function_call`, …);
  - a ` ```json ` or unlabelled block, or a bare JSON object, that names one of the request's tools;
  - an array of calls, or a `{"tool_calls": […]}` wrapper;
  - OpenAI-style `{"function": {"name", "arguments": "<json string>"}}`;
  - trailing commas.

  Tool names match case-insensitively. JSON that names no tool (an answer that happens to contain `{"name": "Bob"}`) stays text.
- **Each run has its own label.** The instructions ask for ` ```tool_call-7f3a9c01 `, with a random token that is new for every run, and earlier calls in the transcript are shown with it. Text written before the run (a file, a web page, an issue) cannot carry it.
- **Tool results are data.** Each result is rendered between `[tool_result]` and `[/tool_result]`, and the instructions say a call inside one is not the model's. Before it goes into the transcript, a result is defused: runs of three or more backticks are broken up with zero-width spaces, a line that starts a JSON object or array gets a zero-width space in front, and a line such as `[user]` or `[/tool_result]` gets a backslash. A model that repeats a hostile result word for word therefore writes no call, and the result cannot pose as the end of the result or as a user turn.
- **Strict parsing.** `PromptedToolParsing: Strict` reads only the run's own fence as a call, naming one of the request's tools, at the end of the reply. Bare JSON, ` ```json ` and other labels stay text, and calls followed by more text are kept as text (`tool call not at the end of the reply, kept as text` in Activity). It is harder to trick, but models that do not follow the format answer in text instead of calling the tool. `Lenient` (the default) keeps the near misses above.
- **Calls are marked.** An activity entry whose reply carried prompted calls says so: `prompted tool calls: Read, Bash`. The client still decides whether to run each call under its own permission settings; the router only reads it out of the text.
- **Broken calls are repaired once.** When a block meant as a call cannot be read (invalid JSON, a tool that does not exist, missing arguments), the router runs the CLI once more with a short prompt. That prompt holds only the broken block, the problem and that tool's schema, not the conversation. The repaired call replaces the broken block. If the repair fails too, the block reaches the client as text. Either way the activity entry says so (`tool call repaired`, or `malformed tool call: …`), and the log has a line. `RepairToolCalls: false` turns the repair run off.
- **Measured.** The dashboard's Overview shows time to first byte and total time per connector. `benchmarks/SemanticRouter.Benchmarks -- latency <router url> <model> …` runs a fixed three-turn agentic task (a Read tool, a scripted tool result, a follow-up) against any models and prints time to first output, total time and input tokens per turn.

  On the simulated Claude Code agent from the tests (`fake-cli` in `claude-agent` mode: 1.5 s start-up, then a word every 30 ms), streaming moves the first output from the end of the reply to the start:

  | Connector | Turn | First output p50 | Total p50 | Input tokens |
  |---|---|---|---|---|
  | buffered (`StreamPromptedReplies: false`, the previous behaviour) | 1 (text + tool call) | 2864 ms | 2868 ms | 261 |
  | streaming (default) | 1 (text + tool call) | 1607 ms | 2875 ms | 261 |
  | buffered | 2 (answer after the tool result) | 2936 ms | 2939 ms | 392 |
  | streaming | 2 | 1594 ms | 2924 ms | 392 |

  Total time and input tokens do not change: every turn still starts a CLI process and re-sends the transcript. [TERMS.md](TERMS.md#session-reuse-and-long-lived-processes) explains why session reuse and long-lived processes are not used. Real CLIs start more slowly and write longer replies than the simulation, so the gain in time to first output is larger. Run the benchmark against your own models to see it.

Across a tool-calling turn the router pins follow-up requests (those carrying tool results) to the model that issued the calls (`Middleware:PinToolCallingTurns`), so a turn never changes models halfway.

## What survives translation

When a backend speaks the client's protocol, the body passes through untouched (only `model` is rewritten). Otherwise the request is translated, and anything the backend cannot carry is listed in the `X-Prompt-Route-Dropped` response header and the activity entry's `dropped` field.

| Feature | OpenAI-compatible backend | Anthropic backend | Responses backend | CLI backend |
|---|---|---|---|---|
| Text, images, streaming | ✓ | ✓ | ✓ | text only (images: skipped) |
| Tools and tool results | ✓ | ✓ | ✓ | with `ToolCalling: Prompted` |
| Codex custom tools and namespaces | as functions | as tools | ✓ | with `ToolCalling: Prompted` |
| Reasoning request (`reasoning_effort`, `thinking`, `reasoning.effort`) | `reasoning_effort` on models with `reasoning` | `thinking` with a budget on models with `reasoning` | `reasoning.effort` on models with `reasoning` | dropped |
| Reasoning in the reply | read from `reasoning_content` | `thinking` blocks | reasoning summaries | n/a |
| Structured output (`response_format`, `text.format`) | ✓ | a forced `structured_output` tool for a JSON schema; an instruction for JSON mode | ✓ | dropped |
| `parallel_tool_calls: false` | ✓ | `disable_parallel_tool_use` | ✓ | dropped |
| Stop reasons (length, refusal, tool use) | ✓ | ✓ (`refusal`, `pause_turn`) | ✓ (`incomplete`) | end of turn, or tool use with prompted tools |
| Hosted tools (`web_search`, …) | dropped | dropped | ✓ | dropped |
| Prompt caching | automatic | markers added above the model's minimum ([`PromptCaching`](#prompt-caching-on-translated-requests)) | automatic | n/a |
| `cache_control` markers from an Anthropic client | dropped (the backend caches on its own) | ✓ | dropped (the backend caches on its own) | dropped |
| `prompt_cache_key` | ✓ | not needed (the router adds cache markers) | ✓ | not needed |
| Cached-token counts in the reply | `prompt_tokens_details.cached_tokens` | `cache_read_input_tokens`, `cache_creation_input_tokens` (also on a streamed `message_delta`) | `input_tokens_details.cached_tokens` | read from the CLI's usage (claude, codex) |
| Protocol-only features (documents, `logprobs`, `n` > 1, audio, `previous_response_id`, reasoning items) | dropped | dropped | dropped | dropped |

Notes:

- **Reasoning across protocols.** Reasoning a backend returns is shown to the client in its own format. An Anthropic client gets a `thinking` block signed `semantic-router-relayed`. Anthropic's API would reject that signature, so the router removes such blocks before a native Anthropic backend sees the conversation. Real Anthropic thinking blocks pass through.
- **Thinking and a schema don't mix.** Anthropic cannot think while a tool is forced, so a JSON-schema request to an Anthropic backend drops the reasoning request. Thinking also needs `max_tokens` above 1,024. While thinking is on, `temperature` and `top_p` are not sent.
- **Declare `reasoning` only on models that accept it.** Servers that don't know `reasoning_effort` may reject the request.

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
