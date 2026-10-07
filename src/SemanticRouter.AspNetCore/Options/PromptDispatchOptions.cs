namespace SemanticRouter.AspNetCore;

/// <summary>
/// Upstream endpoints used in <see cref="PromptRouterMode.Dispatch"/> mode. Bound from <c>SemanticRouter:Endpoints</c>.
/// </summary>
public sealed class PromptDispatchOptions
{
    /// <summary>The fast / self-hosted endpoint (e.g. Ollama at <c>http://localhost:11434</c>).</summary>
    public RouteEndpointOptions Local { get; set; } = new();

    /// <summary>The frontier-model endpoint.</summary>
    public RouteEndpointOptions Cloud { get; set; } = new();

    /// <summary>Returns the endpoint for <paramref name="target"/>.</summary>
    public RouteEndpointOptions Get(RouteTarget target) => target == RouteTarget.Local ? Local : Cloud;
}

/// <summary>An upstream LLM endpoint.</summary>
/// <remarks>
/// The request body is forwarded verbatim (apart from <see cref="Model"/>), so both endpoints must speak the
/// same wire protocol, typically OpenAI-compatible <c>/v1/chat/completions</c>, which Ollama, vLLM,
/// LM Studio, OpenAI, Azure OpenAI, Anthropic's OpenAI-compatibility endpoint and most gateways accept.
/// </remarks>
public sealed class RouteEndpointOptions
{
    /// <summary>Scheme, host and optional port, e.g. <c>http://localhost:11434</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Path to call on <see cref="BaseAddress"/>, e.g. <c>/v1/chat/completions</c>. When <see langword="null"/>,
    /// the incoming request's path and query string are reused.
    /// </summary>
    public string? RequestPath { get; set; }

    /// <summary>When set, overwrites the top-level <c>model</c> property of the forwarded JSON body.</summary>
    public string? Model { get; set; }

    /// <summary>When set, sent as <c>Authorization: Bearer {ApiKey}</c>. Load it from user secrets or a vault, not appsettings.json.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Extra headers sent upstream (e.g. <c>x-api-key</c>, <c>anthropic-version</c>, <c>api-key</c> for Azure).</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Time allowed until response headers arrive (streamed bodies are not limited). Default 100 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
}
