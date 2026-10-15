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

    /// <summary>
    /// Track how often each model's answers draw feedback, per route and for tool use, and stop offering a model for the
    /// kind of work it keeps failing at, for a while. On by default.
    /// </summary>
    public ReliabilityOptions Reliability { get; set; } = new();

    /// <summary>
    /// Recognise the requests agentic clients make for themselves (session titles, topic checks, compaction summaries,
    /// warm-up pings, command checks) and send them to a small model; send a session resumed from a compaction summary
    /// to the tier of the work it continues. On by default.
    /// </summary>
    public HousekeepingOptions Housekeeping { get; set; } = new();

    /// <summary>
    /// The optional judge: a model, preferably local, that grades a small sample of cheap models' answers. A low grade
    /// becomes feedback the router learns from. Off by default.
    /// </summary>
    public JudgeOptions Judge { get; set; } = new();

    /// <summary>The request header that names a routing profile (<c>SemanticRouter:Profiles</c>). Default <c>X-Router-Profile</c>.</summary>
    public string ProfileHeader { get; set; } = "X-Router-Profile";

    /// <summary>
    /// The request header that carries a project label for <see cref="ProfileRules"/> and the activity log. Default
    /// <c>X-Router-Project</c>. Without it, a hash of the Anthropic <c>metadata.user_id</c> or OpenAI <c>user</c> field is
    /// used (<c>user:</c> and 12 hex digits), never the id itself.
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
    /// Ignore the context blocks that agentic clients inject into user messages when classifying, so routing reflects
    /// what the person asked: Claude Code's <c>&lt;system-reminder&gt;</c>, the GitHub Copilot app's
    /// <c>&lt;system_reminder&gt;</c>, <c>&lt;current_datetime&gt;</c> and <c>&lt;copilot_tauri_workspace&gt;</c>, and
    /// <see cref="InjectedTags"/>. Default true.
    /// </summary>
    public bool StripSystemReminders { get; set; } = true;

    /// <summary>
    /// More tags (names without angle brackets, e.g. <c>environment_details</c>) whose blocks a client injects into
    /// user messages and that are ignored when classifying, besides the built-in ones. Empty by default.
    /// </summary>
    public IList<string> InjectedTags { get; } = new List<string>();

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
    /// backends always come from the connector configuration. Empty by default: clients on this machine need no key, and
    /// other machines are refused (see <see cref="AllowUnauthenticatedRemoteClients"/>).
    /// </summary>
    public IDictionary<string, string> GatewayKeys { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serve requests from other machines without a gateway key while <see cref="GatewayKeys"/> is empty. Default
    /// <see langword="false"/>: with no keys configured, remote clients get a 401, because anyone who can reach the port
    /// could otherwise spend the connectors' keys and subscriptions. Turn this on only behind a reverse proxy that
    /// authenticates every request. Ignored once any gateway key is configured.
    /// </summary>
    public bool AllowUnauthenticatedRemoteClients { get; set; }

    /// <summary>
    /// Require a gateway key from clients on this machine too. Default <see langword="false"/>: loopback clients may
    /// omit the key (and are still identified when they send one). Turn this on when a reverse proxy on this machine
    /// forwards remote traffic, since such requests arrive from loopback.
    /// </summary>
    public bool RequireGatewayKeyForLocalClients { get; set; }

    /// <summary>
    /// Rate limits per gateway client (a name from <see cref="GatewayKeys"/>), plus <c>"*"</c> for every other client: a
    /// client without its own entry, or one that sends no key (counted per address). A client over its limit gets a 429
    /// with <c>Retry-After</c> in its own protocol, and the request is recorded in the activity log. Empty by default (no
    /// limits). Budgets stop spending once a period's limit is reached; rate limits stop a client stuck in a loop from
    /// spending it in minutes, or crowding out the others.
    /// </summary>
    public IDictionary<string, RateLimitOptions> RateLimits { get; } = new Dictionary<string, RateLimitOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Requests per minute for each client on another machine (by gateway client, or by address without a key) that no
    /// <see cref="RateLimits"/> entry covers. Default 120, so a remote client stuck in a loop cannot spend the
    /// connectors' keys at full speed even before limits are configured. 0 turns it off. Clients on this machine are not
    /// limited by it.
    /// </summary>
    public int RemoteRequestsPerMinute { get; set; } = 120;
}

