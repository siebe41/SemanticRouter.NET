using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>Model reliability per kind of work (#44): the router leaves a model out of the work it keeps failing at.</summary>
public class ModelReliabilityRoutingTests
{
    private static readonly ModelDefinition Cheap = Model("cheap", 1, local: true, capabilities: "tools");
    private static readonly ModelDefinition Steady = Model("steady", 1, costPerMillion: 2m, capabilities: "tools");

    /// <summary>"cheap" is unreliable for the agentic route and for tool use; everything else is fine.</summary>
    private sealed class Reliability : IModelReliability
    {
        public List<(string Model, string? Route, IReadOnlyList<string> Capabilities)> Asked { get; } = [];

        public bool IsReliable(ModelDefinition model, string? route, IReadOnlyList<string> capabilities)
        {
            Asked.Add((model.Name, route, capabilities));
            return model != Cheap || (route != "agentic" && !capabilities.Contains("tools"));
        }
    }

    private static PromptRouter Create(Reliability reliability, params ModelDefinition[] models)
    {
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => new PromptClassificationResult
            {
                RequiredTier = 1,
                Reason = RoutingReason.SemanticMatch,
                RouteName = prompt.StartsWith("task", StringComparison.Ordinal) ? "agentic" : "chat",
                Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
            });
        var options = new SemanticRouterOptions();
        foreach (ModelDefinition model in models.Length > 0 ? models : [Cheap, Steady])
        {
            options.Models.Add(model);
        }

        return new PromptRouter(classifier.Object, options, reliability: reliability);
    }

    [Fact]
    public async Task A_model_is_left_out_of_the_work_it_fails_at_and_still_serves_other_work()
    {
        var reliability = new Reliability();
        PromptRouter router = Create(reliability);

        RoutingPlan task = await router.RouteAsync(new PromptRoutingRequest { Prompt = "task: wire the endpoint" });
        RoutingPlan chat = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hello there" });
        RoutingPlan tools = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hello there", AdditionalCapabilities = ["tools"] });

        task.Candidates.Select(c => c.Model.Name).Should().Equal("steady");
        task.Unreliable.Should().Equal("cheap");
        chat.Candidates.Select(c => c.Model.Name).Should().Equal("cheap", "steady");
        chat.Unreliable.Should().BeEmpty();
        tools.Candidates[0].Model.Name.Should().Be("steady", "cheap is unreliable with tools on any route");
        reliability.Asked.Should().Contain(a => a.Model == "cheap" && a.Route == "agentic", "the route the prompt matched is passed on");
    }

    [Fact]
    public async Task An_unreliable_model_still_serves_when_nothing_else_can()
    {
        PromptRouter router = Create(new Reliability(), Cheap);

        RoutingPlan task = await router.RouteAsync(new PromptRoutingRequest { Prompt = "task: wire the endpoint" });

        task.Candidates.Select(c => c.Model.Name).Should().Equal("cheap");
        task.Unreliable.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_choice_is_never_overridden()
    {
        var reliability = new Reliability();
        PromptRouter router = Create(reliability);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "task: wire the endpoint", RequestedModel = "cheap" });

        plan.Candidates.Select(c => c.Model.Name).Should().Equal("cheap");
        plan.Unreliable.Should().BeEmpty();
        reliability.Asked.Should().BeEmpty("a model named in the request is not asked about");

        RoutingPlan pinned = await router.RouteAsync(new PromptRoutingRequest { Prompt = "task: wire the endpoint", AffinityModel = "cheap" });
        pinned.Candidates[0].Model.Name.Should().Be("cheap", "a tool loop finishes on the model that started it");
    }
}
