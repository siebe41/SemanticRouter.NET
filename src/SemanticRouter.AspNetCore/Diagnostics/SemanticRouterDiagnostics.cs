using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Telemetry identifiers. Subscribe with OpenTelemetry via <c>metrics.AddMeter(SemanticRouterDiagnostics.MeterName)</c>
/// and <c>tracing.AddSource(SemanticRouterDiagnostics.ActivitySourceName)</c>. No instrument or span carries prompt text.
/// </summary>
public static class SemanticRouterDiagnostics
{
    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "SemanticRouter";

    /// <summary>The <see cref="ActivitySource"/> name (spans <c>semantic_router.route</c>, <c>.classify</c>, <c>.select</c>, <c>.attempt</c>).</summary>
    public const string ActivitySourceName = RouterTelemetry.ActivitySourceName;

    /// <summary>Counter of routing decisions, tagged with <c>tier</c>, <c>reason</c> and <c>route</c>.</summary>
    public const string DecisionsInstrument = "semantic_router.decisions";

    /// <summary>Histogram of classification latency in seconds, tagged with <c>reason</c>.</summary>
    public const string DurationInstrument = "semantic_router.classification.duration";

    /// <summary>Counter of model attempts in Dispatch mode, tagged with <c>model</c> and <c>outcome</c> (Completed, Failed, Skipped).</summary>
    public const string AttemptsInstrument = "semantic_router.attempts";

    /// <summary>Histogram of routed request duration in seconds, tagged with <c>model</c>, <c>connector</c> and <c>outcome</c> (served, failed).</summary>
    public const string RequestDurationInstrument = "semantic_router.request.duration";

    /// <summary>Histogram of time to the first byte of the reply in seconds, tagged with <c>model</c> and <c>connector</c>.</summary>
    public const string TimeToFirstByteInstrument = "semantic_router.time_to_first_byte";

    /// <summary>Counter of tokens the backends reported, tagged with <c>model</c>, <c>connector</c> and <c>type</c> (input, output, cache_read, cache_write).</summary>
    public const string TokensInstrument = "semantic_router.tokens";

    /// <summary>Counter of cost in USD at the configured prices, tagged with <c>model</c> and <c>connector</c>.</summary>
    public const string CostInstrument = "semantic_router.cost";

    /// <summary>Counter of cooldowns started, tagged with <c>model</c> and <c>kind</c> (Unavailable, QuotaExceeded).</summary>
    public const string CooldownsInstrument = "semantic_router.cooldowns";

    /// <summary>Counter of failovers (a model failed and the next was tried), tagged with <c>model</c> (the one that failed) and <c>kind</c>.</summary>
    public const string FailoversInstrument = "semantic_router.failovers";

    /// <summary>Counter of models skipped because a budget was spent, tagged with <c>budget</c> and <c>model</c>.</summary>
    public const string BudgetSkipsInstrument = "semantic_router.budget.skips";

    /// <summary>Up-down counter of CLI processes running, tagged with <c>connector</c>.</summary>
    public const string CliActiveInstrument = "semantic_router.cli.active";

    /// <summary>Up-down counter of requests waiting for a CLI slot (<c>MaxConcurrency</c>), tagged with <c>connector</c>.</summary>
    public const string CliQueuedInstrument = "semantic_router.cli.queued";
}

internal sealed class PromptRouterMetrics
{
    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _attempts;
    private readonly Histogram<double> _requestDuration;
    private readonly Histogram<double> _timeToFirstByte;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Counter<long> _cooldowns;
    private readonly Counter<long> _failovers;
    private readonly Counter<long> _budgetSkips;
    private readonly UpDownCounter<long> _cliActive;
    private readonly UpDownCounter<long> _cliQueued;

    public PromptRouterMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(SemanticRouterDiagnostics.MeterName);
        _decisions = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.DecisionsInstrument, "{decision}", "Prompt routing decisions.");
        _duration = meter.CreateHistogram<double>(
            SemanticRouterDiagnostics.DurationInstrument, "s", "Time spent classifying a prompt.");
        _attempts = meter.CreateCounter<long>(
            SemanticRouterDiagnostics.AttemptsInstrument, "{attempt}", "Model attempts made while dispatching requests.");
        _requestDuration = meter.CreateHistogram<double>(
            SemanticRouterDiagnostics.RequestDurationInstrument, "s", "Routed request duration, from routing to the end of the reply.");
        _timeToFirstByte = meter.CreateHistogram<double>(
            SemanticRouterDiagnostics.TimeToFirstByteInstrument, "s", "Time from routing to the first byte of the reply.");
        _tokens = meter.CreateCounter<long>(SemanticRouterDiagnostics.TokensInstrument, "{token}", "Tokens reported by the backends.");
        _cost = meter.CreateCounter<double>(SemanticRouterDiagnostics.CostInstrument, "USD", "Cost at the configured model prices.");
        _cooldowns = meter.CreateCounter<long>(SemanticRouterDiagnostics.CooldownsInstrument, "{cooldown}", "Model cooldowns started.");
        _failovers = meter.CreateCounter<long>(SemanticRouterDiagnostics.FailoversInstrument, "{failover}", "Failed attempts followed by another model.");
        _budgetSkips = meter.CreateCounter<long>(SemanticRouterDiagnostics.BudgetSkipsInstrument, "{skip}", "Models not tried because a budget was spent.");
        _cliActive = meter.CreateUpDownCounter<long>(SemanticRouterDiagnostics.CliActiveInstrument, "{process}", "CLI processes running.");
        _cliQueued = meter.CreateUpDownCounter<long>(SemanticRouterDiagnostics.CliQueuedInstrument, "{request}", "Requests waiting for a CLI slot.");
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

    public void RecordRequest(string? model, string? connector, string outcome, TimeSpan duration) =>
        _requestDuration.Record(duration.TotalSeconds, Tag("model", model ?? "none"), Tag("connector", connector ?? "none"), Tag("outcome", outcome));

    public void RecordTimeToFirstByte(string model, string connector, TimeSpan elapsed) =>
        _timeToFirstByte.Record(elapsed.TotalSeconds, Tag("model", model), Tag("connector", connector));

    public void RecordUsage(string model, string connector, ChatUsage usage, decimal cost)
    {
        foreach ((string type, int? count) in new[] { ("input", usage.InputTokens), ("output", usage.OutputTokens), ("cache_read", usage.CacheReadTokens), ("cache_write", usage.CacheCreationTokens) })
        {
            if (count is > 0)
            {
                _tokens.Add(count.Value, Tag("model", model), Tag("connector", connector), Tag("type", type));
            }
        }

        if (cost > 0)
        {
            _cost.Add((double)cost, Tag("model", model), Tag("connector", connector));
        }
    }

    public void RecordCooldown(string model, ModelFailureKind kind) => _cooldowns.Add(1, Tag("model", model), Tag("kind", kind.ToString()));

    public void RecordFailover(string model, ModelFailureKind kind) => _failovers.Add(1, Tag("model", model), Tag("kind", kind.ToString()));

    public void RecordBudgetSkip(string budget, string model) => _budgetSkips.Add(1, Tag("budget", budget), Tag("model", model));

    public void CliActive(string connector, int delta) => _cliActive.Add(delta, Tag("connector", connector));

    public void CliQueued(string connector, int delta) => _cliQueued.Add(delta, Tag("connector", connector));

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
