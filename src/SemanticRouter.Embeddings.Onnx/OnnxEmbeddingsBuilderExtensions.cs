using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers in-process ONNX embeddings with the router.</summary>
public static class OnnxEmbeddingsBuilderExtensions
{
    /// <summary>
    /// Makes <see cref="EmbeddingProviderKind.Onnx"/> available, so configuration (or the dashboard) can select it, without
    /// selecting it. Models are downloaded on first use; see <see cref="OnnxEmbeddingOptions"/>. Turns ONNX Runtime's
    /// telemetry off for the process (<see cref="OnnxRuntimeEnvironment.DisableTelemetry"/>).
    /// </summary>
    public static SemanticRouterBuilder AddOnnxEmbeddings(this SemanticRouterBuilder builder, Action<OnnxEmbeddingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ONNX Runtime's telemetry is off before anything can load it (OnnxRuntimeEnvironment).
        OnnxRuntimeEnvironment.DisableTelemetry();
        builder.Services.AddOptions<OnnxEmbeddingOptions>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.AddHttpClient(OnnxEmbeddingOptions.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEmbeddingProviderFactory, OnnxEmbeddingProviderFactory>());
        return builder;
    }

    /// <summary>
    /// Routes with an in-process ONNX model: no embedding server needed. <paramref name="model"/> is a known model
    /// (<c>all-MiniLM-L6-v2</c> by default, or <c>bge-small-en-v1.5</c>), downloaded and checksum-verified on first use.
    /// Set a matching <c>DefaultSimilarityThreshold</c> (see <see cref="OnnxModelInfo.SuggestedThreshold"/>).
    /// </summary>
    public static SemanticRouterBuilder UseOnnxEmbeddings(this SemanticRouterBuilder builder, string model = OnnxEmbeddingModels.DefaultModel, Action<OnnxEmbeddingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddOnnxEmbeddings(configure);
        builder.Services.Configure<EmbeddingProviderOptions>(o =>
        {
            o.Provider = EmbeddingProviderKind.Onnx;
            o.Model = model;
        });
        return builder;
    }
}
