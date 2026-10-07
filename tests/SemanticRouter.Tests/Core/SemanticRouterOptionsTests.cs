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
            Route("", RouteTarget.Local, "a"),
            Route("empty", RouteTarget.Local, " "),
            Route("bad-threshold", RouteTarget.Cloud, "b"));
        options.Routes[2].SimilarityThreshold = 1.5f;
        options.DefaultSimilarityThreshold = 2f;
        options.LocalMaxPromptTokens = -1;
        options.Complexity.CodeWeight = 3;

        IReadOnlyList<string> errors = options.GetValidationErrors();

        errors.Should().HaveCount(6);
        errors.Should().Contain(e => e.Contains("must have a Name"));
        errors.Should().Contain(e => e.Contains("'empty' must have at least one non-empty utterance"));
        errors.Should().Contain(e => e.Contains("'bad-threshold' SimilarityThreshold"));
        errors.Should().Contain(e => e.Contains("DefaultSimilarityThreshold"));
        errors.Should().Contain(e => e.Contains("LocalMaxPromptTokens"));
        errors.Should().Contain(e => e.Contains("Complexity.CodeWeight"));
    }

    [Fact]
    public void Route_names_must_be_unique_ignoring_case()
    {
        SemanticRouterOptions options = Options(Route("Chat", RouteTarget.Local, "a"), Route("chat", RouteTarget.Cloud, "b"));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*used more than once*");
    }
}
