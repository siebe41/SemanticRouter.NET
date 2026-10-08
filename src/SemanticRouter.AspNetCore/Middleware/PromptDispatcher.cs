using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Serves a request by trying the plan's candidate models in order through their connectors. A model that fails before
/// producing output is put into cooldown and the next candidate is tried, up to
/// <see cref="ModelSelectionOptions.MaxAttempts"/>. Models whose budget is spent are not tried. Each attempt is a
/// <c>semantic_router.attempt</c> span; the usage the backend reports is recorded in the activity entry, the usage
/// ledger and the metrics.
/// </summary>
internal sealed partial class PromptDispatcher
{
    private readonly IModelHealth _health;
    private readonly PromptRouterMetrics _metrics;
    private readonly IUsageLedger? _ledger;
    private readonly BudgetGate? _budgets;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PromptDispatcher(
        IModelHealth health,
        PromptRouterMetrics metrics,
        ILogger<PromptDispatcher> logger,
        IUsageLedger? ledger = null,
        BudgetGate? budgets = null,
        TimeProvider? time = null)
    {
        _health = health;
        _metrics = metrics;
        _logger = logger;
        _ledger = ledger;
        _budgets = budgets;
        _time = time ?? TimeProvider.System;
    }

    public async Task DispatchAsync(
        HttpContext context, RouterRuntime runtime, RoutingPlan plan, ChatCompletionRequest chat, ReadOnlyMemory<byte> body, RoutingActivityEntry? activity = null)
    {
        PromptRouterMiddlewareOptions middleware = runtime.Configuration.Middleware;
        UsageOptions usage = runtime.Configuration.Usage;
        int maxAttempts = Math.Max(1, runtime.Configuration.Selection.MaxAttempts);
        var attempts = new List<string>();
        var budgetsHit = new List<string>();
        var cooledDuringRequest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tried = 0;
        long started = Stopwatch.GetTimestamp();

        // The first byte decides time-to-first-byte for whichever attempt is serving at that moment.
        var current = new CurrentAttempt();
        context.Response.OnStarting(() =>
        {
            if (current.Model is { } model)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                _metrics.RecordTimeToFirstByte(model.Name, current.Connector!, elapsed);
                if (activity is not null)
                {
                    activity.TimeToFirstByteMilliseconds = Math.Round(elapsed.TotalMilliseconds, 1);
                }

                if (middleware.EmitResponseHeaders && context.Features.Get<DroppedFeatures>() is { Names.Count: > 0 } dropped)
                {
                    context.Response.Headers[middleware.HeaderPrefix + "-Dropped"] = string.Join(", ", dropped.Names);
                }
            }

            return Task.CompletedTask;
        });

