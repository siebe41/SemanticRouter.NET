using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>A graded answer whose prompt is also labelled by hand, and whether the two agree.</summary>
public sealed record JudgeAgreementPair
{
    /// <summary>The activity entry.</summary>
    public required long EntryId { get; init; }

    /// <summary>When it was answered.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>The judge's grade (1 to 5).</summary>
    public required int Grade { get; init; }

    /// <summary>The tier of the model that answered.</summary>
    public required int AnsweredTier { get; init; }

    /// <summary>The tier the hand label says the prompt needs.</summary>
    public required int LabelTier { get; init; }

    /// <summary>A low grade with a label above the answered tier, or a good grade with a label at or below it.</summary>
    public required bool Agrees { get; init; }
}

/// <summary>How often the judge's grades agree with the evaluation set's hand labels (<c>GET api/judge/agreement</c>).</summary>
public sealed record JudgeAgreementReport
{
    /// <summary>Graded answers with a hand label.</summary>
    public required int Pairs { get; init; }

    /// <summary>How many of them agree.</summary>
    public required int Agreeing { get; init; }

    /// <summary>The share that agree; <see langword="null"/> without pairs.</summary>
    public double? Rate { get; init; }

    /// <summary>Pairs with a failing grade.</summary>
    public required int LowGrades { get; init; }

    /// <summary>How many of those the label confirms (it is above the answered tier).</summary>
    public required int LowAgreeing { get; init; }

    /// <summary>Pairs with a passing grade.</summary>
    public required int GoodGrades { get; init; }

    /// <summary>How many of those the label confirms (it is at or below the answered tier).</summary>
    public required int GoodAgreeing { get; init; }

    /// <summary>Pairs needed before the agreement scales the judge's weight (<see cref="TuningOptions.JudgeAgreementPairs"/>).</summary>
    public required int MinimumPairs { get; init; }

    /// <summary>The judge's weight as configured (<see cref="TuningOptions.JudgeWeight"/>).</summary>
    public required double ConfiguredWeight { get; init; }

    /// <summary>The weight tuning gives judge labels: the configured weight times the agreement, once there are enough pairs.</summary>
    public required double EffectiveWeight { get; init; }

    /// <summary>The latest pairs, newest first.</summary>
    public required IReadOnlyList<JudgeAgreementPair> Recent { get; init; }

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
            pairs.Add(new JudgeAgreementPair
            {
                EntryId = entry.Id,
                Timestamp = entry.Timestamp,
                Model = entry.ServedBy,
                Grade = grade,
                AnsweredTier = answered,
                LabelTier = label.ExpectedTier,
                Agrees = low ? label.ExpectedTier > answered : label.ExpectedTier <= answered,
            });
        }

        int agreeing = pairs.Count(p => p.Agrees);
        double? rate = pairs.Count > 0 ? (double)agreeing / pairs.Count : null;
        int minimum = tuning.JudgeAgreementPairs;
        double effective = rate is { } r && pairs.Count >= minimum ? tuning.JudgeWeight * r : tuning.JudgeWeight;
        return new JudgeAgreementReport
        {
            Pairs = pairs.Count,
            Agreeing = agreeing,
            Rate = rate,
            LowGrades = pairs.Count(p => p.Grade <= failingGrade),
            LowAgreeing = pairs.Count(p => p.Grade <= failingGrade && p.Agrees),
            GoodGrades = pairs.Count(p => p.Grade > failingGrade),
            GoodAgreeing = pairs.Count(p => p.Grade > failingGrade && p.Agrees),
            MinimumPairs = minimum,
            ConfiguredWeight = tuning.JudgeWeight,
            EffectiveWeight = effective,
            Recent = [.. pairs.OrderByDescending(p => p.Timestamp).ThenByDescending(p => p.EntryId).Take(RecentPairs)],
        };
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
