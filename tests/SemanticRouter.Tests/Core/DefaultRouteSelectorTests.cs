using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class DefaultRouteSelectorTests
{
    private static readonly RouteDefinition LocalRoute = Route("chat", RouteTarget.Local, "x");
    private static readonly RouteDefinition CloudRoute = Route("reasoning", RouteTarget.Cloud, "y");
    private static readonly ComplexityAnalysis Simple = new() { Score = 0.1, EstimatedTokens = 12 };

    private static SemanticRouterOptions DefaultOptions() => new()
    {
        DefaultSimilarityThreshold = 0.75f,
        FallbackTarget = RouteTarget.Cloud,
        ComplexityEscalationThreshold = 0.65,
        LocalMaxPromptTokens = 4096,
        AmbiguityMargin = 0.02f,
    };

    private static RouteDecision Select(SemanticRouterOptions options, ComplexityAnalysis complexity, params RouteScore[] ranked) =>
        new DefaultRouteSelector(options).SelectRoute(ranked, complexity);

    [Fact]
    public void Matches_the_best_route_above_threshold()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new(LocalRoute, 0.9f), new(CloudRoute, 0.5f));

        decision.Should().Be(new RouteDecision(RouteTarget.Local, RoutingReason.SemanticMatch, LocalRoute, 0.9f));
    }

    [Fact]
    public void Score_exactly_at_threshold_matches()
    {
        Select(DefaultOptions(), Simple, new RouteScore(LocalRoute, 0.75f)).Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Theory]
    [InlineData(RouteTarget.Cloud)]
    [InlineData(RouteTarget.Local)]
    public void Falls_back_when_best_score_is_below_threshold(RouteTarget fallback)
    {
        SemanticRouterOptions options = DefaultOptions();
        options.FallbackTarget = fallback;

        RouteDecision decision = Select(options, Simple, new RouteScore(LocalRoute, 0.74f));

        decision.Target.Should().Be(fallback);
        decision.Reason.Should().Be(RoutingReason.NoMatchFallback);
        decision.Route.Should().BeNull();
        decision.Similarity.Should().Be(0.74f, "the best candidate is still reported for tuning");
    }

    [Fact]
    public void Falls_back_when_no_routes_are_configured()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple);

        decision.Should().Be(new RouteDecision(RouteTarget.Cloud, RoutingReason.NoMatchFallback));
    }

    [Fact]
    public void Per_route_threshold_overrides_the_default()
    {
        RouteDefinition strict = Route("strict", RouteTarget.Local, "x");
        strict.SimilarityThreshold = 0.95f;

        Select(DefaultOptions(), Simple, new RouteScore(strict, 0.9f)).Reason.Should().Be(RoutingReason.NoMatchFallback);

        RouteDefinition lenient = Route("lenient", RouteTarget.Local, "x");
        lenient.SimilarityThreshold = 0.3f;

        Select(DefaultOptions(), Simple, new RouteScore(lenient, 0.4f)).Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Fact]
    public void Escalates_complex_prompts_from_local_to_cloud()
    {
        var complex = new ComplexityAnalysis { Score = 0.7, EstimatedTokens = 300 };

        RouteDecision decision = Select(DefaultOptions(), complex, new RouteScore(LocalRoute, 0.9f));

        decision.Should().Be(new RouteDecision(RouteTarget.Cloud, RoutingReason.ComplexityEscalation, LocalRoute, 0.9f));
    }

    [Fact]
    public void Escalates_prompts_that_exceed_the_local_context_budget()
    {
        var huge = new ComplexityAnalysis { Score = 0.2, EstimatedTokens = 5000 };

        Select(DefaultOptions(), huge, new RouteScore(LocalRoute, 0.9f)).Reason
            .Should().Be(RoutingReason.ContextWindowEscalation);
    }

    [Fact]
    public void Context_window_check_can_be_disabled()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.LocalMaxPromptTokens = 0;
        var huge = new ComplexityAnalysis { Score = 0.2, EstimatedTokens = 50_000 };

        Select(options, huge, new RouteScore(LocalRoute, 0.9f)).Target.Should().Be(RouteTarget.Local);
    }

    [Fact]
    public void Pinned_local_routes_are_never_escalated()
    {
        RouteDefinition pinned = Route("pii", RouteTarget.Local, "x");
        pinned.AllowEscalation = false;
        var complexAndHuge = new ComplexityAnalysis { Score = 0.99, EstimatedTokens = 50_000 };

        RouteDecision decision = Select(DefaultOptions(), complexAndHuge, new(pinned, 0.8f), new(CloudRoute, 0.795f));

        decision.Should().Be(new RouteDecision(RouteTarget.Local, RoutingReason.SemanticMatch, pinned, 0.8f));
    }

    [Fact]
    public void Cloud_matches_are_never_downgraded()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new(CloudRoute, 0.8f), new(LocalRoute, 0.79f));

        decision.Target.Should().Be(RouteTarget.Cloud);
        decision.Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Fact]
    public void Narrow_local_win_over_a_matching_cloud_route_escalates()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new(LocalRoute, 0.80f), new(CloudRoute, 0.79f));

        decision.Should().Be(new RouteDecision(RouteTarget.Cloud, RoutingReason.AmbiguousMatchEscalation, CloudRoute, 0.79f));
    }

    [Fact]
    public void Ambiguity_ignores_cloud_routes_below_their_threshold()
    {
        SemanticRouterOptions options = DefaultOptions();
        RouteDefinition strictCloud = Route("strict-cloud", RouteTarget.Cloud, "y");
        strictCloud.SimilarityThreshold = 0.9f;

        Select(options, Simple, new(LocalRoute, 0.80f), new(strictCloud, 0.79f)).Target.Should().Be(RouteTarget.Local);
    }

    [Fact]
    public void Clear_local_win_is_not_ambiguous()
    {
        Select(DefaultOptions(), Simple, new(LocalRoute, 0.85f), new(CloudRoute, 0.80f)).Target.Should().Be(RouteTarget.Local);
    }

    [Fact]
    public void Ambiguity_check_can_be_disabled()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.AmbiguityMargin = 0f;

        Select(options, Simple, new(LocalRoute, 0.80f), new(CloudRoute, 0.80f)).Target.Should().Be(RouteTarget.Local);
    }

    [Fact]
    public void Local_fallback_is_still_escalated_for_complex_prompts()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.FallbackTarget = RouteTarget.Local;
        var complex = new ComplexityAnalysis { Score = 0.8 };

        RouteDecision decision = Select(options, complex, new RouteScore(LocalRoute, 0.1f));

        decision.Target.Should().Be(RouteTarget.Cloud);
        decision.Reason.Should().Be(RoutingReason.ComplexityEscalation);
    }
}
