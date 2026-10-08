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

    /// <summary>
    /// A sentence-embedding model run in-process with ONNX Runtime: no embedding server needed. Needs the
    /// <c>SemanticRouter.Embeddings.Onnx</c> package (<c>builder.UseOnnxEmbeddings()</c>); the <c>semantic-router</c>
    /// tool includes it.
    /// </summary>
    Onnx = 2,
}

/// <summary>How token vectors become one sentence vector (ONNX models).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EmbeddingPooling>))]
public enum EmbeddingPooling
{
    /// <summary>Average of the token vectors (sentence-transformers models such as all-MiniLM-L6-v2).</summary>
    Mean = 0,

    /// <summary>The first (<c>[CLS]</c>) token's vector (BGE models).</summary>
    Cls = 1,
}

/// <summary>
/// Builds an embedding provider for a <see cref="EmbeddingProviderKind"/> the router does not build itself (registered by
/// a package such as <c>SemanticRouter.Embeddings.Onnx</c>). Register as a singleton; the router asks it for a provider
/// each time a configuration is applied, so expensive state (a loaded model) should be cached by the factory.
/// </summary>
public interface IEmbeddingProviderFactory
{
    /// <summary>The provider kind this factory builds.</summary>
    EmbeddingProviderKind Kind { get; }

    /// <summary>Builds a provider for <paramref name="options"/>. Slow work (downloads, model loading) belongs in the first call, not here.</summary>
    IEmbeddingProvider Create(EmbeddingProviderOptions options);

    /// <summary>Models the dashboard offers for this kind (by <see cref="EmbeddingProviderOptions.Model"/> name).</summary>
    IReadOnlyList<EmbeddingModelChoice> Models => [];
}

/// <summary>A model an <see cref="IEmbeddingProviderFactory"/> can provide by name.</summary>
/// <param name="Name">The value for <see cref="EmbeddingProviderOptions.Model"/>.</param>
/// <param name="Description">What it is.</param>
/// <param name="SuggestedThreshold">A starting <c>DefaultSimilarityThreshold</c>.</param>
/// <param name="DownloadBytes">Size of the first-use download.</param>
public sealed record EmbeddingModelChoice(string Name, string Description, float SuggestedThreshold, long DownloadBytes);

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

    /// <summary>
    /// Embedding model name, e.g. <c>nomic-embed-text</c> or <c>text-embedding-3-small</c>. For <see cref="EmbeddingProviderKind.Onnx"/>:
    /// a known model (<c>all-MiniLM-L6-v2</c>, the default, or <c>bge-small-en-v1.5</c>), downloaded on first use.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// <see cref="EmbeddingProviderKind.Onnx"/> only: a directory holding <c>model.onnx</c> and <c>vocab.txt</c> (a BERT-style
    /// sentence-embedding model) to load instead of downloading <see cref="Model"/>.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary><see cref="EmbeddingProviderKind.Onnx"/> only: the pooling of a model loaded from <see cref="ModelPath"/>. Known models know their own.</summary>
    public EmbeddingPooling? Pooling { get; set; }

    /// <summary>Optional bearer token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum inputs per embeddings call when indexing utterances. Default 64.</summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>HTTP timeout for embedding calls. This sits on the hot path of every request, so keep it short. Default 10 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Embed route utterances in the background at start-up instead of on the first request. Default true.</summary>
    public bool WarmUpOnStartup { get; set; } = true;
}
