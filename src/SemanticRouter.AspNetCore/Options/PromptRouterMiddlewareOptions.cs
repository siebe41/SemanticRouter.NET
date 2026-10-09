using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What the prompt routing middleware does once a prompt is classified.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptRouterMode>))]
public enum PromptRouterMode
{
    /// <summary>
    /// Attach the decision to the request (<see cref="IPromptRoutingFeature"/>) and response headers,
    /// then continue down the pipeline. Your endpoint decides what to do with it.
    /// </summary>
    Annotate = 0,

    /// <summary>
    /// Serve the request: try the ranked models through their connectors (with failover) and stream the reply back
    /// in OpenAI chat-completions format. Short-circuits the pipeline.
    /// </summary>
    Dispatch = 1,
}

/// <summary>Which chat messages contribute to the routed prompt text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptMessageSelection>))]
public enum PromptMessageSelection
{
    /// <summary>Only the most recent <c>user</c> message: routes on the current ask. Default.</summary>
    LastUserMessage = 0,

    /// <summary>Every <c>user</c> message, joined.</summary>
    AllUserMessages = 1,

    /// <summary>Every message, including <c>system</c> and <c>assistant</c> turns: routes on the full context size.</summary>
    AllMessages = 2,
}

/// <summary>
/// Options for <c>UsePromptRouter()</c>. Bound from <c>SemanticRouter:Middleware</c>.
/// </summary>
public sealed class PromptRouterMiddlewareOptions
{
    /// <summary>Default prefix for routing response headers.</summary>
    public const string DefaultHeaderPrefix = "X-Prompt-Route";

    /// <summary>Annotate (default) or dispatch.</summary>
    public PromptRouterMode Mode { get; set; } = PromptRouterMode.Annotate;

    /// <summary>
    /// Request path prefixes the middleware applies to (segment-aware, case-insensitive). When empty, every
    /// <c>POST</c> with a JSON body is inspected. Required in <see cref="PromptRouterMode.Dispatch"/> mode so
    /// unrelated endpoints are never proxied.
    /// </summary>
    public IList<string> PathPrefixes { get; } = new List<string>();

    /// <summary>The Anthropic path used when <see cref="AnthropicPathPrefixes"/> is empty.</summary>
    public const string DefaultAnthropicPathPrefix = "/v1/messages";

    /// <summary>
    /// Request path prefixes that carry Anthropic Messages API bodies (<c>POST /v1/messages</c>), as sent by Claude
    /// Code and the Anthropic SDKs. When empty, <see cref="DefaultAnthropicPathPrefix"/> applies. Responses and
    /// errors on these paths use the Anthropic format whatever backend answers.
    /// </summary>
    public IList<string> AnthropicPathPrefixes { get; } = new List<string>();

    /// <summary>The Responses API path used when <see cref="ResponsesPathPrefixes"/> is empty.</summary>
    public const string DefaultResponsesPathPrefix = "/v1/responses";

    /// <summary>
    /// Paths that carry OpenAI Responses API bodies (<c>POST /v1/responses</c>), as sent by Codex CLI. When empty,
    /// <see cref="DefaultResponsesPathPrefix"/> applies. Only the path itself is routed: <c>/v1/responses/{id}</c> and
    /// other sub-paths pass through to the application.
    /// </summary>
    public IList<string> ResponsesPathPrefixes { get; } = new List<string>();

    /// <summary>The Responses prefixes in effect: the configured ones, or the default.</summary>
    [JsonIgnore]
    public IEnumerable<string> EffectiveResponsesPathPrefixes =>
        ResponsesPathPrefixes.Any(p => !string.IsNullOrWhiteSpace(p)) ? ResponsesPathPrefixes : [DefaultResponsesPathPrefix];

    /// <summary>The Anthropic prefixes in effect: the configured ones, or the default.</summary>
    [JsonIgnore]
    public IEnumerable<string> EffectiveAnthropicPathPrefixes =>
        AnthropicPathPrefixes.Any(p => !string.IsNullOrWhiteSpace(p)) ? AnthropicPathPrefixes : [DefaultAnthropicPathPrefix];

    /// <summary>
    /// Maps the model names clients send to routing aliases or catalog models, so tools keep their own model
    /// settings and the router still decides. Keys are case-insensitive globs (<c>*</c> matches anything), e.g.
    /// <c>"*haiku*": "auto:cheapest"</c>, <c>"*opus*": "family:opus"</c> (the catalog's Opus model, else the most capable), <c>"gpt-*": "auto"</c>. Names that match no
    /// pattern and no catalog model are routed as <c>auto</c>.
    /// </summary>
    public IDictionary<string, string> ModelAliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pin the follow-up requests of a tool-calling turn (those carrying tool results) to the model that issued the
    /// tool calls, instead of classifying them again. Default true.
    /// </summary>
    public bool PinToolCallingTurns { get; set; } = true;

