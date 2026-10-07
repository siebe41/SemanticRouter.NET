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

    /// <summary>Counter of routing decisions, tagged with <c>target</c>, <c>reason</c> and <c>route</c>.</summary>
    public const string DecisionsInstrument = "semantic_router.decisions";

    /// <summary>Histogram of classification latency in seconds, tagged with <c>target</c>.</summary>
    public const string DurationInstrument = "semantic_router.classification.duration";

    /// <summary>Counter of dispatches that failed over from the local to the cloud endpoint.</summary>
    public const string FailoverInstrument = "semantic_router.failovers";
}

internal sealed class PromptRouterMetrics
{
    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failovers;

    public PromptRouterMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(SemanticRouterDiagnostics.MeterName);
        _decisions = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.DecisionsInstrument, "{decision}", "Prompt routing decisions.");
        _duration = meter.CreateHistogram<double>(
            SemanticRouterDiagnostics.DurationInstrument, "s", "Time spent classifying a prompt.");
        _failovers = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.FailoverInstrument, "{failover}", "Local dispatch failures retried on the cloud endpoint.");
    }

    public void RecordDecision(PromptClassificationResult result)
    {
        var tags = new TagList
        {
            { "target", result.Target.ToString() },
            { "reason", result.Reason.ToString() },
            { "route", result.RouteName ?? "none" },
        };
        _decisions.Add(1, tags);
        _duration.Record(result.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("target", result.Target.ToString()));
    }

    public void RecordFailover() => _failovers.Add(1);
}
