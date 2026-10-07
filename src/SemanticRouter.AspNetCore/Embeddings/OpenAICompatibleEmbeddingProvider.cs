using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// An <see cref="IEmbeddingProvider"/> for any server implementing the OpenAI <c>POST /v1/embeddings</c>
/// contract: Ollama (<c>nomic-embed-text</c>, <c>mxbai-embed-large</c>), OpenAI, Azure OpenAI, vLLM,
/// LM Studio, text-embeddings-inference and most gateways.
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider : IEmbeddingProvider
{
    private readonly Func<HttpClient> _clientFactory;
    private readonly EmbeddingProviderOptions _options;
    private readonly Uri _requestUri;

    /// <summary>Creates a provider that sends requests through <paramref name="httpClient"/>.</summary>
    public OpenAICompatibleEmbeddingProvider(HttpClient httpClient, EmbeddingProviderOptions options)
        : this(() => httpClient, options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
    }

    internal OpenAICompatibleEmbeddingProvider(Func<HttpClient> clientFactory, EmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint is null)
        {
            throw new InvalidOperationException("SemanticRouter:Embeddings:Endpoint is required for the OpenAICompatible provider.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("SemanticRouter:Embeddings:Model is required for the OpenAICompatible provider.");
        }

        _clientFactory = clientFactory;
        _options = options;
        _requestUri = new Uri(options.Endpoint, options.RequestPath);
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        float[][] vectors = await PostAsync([text], cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new List<ReadOnlyMemory<float>>(texts.Count);
        int batchSize = Math.Max(1, _options.MaxBatchSize);

        for (int offset = 0; offset < texts.Count; offset += batchSize)
        {
            string[] batch = texts.Skip(offset).Take(batchSize).ToArray();
            float[][] vectors = await PostAsync(batch, cancellationToken).ConfigureAwait(false);
            results.AddRange(vectors.Select(v => new ReadOnlyMemory<float>(v)));
        }

        return results;
    }

    private async Task<float[][]> PostAsync(string[] input, CancellationToken cancellationToken)
    {
        // Serialize up-front so the request carries a Content-Length: some embedding servers reject chunked bodies.
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            new EmbeddingRequest(_options.Model!, input),
            EmbeddingJsonContext.Default.EmbeddingRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
        {
            Content = new ByteArrayContent(payloadBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpClient client = _clientFactory();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        EmbeddingResponse? payload = await response.Content
            .ReadFromJsonAsync(EmbeddingJsonContext.Default.EmbeddingResponse, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Data is not { Count: > 0 } data || data.Count != input.Length)
        {
            throw new InvalidOperationException(
                $"Embeddings endpoint returned {payload?.Data?.Count ?? 0} vectors for {input.Length} inputs.");
        }

        var vectors = new float[input.Length][];
        foreach (EmbeddingData item in data)
        {
            if ((uint)item.Index >= (uint)vectors.Length || item.Embedding is not { Length: > 0 })
            {
                throw new InvalidOperationException("Embeddings endpoint returned a malformed vector.");
            }

            vectors[item.Index] = item.Embedding;
        }

        return vectors;
    }
}

internal sealed record EmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

internal sealed record EmbeddingResponse(
    [property: JsonPropertyName("data")] List<EmbeddingData>? Data);

internal sealed record EmbeddingData(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] float[]? Embedding);

[JsonSerializable(typeof(EmbeddingRequest))]
[JsonSerializable(typeof(EmbeddingResponse))]
internal sealed partial class EmbeddingJsonContext : JsonSerializerContext;
