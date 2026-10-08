using SemanticRouter.AspNetCore;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>A sentence-embedding model the package can download, pinned to one revision and checksum.</summary>
/// <param name="Name">The name used in configuration (<c>Embeddings:Model</c>).</param>
/// <param name="Repository">The Hugging Face repository with the ONNX export.</param>
/// <param name="Revision">The pinned commit.</param>
/// <param name="ModelFile">The ONNX file in the repository.</param>
/// <param name="ModelSha256">SHA-256 of the ONNX file.</param>
/// <param name="ModelBytes">Size of the ONNX file.</param>
/// <param name="VocabSha256">SHA-256 of <c>vocab.txt</c>.</param>
/// <param name="Pooling">How token vectors become the sentence vector.</param>
/// <param name="Dimensions">Size of the sentence vector.</param>
/// <param name="SuggestedThreshold">A starting <c>DefaultSimilarityThreshold</c> for the model.</param>
/// <param name="Description">What the model is.</param>
public sealed record OnnxModelInfo(
    string Name,
    string Repository,
    string Revision,
    string ModelFile,
    string ModelSha256,
    long ModelBytes,
    string VocabSha256,
    EmbeddingPooling Pooling,
    int Dimensions,
    float SuggestedThreshold,
    string Description)
{
    /// <summary>Where the ONNX file is downloaded from.</summary>
    public Uri ModelUrl => new($"https://huggingface.co/{Repository}/resolve/{Revision}/{ModelFile}");

    /// <summary>Where <c>vocab.txt</c> is downloaded from.</summary>
    public Uri VocabUrl => new($"https://huggingface.co/{Repository}/resolve/{Revision}/vocab.txt");
}

/// <summary>The models <see cref="EmbeddingProviderKind.Onnx"/> can download by name.</summary>
public static class OnnxEmbeddingModels
{
    /// <summary>The model used when <c>Embeddings:Model</c> is not set.</summary>
    public const string DefaultModel = "all-MiniLM-L6-v2";

    private const string BertUncasedVocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    /// <summary>The downloadable models, int8-quantized ONNX exports that run on any x64 or arm64 CPU.</summary>
    public static IReadOnlyList<OnnxModelInfo> Known { get; } =
    [
        new(
            DefaultModel, "Xenova/all-MiniLM-L6-v2", "751bff37182d3f1213fa05d7196b954e230abad9", "onnx/model_quantized.onnx",
            "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1", 22_972_370, BertUncasedVocabSha256,
            EmbeddingPooling.Mean, 384, 0.40f,
            "sentence-transformers/all-MiniLM-L6-v2: small and fast (23 MB, 384 dimensions), English."),
        new(
            "bge-small-en-v1.5", "Xenova/bge-small-en-v1.5", "ea104dacec62c0de699686887e3f920caeb4f3e3", "onnx/model_quantized.onnx",
            "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4", 34_014_426, BertUncasedVocabSha256,
            EmbeddingPooling.Cls, 384, 0.60f,
            "BAAI/bge-small-en-v1.5: stronger retrieval model (34 MB, 384 dimensions), English."),
    ];

    /// <summary>The known model called <paramref name="name"/> (case-insensitive), or <see langword="null"/>.</summary>
    public static OnnxModelInfo? Find(string? name) =>
        Known.FirstOrDefault(m => string.Equals(m.Name, string.IsNullOrWhiteSpace(name) ? DefaultModel : name.Trim(), StringComparison.OrdinalIgnoreCase));
}
