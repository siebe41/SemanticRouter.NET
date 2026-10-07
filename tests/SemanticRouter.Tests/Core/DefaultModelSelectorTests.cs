using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class DefaultModelSelectorTests
{
    // A realistic catalog: free local models, a flat-rate subscription CLI (marginal cost 0), and paid APIs.
    private static readonly ModelDefinition LocalSmall = Model("local-small", 1, local: true);
    private static readonly ModelDefinition LocalCoder = Model("local-coder", 2, local: true, capabilities: "code");
    private static readonly ModelDefinition Subscription = Model("claude-cli", 4, capabilities: "code");
    private static readonly ModelDefinition ApiMini = Model("api-mini", 2, costPerMillion: 0.5m, capabilities: ["code", "tools", "vision"]);
    private static readonly ModelDefinition ApiFrontier = Model("api-frontier", 5, costPerMillion: 15m, capabilities: ["code", "tools", "vision"]);
    private static readonly ModelDefinition[] Catalog = [LocalSmall, LocalCoder, Subscription, ApiMini, ApiFrontier];

    private static string[] Rank(ModelRequirements requirements, ModelSelectionOptions? options = null, Func<ModelDefinition, bool>? available = null) =>
        new DefaultModelSelector(options).RankCandidates(requirements, Catalog, available).Select(c => c.Model.Name).ToArray();

    [Fact]
    public void Cheapest_capable_prefers_free_models_and_the_lowest_sufficient_tier()
    {
        Rank(new ModelRequirements { RequiredTier = 1 })
            .Should().Equal("local-small", "local-coder", "claude-cli", "api-mini", "api-frontier");
    }

    [Fact]
    public void Cheapest_capable_skips_models_below_the_tier_until_the_fallback_section()
    {
        Rank(new ModelRequirements { RequiredTier = 4 })
            .Should().Equal("claude-cli", "api-frontier", "local-coder", "api-mini", "local-small");
    }

    [Fact]
    public void Most_capable_orders_by_tier_then_cost()
    {
        Rank(new ModelRequirements { RequiredTier = 1, Strategy = SelectionStrategy.MostCapable })
            .Should().Equal("api-frontier", "claude-cli", "local-coder", "api-mini", "local-small");
    }

    [Fact]
    public void Priority_strategy_follows_user_priority()
    {
        ModelDefinition first = Model("preferred", 3, costPerMillion: 9m);
        first.Priority = -10;
        var catalog = new[] { LocalSmall, Subscription, first };

        new DefaultModelSelector(new ModelSelectionOptions { Strategy = SelectionStrategy.Priority })
            .RankCandidates(new ModelRequirements { RequiredTier = 2 }, catalog)
            .Select(c => c.Model.Name)
            .Should().Equal("preferred", "claude-cli", "local-small");
    }

    [Fact]
    public void Priority_breaks_cost_and_tier_ties()
    {
        ModelDefinition a = Model("a", 2, local: true);
        ModelDefinition b = Model("b", 2, local: true);
        b.Priority = -1;

        new DefaultModelSelector().RankCandidates(new ModelRequirements { RequiredTier = 2 }, [a, b])
            .Select(c => c.Model.Name).Should().Equal("b", "a");
    }

    [Fact]
    public void Required_capabilities_filter_models_case_insensitively()
    {
        Rank(new ModelRequirements { RequiredTier = 1, RequiredCapabilities = ["TOOLS"] })
            .Should().Equal("api-mini", "api-frontier");
    }

    [Fact]
    public void Local_only_requests_never_see_remote_models()
    {
        Rank(new ModelRequirements { RequiredTier = 4, LocalOnly = true })
            .Should().Equal("local-coder", "local-small");
    }

    [Fact]
    public void Context_window_excludes_models_that_cannot_fit_the_request()
    {
        ModelDefinition small = Model("small-context", 1, local: true);
        small.ContextWindow = 8_000;
        ModelDefinition large = Model("large-context", 1, costPerMillion: 1m);
        large.ContextWindow = 200_000;

        new DefaultModelSelector().RankCandidates(new ModelRequirements { RequiredTier = 1, EstimatedInputTokens = 7_800 }, [small, large])
            .Select(c => c.Model.Name).Should().Equal(["large-context"], "7,800 input + 512 reserved output exceeds 8,000");
    }

    [Fact]
    public void Disabled_and_unavailable_models_are_excluded()
    {
        ModelDefinition disabled = Model("disabled", 1, local: true);
        disabled.Enabled = false;

        new DefaultModelSelector().RankCandidates(new ModelRequirements(), [disabled, LocalSmall, LocalCoder], m => m != LocalSmall)
            .Select(c => c.Model.Name).Should().Equal("local-coder");
    }

    [Fact]
    public void Below_tier_fallback_can_be_disabled()
    {
        Rank(new ModelRequirements { RequiredTier = 4 }, new ModelSelectionOptions { AllowBelowRequiredTier = false })
            .Should().Equal("claude-cli", "api-frontier");
    }

    [Fact]
    public void Candidates_report_estimated_cost_and_whether_they_meet_the_tier()
    {
        IReadOnlyList<ModelCandidate> candidates = new DefaultModelSelector(new ModelSelectionOptions { EstimatedOutputTokens = 1_000 })
            .RankCandidates(new ModelRequirements { RequiredTier = 5, EstimatedInputTokens = 1_000 }, [ApiFrontier, ApiMini]);

        candidates[0].Should().Be(new ModelCandidate(ApiFrontier, 0.03m, true));
        candidates[1].Should().Be(new ModelCandidate(ApiMini, 0.001m, false));
    }
}
