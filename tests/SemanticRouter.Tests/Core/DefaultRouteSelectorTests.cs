using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class DefaultRouteSelectorTests
{
    private static readonly RouteDefinition Chat = Route("chat", 1, "x");
    private static readonly RouteDefinition Code = Route("code", 2, "y");
    private static readonly RouteDefinition Reasoning = Route("reasoning", 4, "z");
    private static readonly ComplexityAnalysis Simple = new() { Score = 0.1, EstimatedTokens = 12 };

    private static SemanticRouterOptions DefaultOptions() => new()
    {
        DefaultSimilarityThreshold = 0.75f,
        FallbackTier = 3,
        AmbiguityMargin = 0.02f,
    };

    private static RouteDecision Select(SemanticRouterOptions options, ComplexityAnalysis complexity, params RouteScore[] ranked) =>
        new DefaultRouteSelector(options).SelectRoute(ranked, complexity);

    [Fact]
    public void Matched_route_sets_the_required_tier()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.9f), new RouteScore(Reasoning, 0.5f));

        decision.RequiredTier.Should().Be(1);
        decision.Reason.Should().Be(RoutingReason.SemanticMatch);
        decision.Route.Should().BeSameAs(Chat);
        decision.Similarity.Should().Be(0.9f);
        decision.LocalOnly.Should().BeFalse();
    }

    [Fact]
    public void Score_exactly_at_threshold_matches()
    {
        Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.75f)).Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Fact]
    public void Falls_back_to_the_fallback_tier_below_threshold()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.74f));

        decision.RequiredTier.Should().Be(3);
        decision.Reason.Should().Be(RoutingReason.NoMatchFallback);
        decision.Route.Should().BeNull();
        decision.Similarity.Should().Be(0.74f, "the best candidate is still reported for tuning");
    }

    [Fact]
    public void Falls_back_when_no_routes_are_configured()
    {
        Select(DefaultOptions(), Simple).Should().Be(new RouteDecision(3, RoutingReason.NoMatchFallback));
    }

    [Fact]
    public void Per_route_threshold_overrides_the_default()
    {
        RouteDefinition strict = Route("strict", 1, "x");
        strict.SimilarityThreshold = 0.95f;
        Select(DefaultOptions(), Simple, new RouteScore(strict, 0.9f)).Reason.Should().Be(RoutingReason.NoMatchFallback);

        RouteDefinition lenient = Route("lenient", 1, "x");
        lenient.SimilarityThreshold = 0.3f;
        Select(DefaultOptions(), Simple, new RouteScore(lenient, 0.4f)).Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Theory]
    [InlineData(0.44, 1, RoutingReason.SemanticMatch)]
    [InlineData(0.45, 2, RoutingReason.ComplexityEscalation)]
    [InlineData(0.70, 3, RoutingReason.ComplexityEscalation)]
    [InlineData(0.95, 4, RoutingReason.ComplexityEscalation)]
    public void Default_complexity_rules_raise_the_tier(double score, int expectedTier, RoutingReason expectedReason)
    {
        RouteDecision decision = Select(DefaultOptions(), new ComplexityAnalysis { Score = score }, new RouteScore(Chat, 0.9f));

        decision.RequiredTier.Should().Be(expectedTier);
        decision.Reason.Should().Be(expectedReason);
        decision.Route.Should().BeSameAs(Chat, "escalation keeps the route attribution");
    }

    [Fact]
    public void Complexity_never_lowers_a_route_tier()
    {
        Select(DefaultOptions(), new ComplexityAnalysis { Score = 0.5 }, new RouteScore(Reasoning, 0.9f))
            .Should().Match<RouteDecision>(d => d.RequiredTier == 4 && d.Reason == RoutingReason.SemanticMatch);
    }

    [Fact]
    public void Custom_complexity_rules_replace_the_defaults()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.ComplexityTiers = [new ComplexityTierRule { MinScore = 0.2, Tier = 5 }];

        Select(options, new ComplexityAnalysis { Score = 0.25 }, new RouteScore(Chat, 0.9f)).RequiredTier.Should().Be(5);
    }

    [Fact]
    public void Empty_complexity_rules_disable_escalation()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.ComplexityTiers = [];

        Select(options, new ComplexityAnalysis { Score = 0.99 }, new RouteScore(Chat, 0.9f)).RequiredTier.Should().Be(1);
    }

    [Fact]
    public void Complex_unmatched_prompts_escalate_above_the_fallback_tier()
    {
        RouteDecision decision = Select(DefaultOptions(), new ComplexityAnalysis { Score = 0.9 }, new RouteScore(Chat, 0.1f));

        decision.RequiredTier.Should().Be(4);
        decision.Reason.Should().Be(RoutingReason.ComplexityEscalation);
    }

    [Fact]
    public void Near_ties_take_the_higher_tier()
    {
        RouteDecision decision = Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.80f), new RouteScore(Reasoning, 0.79f));

        decision.RequiredTier.Should().Be(4);
        decision.Reason.Should().Be(RoutingReason.AmbiguousMatchEscalation);
        decision.Route.Should().BeSameAs(Chat);
    }

    [Fact]
    public void Ambiguity_ignores_routes_below_their_threshold_and_clear_wins()
    {
        RouteDefinition strictReasoning = Route("strict", 4, "z");
        strictReasoning.SimilarityThreshold = 0.9f;

        Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.80f), new RouteScore(strictReasoning, 0.79f)).RequiredTier.Should().Be(1);
        Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.85f), new RouteScore(Reasoning, 0.80f)).RequiredTier.Should().Be(1);
    }

    [Fact]
    public void Ambiguity_check_can_be_disabled()
    {
        SemanticRouterOptions options = DefaultOptions();
        options.AmbiguityMargin = 0f;

        Select(options, Simple, new RouteScore(Chat, 0.80f), new RouteScore(Reasoning, 0.80f)).RequiredTier.Should().Be(1);
    }

    [Fact]
    public void Required_capabilities_are_unioned_across_near_ties()
    {
        RouteDefinition vision = Route("vision", 2, "v");
        vision.RequiredCapabilities.Add("vision");
        RouteDefinition code = Route("code", 2, "c");
        code.RequiredCapabilities.Add("code");

        RouteDecision decision = Select(DefaultOptions(), Simple, new RouteScore(vision, 0.9f), new RouteScore(code, 0.89f));

        decision.RequiredCapabilities.Should().BeEquivalentTo("vision", "code");
    }

    [Fact]
    public void Local_only_is_sticky_and_survives_escalation()
    {
        RouteDefinition pii = Route("pii", 1, "p");
        pii.LocalOnly = true;

        RouteDecision direct = Select(DefaultOptions(), new ComplexityAnalysis { Score = 0.95 }, new RouteScore(pii, 0.9f));
        RouteDecision viaTie = Select(DefaultOptions(), Simple, new RouteScore(Chat, 0.9f), new RouteScore(pii, 0.89f));

        direct.LocalOnly.Should().BeTrue();
        direct.RequiredTier.Should().Be(4, "complexity still raises the tier, but only local models can serve it");
        viaTie.LocalOnly.Should().BeTrue("a near-tie with a sensitive route errs on the side of privacy");
    }
}
