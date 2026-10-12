using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What automatic tuning does with the changes it finds (<see cref="TuningOptions.Mode"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TuningMode>))]
public enum TuningMode
{
    /// <summary>Changes are applied, shown under "Router learned" on the dashboard, and can be undone one by one.</summary>
    Apply,

    /// <summary>Changes wait on the dashboard until someone applies them.</summary>
    Suggest,

    /// <summary>No tuning.</summary>
    Off,
}

/// <summary>
/// Automatic tuning (the <c>Tuning</c> section): similarity thresholds and route utterances learned from the
/// evaluation set and from feedback, in small steps, only when that is safe, and each change undoable.
/// </summary>
public sealed class TuningOptions
{
    /// <summary>Apply changes (the default), only suggest them, or do nothing.</summary>
    public TuningMode Mode { get; set; } = TuningMode.Apply;

    /// <summary>How often tuning runs. Default one day.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Run early once this many prompts were labelled since the last run. 0: only on <see cref="Interval"/>.</summary>
    public int NewLabels { get; set; } = 10;

    /// <summary>The most a threshold moves in one run. Default 0.02.</summary>
    public float MaxStep { get; set; } = 0.02f;

    /// <summary>Labelled prompts (evaluation set plus feedback) needed before anything is tuned. Default 20.</summary>
    public int MinimumPrompts { get; set; } = 20;

    /// <summary>How much a prompt labelled from feedback counts next to one labelled by hand (1). Default 0.5.</summary>
    public double FeedbackWeight { get; set; } = 0.5;

    /// <summary>
    /// How much a prompt labelled only by the judge's low grade (<see cref="JudgeOptions"/>) counts. Default 0.75: a
    /// grade reads the answer, so it says more than a retry, but less than a label set by hand.
    /// </summary>
    public double JudgeWeight { get; set; } = 0.75;

    /// <summary>
    /// Graded answers with a hand label in the evaluation set needed before the judge's agreement with those labels
    /// scales <see cref="JudgeWeight"/> (<see cref="JudgeAgreement"/>): a judge that agrees half the time counts half as
    /// much. Default 10.
    /// </summary>
    public int JudgeAgreementPairs { get; set; } = 10;

    /// <summary>Feedback from the last this many days is learned from. Default 30.</summary>
    public int FeedbackDays { get; set; } = 30;

    /// <summary>
    /// Add a labelled prompt that its route misses as an example (utterance) of that route, when that routes no other
    /// labelled prompt worse. Default <see langword="true"/>.
    /// </summary>
    public bool Utterances { get; set; } = true;

    /// <summary>Utterances added to one route in one run. Default 2.</summary>
    public int MaxUtterancesPerRoute { get; set; } = 2;

    /// <summary>
    /// Prompts labelled local-only may become utterances. Default <see langword="false"/>: utterances are part of the
    /// configuration, which is shared and exported more freely than the evaluation set.
    /// </summary>
    public bool LocalOnlyUtterances { get; set; }

    /// <summary>An undone change is not made again for this many days. Default 30.</summary>
    public int HoldDays { get; set; } = 30;

    /// <summary>
    /// The share of a route's plain matches that try the next cheaper model able to do its work, while a trial runs
    /// (<see cref="ModelTrialTurns"/>). Trials run only when <see cref="Mode"/> is <c>Apply</c>; 0 turns them off.
    /// Never for explicit choices, escalated or follow-up turns, and a local-only request never goes to a cloud model.
    /// Default 0.05.
    /// </summary>
    public double ModelTrialRate { get; set; } = 0.05;

    /// <summary>Turns a trial gets before it is judged: the cheaper model takes the route over, or is dropped. Default 20.</summary>
    public int ModelTrialTurns { get; set; } = 20;

    /// <summary>
    /// How much more often the cheaper model's turns may draw feedback (retried, corrected, tool errors, empty replies,
    /// graded low) than the route's current model's and still take over. Default 0.05.
    /// </summary>
    public double ModelTrialMargin { get; set; } = 0.05;

    /// <summary>
    /// Within <see cref="HoldDays"/>, an undone change comes back as a suggestion (never applied) when its evidence is
    /// this many times stronger than when it was undone. Default 2.
    /// </summary>
    public double StrongerEvidence { get; set; } = 2;

    /// <summary>Validation errors; empty when valid.</summary>
    internal IEnumerable<string> GetValidationErrors()
    {
        if (Interval < TimeSpan.FromMinutes(1))
        {
            yield return "Tuning:Interval must be at least one minute.";
        }

        if (NewLabels < 0)
        {
            yield return "Tuning:NewLabels must not be negative.";
        }

        if (!(MaxStep > 0f && MaxStep <= 1f))
        {
            yield return "Tuning:MaxStep must be greater than 0 and at most 1.";
        }

        if (MinimumPrompts < 1)
        {
            yield return "Tuning:MinimumPrompts must be at least 1.";
        }

        if (!(FeedbackWeight >= 0 && FeedbackWeight <= 1))
        {
            yield return "Tuning:FeedbackWeight must be between 0 and 1.";
        }

        if (!(JudgeWeight >= 0 && JudgeWeight <= 1))
        {
            yield return "Tuning:JudgeWeight must be between 0 and 1.";
        }

        if (!(ModelTrialRate >= 0 && ModelTrialRate <= 1) || !(ModelTrialMargin >= 0 && ModelTrialMargin <= 1))
        {
            yield return "Tuning:ModelTrialRate and ModelTrialMargin must be between 0 and 1.";
        }

        if (ModelTrialTurns < 1)
        {
            yield return "Tuning:ModelTrialTurns must be at least 1.";
        }

        if (JudgeAgreementPairs < 1)
        {
            yield return "Tuning:JudgeAgreementPairs must be at least 1.";
        }

        if (FeedbackDays < 1)
        {
            yield return "Tuning:FeedbackDays must be at least 1.";
        }

        if (MaxUtterancesPerRoute < 0)
        {
            yield return "Tuning:MaxUtterancesPerRoute must not be negative.";
        }

        if (HoldDays < 0)
        {
            yield return "Tuning:HoldDays must not be negative.";
        }

        if (!(StrongerEvidence >= 1))
        {
            yield return "Tuning:StrongerEvidence must be at least 1.";
        }
    }
}
