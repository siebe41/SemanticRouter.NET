using SemanticRouter.AspNetCore;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>A sentence-embedding model the package can download, pinned to one revision and checksum.</summary>
public sealed record OnnxModelInfo
{
    /// <summary>The name used in configuration (<c>Embeddings:Model</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The Hugging Face repository with the ONNX export.</summary>
    public required string Repository { get; init; }

    /// <summary>The pinned commit.</summary>
    public required string Revision { get; init; }

    /// <summary>The ONNX file in the repository.</summary>
    public required string ModelFile { get; init; }

    /// <summary>SHA-256 of the ONNX file.</summary>
    public required string ModelSha256 { get; init; }

    /// <summary>Size of the ONNX file.</summary>
    public required long ModelBytes { get; init; }

    /// <summary>SHA-256 of <c>vocab.txt</c>.</summary>
    public required string VocabSha256 { get; init; }

    /// <summary>How token vectors become the sentence vector.</summary>
    public required EmbeddingPooling Pooling { get; init; }

    /// <summary>Size of the sentence vector.</summary>
    public required int Dimensions { get; init; }

    /// <summary>A starting <c>DefaultSimilarityThreshold</c> for the model.</summary>
    public required float SuggestedThreshold { get; init; }

    /// <summary>What the model is.</summary>
    public required string Description { get; init; }

    /// <summary>Where the ONNX file is downloaded from.</summary>
    public Uri ModelUrl => new($"https://huggingface.co/{Repository}/resolve/{Revision}/{ModelFile}");

    /// <summary>Where <c>vocab.txt</c> is downloaded from.</summary>
    public Uri VocabUrl => new($"https://huggingface.co/{Repository}/resolve/{Revision}/vocab.txt");
}

/// <summary>The models <see cref="OnnxEmbeddings.Provider"/> can download by name.</summary>
public static class OnnxEmbeddingModels
{
    /// <summary>The model used when <c>Embeddings:Model</c> is not set.</summary>
    public const string DefaultModel = "all-MiniLM-L6-v2";

    /// <summary>
    /// The hosts a model download reaches: Hugging Face answers for the ONNX file with a redirect to its CDN, whose host
    /// name is regional (<c>us.aws.cdn.hf.co</c>, <c>cas-bridge.xethub.hf.co</c>…). A firewall allowlist needs both.
    /// </summary>
    public const string DownloadHosts = "huggingface.co and its CDN (*.hf.co)";

    private const string BertUncasedVocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    /// <summary>The downloadable models, int8-quantized ONNX exports that run on any x64 or arm64 CPU.</summary>
    public static IReadOnlyList<OnnxModelInfo> Known { get; } =
    [
        new OnnxModelInfo
        {
            Name = DefaultModel,
            Repository = "Xenova/all-MiniLM-L6-v2",
            Revision = "751bff37182d3f1213fa05d7196b954e230abad9",
            ModelFile = "onnx/model_quantized.onnx",
            ModelSha256 = "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1",
            ModelBytes = 22_972_370,
            VocabSha256 = BertUncasedVocabSha256,
            Pooling = EmbeddingPooling.Mean,
            Dimensions = 384,
            SuggestedThreshold = 0.40f,
            Description = "sentence-transformers/all-MiniLM-L6-v2: small and fast (23 MB, 384 dimensions), English.",
        },
        new OnnxModelInfo
        {
            Name = "bge-small-en-v1.5",
            Repository = "Xenova/bge-small-en-v1.5",
            Revision = "ea104dacec62c0de699686887e3f920caeb4f3e3",
            ModelFile = "onnx/model_quantized.onnx",
            ModelSha256 = "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4",
            ModelBytes = 34_014_426,
            VocabSha256 = BertUncasedVocabSha256,
            Pooling = EmbeddingPooling.Cls,
            Dimensions = 384,
            SuggestedThreshold = 0.60f,
            Description = "BAAI/bge-small-en-v1.5: stronger retrieval model (34 MB, 384 dimensions), English.",
        },
    ];

    /// <summary>The known model called <paramref name="name"/> (case-insensitive), or <see langword="null"/>.</summary>
    public static OnnxModelInfo? Find(string? name) =>
        Known.FirstOrDefault(m => string.Equals(m.Name, string.IsNullOrWhiteSpace(name) ? DefaultModel : name.Trim(), StringComparison.OrdinalIgnoreCase));
}
