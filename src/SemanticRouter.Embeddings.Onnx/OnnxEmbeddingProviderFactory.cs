using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>Options for <see cref="OnnxEmbeddingsBuilderExtensions.AddOnnxEmbeddings"/>.</summary>
public sealed class OnnxEmbeddingOptions
{
    /// <summary>The HTTP client name used for model downloads.</summary>
    public const string HttpClientName = "SemanticRouter.OnnxModels";

    /// <summary>
    /// Where downloaded models are kept, one directory per model. Default: a <c>models</c> directory next to the configuration
    /// file (<c>UseConfigurationFile</c>), else <c>semantic-router/models</c> under the user's local application data.
    /// </summary>
    public string? ModelsDirectory { get; set; }

    /// <summary>Longest input in tokens. Default <see cref="OnnxEmbeddingProvider.DefaultMaxTokens"/>.</summary>
    public int MaxTokens { get; set; } = OnnxEmbeddingProvider.DefaultMaxTokens;
}

/// <summary>
/// Builds <see cref="EmbeddingProviderKind.Onnx"/> providers. A model is downloaded and loaded once, on first use, and
/// shared by every configuration version that uses it, so applying a configuration does not reload it.
/// </summary>
internal sealed class OnnxEmbeddingProviderFactory : IEmbeddingProviderFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<OnnxEmbeddingProvider>>> _loaded = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;
    private readonly OnnxEmbeddingOptions _options;

    public OnnxEmbeddingProviderFactory(IServiceProvider services, IOptions<OnnxEmbeddingOptions> options)
    {
        _services = services;
        _options = options.Value;

        // Early, while the router starts: setting a variable is safest before other threads read the environment.
        OnnxRuntimeEnvironment.DisableTelemetry();
    }

    public EmbeddingProviderKind Kind => EmbeddingProviderKind.Onnx;

    public IReadOnlyList<EmbeddingModelChoice> Models { get; } =
        OnnxEmbeddingModels.Known.Select(m => new EmbeddingModelChoice(m.Name, m.Description, m.SuggestedThreshold, m.ModelBytes)).ToArray();

    /// <summary>Where downloaded models go.</summary>
    public string ModelsDirectory =>
        _options.ModelsDirectory
        ?? (_services.GetService<IRouterConfigurationStore>() is JsonFileRouterConfigurationStore store
            ? Path.Combine(Path.GetDirectoryName(store.Location)!, "models")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "semantic-router", "models"));

    public IEmbeddingProvider Create(EmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ModelPath))
        {
            string directory = Path.GetFullPath(options.ModelPath);
            string modelFile = File.Exists(directory) ? directory : Path.Combine(directory, "model.onnx");
            string vocabFile = Path.Combine(Path.GetDirectoryName(modelFile)!, "vocab.txt");
            EmbeddingPooling pooling = options.Pooling ?? EmbeddingPooling.Mean;
            return new Shared(this, $"{modelFile}|{pooling}", modelFile, _ =>
                Task.FromResult(new OnnxEmbeddingProvider(modelFile, vocabFile, pooling, _options.MaxTokens)));
        }

        if (OnnxEmbeddingModels.Find(options.Model) is not { } model)
        {
            string names = string.Join(", ", OnnxEmbeddingModels.Known.Select(m => m.Name));
            return new UnavailableProvider($"'{options.Model}' is not a known ONNX model ({names}); set Embeddings:ModelPath to load your own.");
        }

        string models = ModelsDirectory;

        // Offline mode or the machine policy's allowed endpoints can rule out the download: then the files must be there.
        string? refused = MachinePolicy.Effective(_services.GetService<MachinePolicy>(), NetworkOptions.Of(_services))
            .Refuses(model.ModelUrl);
        return new Shared(this, $"{model.Name}|{models}", model.Name, async cancellationToken =>
        {
            HttpClient http = _services.GetRequiredService<IHttpClientFactory>().CreateClient(OnnxEmbeddingOptions.HttpClientName);
            (string modelFile, string vocabFile) = await OnnxModelDownloader.EnsureAsync(model, models, http, refused, cancellationToken).ConfigureAwait(false);
            return new OnnxEmbeddingProvider(modelFile, vocabFile, options.Pooling ?? model.Pooling, _options.MaxTokens);
        });
    }

    public void Dispose()
    {
        foreach (Lazy<Task<OnnxEmbeddingProvider>> loaded in _loaded.Values)
        {
            if (loaded.IsValueCreated && loaded.Value.IsCompletedSuccessfully)
            {
                loaded.Value.Result.Dispose();
            }
        }
    }

    private Task<OnnxEmbeddingProvider> LoadAsync(string key, Func<CancellationToken, Task<OnnxEmbeddingProvider>> load)
    {
        // Loaded without the caller's token: one aborted request must not cancel the shared download.
        Lazy<Task<OnnxEmbeddingProvider>> lazy = _loaded.GetOrAdd(key, _ => new Lazy<Task<OnnxEmbeddingProvider>>(() => Task.Run(() => load(CancellationToken.None))));
        Task<OnnxEmbeddingProvider> task = lazy.Value;
        if (task.IsFaulted || task.IsCanceled)
        {
            // Retry on the next call (the network may be back, the file may have been fixed).
            _loaded.TryRemove(new KeyValuePair<string, Lazy<Task<OnnxEmbeddingProvider>>>(key, lazy));
        }

        return task;
    }

    /// <summary>A provider for one configuration version; the model behind it is shared.</summary>
    private sealed class Shared(OnnxEmbeddingProviderFactory factory, string key, string name, Func<CancellationToken, Task<OnnxEmbeddingProvider>> load) : IEmbeddingProvider
    {
        public async ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            OnnxEmbeddingProvider provider = await GetAsync(cancellationToken).ConfigureAwait(false);
            return await Wrap(() => provider.GenerateEmbeddingAsync(text, cancellationToken)).ConfigureAwait(false);
        }

        public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            OnnxEmbeddingProvider provider = await GetAsync(cancellationToken).ConfigureAwait(false);
            return await Wrap(() => provider.GenerateEmbeddingsAsync(texts, cancellationToken)).ConfigureAwait(false);
        }

        private async Task<OnnxEmbeddingProvider> GetAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await factory.LoadAsync(key, load).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OnnxRuntimeException or InvalidDataException or FormatException or ArgumentException)
            {
                throw new InvalidOperationException($"ONNX model '{name}' could not be loaded: {ex.Message}", ex);
            }
        }

        private async ValueTask<T> Wrap<T>(Func<ValueTask<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (OnnxRuntimeException ex)
            {
                throw new InvalidOperationException($"ONNX model '{name}' failed: {ex.Message}", ex);
            }
        }
    }

    private sealed class UnavailableProvider(string reason) : IEmbeddingProvider
    {
        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(reason);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(reason);
    }
}
