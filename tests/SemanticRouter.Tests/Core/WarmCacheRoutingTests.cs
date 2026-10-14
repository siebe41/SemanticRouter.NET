using Moq;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// Cache-aware stickiness (#94): a conversation stays on the model that holds its prompt cache when moving would cost
/// more this turn, even above the required tier, and moves when the move is cheaper.
/// </summary>
public class WarmCacheRoutingTests
{
    // List prices per million tokens: input, output, cache read, cache write (5-minute entries).
    private static ModelDefinition Opus => Priced("opus", 4, 4m, 20m, 0.2m, 5m);

    private static ModelDefinition Sonnet => Priced("sonnet", 3, 2m, 10m, 0.2m, 2.5m);

    private static ModelDefinition Haiku => Priced("haiku", 2, 0.1m, 0.5m, 0.01m, 0.125m);

    private static ModelDefinition Priced(string name, int tier, decimal input, decimal output, decimal read, decimal write) => new()
    {
        Name = name,
        Connector = "test",
        Tier = tier,
        InputCostPerMillionTokens = input,
        OutputCostPerMillionTokens = output,
        CacheReadCostPerMillionTokens = read,
        CacheWriteCostPerMillionTokens = write,
    };

    private static PromptRouter Router(SelectionStrategy strategy = SelectionStrategy.CheapestCapable, IModelHealth? health = null, params ModelDefinition[] models)
    {
        var options = new SemanticRouterOptions { Selection = { Strategy = strategy } };
        foreach (ModelDefinition model in models)
        {
            options.Models.Add(model);
        }

        return new PromptRouter(new Mock<IPromptClassifier>().Object, options, health: health);
    }

    private static PromptClassificationResult Tier(int tier) =>
        new() { RequiredTier = tier, Reason = RoutingReason.SemanticMatch, Complexity = new ComplexityAnalysis { EstimatedTokens = 10 } };

    /// <summary>A follow-up turn of an 82k-token conversation whose first 80k tokens are cached on <paramref name="warm"/>.</summary>
    private static PromptRoutingRequest Turn(params string[] warm) => new()
    {
        Prompt = "now update the tests",
        EstimatedInputTokens = 82_000,
        WarmCaches = [.. warm.Select(model => new WarmCache(model, 80_000))],
    };

    [Fact]
    public void A_conversation_stays_on_its_warm_model_above_the_tier_when_moving_costs_more()
    {
        RoutingPlan plan = Router(models: [Opus, Sonnet]).Plan(Tier(3), Turn("opus"));

        // Opus reads 80k cached tokens (~$0.034); Sonnet would re-send and re-write them (~$0.209).
        plan.Candidates.Select(c => c.Model.Name).Should().Equal("opus", "sonnet");
        plan.Sticky.Should().BeTrue();
        plan.SelectionNote.Should().Be("prompt cache: opus (80,000 tokens cached) over sonnet, which would cost $0.1749 more this turn");
    }

    [Fact]
    public void A_conversation_moves_when_the_cheaper_model_costs_less_even_with_the_cache_lost()
    {
        RoutingPlan plan = Router(models: [Sonnet, Haiku]).Plan(Tier(2), Turn("sonnet"));

        // Sonnet reading its cache (~$0.025) still costs more than Haiku re-sending and caching everything (~$0.010).
        plan.Candidates[0].Model.Name.Should().Be("haiku");
        (plan.Sticky, plan.SelectionNote).Should().Be((false, null));
    }

    [Fact]
    public void Without_a_warm_cache_the_ranking_is_unchanged()
    {
        RoutingPlan plan = Router(models: [Opus, Sonnet]).Plan(Tier(3), Turn());

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("sonnet", "opus");
        plan.Sticky.Should().BeFalse();
    }

    [Fact]
    public void A_warm_model_that_is_cooling_down_or_below_the_tier_is_not_kept()
    {
        var health = new ModelHealthTracker(new ModelSelectionOptions());
        ModelDefinition opus = Opus;
        health.ReportFailure(opus, ModelFailureKind.Unavailable);

        Router(health: health, models: [opus, Sonnet]).Plan(Tier(3), Turn("opus")).Candidates[0].Model.Name.Should().Be("sonnet");
        Router(models: [Sonnet, Haiku]).Plan(Tier(3), Turn("haiku")).Candidates[0].Model.Name.Should().Be("sonnet", "haiku cannot do tier-3 work");
    }

    [Fact]
    public void After_a_failover_the_conversation_returns_to_the_model_holding_its_cache()
    {
        // Opus failed over to an equally strong model, which answered the last turn without caching; opus is back.
        ModelDefinition stand_in = Priced("stand-in", 4, 3m, 15m, 3m, 3m);
        stand_in.CacheReadCostPerMillionTokens = null;
        stand_in.CacheWriteCostPerMillionTokens = null;

        RoutingPlan plan = Router(models: [Opus, stand_in]).Plan(Tier(4), Turn("opus") with { PreferredModel = "stand-in" });

        plan.Candidates[0].Model.Name.Should().Be("opus", "staying on the stand-in would re-send 80k tokens at full price");
        plan.SelectionNote.Should().StartWith("prompt cache: opus (80,000 tokens cached) over stand-in");
    }

    [Theory]
    [InlineData(SelectionStrategy.Priority)]
    [InlineData(SelectionStrategy.Fastest)]
    public void Strategies_that_do_not_choose_by_cost_keep_their_order(SelectionStrategy strategy)
    {
        ModelDefinition opus = Opus;
        opus.Priority = 1;

        RoutingPlan plan = Router(strategy, models: [opus, Sonnet]).Plan(Tier(3), Turn("opus"));

        plan.Candidates[0].Model.Name.Should().Be("sonnet");
    }

    [Fact]
    public void Of_two_warm_models_the_cheaper_one_with_its_cache_is_chosen()
    {
        // After a failover both hold the conversation: the one that is cheaper with its cache wins.
        RoutingPlan plan = Router(models: [Opus, Sonnet, Priced("sonnet-pricey", 3, 3m, 15m, 0.3m, 3.75m)]).Plan(Tier(3), Turn("opus", "sonnet-pricey"));

        plan.Candidates[0].Model.Name.Should().Be("opus", "opus reads at $0.20 per million; sonnet-pricey at $0.30 with dearer output");
    }
}
