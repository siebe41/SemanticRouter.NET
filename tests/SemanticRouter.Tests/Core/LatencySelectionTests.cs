using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>Latency as a selection factor (#70): Fastest and Balanced use recent time to first byte; CheapestCapable does not.</summary>
public class LatencySelectionTests
{
    private static readonly ModelDefinition Slow = Model("slow-cheap", 2, costPerMillion: 1m);
    private static readonly ModelDefinition Quick = Model("quick", 2, costPerMillion: 2m);
    private static readonly ModelDefinition Cheapest = Model("cheapest", 2, costPerMillion: 0.5m);

    private sealed class Latency(Dictionary<string, int> milliseconds) : IModelLatency
    {
        public TimeSpan? TypicalTimeToFirstByte(ModelDefinition model) =>
            milliseconds.TryGetValue(model.Name, out int ms) ? TimeSpan.FromMilliseconds(ms) : null;
    }

    private static PromptRouter Router(Dictionary<string, int> latency, SelectionStrategy strategy = SelectionStrategy.CheapestCapable, double factor = 1.5, params ModelDefinition[] models)
    {
        var options = new SemanticRouterOptions { Selection = { Strategy = strategy, BalancedLatencyFactor = factor } };
        foreach (ModelDefinition model in models.Length > 0 ? models : [Slow, Quick])
        {
            options.Models.Add(model);
        }

        var source = new Latency(latency);
        return new PromptRouter(new Mock<IPromptClassifier>().Object, options, new PromptRouterDependencies { Selector = new DefaultModelSelector(options.Selection, new DefaultModelSelectorDependencies { Latency = source }), Latency = source });
    }

    private static PromptClassificationResult Tier2 => new() { RequiredTier = 2, Reason = RoutingReason.SemanticMatch, Complexity = new ComplexityAnalysis { EstimatedTokens = 10 } };

    private static RoutingPlan Plan(PromptRouter router, SelectionStrategy? strategy = null) =>
        router.Plan(Tier2, new PromptRoutingRequest { Prompt = "hello", Strategy = strategy });

    [Fact]
    public void Under_fastest_the_quicker_of_two_equally_capable_models_is_chosen_and_the_reason_says_so()
    {
        RoutingPlan plan = Plan(Router(new() { ["slow-cheap"] = 1500, ["quick"] = 300 }, SelectionStrategy.Fastest));

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("quick", "slow-cheap");
        plan.SelectionNote.Should().Be("fastest: quick (300 ms to first byte) over cheaper slow-cheap (1,500 ms to first byte)");
    }

    [Fact]
    public void Cheapest_capable_ignores_latency_exactly_as_before()
    {
        RoutingPlan plan = Plan(Router(new() { ["slow-cheap"] = 1500, ["quick"] = 300 }));

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("slow-cheap", "quick");
        plan.SelectionNote.Should().BeNull();
    }

    [Fact]
    public void A_request_can_ask_for_fastest_and_without_any_latency_it_orders_by_cost()
    {
        Plan(Router(new() { ["slow-cheap"] = 1500, ["quick"] = 300 }), SelectionStrategy.Fastest).Candidates[0].Model.Name.Should().Be("quick");

        RoutingPlan unknown = Plan(Router([], SelectionStrategy.Fastest));
        unknown.Candidates.Select(c => c.Model.Name).Should().Equal("slow-cheap", "quick");
        unknown.SelectionNote.Should().Be("fastest: slow-cheap (no recent latency), also the cheapest");
    }

    [Fact]
    public void A_model_without_recent_latency_counts_as_typical()
    {
        // quick 300 ms, slow-cheap 1500 ms, cheapest unknown: it counts as the median (900 ms), between the two.
        RoutingPlan plan = Plan(Router(new() { ["slow-cheap"] = 1500, ["quick"] = 300 }, SelectionStrategy.Fastest, models: [Slow, Quick, Cheapest]));

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("quick", "cheapest", "slow-cheap");
    }

    [Theory]
    [InlineData(1.5, "quick", "cheapest", "slow-cheap")]
    [InlineData(2.5, "slow-cheap", "quick", "cheapest")]
    public void Balanced_takes_the_cheapest_model_that_is_nearly_as_fast_as_the_fastest(double factor, string chosen, string second, string third)
    {
        // quick 300 ms is fastest; slow-cheap (600 ms) is within 2.5x but not 1.5x of it; cheapest (2,000 ms) is never.
        // The models that are not fast enough follow by cost.
        RoutingPlan plan = Plan(Router(new() { ["slow-cheap"] = 600, ["quick"] = 300, ["cheapest"] = 2000 }, SelectionStrategy.Balanced, factor, Slow, Quick, Cheapest));

        plan.Candidates.Select(c => c.Model.Name).Should().Equal(chosen, second, third);
        plan.SelectionNote.Should().StartWith($"balanced: {chosen} (");
    }
}
