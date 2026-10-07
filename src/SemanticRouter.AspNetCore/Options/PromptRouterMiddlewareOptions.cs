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

    /// <summary>Largest request body (bytes) the middleware will buffer; larger requests receive 413. Default 1 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

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
}