        foreach (ModelCandidate candidate in plan.Candidates)
        {
            if (tried >= maxAttempts)
            {
                break;
            }

            ModelDefinition model = candidate.Model;
            if (cooledDuringRequest.Contains(model.Name))
            {
                attempts.Add($"{model.Name}: skipped (its account hit a limit earlier in this request)");
                continue;
            }

            if (usage.Budgets.Count > 0 && _budgets?.Blocking(model, usage) is { } spent)
            {
                attempts.Add($"{model.Name}: budget '{spent.Name}' is spent ({spent.Describe()})");
                budgetsHit.Add(spent.Name);
                _metrics.RecordBudgetSkip(spent.Name, model.Name);
                if (middleware.EmitResponseHeaders)
                {
                    context.Response.Headers[middleware.HeaderPrefix + "-Budget"] = string.Join(", ", budgetsHit.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                continue;
            }

            if (!runtime.Connectors.TryGet(model.Connector, out IChatConnector connector))
            {
                attempts.Add($"{model.Name}: unknown connector '{model.Connector}'");
                continue;
            }

            tried++;
            if (middleware.EmitResponseHeaders)
            {
                string prefix = middleware.HeaderPrefix;
                context.Response.Headers[prefix + "-Model"] = model.Name;
                context.Response.Headers[prefix + "-Connector"] = connector.Name;
                context.Response.Headers[prefix + "-Attempt"] = tried.ToString(CultureInfo.InvariantCulture);
                if (candidate.MeetsRequiredTier)
                {
                    context.Response.Headers.Remove(prefix + "-Degraded");
                }
                else
                {
                    // Served by a less capable model than the prompt needs (e.g. the frontier model is out of quota).
                    context.Response.Headers[prefix + "-Degraded"] = "true";
                }
            }

            var capture = new UsageCapture();
            context.Features.Set(capture);
            var dropped = new DroppedFeatures();
            context.Features.Set(dropped);
            current.Model = model;
            current.Connector = connector.Name;

            ConnectorResult result;
            using (Activity? span = RouterTelemetry.Source.StartActivity("semantic_router.attempt"))
            {
                span?.SetTag("semantic_router.model", model.Name);
                span?.SetTag("semantic_router.connector", connector.Name);
                span?.SetTag("semantic_router.attempt", tried);
                result = await connector.SendAsync(new ConnectorRequest(chat, model, body), context, context.RequestAborted).ConfigureAwait(false);
                span?.SetTag("semantic_router.outcome", result.Outcome.ToString());
                if (dropped.Names.Count > 0)
                {
                    span?.SetTag("semantic_router.dropped", string.Join(",", dropped.Names));
                }

                if (result.Outcome == ConnectorOutcome.Failed)
                {
                    span?.SetTag("semantic_router.failure.kind", result.FailureKind.ToString());
                    span?.SetTag("semantic_router.failure.detail", runtime.Redactor.Redact(result.Detail ?? "failed"));
                    span?.SetStatus(ActivityStatusCode.Error, result.FailureKind.ToString());
                }
                else if (result.Outcome == ConnectorOutcome.Skipped)
                {
                    span?.SetTag("semantic_router.skip.reason", runtime.Redactor.Redact(result.Detail ?? "skipped"));
                }
            }

            _metrics.RecordAttempt(model.Name, result.Outcome);
            switch (result.Outcome)
            {
                case ConnectorOutcome.Completed:
                    _health.ReportSuccess(model);
                    LogServed(_logger, model.Name, connector.Name, tried);
                    RecordUsage(runtime, model, connector.Name, capture.Usage, activity);
                    _metrics.RecordRequest(model.Name, connector.Name, "served", Stopwatch.GetElapsedTime(started));
                    if (activity is not null)
                    {
                        activity.ServedBy = model.Name;
                        activity.Connector = connector.Name;
                        activity.Attempt = tried;
                        activity.Degraded = !candidate.MeetsRequiredTier;
                        activity.Outcome = "served";
                        activity.Detail = attempts.Count > 0 ? string.Join("; ", attempts) : null;
                        activity.Budget = budgetsHit.Count > 0 ? string.Join(", ", budgetsHit.Distinct(StringComparer.OrdinalIgnoreCase)) : null;
                        activity.Dropped = dropped.Names.Count > 0 ? string.Join(", ", dropped.Names) : null;
                    }

                    return;

                case ConnectorOutcome.Failed:
                    ReportFailure(model, result);
                    if (result.AffectsAllConnectorModels)
                    {
                        foreach (ModelDefinition sibling in runtime.Router.Models.Where(m =>
                                     m != model && string.Equals(m.Connector, model.Connector, StringComparison.OrdinalIgnoreCase)))
                        {
                            ReportFailure(sibling, result);
                            cooledDuringRequest.Add(sibling.Name);
                        }
                    }

                    string failure = runtime.Redactor.Redact(result.Detail ?? "failed");
                    LogFailover(_logger, model.Name, result.FailureKind, failure);
                    _metrics.RecordFailover(model.Name, result.FailureKind);
                    attempts.Add($"{model.Name}: {failure}");
                    break;

                default:
                    string reason = runtime.Redactor.Redact(result.Detail ?? "skipped");
                    LogSkipped(_logger, model.Name, reason);
                    attempts.Add($"{model.Name}: skipped ({reason})");
                    break;
            }
        }

        current.Model = null;
        string detail = plan.Candidates.Count == 0
            ? $"No configured model satisfies the request (tier {plan.Requirements.RequiredTier}" +
              (plan.Requirements.RequiredCapabilities.Count > 0 ? $", capabilities {string.Join(", ", plan.Requirements.RequiredCapabilities)}" : string.Empty) +
              (plan.Requirements.LocalOnly ? ", local only" : string.Empty) + ")."
            : "Every candidate model failed: " + string.Join("; ", attempts);

        _metrics.RecordRequest(null, null, "failed", Stopwatch.GetElapsedTime(started));
        if (activity is not null)
        {
            activity.Outcome = "failed";
            activity.Detail = detail;
            activity.Budget = budgetsHit.Count > 0 ? string.Join(", ", budgetsHit.Distinct(StringComparer.OrdinalIgnoreCase)) : null;
        }

        await PromptRoutingMiddleware.WriteProblemAsync(
            context, chat.Protocol, StatusCodes.Status503ServiceUnavailable, "No model available", detail).ConfigureAwait(false);
    }

    private void ReportFailure(ModelDefinition model, ConnectorResult result)
    {
        _health.ReportFailure(model, result.FailureKind, result.RetryAt);
        _metrics.RecordCooldown(model.Name, result.FailureKind);
    }

    private void RecordUsage(RouterRuntime runtime, ModelDefinition model, string connector, ChatUsage? reported, RoutingActivityEntry? activity)
    {
        if (reported is not { } usage)
        {
            return;
        }

        long input = usage.InputTokens ?? 0, output = usage.OutputTokens ?? 0, cacheRead = usage.CacheReadTokens ?? 0, cacheWrite = usage.CacheCreationTokens ?? 0;
        decimal cost = model.Cost(input, output, cacheRead, cacheWrite);
        ModelDefinition? reference = runtime.Configuration.Usage.ReferenceModel is { Length: > 0 } name
            ? runtime.Configuration.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;

        // At the reference model's prices the cache tokens would have been ordinary input.
        decimal referenceCost = reference?.Cost(input + cacheRead + cacheWrite, output) ?? cost;
        _metrics.RecordUsage(model.Name, connector, usage, cost);
        if (activity is not null)
        {
            activity.InputTokens = usage.InputTokens;
            activity.OutputTokens = usage.OutputTokens;
            activity.CacheReadTokens = usage.CacheReadTokens;
            activity.CacheCreationTokens = usage.CacheCreationTokens;
            activity.CostUsd = cost;
        }

        _ledger?.Record(new UsageRecord(
            _time.GetUtcNow(), model.Name, connector, activity?.Route, activity?.Client, model.IsLocal,
            input, output, cacheRead, cacheWrite, cost, referenceCost));
    }

    /// <summary>The model being tried, for the response-started callback.</summary>
    private sealed class CurrentAttempt
    {
        public ModelDefinition? Model { get; set; }

        public string? Connector { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Served by {Model} via {Connector} (attempt {Attempt}).")]
    private static partial void LogServed(ILogger logger, string model, string connector, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {Model} failed ({Kind}): {Detail}. Trying the next candidate.")]
    private static partial void LogFailover(ILogger logger, string model, ModelFailureKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Model {Model} skipped: {Detail}.")]
    private static partial void LogSkipped(ILogger logger, string model, string detail);
}
