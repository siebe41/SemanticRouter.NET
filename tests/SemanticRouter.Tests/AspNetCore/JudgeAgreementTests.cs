using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The judge's agreement with the hand labels (#68), and the weight it gives the judge's labels in tuning.</summary>
public sealed class JudgeAgreementTests
{
    private static readonly ModelDefinition[] Models = [Model("small", 1, local: true), Model("big", 4)];

    private static RoutingActivityEntry Graded(long id, string prompt, string model, int grade) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        PromptPreview = prompt,
        Reason = RoutingReason.SemanticMatch,
        Tier = 1,
        ServedBy = model,
        Grade = grade,
    };

    [Fact]
    public void Low_grades_agree_with_labels_above_the_answering_tier_and_good_grades_with_labels_at_or_below_it()
    {
        RoutingActivityEntry[] entries =
        [
            Graded(1, "fix the race in the scheduler", "small", 2),   // label 3 > tier 1: agrees
            Graded(2, "say hello", "small", 2),                       // label 1: disagrees
            Graded(3, "design the ledger", "big", 5),                 // label 4 <= tier 4: agrees
            Graded(4, "summarize this", "small", 4),                  // label 2 > tier 1: disagrees
            Graded(5, "not labelled", "small", 1),
            Graded(6, "a cut preview…", "small", 1),
            new() { Id = 7, Timestamp = DateTimeOffset.UtcNow, PromptPreview = "say hello", Reason = RoutingReason.SemanticMatch },
        ];
        EvaluationCase[] labels =
        [
            new("fix the race in the scheduler", 3), new("say hello", 1), new("design the ledger", 4), new("summarize this", 2), new("a cut preview", 1),
        ];

        JudgeAgreementReport report = JudgeAgreement.Measure(entries, labels, Models, failingGrade: 2, new TuningOptions { JudgeAgreementPairs = 4 });

        (report.Pairs, report.Agreeing, report.Rate).Should().Be((4, 2, 0.5));
        (report.LowGrades, report.LowAgreeing, report.GoodGrades, report.GoodAgreeing).Should().Be((2, 1, 2, 1));
        report.EffectiveWeight.Should().Be(0.375, "0.75 times half the grades agreeing");
        report.Summary.Should().Be("its grades agree with 2 of 4 hand labels");
        report.Recent.Select(p => (p.EntryId, p.Agrees)).Should().Equal((4L, false), (3L, true), (2L, false), (1L, true));
    }

    [Fact]
    public void Too_few_pairs_leave_the_judges_weight_as_configured()
    {
        JudgeAgreementReport report = JudgeAgreement.Measure(
            [Graded(1, "say hello", "small", 2)], [new EvaluationCase("say hello", 1)], Models, failingGrade: 2, new TuningOptions());

        (report.Pairs, report.Agreeing, report.Applied, report.EffectiveWeight).Should().Be((1, 0, false, 0.75));
    }
}