/// <summary>One client's rate limit (<see cref="PromptRouterMiddlewareOptions.RateLimits"/>), over a sliding minute.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Requests per minute; <see langword="null"/> for no limit.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>
    /// Tokens per minute (input and output); <see langword="null"/> for no limit. A request is charged its estimated input
    /// when it is admitted and the rest once the reply's usage is known; it is admitted while the client is under the limit.
    /// </summary>
    public int? TokensPerMinute { get; set; }
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

    /// <summary>
    /// How long a model's prompt cache stays warm after a turn that read or wrote it. While it does, the conversation
    /// stays on that model when moving would cost more (re-sending and re-writing the cached context), above the
    /// required tier too, and returns to it after a failover. 5 minutes matches Anthropic's default entries and
    /// OpenAI's; set 1 hour when your requests ask for 1-hour entries. Default 5 minutes.
    /// </summary>
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// How close in meaning (cosine similarity, with the routing embedding model) a short follow-up must be to an example
    /// correction to count as one, when no known phrase matched: catches other wording and other languages. 0 turns the
    /// check off; it never runs with the lexical hashing embeddings. Default 0.72.
    /// </summary>
    public float CorrectionSimilarity { get; set; } = 0.72f;

    /// <summary>How much closer it must be to the example corrections than to the example follow-ups that are not ("now add a test"). Default 0.12.</summary>
    public float CorrectionMargin { get; set; } = 0.12f;
}

/// <summary>
/// Model reliability per kind of work (<see cref="PromptRouterMiddlewareOptions.Reliability"/>): a model whose turns on a
/// route, or with tools, draw feedback (retried, corrected, tool errors, empty replies) far more often than its peers'
/// is not offered for that work for <see cref="Period"/>, then tried again. Explicit choices are never overridden.
/// </summary>
public sealed class ReliabilityOptions
{
    /// <summary>Turns it on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far back turns and feedback count. Default 7 days.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Turns a model needs on a kind of work before it can be judged on it. Default 10.</summary>
    public int MinimumTurns { get; set; } = 10;

    /// <summary>The share of turns with feedback above which a model counts as unreliable for that work. Default 0.3.</summary>
    public double MaxFailureRate { get; set; } = 0.3;

    /// <summary>How far above its peers' share (models with enough turns on the same work) it must also be. Default 0.15.</summary>
    public double PeerMargin { get; set; } = 0.15;

    /// <summary>How long it is left out before it is offered again, with a fresh count. Default 1 day.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromDays(1);
}

/// <summary>Client side requests (<see cref="PromptRouterMiddlewareOptions.Housekeeping"/>).</summary>
public sealed class HousekeepingOptions
{
    /// <summary>Turns it on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The tier side requests get. Default 1.</summary>
    public int Tier { get; set; } = 1;

    /// <summary>The tier a session resumed from a compaction summary gets: the agentic work it continues. Default 4.</summary>
    public int ResumedTier { get; set; } = 4;

    /// <summary>
    /// More side requests to recognise: regular expressions (case-insensitive) matched on the latest user message and
    /// the system prompt, for another client's wording.
    /// </summary>
    public IList<string> Patterns { get; } = new List<string>();
}

/// <summary>
/// The judge (<see cref="PromptRouterMiddlewareOptions.Judge"/>): a share of the answers from models of tier
/// <see cref="MaxTier"/> or below is graded by <see cref="Model"/> after the client has its reply. A grade of
/// <see cref="FailingGrade"/> or lower is <see cref="FeedbackKind.Judged"/> feedback: the decision needed a tier more.
/// Only decisions the router made itself are graded, at most <see cref="DailyLimit"/> a day, and never a local-only
/// request by a judge that is not local. The judge's calls are served like any other (budgets, usage, cost).
/// </summary>
public sealed class JudgeOptions
{
    /// <summary>Turns it on. Default <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The catalog model that grades. A local one costs nothing and may grade local-only requests.</summary>
    public string? Model { get; set; }

    /// <summary>The share of eligible answers graded. Default 0.02 (2%).</summary>
    public double SampleRate { get; set; } = 0.02;

    /// <summary>Answers from models of this tier or below are graded. Default 2.</summary>
    public int MaxTier { get; set; } = 2;

    /// <summary>The most answers graded in a day (UTC). Default 100.</summary>
    public int DailyLimit { get; set; } = 100;

    /// <summary>Grades (1 to 5) at or below this count as failing. Default 2.</summary>
    public int FailingGrade { get; set; } = 2;

    /// <summary>The most characters of the request and of the answer sent to the judge. Default 6000 each.</summary>
    public int MaxCharacters { get; set; } = 6000;
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
