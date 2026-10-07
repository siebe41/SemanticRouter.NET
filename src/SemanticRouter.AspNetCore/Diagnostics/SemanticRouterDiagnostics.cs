using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Telemetry identifiers. Subscribe with OpenTelemetry via
/// <c>metrics.AddMeter(SemanticRouterDiagnostics.MeterName)</c>.
/// </summary>
public static class SemanticRouterDiagnostics
{
    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "SemanticRouter";

    /// <summary>Counter of routing decisions, tagged with <c>tier</c>, <c>reason</c> and <c>route</c>.</summary>
    public const string DecisionsInstrument = "semantic_router.decisions";

    /// <summary>Histogram of classification latency in seconds, tagged with <c>reason</c>.</summary>
    public const string DurationInstrument = "semantic_router.classification.duration";

    /// <summary>Counter of model attempts in Dispatch mode, tagged with <c>model</c> and <c>outcome</c> (Completed, Failed, Skipped).</summary>
    public const string AttemptsInstrument = "semantic_router.attempts";
}

internal sealed class PromptRouterMetrics
{
    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _attempts;

    public PromptRouterMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(SemanticRouterDiagnostics.MeterName);
        _decisions = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.DecisionsInstrument, "{decision}", "Prompt routing decisions.");
        _duration = meter.CreateHistogram<double>(
            SemanticRouterDiagnostics.DurationInstrument, "s", "Time spent classifying a prompt.");
        _attempts = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.AttemptsInstrument, "{attempt}", "Model attempts made while dispatching requests.");
    }

    public void RecordDecision(PromptClassificationResult result)
    {
        var tags = new TagList
        {
            { "tier", result.RequiredTier },
            { "reason", result.Reason.ToString() },
            { "route", result.RouteName ?? "none" },
        };
        _decisions.Add(1, tags);
        _duration.Record(result.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("reason", result.Reason.ToString()));
    }

    public void RecordAttempt(string model, ConnectorOutcome outcome) =>
        _attempts.Add(1, new KeyValuePair<string, object?>("model", model), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
}
