using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What kind of model a server reports.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveredModelKind>))]
public enum DiscoveredModelKind
{
    /// <summary>A chat (text generation) model, or the server did not say.</summary>
    Chat = 0,

    /// <summary>A chat model that also reads images (LM Studio's <c>vlm</c>).</summary>
    Vision = 1,

    /// <summary>An embedding model: usable for <c>SemanticRouter:Embeddings</c>, not as a catalog model.</summary>
    Embedding = 2,
}

/// <summary>How a model server makes its models available.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelLoading>))]
public enum ModelLoading
{
    /// <summary>The server does not say (vLLM, llama.cpp, gateways, cloud APIs): it serves what it lists.</summary>
    Unknown = 0,

    /// <summary>
    /// Asking for a model that is not loaded loads it: LM Studio with just-in-time loading on, and Ollama always. The
    /// first request to a cold model waits for it to load, and loading one may unload another.
    /// </summary>
    OnDemand = 1,

    /// <summary>Only loaded models answer: LM Studio with just-in-time loading off. Load a model before routing to it.</summary>
    LoadedOnly = 2,
}

/// <summary>One model a connector's server reports, with whatever details the server gives.</summary>
/// <param name="Id">The id to send as <see cref="ModelDefinition.Model"/>.</param>
public sealed record DiscoveredModel(string Id)
{
    /// <summary>Chat, vision or embedding.</summary>
    public DiscoveredModelKind Kind { get; init; }

    /// <summary>Whether the server's chat endpoint lists it. LM Studio with just-in-time loading off reports downloaded models it will not serve until they are loaded.</summary>
    public bool Listed { get; init; } = true;

    /// <summary>Whether the model is in memory now. <see langword="null"/>: the server does not say.</summary>
    public bool? Loaded { get; init; }

    /// <summary>The largest context the model supports, in tokens.</summary>
    public int? MaxContextLength { get; init; }

    /// <summary>The context it is loaded with now, in tokens: what a request can actually use.</summary>
    public int? LoadedContextLength { get; init; }

    /// <summary>Parameter count in billions, from the server or the model id (<c>qwen3-8b</c>).</summary>
    public double? ParametersBillions { get; init; }

    /// <summary>What the server says the model can do, in catalog terms (<c>tools</c>, <c>vision</c>, <c>reasoning</c>).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The quantization, when the server reports it (<c>Q4_K_M</c>, <c>4bit</c>).</summary>
    public string? Quantization { get; init; }

    /// <summary>
    /// Whether a local server forwards this model to a remote service (Ollama's cloud models). <see langword="false"/>:
    /// a recognised local server runs it itself. <see langword="null"/>: the server does not say, so a local address
    /// alone does not prove prompts stay local (it may be a proxy such as LiteLLM).
    /// </summary>
    public bool? Remote { get; init; }

    /// <summary>A human-readable name, when the server has one.</summary>
    public string? DisplayName { get; init; }
}