    /// <summary>
    /// Raise the tier when a client sends the same prompt again shortly after it was answered (a common sign of a
    /// poor answer). Off by default.
    /// </summary>
    public RetryEscalationOptions RetryEscalation { get; set; } = new();

    /// <summary>
    /// Route by the conversation, not only its latest message: keep the tier a conversation needed on recent turns, so
    /// a short follow-up ("ok, go ahead") does not drop a task to a weaker model, and stay on the same model while it
    /// fits. On by default.
    /// </summary>
    public ConversationRoutingOptions Conversation { get; set; } = new();

    /// <summary>
    /// Inline callouts: a message that starts with <c>@opus</c>, <c>@local</c>, <c>@best</c>, <c>@tier3</c> or a
    /// catalog model name routes as asked, and the choice holds for the rest of the conversation until <c>@auto</c>.
    /// On by default.
    /// </summary>
    public CalloutOptions Callouts { get; set; } = new();

    /// <summary>
    /// Notice, after the fact, when a routing decision was not good enough (retried, corrected, overridden with a
    /// callout, failing tool calls, an empty reply) and record it with the decision, without prompt text. On by default.
    /// </summary>
    public FeedbackOptions Feedback { get; set; } = new();

    /// <summary>The request header that names a routing profile (<c>SemanticRouter:Profiles</c>). Default <c>X-Router-Profile</c>.</summary>
    public string ProfileHeader { get; set; } = "X-Router-Profile";

    /// <summary>
    /// The request header that carries a project label for <see cref="ProfileRules"/> and the activity log. Default
    /// <c>X-Router-Project</c>. Without it, the Anthropic <c>metadata.user_id</c> or OpenAI <c>user</c> field is used.
    /// </summary>
    public string ProjectHeader { get; set; } = "X-Router-Project";

    /// <summary>
    /// Gateway client (a <see cref="GatewayKeys"/> name) → the profile its requests always use. A client cannot leave its
    /// profile with a header or model name.
    /// </summary>
    public IDictionary<string, string> ClientProfiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pick a profile from the request when none is named: the first rule whose conditions all hold applies.
    /// </summary>
    public IList<ProfileRule> ProfileRules { get; } = new List<ProfileRule>();

    /// <summary>
    /// Model names clients can pick that stand for a profile and a strategy, e.g. <c>"private": { "Profile": "private" }</c>
    /// or <c>"fast": { "Strategy": "CheapestCapable" }</c>. Listed by <c>GET /v1/models</c>, so chat UIs offer them.
    /// </summary>
    public IDictionary<string, VirtualModel> VirtualModels { get; } = new Dictionary<string, VirtualModel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content that keeps a request on local models whatever its profile.</summary>
    public LocalOnlySignals LocalOnlyWhen { get; set; } = new();

    /// <summary>
    /// Ignore <c>&lt;system-reminder&gt;</c> blocks that agentic clients inject into user messages when classifying,
    /// so routing reflects what the person asked. Default true.
    /// </summary>
    public bool StripSystemReminders { get; set; } = true;

    /// <summary>Largest request body (bytes) the middleware will buffer; larger requests receive 413. Default 8 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Whether to add routing headers to the response. Disable to keep routing opaque to clients. Default true.</summary>
    public bool EmitResponseHeaders { get; set; } = true;

    /// <summary>Response header prefix. Default <c>X-Prompt-Route</c>.</summary>
    public string HeaderPrefix { get; set; } = DefaultHeaderPrefix;

    /// <summary>
    /// When classification throws (for example the embedding endpoint is down), route with
    /// <see cref="SemanticRouterOptions.FallbackTier"/> instead of failing the request. Default true.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>Which chat messages are used as the prompt. Default <see cref="PromptMessageSelection.LastUserMessage"/>.</summary>
    public PromptMessageSelection MessageSelection { get; set; } = PromptMessageSelection.LastUserMessage;

    /// <summary>
    /// Optional keys that clients present to use the gateway, by client name (for example <c>"laptop": "${env:ROUTER_KEY_LAPTOP}"</c>).
    /// When any are configured, requests from other machines must send one as <c>Authorization: Bearer {key}</c> or
    /// <c>x-api-key: {key}</c> (what <c>ANTHROPIC_AUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c> and <c>OPENAI_API_KEY</c> send),
    /// and the client name is recorded in the activity log. Keys are compared, never forwarded: credentials for the
    /// backends always come from the connector configuration. Empty by default (no gateway authentication).
    /// </summary>
    public IDictionary<string, string> GatewayKeys { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Require a gateway key from clients on this machine too. Default <see langword="false"/>: loopback clients may
    /// omit the key (and are still identified when they send one). Turn this on when a reverse proxy on this machine
    /// forwards remote traffic, since such requests arrive from loopback.
    /// </summary>
    public bool RequireGatewayKeyForLocalClients { get; set; }
}

/// <summary>
/// Retry escalation: a prompt repeated by the same client within <see cref="Window"/> of being answered goes to a model
/// at least one tier above the one that answered it. Only the prompt's hash is kept, in memory. Requests that carry tool
/// results and requests for a specific catalog model are never escalated.
/// </summary>
public sealed class RetryEscalationOptions
{
    /// <summary>Turns retry escalation on. Default <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>How soon after an answer a repeat counts as a retry. Default 2 minutes.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Conversation-aware routing (<see cref="PromptRouterMiddlewareOptions.Conversation"/>). A conversation is recognised
/// by a hash of the client, the system prompt and the first user message, which chat clients resend on every turn; no
/// prompt text is kept. State is in memory and capped.
/// </summary>
public sealed class ConversationRoutingOptions
{
    /// <summary>Turns conversation routing on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many turns that need less than the conversation's peak tier it takes for the floor to drop one tier, so a
    /// conversation that moved on to simpler work gets cheaper models again. Short follow-ups do not count. Default 3.
    /// </summary>
    public int DecayTurns { get; set; } = 3;

