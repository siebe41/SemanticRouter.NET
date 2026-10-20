using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// Cache-aware stickiness (#94): a conversation stays on the model with its warm prompt cache when moving costs more,
/// moves when it costs less, and goes back to it after a failover while the cache lives.
/// </summary>
public class WarmCacheRoutingTests
{
    private readonly ManualTimeProvider _time = new();

    // Prices per million tokens: input, output, cache read, cache write.
    private static readonly ModelDefinition Opus = Priced("opus", 4, 5m, 25m, 0.5m, 6.25m);
    private static readonly ModelDefinition Sonnet = Priced("sonnet", 3, 3m, 15m, 0.3m, 3.75m);
    private static readonly ModelDefinition Haiku = Priced("haiku", 2, 1m, 5m, 0.1m, 1.25m);
    private static readonly ModelDefinition Local = Model("local", 2, local: true);

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

    private PromptRouter Create(IModelHealth? health = null, params ModelDefinition[] models)
    {
        var options = new SemanticRouterOptions();
        foreach (ModelDefinition model in models.Length > 0 ? models : [Opus, Sonnet, Haiku])
        {
            options.Models.Add(model);
        }

        return new PromptRouter(new Moq.Mock<IPromptClassifier>().Object, options, new PromptRouterDependencies { Health = health, Time = _time });
    }

    private static PromptClassificationResult Tier(int tier) => new()
    {
        RequiredTier = tier,
        Reason = RoutingReason.SemanticMatch,
        RouteName = "code",
        Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
    };

    private static PromptRoutingRequest Request(string? preferred, params WarmCache[] caches) =>
        new() { Prompt = "and now the tests", EstimatedInputTokens = 82_000, PreferredModel = preferred, WarmCaches = caches };

    private WarmCache Warm(string model, int tokens = 80_000) => new WarmCache { Model = model, CachedTokens = tokens, ExpiresAt = _time.GetUtcNow().AddMinutes(4) };

    [Fact]
    public void A_conversation_whose_tier_drops_stays_on_its_warm_model_when_moving_costs_more()
    {
        RoutingPlan plan = Create().Plan(Tier(3), Request("opus", Warm("opus")));

        plan.Candidates[0].Model.Name.Should().Be("opus", "reading 80k from opus's cache costs far less than writing it to sonnet's");
        plan.Candidates.Select(c => c.Model.Name).Should().Contain("sonnet", "the ranking stays behind it");
        plan.Sticky.Should().BeTrue();
        plan.SelectionNote.Should().StartWith("cache: stayed on opus (80k cached); moving to sonnet would cost $0.2");
    }

    [Fact]
    public void It_moves_when_moving_costs_less()
    {
        RoutingPlan plan = Create(null, Opus, Sonnet, Local).Plan(Tier(2), Request("sonnet", Warm("sonnet")));

        plan.Candidates[0].Model.Name.Should().Be("local", "a free model beats even a cache read");
        plan.Sticky.Should().BeFalse();
        plan.SelectionNote.Should().Be("cache: moved to local, $0 a turn against $0.0377 on sonnet with 80k cached");
    }

    [Fact]
    public void A_warm_model_below_the_required_tier_is_left()
    {
        RoutingPlan plan = Create().Plan(Tier(4), Request("sonnet", Warm("sonnet")));

        plan.Candidates[0].Model.Name.Should().Be("opus");
        (plan.Sticky, plan.SelectionNote).Should().Be((false, null));
    }

    [Fact]
    public void After_a_failover_it_returns_to_the_warm_model_while_the_cache_is_alive()
    {
        // Two tier-4 models; "opus-b" is pricier, so the ranking prefers opus. Opus fails over to opus-b for a turn.
        ModelDefinition opusB = Priced("opus-b", 4, 6m, 30m, 3m, 7.5m);
        var health = new ModelHealthTracker(new ModelSelectionOptions { QuotaCooldown = TimeSpan.FromMinutes(2) }, new ModelHealthTrackerDependencies { Time = _time });
        PromptRouter router = Create(health, Opus, opusB);
        WarmCache opusCache = Warm("opus");
        health.ReportFailure(Opus, ModelFailureKind.QuotaExceeded);

        RoutingPlan during = router.Plan(Tier(4), Request("opus", opusCache));
        during.Candidates[0].Model.Name.Should().Be("opus-b", "opus is cooling down");

        _time.Advance(TimeSpan.FromMinutes(3));
        RoutingPlan back = router.Plan(Tier(4), Request("opus-b", opusCache, Warm("opus-b", 81_000)));
        back.Candidates[0].Model.Name.Should().Be("opus", "its cache is still warm and cheaper to read than opus-b's");
        back.Sticky.Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(2));
        RoutingPlan expired = router.Plan(Tier(4), Request("opus-b", opusCache, new WarmCache { Model = "opus-b", CachedTokens = 81_000, ExpiresAt = _time.GetUtcNow().AddMinutes(4) }));
        expired.Candidates[0].Model.Name.Should().Be("opus-b", "opus's cache expired: only opus-b's is warm, and moving would write 82k");
    }

    [Fact]
    public void Without_a_warm_cache_routing_is_unchanged()
    {
        PromptRouter router = Create();
        WarmCache expired = new WarmCache { Model = "opus", CachedTokens = 80_000, ExpiresAt = _time.GetUtcNow().AddSeconds(-1) };

        RoutingPlan cold = router.Plan(Tier(3), Request("opus", expired));
        RoutingPlan none = router.Plan(Tier(3), Request("opus"));

        cold.Candidates.Select(c => c.Model.Name).Should().Equal(none.Candidates.Select(c => c.Model.Name));
        none.Candidates[0].Model.Name.Should().Be("sonnet", "opus is above the required tier, so the old rule moves on");
        (cold.Sticky, cold.SelectionNote, none.Sticky).Should().Be((false, null, false));
    }

    [Fact]
    public void A_warm_model_that_is_first_anyway_is_sticky_without_a_note()
    {
        RoutingPlan plan = Create().Plan(Tier(3), Request("sonnet", Warm("sonnet")));

        plan.Candidates[0].Model.Name.Should().Be("sonnet");
        (plan.Sticky, plan.SelectionNote).Should().Be((true, null));
    }
}
