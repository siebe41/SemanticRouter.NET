using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>Automatic threshold tuning (#45): bounded steps toward the calibrated value, never at the cost of quality.</summary>
public class ThresholdTunerTests
{
    private readonly Dictionary<string, float[]> _table = new()
    {
        ["hello"] = [1f, 0f, 0f, 0f],
        ["my diagnosis"] = [0f, 1f, 0f, 0f],
        ["design a system"] = [0f, 0f, 1f, 0f],
    };

    /// <summary>
    /// A prompt at <paramref name="similarity"/> from the smalltalk route ("hello") and unrelated to the others.
    /// Similarities sit between grid points (0.705, not 0.70), so float rounding cannot decide a test.
    /// </summary>
    private string At(string prompt, float similarity)
    {
        _table[prompt] = [similarity, 0f, 0f, MathF.Sqrt(1 - (similarity * similarity))];
        return prompt;
    }

    private static SemanticRouterOptions Configuration(float threshold, int fallbackTier = 3)
    {
        SemanticRouterOptions options = Options(Route("smalltalk", 1, "hello"), Route("reasoning", 4, "design a system"));
        options.DefaultSimilarityThreshold = threshold;
        options.FallbackTier = fallbackTier;
        return options;
    }

    private static IComplexityAnalyzer Simple()
    {
        var analyzer = new Mock<IComplexityAnalyzer>();
        analyzer.Setup(a => a.Analyze(It.IsAny<string>())).Returns(new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 5 });
        return analyzer.Object;
    }

    private Task<ThresholdTuning> TuneAsync(SemanticRouterOptions options, EvaluationCase[] confirmed, EvaluationCase[]? automatic = null, ThresholdTuningOptions? limits = null) =>
        new ThresholdTuner(new RoutingEvaluator(FromTable(_table).Object, options, Simple()))
            .TuneAsync(confirmed, automatic ?? [], limits ?? new ThresholdTuningOptions());

    [Fact]
    public async Task A_threshold_that_is_too_high_moves_in_bounded_steps_and_stops_at_the_calibrated_value()
    {
        EvaluationCase[] confirmed =
        [
            new EvaluationCase { Prompt = At("hi there", 0.725f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("hey you", 0.715f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("good morning", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("what is the weather in paris", 0.30f), ExpectedTier = 3 },
        ];
        var path = new List<float>();
        var fixedCounts = new List<int>();
        float threshold = 0.80f;

        for (int run = 0; run < 10; run++)
        {
            ThresholdTuning tuning = await TuneAsync(Configuration(threshold), confirmed);
            if (tuning.Steps.Count == 0)
            {
                break;
            }

            ThresholdStep step = tuning.Steps.Should().ContainSingle().Subject;
            (step.Route, step.Current, step.Target).Should().Be(((string?)null, threshold, 0.70f));
            step.Fixed.Select(c => c.Prompt).Should().BeSubsetOf(["hi there", "hey you", "good morning"]);
            step.Broken.Should().BeEmpty();
            fixedCounts.Add(step.Fixed.Count);
            threshold = step.Proposed!.Value;
            path.Add(threshold);
        }

        path.Should().Equal(0.78f, 0.76f, 0.74f, 0.72f, 0.70f);
        fixedCounts.Should().Equal([3, 3, 3, 3, 2], "the evidence is what the target fixes that is still wrong: 'hi there' (0.725) is right from 0.72");
    }

    [Fact]
    public async Task A_change_that_would_route_a_prompt_too_low_is_never_proposed()
    {
        // Lowering the threshold fixes three small-talk prompts but sends a tier-4 prompt to the tier-1 route.
        EvaluationCase[] confirmed =
        [
            new EvaluationCase { Prompt = At("hi there", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("hey you", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("good morning", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("hello world, now prove the scheduler is deadlock free", 0.725f), ExpectedTier = 4 },
        ];

        ThresholdTuning tuning = await TuneAsync(Configuration(0.80f, fallbackTier: 4), confirmed);

        tuning.Steps.Should().BeEmpty("every threshold that fixes the three would route the proof too low");
        tuning.ConfirmedAfter.Should().Be(tuning.ConfirmedBefore);
    }

    [Fact]
    public async Task Automatic_labels_count_at_their_weight_and_need_the_minimum_gain()
    {
        EvaluationCase[] confirmed = [new EvaluationCase { Prompt = At("what is the weather in paris", 0.30f), ExpectedTier = 3 }];
        EvaluationCase one = new EvaluationCase { Prompt = At("hiya", 0.705f), ExpectedTier = 1 };
        EvaluationCase[] three = [one, new EvaluationCase { Prompt = At("howdy", 0.705f), ExpectedTier = 1 }, new EvaluationCase { Prompt = At("heya", 0.715f), ExpectedTier = 1 }];

        ThresholdTuning single = await TuneAsync(Configuration(0.72f), confirmed, [one]);
        ThresholdTuning several = await TuneAsync(Configuration(0.72f), confirmed, three);

        single.Steps.Should().BeEmpty("one automatic label weighs 0.5, below the minimum gain of 1");
        ThresholdStep step = several.Steps.Should().ContainSingle().Subject;
        (step.Proposed, step.Gain).Should().Be((0.70f, 1.5));
        several.AutomaticAfter.Correct.Should().Be(3);
        several.ConfirmedAfter.Correct.Should().Be(1, "the confirmed prompt still routes as labelled");
    }

    [Fact]
    public async Task Each_automatic_label_can_carry_its_own_weight()
    {
        EvaluationCase[] confirmed = [new EvaluationCase { Prompt = At("what is the weather in paris", 0.30f), ExpectedTier = 3 }];
        EvaluationCase[] two = [new EvaluationCase { Prompt = At("hiya", 0.705f), ExpectedTier = 1 }, new EvaluationCase { Prompt = At("howdy", 0.705f), ExpectedTier = 1 }];

        ThresholdTuning implicitOnly = await TuneAsync(Configuration(0.72f), confirmed, two, new ThresholdTuningOptions { AutomaticWeight = 0.4 });
        ThresholdTuning judged = await TuneAsync(Configuration(0.72f), confirmed, two, new ThresholdTuningOptions { AutomaticWeight = 0.4, AutomaticWeights = [0.75, 0.4] });

        implicitOnly.Steps.Should().BeEmpty("two labels at 0.4 weigh 0.8, below the minimum gain of 1");
        judged.Steps.Should().ContainSingle().Which.Gain.Should().BeApproximately(1.15, 1e-9, "a judged label (0.75) and an implicit one (0.4)");
    }

    [Fact]
    public async Task A_route_threshold_moves_on_its_own_and_frozen_settings_stay()
    {
        // The default is right for reasoning; only small talk needs a lower bar.
        _table["plan the rollout"] = [0f, 0f, 0.79f, 0.613f];
        EvaluationCase[] confirmed =
        [
            new EvaluationCase { Prompt = At("hi there", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = At("hey you", 0.705f), ExpectedTier = 1 },
            new EvaluationCase { Prompt = "plan the rollout", ExpectedTier = 3, Note = "0.79 from reasoning: must stay below the threshold" },
        ];
        SemanticRouterOptions options = Configuration(0.80f);
        options.Routes[0].SimilarityThreshold = 0.74f;

        ThresholdTuning tuning = await TuneAsync(options, confirmed, limits: new ThresholdTuningOptions { CanChange = route => route is not null });
        ThresholdTuning frozen = await TuneAsync(options, confirmed, limits: new ThresholdTuningOptions { CanChange = route => route is null });

        ThresholdStep step = tuning.Steps.Should().ContainSingle().Subject;
        (step.Route, step.Current, step.Proposed, step.Target).Should().Be(("smalltalk", 0.74f, 0.72f, 0.70f));
        frozen.Steps.Should().BeEmpty("the default cannot move without routing 'plan the rollout' to reasoning");
    }

    [Fact]
    public async Task A_local_only_prompt_never_leaves_the_machine_for_a_better_score()
    {
        // Raising the default would route the three summaries right, but the private prompt (0.717 from the
        // local-only route) would fall below it and leave the machine. Small talk's own threshold can do it instead.
        SemanticRouterOptions options = Configuration(0.70f);
        RouteDefinition privateRoute = Route("private", 1, "my diagnosis");
        privateRoute.LocalOnly = true;
        options.Routes.Add(privateRoute);
        _table["my diagnosis is hello"] = [0.68f, 0.717f, 0f, 0.1533f];
        EvaluationCase[] confirmed =
        [
            new EvaluationCase { Prompt = "my diagnosis is hello", ExpectedTier = 1, LocalOnly = true },
            new EvaluationCase { Prompt = At("summarize the incident", 0.715f), ExpectedTier = 3 },
            new EvaluationCase { Prompt = At("summarize the outage", 0.715f), ExpectedTier = 3 },
            new EvaluationCase { Prompt = At("summarize the postmortem", 0.715f), ExpectedTier = 3 },
        ];

        ThresholdTuning tuning = await TuneAsync(options, confirmed);

        ThresholdStep step = tuning.Steps.Should().ContainSingle().Subject;
        (step.Route, step.Current, step.Proposed).Should().Be(("smalltalk", (float?)null, 0.72f));
        tuning.ConfirmedBefore.LocalOnlyMissed.Should().Be(0);
        tuning.ConfirmedAfter.Should().Be(new TuningScore { Total = 4, Correct = 4, TooLow = 0, TooHigh = 0, LocalOnlyMissed = 0 });
    }
}
