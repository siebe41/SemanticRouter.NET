using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class RoutingEvaluationTests
{
    // Two routes on orthogonal axes; the cases sit at known similarities from them.
    private static readonly Dictionary<string, float[]> Table = new()
    {
        ["hello"] = [1f, 0f, 0f],
        ["design a system"] = [0f, 1f, 0f],
        ["hey"] = [0.8f, 0.6f, 0f],                  // smalltalk 0.80
        ["yo there"] = [0.7f, 0.1f, 0.7f],           // smalltalk 0.70
        ["plan the architecture"] = [0.1f, 0.72f, 0.69f], // reasoning 0.72
        ["weather in paris"] = [0.5f, 0.5f, 0.5f],   // both 0.58
    };

    private static readonly EvaluationCase[] Cases =
    [
        new EvaluationCase { Prompt = "hey", ExpectedTier = 1, ExpectedRoute = "smalltalk" },
        new EvaluationCase { Prompt = "yo there", ExpectedTier = 1, ExpectedRoute = "smalltalk" },
        new EvaluationCase { Prompt = "plan the architecture", ExpectedTier = 4, ExpectedRoute = "reasoning" },
        new EvaluationCase { Prompt = "weather in paris", ExpectedTier = 3 },
    ];

    private static SemanticRouterOptions CreateOptions()
    {
        SemanticRouterOptions options = Options(Route("smalltalk", 1, "hello"), Route("reasoning", 4, "design a system"));
        options.DefaultSimilarityThreshold = 0.75f;
        options.FallbackTier = 3;
        return options;
    }

    private static IComplexityAnalyzer Simple()
    {
        var analyzer = new Mock<IComplexityAnalyzer>();
        analyzer.Setup(a => a.Analyze(It.IsAny<string>())).Returns(new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 5 });
        return analyzer.Object;
    }

    [Fact]
    public void Parses_arrays_case_objects_and_json_lines()
    {
        IReadOnlyList<EvaluationCase> array = EvaluationSet.Parse("""[{"prompt":"hi","tier":1,"route":"smalltalk"},{"Prompt":"x","ExpectedTier":3,"localOnly":true}]""");
        IReadOnlyList<EvaluationCase> wrapped = EvaluationSet.Parse("""{"cases":[{"prompt":"hi","tier":1}]}""");
        IReadOnlyList<EvaluationCase> lines = EvaluationSet.Parse("""
            # comment
            {"prompt":"hi","tier":1,"note":"from activity"}

            // another comment
            {"prompt":"design it","expectedRoute":"reasoning","tier":4}
            """);

        array.Should().Equal(new EvaluationCase { Prompt = "hi", ExpectedTier = 1, ExpectedRoute = "smalltalk" }, new EvaluationCase { Prompt = "x", ExpectedTier = 3, LocalOnly = true });
        wrapped.Should().Equal(new EvaluationCase { Prompt = "hi", ExpectedTier = 1 });
        lines.Should().Equal(new EvaluationCase { Prompt = "hi", ExpectedTier = 1, Note = "from activity" }, new EvaluationCase { Prompt = "design it", ExpectedTier = 4, ExpectedRoute = "reasoning" });
        EvaluationSet.Parse(EvaluationSet.ToJsonLines(lines)).Should().Equal(lines);
    }

    [Theory]
    [InlineData("""{"prompt":"hi","tier":1}""" + "\n" + """{"tier":2}""", "line 2 has no 'prompt'")]
    [InlineData("""{"prompt":"hi"}""", "case 1 has no valid 'tier'")]
    [InlineData("""{"prompt":"hi","tier":1}""" + "\n" + "not json", "line 2 is not valid JSON")]
    public void Reports_where_a_set_is_broken(string text, string message) =>
        FluentActions.Invoking(() => EvaluationSet.Parse(text)).Should().Throw<FormatException>().WithMessage(message + "*");

    [Fact]
    public async Task Reports_accuracy_the_confusion_matrix_and_the_misroutes()
    {
        var evaluator = new RoutingEvaluator(FromTable(Table).Object, CreateOptions(), Simple());

        EvaluationReport report = await evaluator.EvaluateAsync(Cases);

        report.Total.Should().Be(4);
        report.Correct.Should().Be(2);
        report.Accuracy.Should().Be(0.5);
        report.TooHigh.Should().Be(1, "'yo there' falls back to tier 3");
        report.TooLow.Should().Be(1, "'plan the architecture' falls back to tier 3");
        report.Tiers.Should().Equal(1, 3, 4);
        report.Confusion.Select(row => row.ToArray()).Should().BeEquivalentTo(new[] { new[] { 1, 1, 0 }, new[] { 0, 1, 0 }, new[] { 0, 1, 0 } }, o => o.WithStrictOrdering());
        report.PerTier.Should().Equal(new TierAccuracy { Tier = 1, Total = 2, Correct = 1 }, new TierAccuracy { Tier = 3, Total = 1, Correct = 1 }, new TierAccuracy { Tier = 4, Total = 1, Correct = 0 });
        report.RouteLabelled.Should().Be(3);
        report.RouteCorrect.Should().Be(1);
        report.Misroutes.Select(m => m.Case.Prompt).Should().Equal("yo there", "plan the architecture");
        EvaluationResult miss = report.Misroutes.First();
        miss.Reason.Should().Be(RoutingReason.NoMatchFallback);
        miss.TopScores[0].Route.Name.Should().Be("smalltalk");
        miss.TopScores[0].Similarity.Should().BeApproximately(0.7035f, 0.001f);
    }

    [Fact]
    public async Task Calibration_recommends_the_threshold_that_routes_the_set_best()
    {
        var evaluator = new RoutingEvaluator(FromTable(Table).Object, CreateOptions(), Simple());
        PreparedEvaluation prepared = await evaluator.PrepareAsync(Cases);

        CalibrationReport calibration = evaluator.Calibrate(prepared);

        calibration.Current.Should().Be(new CalibrationPoint { Threshold = 0.75f, Correct = 2, TooLow = 1, TooHigh = 1, Total = 4 });
        calibration.Recommended.Correct.Should().Be(4);
        calibration.Recommended.Threshold.Should().Be(0.70f, "the passing range is 0.58-0.70 and ties go to the value closest to the current one");
        calibration.Sweep.Should().Contain(p => p.Threshold == 0.5f && p.Correct == 3 && p.TooHigh == 1, "below 0.58 'weather in paris' matches both routes");
        calibration.Routes.Should().BeEmpty();
        evaluator.Evaluate(prepared, calibration.Recommended.Threshold).Correct.Should().Be(4, "the reported point is reproducible");
    }

    [Fact]
    public async Task Calibration_suggests_route_thresholds_when_no_single_default_fits()
    {
        var table = new Dictionary<string, float[]>
        {
            ["hello"] = [1f, 0f, 0f],
            ["design a system"] = [0f, 1f, 0f],
            ["a quiet greeting"] = [0.63f, 0f, 0.776f], // smalltalk 0.63: should match
            ["design noise"] = [0f, 0.65f, 0.76f],       // reasoning 0.65: should not
        };
        EvaluationCase[] cases = [new EvaluationCase { Prompt = "a quiet greeting", ExpectedTier = 1 }, new EvaluationCase { Prompt = "design noise", ExpectedTier = 3 }];
        var evaluator = new RoutingEvaluator(FromTable(table).Object, CreateOptions(), Simple());

        CalibrationReport calibration = evaluator.Calibrate(await evaluator.PrepareAsync(cases));

        calibration.Recommended.Correct.Should().Be(1);
        calibration.Routes.Should().ContainSingle().Which.Should().Be(new RouteThresholdRecommendation { Route = "smalltalk", Current = null, Recommended = 0.63f, CorrectBefore = 1, CorrectAfter = 2 });
        calibration.CorrectWithRoutes.Should().Be(2);
    }

    [Fact]
    public async Task Local_only_cases_count_as_wrong_when_the_decision_could_leave_the_machine()
    {
        SemanticRouterOptions options = CreateOptions();
        options.Routes[0].LocalOnly = true;
        var evaluator = new RoutingEvaluator(FromTable(Table).Object, options, Simple());

        EvaluationReport report = await evaluator.EvaluateAsync([new EvaluationCase { Prompt = "hey", ExpectedTier = 1, LocalOnly = true }, new EvaluationCase { Prompt = "weather in paris", ExpectedTier = 3, LocalOnly = true }]);

        report.Results[0].Correct.Should().BeTrue();
        report.Results[1].Correct.Should().BeFalse("tier 3 is right, but nothing keeps the prompt local");
        report.LocalOnlyMissed.Should().Be(1);
    }

    [Fact]
    public async Task Calibration_never_lowers_accuracy_on_the_sample_configuration()
    {
        // The starting routes with the in-process hashing embeddings, and a labelled set for them.
        SemanticRouterOptions options = CreateOptions();
        options.Routes.Clear();
        options.Routes.Add(Route("smalltalk", 1, "hi there", "hello, how are you?", "good morning", "thanks a lot", "who are you?"));
        options.Routes.Add(Route("rewrite", 1, "summarize this text", "translate this sentence to French", "rewrite this paragraph", "fix the grammar in this sentence", "make this message shorter"));
        options.Routes.Add(Route("deep-reasoning", 4, "design a scalable architecture", "compare the trade-offs between event sourcing and CRUD", "analyze this contract for legal risks", "explain step by step how to migrate a monolith", "evaluate the security of this flow"));
        options.DefaultSimilarityThreshold = 0.9f;
        EvaluationCase[] cases =
        [
            new EvaluationCase { Prompt = "hello there, good morning", ExpectedTier = 1 }, new EvaluationCase { Prompt = "thanks a lot for that", ExpectedTier = 1 }, new EvaluationCase { Prompt = "translate this paragraph to French", ExpectedTier = 1 },
            new EvaluationCase { Prompt = "summarize this article", ExpectedTier = 1 }, new EvaluationCase { Prompt = "design the architecture of a payment platform", ExpectedTier = 4 },
            new EvaluationCase { Prompt = "compare the trade-offs of microservices", ExpectedTier = 4 }, new EvaluationCase { Prompt = "what is the capital of Australia?", ExpectedTier = 3 },
        ];
        var evaluator = new RoutingEvaluator(new HashingEmbeddingProvider(), options);
        PreparedEvaluation prepared = await evaluator.PrepareAsync(cases);

        CalibrationReport calibration = evaluator.Calibrate(prepared);

        calibration.Recommended.Correct.Should().BeGreaterThanOrEqualTo(calibration.Current.Correct);
        calibration.Recommended.Correct.Should().BeGreaterThan(calibration.Current.Correct, "0.9 is far too strict for lexical embeddings");
        calibration.CorrectWithRoutes.Should().BeGreaterThanOrEqualTo(calibration.Recommended.Correct);
        evaluator.Evaluate(prepared, calibration.Recommended.Threshold).Correct.Should().Be(calibration.Recommended.Correct);
    }

    [Fact]
    public async Task Route_health_reports_thin_duplicate_conflicting_and_overlapping_routes()
    {
        var table = new Dictionary<string, float[]>
        {
            ["hi"] = [1f, 0f, 0f, 0f],
            ["hi!"] = [0.999f, 0.01f, 0f, 0f],
            ["hello"] = [0.9f, 0.3f, 0f, 0f],
            ["design"] = [0f, 0f, 1f, 0f],
            ["architect"] = [0f, 0f, 0.95f, 0.3f],
            ["plan a system"] = [0f, 0.1f, 0.9f, 0.4f],
            ["greetings"] = [1f, 0f, 0f, 0f],
            ["architecture"] = [0f, 0f, 0.96f, 0.28f],
            ["plan"] = [0f, 0.1f, 0.91f, 0.4f],
        };
        SemanticRouterOptions options = Options(
            Route("smalltalk", 1, "hi", "hi!", "hello"),
            Route("reasoning", 4, "design", "architect", "plan a system"),
            Route("planning", 3, "greetings", "architecture", "plan"));
        var analyzer = new RouteHealthAnalyzer(FromTable(table).Object, options);

        RouteHealthReport report = await analyzer.AnalyzeAsync();

        report.UtteranceCount.Should().Be(9);
        report.Findings.Where(f => f.Kind == RouteHealthKind.FewUtterances).Select(f => f.Route).Should().BeEquivalentTo("smalltalk", "reasoning", "planning");
        report.Findings.Should().Contain(f => f.Kind == RouteHealthKind.DuplicateUtterances && f.Route == "smalltalk" && f.Utterances.SequenceEqual(new[] { "hi", "hi!" }));
        report.Findings.Should().Contain(f => f.Kind == RouteHealthKind.ConflictingUtterances && f.Route == "smalltalk" && f.OtherRoute == "planning");
        report.Findings.Should().Contain(f => f.Kind == RouteHealthKind.Overlap && f.Route == "reasoning" && f.OtherRoute == "planning");
        report.Overlaps[0].Should().Match<RouteOverlap>(o => o.Route == "reasoning" && o.OtherRoute == "planning" && o.AmbiguousFraction >= 0.5f);
    }

    [Fact]
    public async Task A_minimum_tier_raises_the_classification_and_says_why()
    {
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult { RequiredTier = 1, Reason = RoutingReason.SemanticMatch, RouteName = "smalltalk", Complexity = ComplexityAnalysis.Empty });
        SemanticRouterOptions options = CreateOptions();
        options.Models.Add(Model("small", 1, local: true));
        options.Models.Add(Model("big", 4, costPerMillion: 5m));
        var router = new PromptRouter(classifier.Object, options);

        RoutingPlan normal = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi" });
        RoutingPlan retried = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi", MinimumTier = 2 });
        RoutingPlan unchanged = await router.RouteAsync(new PromptRoutingRequest { Prompt = "hi", MinimumTier = 1 });

        normal.Candidates[0].Model.Name.Should().Be("small");
        retried.Classification.RequiredTier.Should().Be(2);
        retried.Classification.Reason.Should().Be(RoutingReason.RetryEscalation);
        retried.Classification.RouteName.Should().Be("smalltalk");
        retried.Candidates[0].Model.Name.Should().Be("big");
        unchanged.Classification.Reason.Should().Be(RoutingReason.SemanticMatch);
    }
}
