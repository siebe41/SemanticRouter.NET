using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// Cache-aware cost (#96): with a learned prompt-cache share, a model's cached input is priced at its cache prices when
/// models are compared; without one, list prices decide as before.
/// </summary>
public class CacheAwareCostTests
{
    // "cached" lists a higher input price but reads its cache at a tenth of it; "plain" is a little cheaper and has no cache prices.
    private static readonly ModelDefinition Cached = Priced("cached", input: 3m, cacheRead: 0.3m, cacheWrite: 3.75m);
    private static readonly ModelDefinition Plain = Priced("plain", input: 2.5m);

    private static ModelDefinition Priced(string name, decimal input, decimal? cacheRead = null, decimal? cacheWrite = null)
    {
        ModelDefinition model = Model(name, 3);
        model.InputCostPerMillionTokens = input;
        model.OutputCostPerMillionTokens = 15m;
        model.CacheReadCostPerMillionTokens = cacheRead;
        model.CacheWriteCostPerMillionTokens = cacheWrite;
        return model;
    }

    private sealed class Shares(Dictionary<(string Model, string? Route), PromptCacheShare> shares) : IModelCacheShare
    {
        public PromptCacheShare? Typical(ModelDefinition model, string? route) => shares.GetValueOrDefault((model.Name, route));
    }

    private sealed class SameLatency : IModelLatency
    {
        public TimeSpan? TypicalTimeToFirstByte(ModelDefinition model) => TimeSpan.FromMilliseconds(400);
    }

    private static RoutingPlan Plan(
        IModelCacheShare? cache, string route = "agentic", SelectionStrategy strategy = SelectionStrategy.CheapestCapable, IModelLatency? latency = null)
    {
        var options = new SemanticRouterOptions { Selection = { Strategy = strategy } };
        options.Models.Add(Plain);
        options.Models.Add(Cached);
        var classification = new PromptClassificationResult
        {
            RequiredTier = 3,
            Reason = RoutingReason.SemanticMatch,
            RouteName = route,
            Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
        };
        var router = new PromptRouter(new Mock<IPromptClassifier>().Object, options, new PromptRouterDependencies { Selector = new DefaultModelSelector(options.Selection, new DefaultModelSelectorDependencies { Latency = latency, CacheShare = cache }) });
        return router.Plan(classification, new PromptRoutingRequest { Prompt = "continue", EstimatedInputTokens = 20_000 });
    }

    private static readonly Shares Learned = new(new()
    {
        [("cached", "agentic")] = new PromptCacheShare { Read = 0.8, Write = 0.05 },
        [("plain", "agentic")] = new PromptCacheShare { Read = 0, Write = 0 },
    });

    [Fact]
    public void With_a_learned_share_a_model_with_cheap_cache_reads_ranks_ahead_of_one_with_a_slightly_lower_input_price()
    {
        RoutingPlan plan = Plan(Learned);

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("cached", "plain");
        // 20,000 input: 15% at $3, 80% at $0.30, 5% at $3.75 ($0.8775 per M); 512 output at $15.
        plan.Candidates[0].EstimatedCost.Should().Be(((20_000 * 0.8775m) + (512 * 15m)) / 1_000_000m);
        plan.Candidates[1].EstimatedCost.Should().Be(Plain.EstimateCost(20_000, 512), "no cache prices: its list price");
    }

    [Fact]
    public void Without_cache_data_the_ordering_is_unchanged()
    {
        string[] before = [.. Plan(cache: null).Candidates.Select(c => c.Model.Name)];

        before.Should().Equal("plain", "cached");
        Plan(new Shares([])).Candidates.Select(c => (c.Model.Name, c.EstimatedCost))
            .Should().Equal(("plain", Plain.EstimateCost(20_000, 512)), ("cached", Cached.EstimateCost(20_000, 512)));
        Plan(Learned, route: "chat").Candidates.Select(c => c.Model.Name).Should().Equal(before, "the share was learned on another route");
    }

    [Fact]
    public void Balanced_and_fastest_compare_the_cache_aware_cost_too()
    {
        // Equally quick: Balanced and Fastest both fall to cost, which counts the cache.
        Plan(Learned, strategy: SelectionStrategy.Balanced, latency: new SameLatency()).Candidates.Select(c => c.Model.Name).Should().Equal("cached", "plain");
        Plan(Learned, strategy: SelectionStrategy.Fastest, latency: new SameLatency()).Candidates.Select(c => c.Model.Name).Should().Equal("cached", "plain");
        Plan(null, strategy: SelectionStrategy.Fastest, latency: new SameLatency()).Candidates.Select(c => c.Model.Name).Should().Equal("plain", "cached");
    }

    [Fact]
    public void The_estimate_prices_cached_input_at_the_cache_prices_and_the_rest_at_the_input_price()
    {
        Cached.EstimateCost(1_000_000, 0, null).Should().Be(Cached.EstimateCost(1_000_000, 0));
        Cached.EstimateCost(1_000_000, 0, new PromptCacheShare { Read = 0, Write = 0 }).Should().Be(3m);
        Cached.EstimateCost(1_000_000, 0, new PromptCacheShare { Read = 0.5, Write = 0.25 }).Should().Be(0.75m + 0.15m + 0.9375m);
        Cached.EstimateCost(1_000_000, 1_000_000, new PromptCacheShare { Read = 1, Write = 0 }).Should().Be(0.3m + 15m);
        Plain.EstimateCost(1_000_000, 0, new PromptCacheShare { Read = 0.9, Write = 0.1 }).Should().Be(2.5m, "without cache prices, cached input costs the input price");
    }
}
