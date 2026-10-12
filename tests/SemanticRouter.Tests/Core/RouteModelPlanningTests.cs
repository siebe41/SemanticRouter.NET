using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// A route's learned model and a model on trial (#69): first for the route's plain matches, even below its tier, when
/// they can serve the request; the ranking stays behind them, and other decisions keep it.
/// </summary>
public class RouteModelPlanningTests
{
    private static readonly ModelDefinition Strong = Model("strong", 4, costPerMillion: 3m, capabilities: "tools");
    private static readonly ModelDefinition Mid = Model("mid", 3, costPerMillion: 1m);
    private static readonly ModelDefinition Small = Model("small", 2, costPerMillion: 0.5m, capabilities: "tools");

    private static PromptRouter Create(string? preferred)
    {
        var options = new SemanticRouterOptions();
        foreach (ModelDefinition model in new[] { Strong, Mid, Small })
        {
            options.Models.Add(model);
        }

        RouteDefinition route = Route("agentic", 4, "implement this");
        route.PreferredModel = preferred;
        options.Routes.Add(route);
        return new PromptRouter(new Moq.Mock<IPromptClassifier>().Object, options);
    }

    private static PromptClassificationResult Matched(RoutingReason reason = RoutingReason.SemanticMatch) => new()
    {
        RequiredTier = 4,
        Reason = reason,
        RouteName = "agentic",
        Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
    };

    private static PromptRoutingRequest Request(string? trial = null, params string[] capabilities) =>
        new() { Prompt = "implement the parser", TrialModel = trial, AdditionalCapabilities = capabilities };

    [Fact]
    public void The_routes_learned_model_serves_its_plain_matches_first_with_the_ranking_behind_it()
    {
        RoutingPlan plan = Create("mid").Plan(Matched(), Request());

        plan.Candidates.Select(c => c.Model.Name).Should().StartWith(["mid", "strong"]);
        plan.Candidates[0].MeetsRequiredTier.Should().BeFalse("it is below the route's tier");
        plan.RouteModel.Should().Be("mid");
        plan.Trial.Should().BeNull();
    }

    [Theory]
    [InlineData(RoutingReason.ComplexityEscalation)]
    [InlineData(RoutingReason.RetryEscalation)]
    [InlineData(RoutingReason.ConversationFloor)]
    [InlineData(RoutingReason.ExplicitCallout)]
    public void Other_decisions_keep_the_ranking(RoutingReason reason)
    {
        RoutingPlan plan = Create("mid").Plan(Matched(reason), Request());

        plan.Candidates.Select(c => c.Model.Name).Should().Equal(["strong", "mid", "small"], "the tier's ranking, cheaper models only as fallbacks");
        plan.RouteModel.Should().BeNull();
    }

    [Fact]
    public void A_model_that_cannot_serve_the_request_is_passed_over()
    {
        RoutingPlan plan = Create("mid").Plan(Matched(), Request(capabilities: "tools"));

        plan.Candidates.Select(c => c.Model.Name).Should().Equal(["strong", "small"]);
        plan.RouteModel.Should().BeNull("mid cannot use tools");
    }

    [Fact]
    public void A_model_on_trial_goes_first_ahead_of_the_learned_one()
    {
        RoutingPlan plan = Create("mid").Plan(Matched(), Request(trial: "small"));

        plan.Candidates.Select(c => c.Model.Name).Should().StartWith(["small", "strong"]);
        (plan.Trial, plan.RouteModel).Should().Be(("small", null));
    }
}
