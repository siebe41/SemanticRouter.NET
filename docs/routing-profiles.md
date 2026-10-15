# Routing profiles

The prompt text says how hard a request is. It does not say who is asking, what the code belongs to, or what is allowed to leave the machine. A **routing profile** is a named policy for that context: which models may serve it, how aggressive routing is, and whether prompts must stay local. The same prompt can go to a frontier API for one project and to a local model for another.

A profile only ever narrows. It removes models and raises or caps requirements; it never adds a model the catalog does not have. A request without a profile routes exactly as before.

## Defining profiles

```jsonc
"Profiles": {
  "private": { "LocalOnly": true, "Description": "Client code and secrets: never leaves this machine." },
  "frugal":  { "MaxTier": 2, "DeniedConnectors": [ "anthropic" ], "Strategy": "CheapestCapable" },
  "work":    { "MinTier": 2, "AllowedModels": [ "claude-*", "gpt-*" ], "Strategy": "MostCapable" }
}
```

| Key | Effect |
|---|---|
| `AllowedModels[]` / `DeniedModels[]` | Catalog names, `*` globs allowed (`api-*`, `*-mini`). Case-insensitive. An empty allow list allows every model. |
| `AllowedConnectors[]` / `DeniedConnectors[]` | Connector names. |
| `LocalOnly` | Only `IsLocal` models serve, including on failover. |
| `MinTier` | The required tier is raised to at least this. |
| `MaxTier` | The required tier is capped at this, so cheaper models are acceptable for every prompt. A local-only route still stays local. |
| `Strategy` | Replaces `Selection:Strategy`. A strategy the client asks for (`auto:best`) still wins. |
| `Description` | Shown on the dashboard and by `/v1/models`. |

Profiles apply everywhere a model is chosen. A model a profile denies is never tried:

- **Explicit model.** A client that asks for a denied model by name routes as `auto` inside the profile; the model is not forced.
- **Tool-turn pinning.** The pinned model is ignored when the profile denies it.
- **Failover and degradation.** Later candidates come from the same filtered list. A local-only profile whose local models are all down or cooling down still never reaches a cloud model: those local models are tried anyway, and when they fail the request fails.

## How a request picks a profile

The first of these that applies wins:

1. **The gateway client's profile** (`Middleware:ClientProfiles`, `{ "laptop": "private" }`). Clients are the names under `Middleware:GatewayKeys`. A client mapped here cannot choose another profile: its header and model name are ignored.
2. **The profile header**, `X-Router-Profile: work` (`Middleware:ProfileHeader`).
3. **The model name.** `auto@private`, `auto:best@work` or `claude-sonnet@frugal` asks for a profile, and so does a **virtual model** (below).
4. **The first matching rule** in `Middleware:ProfileRules`.

An unknown profile name is an error (400), not a silent fallback to the default.

### Rules

```jsonc
"Middleware": {
  "ProjectHeader": "X-Router-Project",
  "ProfileRules": [
    { "Profile": "private", "Projects": [ "acme-*" ] },
    { "Profile": "work", "Clients": [ "work-laptop" ] },
    { "Profile": "frugal", "ToolsInclude": [ "web_search" ] }
  ]
}
```

A rule matches when every condition it sets holds:

| Condition | Matches when |
|---|---|
| `Clients[]` | The gateway client is one of these. |
| `Projects[]` | The project matches one of these globs. The project is the `X-Router-Project` header, or else a hash of the client's own user id (Anthropic `metadata.user_id`, OpenAI `user`): `user:` and the first 12 hex digits of its SHA-256, as Activity shows it. The id itself can name an account or a session, so it is never stored. A user id that is a JSON object gives no project: Claude Code's (`{"device_id":…,"account_uuid":…,"session_id":…}`) names the machine and changes with every session. Label Claude Code's projects with the header instead ([clients.md](clients.md#claude-code)). |
| `ToolsInclude[]` | The request offers at least one tool matching these globs (for example `Bash`, `mcp__github__*`). |
| `ToolsExclude[]` | The request offers no tool matching these globs. |

### Virtual models

```jsonc
"Middleware": {
  "VirtualModels": {
    "private": { "Profile": "private", "Description": "Local models only." },
    "fast":    { "Strategy": "Fastest" }
  }
}
```

A virtual model is a model name clients can pick from their model menu. It routes as `auto` with its profile and strategy. `GET /v1/models` lists the virtual models, then `auto@{profile}` for every profile, then the catalog.

## Keeping a conversation local by what it contains

```jsonc
"Middleware": { "LocalOnlyWhen": { "Secrets": true, "FilePaths": false } }
```

With `Secrets`, a conversation that contains something that looks like a credential is routed as local-only, whatever profile it runs under. That covers API keys (OpenAI, Anthropic, GitHub, AWS, Slack, Google), private key blocks and JWTs. With `FilePaths`, absolute paths (`/home/…`, `~/…`, `C:\…`) do the same. Agentic clients mention paths in almost every turn, so `FilePaths` keeps most agent traffic local.

The scan covers every message, tool call and tool result, not only the last user message. It has a time limit per message. A scan that runs out of time counts as a match, so the request stays local.

The activity entry records which signal fired (`local: secret`), and the response carries `X-Prompt-Route-Local-Only: true`.

## Budgets per profile

```jsonc
"Usage": {
  "Budgets": [ { "Name": "side-projects", "Profile": "frugal", "Period": "Day", "MaxCostUsd": 1 } ]
}
```

A budget names exactly one of `Model`, `Connector` or `Profile`. A profile budget counts what requests under that profile cost. Once it is spent, the profile's requests stop going to models that cost money. Free models (local models and flat-rate subscriptions) keep serving it. The **Usage** page groups usage by profile.

## Time of day

```jsonc
"Selection": { "TimeZone": "Europe/Amsterdam" },
"Models": [ { "Name": "claude-opus", "ActiveHours": "08:00-18:00" } ]
```

A model with `ActiveHours` is only tried inside that daily window. The window can cross midnight (`22:00-06:00`). It follows `Selection:TimeZone` (an IANA id), or this machine's zone. Use it to keep a metered model for working hours, or to send overnight batch work to a model that is free at night.

## A queue limit for subscription CLIs

```jsonc
"Connectors": { "claude-code": { "Type": "Cli", "Preset": "ClaudeCode", "MaxConcurrency": 2, "MaxQueue": 4 } }
```

`MaxConcurrency` limits how many CLI processes run at once; later requests wait. Once `MaxQueue` requests (16 unless set) are already waiting, the next request goes to the next model in its plan instead of joining the queue. The skip does not put the model into cooldown.

## Seeing it

- **Dashboard → Profiles** edits the profiles, the rules, the virtual models, client mappings and the content signals. **What would each profile do?** routes one prompt under every profile side by side, without calling a model. It uses the applied configuration.
- **Response headers:** `X-Prompt-Route-Profile` and `X-Prompt-Route-Local-Only`.
- **Activity:** each entry shows its profile, project and the signals that kept it local, and the filter can show one profile's requests.
- **Admin API:** `POST api/classify` takes `{ "prompt": "…", "profile": "work" }` and returns the plan under that profile, with `requiredTier` after the profile's floor and ceiling.

## In code

```csharp
builder.Services
    .AddSemanticPromptRouting(o => o.Profiles["private"] = new RoutingProfile { LocalOnly = true })
    .ConfigureMiddleware(m => m.ClientProfiles["laptop"] = "private");

RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = prompt, Profile = "private" });
```

`PromptRoutingRequest.LocalOnly` forces local-only for a single request without a profile. `IPromptRouter.Profiles` lists the configured names.
