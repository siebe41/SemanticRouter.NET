using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class SemanticRouterOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        new SemanticRouterOptions().GetValidationErrors().Should().BeEmpty();
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        SemanticRouterOptions options = Options(
            Route("", 1, "a"),
            Route("empty", 1, " "),
            Route("bad-threshold", 4, "b"));
        options.Routes[2].SimilarityThreshold = 1.5f;
        options.DefaultSimilarityThreshold = 2f;
        options.FallbackTier = -1;
        options.Complexity.CodeWeight = 3;
        options.Selection.MaxAttempts = 0;
        options.Models.Add(Model("m", 1));
        options.Models.Add(Model("m", 2));
        options.Models.Add(new ModelDefinition { Name = "no-connector" });

        IReadOnlyList<string> errors = options.GetValidationErrors();

        errors.Should().HaveCount(9);
        errors.Should().Contain(e => e.Contains("must have a Name"));
        errors.Should().Contain(e => e.Contains("'empty' must have at least one non-empty utterance"));
        errors.Should().Contain(e => e.Contains("'bad-threshold' SimilarityThreshold"));
        errors.Should().Contain(e => e.Contains("DefaultSimilarityThreshold"));
        errors.Should().Contain(e => e.Contains("FallbackTier"));
        errors.Should().Contain(e => e.Contains("Complexity.CodeWeight"));
        errors.Should().Contain(e => e.Contains("Selection.MaxAttempts"));
        errors.Should().Contain(e => e.Contains("Model name 'm' is used more than once"));
        errors.Should().Contain(e => e.Contains("'no-connector' must name a Connector"));
    }

    [Fact]
    public void Route_names_must_be_unique_ignoring_case()
    {
        SemanticRouterOptions options = Options(Route("Chat", 1, "a"), Route("chat", 2, "b"));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*used more than once*");
    }

    [Fact]
    public void Local_only_routes_need_a_local_model()
    {
        RouteDefinition pii = Route("pii", 1, "ssn");
        pii.LocalOnly = true;
        SemanticRouterOptions options = Options(pii);
        options.Models.Add(Model("cloud", 4));

        options.GetValidationErrors().Should().ContainSingle(e => e.Contains("LocalOnly"));

        options.Models.Add(Model("local", 1, local: true));
        options.GetValidationErrors().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 2)]
    [InlineData(0.65, 3)]
    [InlineData(1.0, 4)]
    public void TierForComplexity_uses_the_highest_matching_rule(double score, int tier)
    {
        new SemanticRouterOptions().TierForComplexity(score).Should().Be(tier);
    }
}
