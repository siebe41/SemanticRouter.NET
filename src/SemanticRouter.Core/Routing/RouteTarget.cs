using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>
/// The class of model endpoint a prompt is dispatched to.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RouteTarget>))]
public enum RouteTarget
{
    /// <summary>A fast, cheap, usually self-hosted model (e.g. Ollama running a small language model).</summary>
    Local = 0,

    /// <summary>An advanced hosted frontier model.</summary>
    Cloud = 1,
}
