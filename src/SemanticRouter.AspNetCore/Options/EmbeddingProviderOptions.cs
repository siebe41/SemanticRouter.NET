using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The embedding providers the router builds itself, for <see cref="EmbeddingProviderOptions.Provider"/>. A package adds
/// another by registering an <see cref="IEmbeddingProviderFactory"/> for its name (for example <c>Onnx</c>, from
/// <c>SemanticRouter.Embeddings.Onnx</c>). Names match case-insensitively.
/// </summary>
public static class EmbeddingProviderKinds
{
    /// <summary>In-process <see cref="HashingEmbeddingProvider"/> (lexical, for development and tests).</summary>
    public const string Hashing = "Hashing";

    /// <summary><see cref="OpenAICompatibleEmbeddingProvider"/>: any <c>/v1/embeddings</c> endpoint (Ollama, OpenAI, vLLM, …).</summary>
    public const string OpenAICompatible = "OpenAICompatible";

    /// <summary>The built-in kinds, in their canonical spelling.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = [Hashing, OpenAICompatible];

    /// <summary>Whether <paramref name="kind"/> names a built-in provider (case-insensitive).</summary>
    public static bool IsBuiltIn(string? kind) => BuiltIn.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="kind"/> trimmed, with a built-in name in its canonical spelling and an empty value read as
    /// <see cref="Hashing"/>. Other names are kept as written.
    /// </summary>
    internal static string Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? Hashing
        : BuiltIn.FirstOrDefault(b => string.Equals(b, kind.Trim(), StringComparison.OrdinalIgnoreCase)) ?? kind.Trim();
}

/// <summary>How token vectors become one sentence vector (in-process models such as <c>Onnx</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EmbeddingPooling>))]
public enum EmbeddingPooling
{
    /// <summary>Average of the token vectors (sentence-transformers models such as all-MiniLM-L6-v2).</summary>
    Mean = 0,

    /// <summary>The first (<c>[CLS]</c>) token's vector (BGE models).</summary>
    Cls = 1,
}

/// <summary>
/// Builds an embedding provider for a <see cref="EmbeddingProviderOptions.Provider"/> name the router does not build itself
/// (registered by a package such as <c>SemanticRouter.Embeddings.Onnx</c>). Register as a singleton; the router asks it for a provider
/// each time a configuration is applied, so expensive state (a loaded model) should be cached by the factory.
/// </summary>
public interface IEmbeddingProviderFactory
{
    /// <summary>
    /// The <see cref="EmbeddingProviderOptions.Provider"/> name this factory builds, such as <c>Onnx</c>. Matched
    /// case-insensitively; it should not be one of <see cref="EmbeddingProviderKinds.BuiltIn"/>.
    /// </summary>
    string Kind { get; }

    /// <summary>Builds a provider for <paramref name="options"/>. Slow work (downloads, model loading) belongs in the first call, not here.</summary>
    IEmbeddingProvider Create(EmbeddingProviderOptions options);

    /// <summary>Models the dashboard offers for this kind (by <see cref="EmbeddingProviderOptions.Model"/> name).</summary>
    IReadOnlyList<EmbeddingModelChoice> Models => [];

    /// <summary>
    /// <see langword="null"/> when this kind can run on this machine; otherwise why not and what to use instead (a native
    /// library with no build for this platform). The dashboard and <c>doctor</c> then do not suggest it.
    /// </summary>
    string? Unavailable => null;
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
    /// <summary>
    /// Which provider to use: one of <see cref="EmbeddingProviderKinds"/>, or a kind an installed package provides through
    /// an <see cref="IEmbeddingProviderFactory"/>. Case-insensitive; built-in names are stored in their canonical spelling.
    /// Default <see cref="EmbeddingProviderKinds.Hashing"/>.
    /// </summary>
    [AllowNull]
    public string Provider
    {
        get => _provider;
        set => _provider = EmbeddingProviderKinds.Normalize(value);
    }

    private string _provider = EmbeddingProviderKinds.Hashing;

    /// <summary>Vector size for the hashing provider. Default 512.</summary>
    public int Dimensions { get; set; } = HashingEmbeddingProvider.DefaultDimensions;

    /// <summary>Base address of the embeddings server, e.g. <c>http://localhost:11434</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Whether <see cref="Endpoint"/> is on this machine or a server you control, so prompts that must stay local may be
    /// sent to it. Default: <see langword="true"/> for a loopback address, <see langword="false"/> for anything else.
    /// The in-process providers are always local.
    /// </summary>
    public bool? IsLocal { get; set; }

    /// <summary>Whether prompts embedded with these settings stay on this machine (or a server declared <see cref="IsLocal"/>).</summary>
    internal bool KeepsPromptsLocal =>
        Provider != EmbeddingProviderKinds.OpenAICompatible || (IsLocal ?? Endpoint is { IsLoopback: true });

    /// <summary>Path of the embeddings API. Default <c>/v1/embeddings</c>.</summary>
    public string RequestPath { get; set; } = "/v1/embeddings";

    /// <summary>
    /// Embedding model name, e.g. <c>nomic-embed-text</c> or <c>text-embedding-3-small</c>. For a packaged provider
    /// such as <c>Onnx</c>: one of its <see cref="IEmbeddingProviderFactory.Models"/>.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// In-process model providers (<c>Onnx</c>): a directory holding the model files to load instead of downloading
    /// <see cref="Model"/>.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>In-process model providers (<c>Onnx</c>): the pooling of a model loaded from <see cref="ModelPath"/>. Known models know their own.</summary>
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
