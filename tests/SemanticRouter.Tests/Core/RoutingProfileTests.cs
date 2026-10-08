using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class RoutingProfileTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SemanticRouterOptions Catalog()
    {
        var options = new SemanticRouterOptions();
        options.Selection.TimeZone = "UTC";
        options.Models.Add(Model("local-small", 1, local: true, connector: "lmstudio"));
        options.Models.Add(Model("local-coder", 2, local: true, connector: "lmstudio"));
        options.Models.Add(Model("api-mini", 2, costPerMillion: 0.4m, connector: "openai"));
        options.Models.Add(Model("api-frontier", 4, costPerMillion: 15m, connector: "anthropic"));
        options.Profiles["private"] = new RoutingProfile { LocalOnly = true };
        options.Profiles["frugal"] = new RoutingProfile { MaxTier = 2, DeniedConnectors = { "anthropic" } };
        options.Profiles["work"] = new RoutingProfile { MinTier = 2, AllowedModels = { "api-*" }, Strategy = SelectionStrategy.MostCapable };
        return options;
    }

    private static PromptRouter Router(SemanticRouterOptions options, int tier, IModelHealth? health = null, TimeProvider? time = null)
    {
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult { RequiredTier = tier, Reason = RoutingReason.SemanticMatch, Complexity = ComplexityAnalysis.Empty });
        return new PromptRouter(classifier.Object, options, health: health, time: time);
    }

    private static string[] Names(RoutingPlan plan) => plan.Candidates.Select(c => c.Model.Name).ToArray();

    [Fact]
    public async Task The_same_prompt_gets_different_candidates_under_different_profiles()
    {
        PromptRouter router = Router(Catalog(), tier: 4);

        RoutingPlan none = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform" });
        RoutingPlan @private = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform", Profile = "private" });
        RoutingPlan frugal = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform", Profile = "FRUGAL" });
        RoutingPlan work = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform", Profile = "work" });

        Names(none)[0].Should().Be("api-frontier");
        Names(@private).Should().Equal("local-coder", "local-small");
        @private.Requirements.LocalOnly.Should().BeTrue();
        frugal.Requirements.RequiredTier.Should().Be(2, "MaxTier caps the requirement");
        Names(frugal).Should().Equal("local-coder", "api-mini", "local-small");
        Names(work).Should().Equal("api-frontier", "api-mini");
        work.Requirements.Strategy.Should().Be(SelectionStrategy.MostCapable);
        work.Requirements.Profile.Should().Be("work");
    }

    [Fact]
    public async Task A_local_only_profile_never_reaches_a_cloud_model_even_when_every_local_model_is_down()
    {
        var health = new ModelHealthTracker(new ModelSelectionOptions());
        SemanticRouterOptions options = Catalog();
        health.ReportFailure(options.Models[0], ModelFailureKind.Unavailable);
        health.ReportFailure(options.Models[1], ModelFailureKind.QuotaExceeded);
        PromptRouter router = Router(options, tier: 4, health);

        RoutingPlan plan = await router.RouteAsync(new PromptRoutingRequest { Prompt = "design a platform", Profile = "private" });
        RoutingPlan forced = await router.RouteAsync(new PromptRoutingRequest { Prompt = "x", Profile = "private", RequestedModel = "api-frontier" });
        RoutingPlan pinned = await router.RouteAsync(new PromptRoutingRequest { Prompt = "x", Profile = "private", AffinityModel = "api-frontier" });
        RoutingPlan flagged = await router.RouteAsync(new PromptRoutingRequest { Prompt = "x", LocalOnly = true });

        // Cooling down models are still tried before failing, but only local ones.
        foreach (RoutingPlan p in new[] { plan, forced, pinned, flagged })
        {
            p.Candidates.Should().NotBeEmpty().And.OnlyContain(c => c.Model.IsLocal);
        }

        forced.Classification.Reason.Should().Be(RoutingReason.SemanticMatch, "a model the profile denies is ignored, not forced");
        pinned.Classification.Reason.Should().Be(RoutingReason.SemanticMatch);
    }

    [Fact]
    public async Task Allowed_explicit_models_and_affinity_still_work_inside_a_profile()
    {
        PromptRouter router = Router(Catalog(), tier: 1);

        RoutingPlan forced = await router.RouteAsync(new PromptRoutingRequest { Prompt = "x", Profile = "work", RequestedModel = "api-mini" });
        RoutingPlan pinned = await router.RouteAsync(new PromptRoutingRequest { Prompt = "x", Profile = "work", AffinityModel = "api-mini" });

        forced.Classification.Reason.Should().Be(RoutingReason.ModelOverride);
        Names(forced).Should().Equal("api-mini");
        pinned.Classification.Reason.Should().Be(RoutingReason.ConversationAffinity);
        Names(pinned)[0].Should().Be("api-mini");
        pinned.Requirements.RequiredTier.Should().Be(2, "MinTier raises the pinned model's tier requirement too");
    }

    [Fact]
    public async Task Unknown_profiles_are_rejected() =>
        await FluentActions.Awaiting(() => Router(Catalog(), 1).RouteAsync(new PromptRoutingRequest { Prompt = "x", Profile = "nope" }).AsTask())
            .Should().ThrowAsync<ArgumentException>().WithMessage("Unknown routing profile 'nope'*");

    [Theory]
    [InlineData("08:00-18:00", 9, true)]
    [InlineData("08:00-18:00", 18, false)]
    [InlineData("22:00-06:00", 23, true)]
    [InlineData("22:00-06:00", 5, true)]
    [InlineData("22:00-06:00", 12, false)]
    [InlineData(null, 3, true)]
    public void Active_hours_wrap_around_midnight(string? hours, int hour, bool active) =>
        new ModelDefinition { ActiveHours = hours }.IsActiveAt(new DateTimeOffset(2026, 10, 8, hour, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc).Should().Be(active);

    [Fact]
    public async Task Models_outside_their_active_hours_are_not_tried()
    {
        SemanticRouterOptions options = Catalog();
        options.Models[3].ActiveHours = "08:00-18:00";
        PromptRouter night = Router(options, tier: 4, time: new Clock(new DateTimeOffset(2026, 10, 8, 23, 0, 0, TimeSpan.Zero)));
        PromptRouter day = Router(options, tier: 4, time: new Clock(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero)));

        Names(await night.RouteAsync(new PromptRoutingRequest { Prompt = "x" })).Should().NotContain("api-frontier");
        Names(await day.RouteAsync(new PromptRoutingRequest { Prompt = "x" }))[0].Should().Be("api-frontier");
    }

    [Theory]
    [InlineData("api-*", "api-mini", true)]
    [InlineData("*-mini", "API-MINI", true)]
    [InlineData("a*i*i", "api-mini", true)]
    [InlineData("api-*-x", "api-mini", false)]
    [InlineData("local", "local-small", false)]
    public void Globs_match_case_insensitively(string pattern, string value, bool matches) =>
        Glob.IsMatch(pattern, value).Should().Be(matches);

    [Fact]
    public void Profiles_are_validated()
    {
        SemanticRouterOptions options = Catalog();
        options.Profiles["bad@name"] = new RoutingProfile();
        options.Profiles["inverted"] = new RoutingProfile { MinTier = 4, MaxTier = 2 };
        options.Profiles["typo"] = new RoutingProfile { AllowedModels = { "api-frontir" }, DeniedConnectors = { "nowhere" } };
        options.Models[0].ActiveHours = "9 to 5";
        options.Selection.TimeZone = "Mars/Olympus";

        options.GetValidationErrors().Should().Contain(e => e.Contains("'bad@name'", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("'inverted'", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("model 'api-frontir'", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("connector 'nowhere'", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("ActiveHours '9 to 5'", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("TimeZone 'Mars/Olympus'", StringComparison.Ordinal));
    }
}
