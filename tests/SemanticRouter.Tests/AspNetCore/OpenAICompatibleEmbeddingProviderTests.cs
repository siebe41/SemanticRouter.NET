using System.Net;
using System.Text.Json;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

public class OpenAICompatibleEmbeddingProviderTests
{
    private static EmbeddingProviderOptions Options(int batchSize = 64) => new()
    {
        Provider = EmbeddingProviderKind.OpenAICompatible,
        Endpoint = new Uri("http://ollama:11434"),
        Model = "nomic-embed-text",
        ApiKey = "secret",
        MaxBatchSize = batchSize,
    };

    // Echoes one 2-d vector per input, [index, length of text], in reverse order to prove re-ordering by "index".
    private static StubHttpMessageHandler EchoHandler() => new((_, body) =>
    {
        using JsonDocument request = JsonDocument.Parse(body);
        string[] inputs = request.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var data = inputs.Select((text, i) => new { index = i, embedding = new[] { (float)i, text.Length } }).Reverse();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data })) };
    });

    [Fact]
    public async Task Posts_an_openai_embeddings_request()
    {
        StubHttpMessageHandler handler = EchoHandler();
        var provider = new OpenAICompatibleEmbeddingProvider(new HttpClient(handler), Options());

        ReadOnlyMemory<float> vector = await provider.GenerateEmbeddingAsync("hello");

        vector.ToArray().Should().Equal(0f, 5f);
        (HttpRequestMessage request, string body) = handler.Requests.Single();
        request.RequestUri.Should().Be(new Uri("http://ollama:11434/v1/embeddings"));
        request.Headers.Authorization!.ToString().Should().Be("Bearer secret");
        body.Should().Be("""{"model":"nomic-embed-text","input":["hello"]}""");
    }

    [Fact]
    public async Task Batches_requests_and_preserves_order()
    {
        StubHttpMessageHandler handler = EchoHandler();
        var provider = new OpenAICompatibleEmbeddingProvider(new HttpClient(handler), Options(batchSize: 2));

        IReadOnlyList<ReadOnlyMemory<float>> vectors = await provider.GenerateEmbeddingsAsync(["a", "bb", "ccc", "dddd", "eeeee"]);

        handler.Requests.Should().HaveCount(3);
        vectors.Select(v => v.Span[1]).Should().Equal(1f, 2f, 3f, 4f, 5f);
    }

    [Fact]
    public async Task Http_errors_surface_as_exceptions()
    {
        var provider = new OpenAICompatibleEmbeddingProvider(
            new HttpClient(StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)), Options());

        Func<Task> act = () => provider.GenerateEmbeddingAsync("x").AsTask();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Vector_count_mismatch_is_rejected()
    {
        var provider = new OpenAICompatibleEmbeddingProvider(
            new HttpClient(StubHttpMessageHandler.Json("""{"data":[]}""")), Options());

        Func<Task> act = () => provider.GenerateEmbeddingAsync("x").AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*0 vectors for 1 inputs*");
    }

    [Fact]
    public void Endpoint_and_model_are_required()
    {
        Action noEndpoint = () => _ = new OpenAICompatibleEmbeddingProvider(new HttpClient(), new EmbeddingProviderOptions { Model = "m" });
        Action noModel = () => _ = new OpenAICompatibleEmbeddingProvider(
            new HttpClient(), new EmbeddingProviderOptions { Endpoint = new Uri("http://x") });

        noEndpoint.Should().Throw<InvalidOperationException>().WithMessage("*Endpoint*");
        noModel.Should().Throw<InvalidOperationException>().WithMessage("*Model*");
    }
}
