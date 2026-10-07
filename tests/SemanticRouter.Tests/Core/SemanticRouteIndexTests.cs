using Moq;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class SemanticRouteIndexTests
{
    private static readonly Dictionary<string, float[]> Table = new()
    {
        ["greet-1"] = [1f, 0f, 0f],
        ["greet-2"] = [0.8f, 0.6f, 0f],
        ["code-1"] = [0f, 0f, 5f],
    };

    private static readonly RouteDefinition Greetings = Route("greetings", RouteTarget.Local, "greet-1", "greet-2", "  ");
    private static readonly RouteDefinition Code = Route("code", RouteTarget.Cloud, "code-1");

    [Fact]
    public async Task MaxSimilarity_scores_each_route_by_its_nearest_utterance()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync([Greetings, Code], FromTable(Table).Object);

        RouteScore[] scores = index.Score([0.8f, 0.6f, 0f]);

        index.VectorCount.Should().Be(3, "blank utterances are skipped");
        index.Dimensions.Should().Be(3);
        scores.Select(s => s.Route.Name).Should().Equal("greetings", "code");
        scores[0].Similarity.Should().BeApproximately(1f, 1e-6f);
        scores[1].Similarity.Should().BeApproximately(0f, 1e-6f);
    }

    [Fact]
    public async Task Centroid_scores_each_route_by_its_mean_direction()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync(
            [Greetings, Code], FromTable(Table).Object, RouteScoringStrategy.Centroid);

        RouteScore[] scores = index.Score([1f, 0f, 0f]);

        index.VectorCount.Should().Be(2);
        // centroid of (1,0,0) and (0.8,0.6,0) is (0.9,0.3,0) -> cos with (1,0,0) = 0.9 / sqrt(0.9)
        scores[0].Similarity.Should().BeApproximately(0.9f / MathF.Sqrt(0.9f), 1e-5f);
    }

    [Fact]
    public async Task Query_magnitude_does_not_affect_scores()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync([Greetings, Code], FromTable(Table).Object);

        index.Score([0f, 0f, 100f])[0].Similarity.Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public async Task Ties_preserve_configuration_order()
    {
        var table = new Dictionary<string, float[]> { ["a"] = [1f, 0f], ["b"] = [1f, 0f] };
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync(
            [Route("first", RouteTarget.Local, "a"), Route("second", RouteTarget.Cloud, "b")], FromTable(table).Object);

        index.Score([1f, 0f]).Select(s => s.Route.Name).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Zero_query_vector_scores_every_route_zero()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync([Greetings, Code], FromTable(Table).Object);

        index.Score([0f, 0f, 0f]).Should().OnlyContain(s => s.Similarity == 0f);
    }

    [Fact]
    public async Task Dimension_mismatch_between_index_and_prompt_is_reported()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync([Greetings, Code], FromTable(Table).Object);

        Action act = () => index.Score([1f, 0f]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*2 dimensions*built with 3*");
    }

    [Fact]
    public async Task Inconsistent_provider_dimensions_fail_the_build()
    {
        var table = new Dictionary<string, float[]> { ["a"] = [1f, 0f], ["b"] = [1f, 0f, 0f] };

        Func<Task> act = () => SemanticRouteIndex.BuildAsync([Route("r", RouteTarget.Local, "a", "b")], FromTable(table).Object);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*inconsistent size*");
    }

    [Fact]
    public async Task Utterances_are_embedded_in_a_single_batch()
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);

        await SemanticRouteIndex.BuildAsync([Greetings, Code], provider.Object);

        provider.Verify(
            p => p.GenerateEmbeddingsAsync(
                It.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "greet-1", "greet-2", "code-1" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(p => p.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Empty_route_set_produces_an_empty_index()
    {
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync([], FromTable(Table).Object);

        index.Score([1f, 2f, 3f]).Should().BeEmpty();
    }
}
