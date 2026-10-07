namespace SemanticRouter;

/// <summary>
/// Produces dense vector embeddings for text. Implementations wrap a local model (Ollama,
/// ONNX Runtime) or a hosted embeddings API, and must be thread-safe.
/// </summary>
/// <remarks>
/// All vectors returned by a provider instance must share the same dimensionality. They do not
/// need to be normalized; the router normalizes them before indexing.
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>Embeds a single text (used once per routed request, so keep it fast).</summary>
    ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds many texts, preserving order. Used to index route utterances at start-up; batch
    /// endpoints should be used where available.
    /// </summary>
    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
