using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>Built-in embedding providers selectable from configuration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EmbeddingProviderKind>))]
public enum EmbeddingProviderKind
{
    /// <summary>In-process <see cref="HashingEmbeddingProvider"/> (lexical, for development and tests).</summary>
    Hashing = 0,

    /// <summary><see cref="OpenAICompatibleEmbeddingProvider"/>: any <c>/v1/embeddings</c> endpoint (Ollama, OpenAI, vLLM, …).</summary>
    OpenAICompatible = 1,
}

/// <summary>
/// Embedding provider configuration. Bound from <c>SemanticRouter:Embeddings</c>.
/// Ignored when a custom <see cref="IEmbeddingProvider"/> is registered.
/// </summary>
public sealed class EmbeddingProviderOptions
{
    /// <summary>Which built-in provider to use. Default <see cref="EmbeddingProviderKind.Hashing"/>.</summary>
    public EmbeddingProviderKind Provider { get; set; } = EmbeddingProviderKind.Hashing;

    /// <summary>Vector size for the hashing provider. Default 512.</summary>
    public int Dimensions { get; set; } = HashingEmbeddingProvider.DefaultDimensions;

    /// <summary>Base address of the embeddings server, e.g. <c>http://localhost:11434</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Path of the embeddings API. Default <c>/v1/embeddings</c>.</summary>
    public string RequestPath { get; set; } = "/v1/embeddings";

    /// <summary>Embedding model name, e.g. <c>nomic-embed-text</c> or <c>text-embedding-3-small</c>.</summary>
    public string? Model { get; set; }

    /// <summary>Optional bearer token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum inputs per embeddings call when indexing utterances. Default 64.</summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>HTTP timeout for embedding calls. This sits on the hot path of every request, so keep it short. Default 10 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Embed route utterances in the background at start-up instead of on the first request. Default true.</summary>
    public bool WarmUpOnStartup { get; set; } = true;
}
