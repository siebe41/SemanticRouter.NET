namespace SemanticRouter.Tests.Core;

public class HashingEmbeddingProviderTests
{
    private readonly HashingEmbeddingProvider _provider = new(256);

    [Fact]
    public async Task Produces_unit_vectors_of_the_configured_size()
    {
        ReadOnlyMemory<float> vector = await _provider.GenerateEmbeddingAsync("design a distributed cache");

        vector.Length.Should().Be(256);
        VectorMath.Norm(vector.Span).Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void Is_deterministic_across_instances()
    {
        float[] a = new HashingEmbeddingProvider(256).Embed("Hello, World");
        float[] b = new HashingEmbeddingProvider(256).Embed("hello world");

        a.Should().Equal(b);
    }

    [Fact]
    public void Lexically_related_texts_are_closer_than_unrelated_ones()
    {
        float[] query = _provider.Embed("summarize this article for me");
        float[] related = _provider.Embed("summarize this article in three bullet points");
        float[] unrelated = _provider.Embed("debug the race condition in my thread pool");

        VectorMath.CosineSimilarity(query, related)
            .Should().BeGreaterThan(VectorMath.CosineSimilarity(query, unrelated) + 0.2f);
    }

    [Fact]
    public void Text_without_words_yields_a_zero_vector()
    {
        _provider.Embed("?!").Should().OnlyContain(x => x == 0f);
    }

    [Fact]
    public async Task Batch_matches_single_embeddings_in_order()
    {
        string[] texts = ["alpha", "beta gamma", "delta"];

        IReadOnlyList<ReadOnlyMemory<float>> batch = await _provider.GenerateEmbeddingsAsync(texts);

        batch.Should().HaveCount(3);
        for (int i = 0; i < texts.Length; i++)
        {
            batch[i].ToArray().Should().Equal(_provider.Embed(texts[i]));
        }
    }

    [Fact]
    public void Rejects_tiny_dimensions()
    {
        Action act = () => _ = new HashingEmbeddingProvider(4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
