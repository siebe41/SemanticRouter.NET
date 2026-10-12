using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>A graded answer whose prompt is also labelled by hand, and whether the two agree.</summary>
/// <param name="EntryId">The activity entry.</param>
/// <param name="Timestamp">When it was answered.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Grade">The judge's grade (1 to 5).</param>
/// <param name="AnsweredTier">The tier of the model that answered.</param>
/// <param name="LabelTier">The tier the hand label says the prompt needs.</param>
/// <param name="Agrees">A low grade with a label above the answered tier, or a good grade with a label at or below it.</param>
public sealed record JudgeAgreementPair(long EntryId, DateTimeOffset Timestamp, string? Model, int Grade, int AnsweredTier, int LabelTier, bool Agrees);

/// <summary>How often the judge's grades agree with the evaluation set's hand labels (<c>GET api/judge/agreement</c>).</summary>
/// <param name="Pairs">Graded answers with a hand label.</param>
/// <param name="Agreeing">How many of them agree.</param>
/// <param name="Rate">The share that agree; <see langword="null"/> without pairs.</param>
/// <param name="LowGrades">Pairs with a failing grade.</param>
/// <param name="LowAgreeing">How many of those the label confirms (it is above the answered tier).</param>
/// <param name="GoodGrades">Pairs with a passing grade.</param>
/// <param name="GoodAgreeing">How many of those the label confirms (it is at or below the answered tier).</param>
/// <param name="MinimumPairs">Pairs needed before the agreement scales the judge's weight (<see cref="TuningOptions.JudgeAgreementPairs"/>).</param>
/// <param name="ConfiguredWeight">The judge's weight as configured (<see cref="TuningOptions.JudgeWeight"/>).</param>
/// <param name="EffectiveWeight">The weight tuning gives judge labels: the configured weight times the agreement, once there are enough pairs.</param>
/// <param name="Recent">The latest pairs, newest first.</param>
public sealed record JudgeAgreementReport(
    int Pairs, int Agreeing, double? Rate, int LowGrades, int LowAgreeing, int GoodGrades, int GoodAgreeing,
    int MinimumPairs, double ConfiguredWeight, double EffectiveWeight, IReadOnlyList<JudgeAgreementPair> Recent)
{
    /// <summary>Whether there are enough pairs for the agreement to scale the judge's weight.</summary>
    public bool Applied => Pairs >= MinimumPairs;

    /// <summary>"its grades agree with 7 of 10 hand labels".</summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture, $"its grades agree with {Agreeing} of {Pairs} hand labels");
}

/// <summary>
/// Compares the judge's grades (<see cref="AnswerJudge"/>) with the evaluation set's hand labels for the same prompts. A
/// graded answer is matched to a label by the activity entry it was labelled from (the Activity page's "label this
/// prompt" form notes it), by the prompt the judge read (kept as a hash on the entry, <see cref="RoutingActivityEntry.PromptKey"/>)
/// or by the entry's preview when it holds the whole prompt.
/// </summary>
internal static partial class JudgeAgreement
{
    private const int RecentPairs = 20;

    /// <summary>The key a prompt is matched by: a hash, so no more of the prompt is kept than the preview already is.</summary>
    public static string KeyOf(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Trim())))[..32];

    /// <summary>The agreement over <paramref name="entries"/>, and the weight it gives judge labels.</summary>
    public static JudgeAgreementReport Measure(
        IEnumerable<RoutingActivityEntry> entries, IReadOnlyList<EvaluationCase> labels, IReadOnlyList<ModelDefinition> models,
        int failingGrade, TuningOptions tuning)
    {
        var byEntry = new Dictionary<long, EvaluationCase>();
        var byKey = new Dictionary<string, EvaluationCase>(StringComparer.Ordinal);
        var byPrompt = new Dictionary<string, EvaluationCase>(StringComparer.Ordinal);
        foreach (EvaluationCase label in labels)
        {
            if (label.Note is { } note && LabelledFrom().Match(note) is { Success: true } from
                && long.TryParse(from.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                byEntry[id] = label;
            }

            byKey.TryAdd(KeyOf(label.Prompt), label);
            byPrompt.TryAdd(label.Prompt, label);
        }

        var pairs = new List<JudgeAgreementPair>();
        foreach (RoutingActivityEntry entry in entries)
        {
            if (entry.Grade is not { } grade)
            {
                continue;
            }

            EvaluationCase? label = byEntry.GetValueOrDefault(entry.Id)
                ?? (entry.PromptKey is { } key ? byKey.GetValueOrDefault(key) : null)
                ?? (!string.IsNullOrWhiteSpace(entry.PromptPreview) && !entry.PromptPreview.EndsWith('…') ? byPrompt.GetValueOrDefault(entry.PromptPreview) : null);
            if (label is null)
            {
                continue;
            }

            int answered = models.FirstOrDefault(m => string.Equals(m.Name, entry.ServedBy, StringComparison.OrdinalIgnoreCase))?.Tier ?? entry.Tier;
            bool low = grade <= failingGrade;
            pairs.Add(new JudgeAgreementPair(
                entry.Id, entry.Timestamp, entry.ServedBy, grade, answered, label.ExpectedTier, low ? label.ExpectedTier > answered : label.ExpectedTier <= answered));
        }

        int agreeing = pairs.Count(p => p.Agrees);
        double? rate = pairs.Count > 0 ? (double)agreeing / pairs.Count : null;
        int minimum = tuning.JudgeAgreementPairs;
        double effective = rate is { } r && pairs.Count >= minimum ? tuning.JudgeWeight * r : tuning.JudgeWeight;
        return new JudgeAgreementReport(
            pairs.Count,
            agreeing,
            rate,
            pairs.Count(p => p.Grade <= failingGrade),
            pairs.Count(p => p.Grade <= failingGrade && p.Agrees),
            pairs.Count(p => p.Grade > failingGrade),
            pairs.Count(p => p.Grade > failingGrade && p.Agrees),
            minimum,
            tuning.JudgeWeight,
            effective,
            [.. pairs.OrderByDescending(p => p.Timestamp).ThenByDescending(p => p.EntryId).Take(RecentPairs)]);
    }

    /// <summary>The agreement over the feedback window (<see cref="TuningOptions.FeedbackDays"/>) of <paramref name="activity"/>.</summary>
    public static JudgeAgreementReport Measure(RouterRuntime runtime, RoutingActivityLog activity, IEvaluationSetStore labels, DateTimeOffset now)
    {
        TuningOptions tuning = runtime.Configuration.Tuning;
        return Measure(
            activity.Between(now - TimeSpan.FromDays(tuning.FeedbackDays), now.AddSeconds(1)),
            labels.Load(),
            runtime.Router.Models,
            runtime.Configuration.Middleware.Judge.FailingGrade,
            tuning);
    }

    [GeneratedRegex(@"labelled from activity #(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LabelledFrom();
}
