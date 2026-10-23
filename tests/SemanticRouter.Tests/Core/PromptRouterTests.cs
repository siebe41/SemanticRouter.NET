using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class PromptRouterTests
{
    private static readonly ModelDefinition Local = Model("local", 1, local: true);
    private static readonly ModelDefinition Cli = Model("claude-cli", 4);
    private static readonly ModelDefinition Api = Model("api", 2, costPerMillion: 1m, capabilities: "tools");

    private static (PromptRouter Router, Mock<IPromptClassifier> Classifier, ModelHealthTracker Health) Create(int tier, bool localOnly = false)
    {
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = tier,
                Reason = RoutingReason.SemanticMatch,
                LocalOnly = localOnly,
                Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
            });

        var options = new SemanticRouterOptions();
        foreach (ModelDefinition model in new[] { Local, Cli, Api })
        {
            options.Models.Add(model);
        }

        var health = new ModelHealthTracker();
        return (new PromptRouter(classifier.Object, options, new PromptRouterDependencies { Health = health }), classifier, health);
    }

    [Fact]
    public async Task Ranks_the_catalog_for_the_classified_tier()
    {
        (PromptRouter router, _, _) = Create(tier: 4);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform" });

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("claude-cli", "api", "local");
        plan.Requirements.RequiredTier.Should().Be(4);
    }

    [Fact]
    public async Task Request_capabilities_and_token_estimates_flow_into_requirements()
    {
        (PromptRouter router, _, _) = Create(tier: 1);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest
        {
            Prompt = "call the weather tool",
            AdditionalCapabilities = ["tools"],
            EstimatedInputTokens = 1234,
        });

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("api");
        plan.Requirements.EstimatedInputTokens.Should().Be(1234);
    }

    [Fact]
    public async Task Models_in_cooldown_are_skipped_so_requests_fall_through()
    {
        (PromptRouter router, _, ModelHealthTracker health) = Create(tier: 4);
        health.ReportFailure(Cli, ModelFailureKind.QuotaExceeded);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform" });

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("api", "local");
    }

    [Fact]
    public async Task When_everything_is_cooling_down_the_cooled_models_are_still_tried()
    {
        (PromptRouter router, _, ModelHealthTracker health) = Create(tier: 1, localOnly: true);
        health.ReportFailure(Local, ModelFailureKind.Unavailable);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "ssn lookup" });

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("local");
    }

    [Fact]
    public async Task Requested_catalog_model_bypasses_classification()
    {
        (PromptRouter router, Mock<IPromptClassifier> classifier, _) = Create(tier: 1);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi", RequestedModel = "CLAUDE-CLI" });

        plan.Classification.Reason.Should().Be(RoutingReason.ModelOverride);
        plan.Candidates.Should().ContainSingle().Which.Model.Should().BeSameAs(Cli);
        classifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unknown_requested_model_falls_back_to_routing()
    {
        (PromptRouter router, _, _) = Create(tier: 1);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi", RequestedModel = "gpt-3.5-turbo" });

        plan.Classification.Reason.Should().Be(RoutingReason.SemanticMatch);
        plan.Candidates[0].Model.Should().BeSameAs(Local);
    }

    [Fact]
    public async Task Strategy_override_applies_per_request()
    {
        (PromptRouter router, _, _) = Create(tier: 1);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi", Strategy = SelectionStrategy.MostCapable });

        plan.Candidates[0].Model.Should().BeSameAs(Cli);
    }

    [Fact]
    public async Task A_route_that_follows_the_conversation_takes_its_tier_down_as_well_as_up()
    {
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult { RequiredTier = 3, Reason = RoutingReason.SemanticMatch, RouteName = "continuation", Complexity = new ComplexityAnalysis { EstimatedTokens = 3 } });
        var options = new SemanticRouterOptions();
        foreach (ModelDefinition model in new[] { Local, Cli, Api })
        {
            options.Models.Add(model);
        }

        RouteDefinition continuation = Route("continuation", 3, "go ahead");
        continuation.FollowsConversation = true;
        options.Routes.Add(continuation);
        var router = new PromptRouter(classifier.Object, options);

        RoutingPlan inEasyConversation = await router.RouteAsync(new PromptRoutingRequest { Prompt = "go ahead", ConversationFloor = 2 });
        RoutingPlan inHardConversation = await router.RouteAsync(new PromptRoutingRequest { Prompt = "go ahead", ConversationFloor = 4 });
        RoutingPlan alone = await router.RouteAsync(new PromptRoutingRequest { Prompt = "go ahead" });

        (inEasyConversation.Classification.RequiredTier, inEasyConversation.Classification.Reason).Should().Be((2, RoutingReason.ConversationFloor));
        inHardConversation.Classification.RequiredTier.Should().Be(4);
        (alone.Classification.RequiredTier, alone.Classification.Reason).Should().Be((3, RoutingReason.SemanticMatch));
    }
}
