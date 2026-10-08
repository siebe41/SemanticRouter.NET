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
    /// <c>"*haiku*": "auto:cheapest"</c>, <c>"*opus*": "auto:best"</c>, <c>"gpt-*": "auto"</c>. Names that match no
    /// pattern and no catalog model are routed as <c>auto</c>.
    /// </summary>
    public IDictionary<string, string> ModelAliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pin the follow-up requests of a tool-calling turn (those carrying tool results) to the model that issued the
    /// tool calls, instead of classifying them again. Default true.
    /// </summary>
    public bool PinToolCallingTurns { get; set; } = true;

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