    /// <summary>A conversation without a turn for this long is forgotten. Default 30 minutes.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A message this short (in characters, without code blocks) is a follow-up such as "yes", "continue" or "try
    /// again": it keeps the conversation's full floor instead of being classified on its own. Default 60.
    /// </summary>
    public int ShortFollowUpLength { get; set; } = 60;

    /// <summary>
    /// Prefer the model that answered the previous turn while it is available and on the required tier: answers stay
    /// coherent and prompt caches keep working. Default <see langword="true"/>.
    /// </summary>
    public bool StickToModel { get; set; } = true;
}

/// <summary>
/// Inline callouts (<see cref="PromptRouterMiddlewareOptions.Callouts"/>). A message that starts with a tag routes as
/// the tag asks, over the router's own judgement; the tag is removed before the request is forwarded. Built in:
/// <c>@auto</c> (back to automatic), <c>@best</c>, <c>@cheap</c>, <c>@priority</c>, <c>@local</c>, <c>@tier1</c> to
/// <c>@tier9</c>, a catalog model name, and a model family (<c>@opus</c>, <c>@qwen</c>: the most capable catalog model
/// whose name or model id contains it). A tag that is none of these is left in the message.
/// </summary>
public sealed class CalloutOptions
{
    /// <summary>Turns inline callouts on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Your own short names, e.g. <c>"fast": "lmstudio-small"</c>, <c>"deep": "family:opus"</c>, <c>"private":
    /// "local"</c>. A target is a catalog model, <c>auto:…</c>, <c>family:…</c>, <c>tierN</c> or <c>local</c>.
    /// </summary>
    public IDictionary<string, string> Names { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Outcome feedback (<see cref="PromptRouterMiddlewareOptions.Feedback"/>): signs that a routing decision was not good
/// enough, each recorded with the decision it is about and the tier it implies the decision needed. Retries are
/// recognised within <see cref="RetryEscalationOptions.Window"/>, whether or not retry escalation is on.
/// </summary>
public sealed class FeedbackOptions
{
    /// <summary>Turns feedback on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Picks a profile from what the request looks like (<see cref="PromptRouterMiddlewareOptions.ProfileRules"/>).</summary>
public sealed class ProfileRule
{
    /// <summary>The profile to use when the rule matches.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>When not empty, the gateway client must be one of these.</summary>
    public IList<string> Clients { get; } = new List<string>();

    /// <summary>When not empty, the project label must match one of these (<c>*</c> globs).</summary>
    public IList<string> Projects { get; } = new List<string>();

    /// <summary>When not empty, the request must offer a tool matching one of these (<c>*</c> globs), e.g. <c>Bash</c>.</summary>
    public IList<string> ToolsInclude { get; } = new List<string>();

    /// <summary>The request must offer no tool matching these.</summary>
    public IList<string> ToolsExclude { get; } = new List<string>();
}

/// <summary>A model name that stands for a profile and a strategy (<see cref="PromptRouterMiddlewareOptions.VirtualModels"/>).</summary>
public sealed class VirtualModel
{
    /// <summary>The profile to route under, if any.</summary>
    public string? Profile { get; set; }

    /// <summary>The selection strategy, if any.</summary>
    public SelectionStrategy? Strategy { get; set; }

    /// <summary>Shown in <c>GET /v1/models</c>.</summary>
    public string? Description { get; set; }
}

/// <summary>Content signals that force a request onto local models (<see cref="PromptRouterMiddlewareOptions.LocalOnlyWhen"/>).</summary>
public sealed class LocalOnlySignals
{
    /// <summary>
    /// The conversation contains something that looks like a credential: an API key (<c>sk-…</c>, <c>ghp_…</c>,
    /// <c>AKIA…</c>, <c>xoxb-…</c>), a private key block or a JWT. Default <see langword="false"/>.
    /// </summary>
    public bool Secrets { get; set; }

    /// <summary>
    /// The conversation contains an absolute file path. Agents mention paths on almost every turn, so this keeps whole
    /// coding sessions local. Default <see langword="false"/>.
    /// </summary>
    public bool FilePaths { get; set; }
}
