namespace SemanticRouter.Embeddings.Onnx;

/// <summary>The embedding provider this package adds (<c>AddOnnxEmbeddings</c>).</summary>
public static class OnnxEmbeddings
{
    /// <summary>
    /// The <see cref="SemanticRouter.AspNetCore.EmbeddingProviderOptions.Provider"/> of in-process ONNX embeddings: a
    /// sentence-embedding model run with ONNX Runtime, so no embedding server is needed.
    /// </summary>
    public const string Provider = "Onnx";
}
