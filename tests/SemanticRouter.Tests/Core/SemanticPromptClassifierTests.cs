using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class SemanticPromptClassifierTests
{
    private static readonly Dictionary<string, float[]> Table = new()
    {
        ["hello"] = [1f, 0f, 0f],
        ["design a system"] = [0f, 1f, 0f],
        ["customer record"] = [0f, 0f, 1f],
        ["hi friend"] = [0.95f, 0.1f, 0f],
        ["architect a platform"] = [0.1f, 0.95f, 0f],
        ["weather in paris"] = [0.5f, 0.5f, 0.5f],
    };

    private static SemanticRouterOptions CreateOptions() => Options(
        Route("smalltalk", 1, "hello"),
        Route("reasoning", 4, "design a system"));

    private static Mock<IComplexityAnalyzer> Complexity(double score, int tokens = 10)
    {
        var analyzer = new Mock<IComplexityAnalyzer>();
        analyzer.Setup(a => a.Analyze(It.IsAny<string>()))
            .Returns(new ComplexityAnalysis { Score = score, EstimatedTokens = tokens });
        return analyzer;
    }

    [Fact]
    public async Task Routes_to_the_semantically_closest_route()
    {
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, CreateOptions(), Complexity(0.1).Object);

        PromptClassificationResult local = await classifier.ClassifyAsync("hi friend");
        PromptClassificationResult cloud = await classifier.ClassifyAsync("architect a platform");

        local.RequiredTier.Should().Be(1);
        local.RouteName.Should().Be("smalltalk");
        local.Reason.Should().Be(RoutingReason.SemanticMatch);
        local.Similarity.Should().BeGreaterThan(0.9f);
        local.IsRouteMatch.Should().BeTrue();

        cloud.RequiredTier.Should().Be(4);
        cloud.RouteName.Should().Be("reasoning");
    }

    [Fact]
    public async Task Unmatched_prompts_use_the_fallback_tier()
    {
        SemanticRouterOptions options = CreateOptions();
        options.FallbackTier = 2;
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, options, Complexity(0.1).Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("weather in paris");

        result.RequiredTier.Should().Be(2);
        result.Reason.Should().Be(RoutingReason.NoMatchFallback);
        result.RouteName.Should().BeNull();
        result.IsRouteMatch.Should().BeFalse();
        result.TopScores.Should().HaveCount(2);
    }

    [Fact]
    public async Task Route_utterances_are_embedded_once_and_cached()
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        SemanticRouterOptions options = CreateOptions();
        options.ClassificationCache.Enabled = false; // every request embeds its prompt; only the index is shared
        var classifier = new SemanticPromptClassifier(provider.Object, options, Complexity(0.1).Object);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => classifier.ClassifyAsync("hi friend").AsTask()));

        provider.Verify(p => p.GenerateEmbeddingsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.GenerateEmbeddingAsync("hi friend", It.IsAny<CancellationToken>()), Times.Exactly(20));
    }

    [Fact]
    public async Task Failed_index_build_is_retried_on_the_next_call()
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        provider.SetupSequence(p => p.GenerateEmbeddingsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("embedding server down"))
            .Returns(ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                [new ReadOnlyMemory<float>(Table["hello"]), new ReadOnlyMemory<float>(Table["design a system"])]));
        var classifier = new SemanticPromptClassifier(provider.Object, CreateOptions(), Complexity(0.1).Object);

        Func<Task> first = () => classifier.ClassifyAsync("hi friend").AsTask();
        await first.Should().ThrowAsync<HttpRequestException>();

        PromptClassificationResult second = await classifier.ClassifyAsync("hi friend");
        second.RouteName.Should().Be("smalltalk");
    }

    [Fact]
    public async Task Very_complex_prompts_short_circuit_without_calling_the_embedding_provider()
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        var classifier = new SemanticPromptClassifier(provider.Object, CreateOptions(), Complexity(0.95).Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("hello");

        result.RequiredTier.Should().Be(4, "0.95 reaches the top default complexity rule");
        result.Reason.Should().Be(RoutingReason.ComplexityShortCircuit);
        result.Similarity.Should().BeNull();
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Short_circuit_is_disabled_while_a_local_only_route_exists()
    {
        SemanticRouterOptions options = CreateOptions();
        RouteDefinition pii = Route("pii", 1, "customer record");
        pii.LocalOnly = true;
        options.Routes.Add(pii);
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, options, Complexity(0.99, tokens: 90_000).Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("customer record");

        result.RouteName.Should().Be("pii");
        result.LocalOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Short_circuit_can_be_disabled()
    {
        SemanticRouterOptions options = CreateOptions();
        options.ComplexityShortCircuitThreshold = 1.1;
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, options, Complexity(0.95).Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("hi friend");

        result.Reason.Should().Be(RoutingReason.ComplexityEscalation);
        result.RouteName.Should().Be("smalltalk");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_prompts_bypass_analysis(string prompt)
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        Mock<IComplexityAnalyzer> analyzer = Complexity(0.1);
        var classifier = new SemanticPromptClassifier(provider.Object, CreateOptions(), analyzer.Object);

        PromptClassificationResult result = await classifier.ClassifyAsync(prompt);

        result.Reason.Should().Be(RoutingReason.EmptyPrompt);
        result.RequiredTier.Should().Be(3);
        provider.VerifyNoOtherCalls();
        analyzer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task No_routes_means_no_embedding_calls()
    {
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        var classifier = new SemanticPromptClassifier(provider.Object, new SemanticRouterOptions(), Complexity(0.1).Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("hello");

        result.Reason.Should().Be(RoutingReason.NoMatchFallback);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delegates_the_decision_to_the_route_selector()
    {
        var selector = new Mock<IRouteSelector>();
        selector.Setup(s => s.SelectRoute(It.IsAny<IReadOnlyList<RouteScore>>(), It.IsAny<ComplexityAnalysis>()))
            .Returns(new RouteDecision(5, RoutingReason.AmbiguousMatchEscalation));
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, CreateOptions(), Complexity(0.1).Object, selector.Object);

        PromptClassificationResult result = await classifier.ClassifyAsync("hi friend");

        result.Reason.Should().Be(RoutingReason.AmbiguousMatchEscalation);
        selector.Verify(s => s.SelectRoute(
            It.Is<IReadOnlyList<RouteScore>>(r => r.Count == 2 && r[0].Route.Name == "smalltalk"),
            It.IsAny<ComplexityAnalysis>()), Times.Once);
    }

    [Fact]
    public async Task Top_scores_are_truncated_to_the_configured_count()
    {
        SemanticRouterOptions options = CreateOptions();
        options.MaxReportedScores = 1;
        var classifier = new SemanticPromptClassifier(FromTable(Table).Object, options, Complexity(0.1).Object);

        (await classifier.ClassifyAsync("hi friend")).TopScores.Should().ContainSingle();
    }

    [Fact]
    public async Task Caller_cancellation_does_not_poison_the_shared_index()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<ReadOnlyMemory<float>>>();
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        provider.Setup(p => p.GenerateEmbeddingsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IReadOnlyList<ReadOnlyMemory<float>>>(gate.Task));
        var classifier = new SemanticPromptClassifier(provider.Object, CreateOptions(), Complexity(0.1).Object);

        using var cts = new CancellationTokenSource();
        Task cancelled = classifier.ClassifyAsync("hi friend", cts.Token).AsTask();
        await cts.CancelAsync();
        await cancelled.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        Task<PromptClassificationResult> pending = classifier.ClassifyAsync("hi friend").AsTask();
        gate.SetResult([new ReadOnlyMemory<float>(Table["hello"]), new ReadOnlyMemory<float>(Table["design a system"])]);

        (await pending).RouteName.Should().Be("smalltalk");
        provider.Verify(p => p.GenerateEmbeddingsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        SemanticRouterOptions options = Options(Route("dup", 1, "a"), Route("dup", 2, "b"));

        Action act = () => _ = new SemanticPromptClassifier(FromTable(Table).Object, options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'dup' is used more than once*");
    }

    [Fact]
    public async Task End_to_end_with_the_hashing_provider_and_default_heuristics()
    {
        SemanticRouterOptions options = Options(
            Route("summarize", 1, "summarize this article", "give me a short summary of this email"),
            Route("architecture", 4, "design a scalable architecture", "compare architecture trade-offs"));
        options.DefaultSimilarityThreshold = 0.3f;
        var classifier = new SemanticPromptClassifier(new HashingEmbeddingProvider(), options);

        PromptClassificationResult summary = await classifier.ClassifyAsync("please summarize this article about rust");
        PromptClassificationResult design = await classifier.ClassifyAsync("design a scalable architecture for payments");

        summary.Should().Match<PromptClassificationResult>(r => r.RequiredTier == 1 && r.RouteName == "summarize");
        design.Should().Match<PromptClassificationResult>(r => r.RequiredTier == 4 && r.RouteName == "architecture");
        summary.Elapsed.Should().BePositive();
    }
}
