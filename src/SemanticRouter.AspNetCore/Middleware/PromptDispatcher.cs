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
    private readonly ConnectorIncidents? _incidents;
    private readonly TimeProvider _time;
    private readonly RouterDashboardLocation? _dashboard;
    private readonly ILogger _logger;

    public PromptDispatcher(
        IModelHealth health,
        PromptRouterMetrics metrics,
        ILogger<PromptDispatcher> logger,
        IUsageLedger? ledger = null,
        BudgetGate? budgets = null,
        TimeProvider? time = null,
        ConnectorIncidents? incidents = null,
        RouterDashboardLocation? dashboard = null)
    {
        _health = health;
        _metrics = metrics;
        _logger = logger;
        _ledger = ledger;
        _budgets = budgets;
        _incidents = incidents;
        _dashboard = dashboard;
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
        long started = _time.GetTimestamp();

        // The first byte decides time-to-first-byte for whichever attempt is serving at that moment: the request's figure
        // counts from dispatch (failed attempts included, as the client waited through them), the serving model's own
        // from the start of its attempt, so a model that rescues a request is not charged for the ones that failed first.
        var current = new CurrentAttempt();
        context.Response.OnStarting(() =>
        {
            if (current.Model is { } model)
            {
                TimeSpan elapsed = _time.GetElapsedTime(started);
                TimeSpan ownElapsed = _time.GetElapsedTime(current.Started);
                _metrics.RecordTimeToFirstByte(model.Name, current.Connector!, elapsed, ownElapsed);
                if (activity is not null)
                {
                    activity.TimeToFirstByteMilliseconds = Math.Round(elapsed.TotalMilliseconds, 1);
                    activity.AttemptTimeToFirstByteMilliseconds = Math.Round(ownElapsed.TotalMilliseconds, 1);
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

            if (usage.Budgets.Count > 0 && _budgets?.Blocking(model, usage, plan.Requirements.Profile) is { } spent)
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
            var notes = new AttemptNotes();
            context.Features.Set(notes);
            current.Model = model;
            current.Connector = connector.Name;
            current.Started = _time.GetTimestamp();

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

                if (notes.Notes.Count > 0)
                {
                    span?.SetTag("semantic_router.notes", string.Join("; ", notes.Notes));
                }

                if (notes.PromptedToolCalls is { } promptedCalls)
                {
                    span?.SetTag("semantic_router.prompted_tool_calls", promptedCalls);
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
            if (result.Outcome != ConnectorOutcome.Skipped)
            {
                activity?.SentTo.Add(model.Name);
            }

            switch (result.Outcome)
            {
                case ConnectorOutcome.Completed:
                    _health.ReportSuccess(model);
                    _incidents?.Served(connector.Name);
                    LogServed(_logger, model.Name, connector.Name, tried);
                    RecordUsage(runtime, model, connector.Name, capture.Usage, activity, plan.Requirements.Profile);
                    _metrics.RecordRequest(model.Name, connector.Name, "served", _time.GetElapsedTime(started));
                    if (activity is not null)
                    {
                        activity.AttemptMilliseconds = Math.Round(_time.GetElapsedTime(current.Started).TotalMilliseconds, 1);
                        activity.ServedBy = model.Name;
                        activity.Connector = connector.Name;
                        activity.Attempt = tried;
                        activity.Degraded = !candidate.MeetsRequiredTier;
                        activity.Outcome = "served";
                        activity.Detail = attempts.Count > 0 ? string.Join("; ", attempts) : null;
                        activity.Budget = budgetsHit.Count > 0 ? string.Join(", ", budgetsHit.Distinct(StringComparer.OrdinalIgnoreCase)) : null;
                        activity.Dropped = dropped.Names.Count > 0 ? string.Join(", ", dropped.Names) : null;
                        activity.Notes = notes.Notes.Count > 0 ? string.Join("; ", notes.Notes) : null;
                        activity.PromptedToolCalls = notes.PromptedToolCalls;
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
                    _incidents?.Failed(connector.Name, result.FailureKind, failure);
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

        // No candidate even ignoring cooldowns: the configuration has no model for this request, and retrying cannot help.
        // A 400 tells clients (the OpenAI and Anthropic SDKs, Claude Code) not to retry, and says where to fix it. When
        // models exist but failed or cooled down, 503 lets them retry later; so does a plan that may differ later: one
        // made without classifying (the embeddings did not answer), or one that models outside their active hours would serve.
        // With no model enabled at all, nothing changes by waiting.
        bool noCandidate = plan.Candidates.Count == 0;
        bool notServable = noCandidate && plan.OutsideActiveHours.Count == 0
            && (plan.Classification.Reason != RoutingReason.ClassifierFailure || !runtime.Configuration.Models.Any(m => m.Enabled));
        string detail = notServable ? NotServable(context, runtime, plan.Requirements)
            : noCandidate ? NotServableNow(plan)
            : "Every candidate model failed: " + string.Join("; ", attempts);

        _metrics.RecordRequest(null, null, "failed", _time.GetElapsedTime(started));
        if (activity is not null)
        {
            activity.Outcome = "failed";
            activity.Detail = detail;
            activity.Budget = budgetsHit.Count > 0 ? string.Join(", ", budgetsHit.Distinct(StringComparer.OrdinalIgnoreCase)) : null;
        }

        if (notServable)
        {
            await ProtocolErrors.WriteNotServableAsync(context, chat.Protocol, detail, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await PromptRoutingMiddleware.WriteProblemAsync(
            context, chat.Protocol, StatusCodes.Status503ServiceUnavailable, "No model available", detail).ConfigureAwait(false);
    }

    /// <summary>
    /// Why no model can answer this request now although one may later (a 503): no model is in its active hours, or the
    /// prompt could not be classified and no model fits the fallback.
    /// </summary>
    private static string NotServableNow(RoutingPlan plan) =>
        plan.OutsideActiveHours.Count > 0
            ? $"SemanticRouter cannot answer this now: no model is in its active hours right now ({string.Join(", ", plan.OutsideActiveHours)} would fit later). Try again later."
            : $"SemanticRouter could not classify this prompt (see the router's log; often the embeddings are not answering), and no enabled model fits the fallback it uses meanwhile ({Needs(plan.Requirements)}). Try again once classification works.";

    /// <summary>
    /// Why no model can answer and where to fix it, e.g. <c>SemanticRouter has no model that can answer this (no model is
    /// enabled). Add one on the dashboard: http://localhost:5080/router#/overview</c>.
    /// </summary>
    private string NotServable(HttpContext context, RouterRuntime runtime, ModelRequirements requirements)
    {
        bool anyEnabled = runtime.Configuration.Models.Any(m => m.Enabled);
        string why = anyEnabled ? "no enabled model fits what it needs: " + Needs(requirements) : "no model is enabled";

        // The dashboard as this client reaches the router; the setup on Overview when nothing is enabled yet.
        string? page = _dashboard?.Prefix is { } prefix
            ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{prefix}#/{(anyEnabled ? "models" : "overview")}"
            : null;
        string fix = (anyEnabled, page) switch
        {
            (false, { } url) => $"Add one on the dashboard: {url}",
            (false, null) => "Add one to the router's configuration (SemanticRouter:Models).",
            (true, { } url) => $"Enable or add one that fits on the dashboard: {url}",
            (true, null) => "Enable or add one that fits in the router's configuration (SemanticRouter:Models).",
        };
        return $"SemanticRouter has no model that can answer this ({why}). {fix}";
    }

    /// <summary>What a request needs of a model, e.g. <c>tier 1, local only</c>.</summary>
    private static string Needs(ModelRequirements requirements)
    {
        var needs = new List<string> { $"tier {requirements.RequiredTier.ToString(CultureInfo.InvariantCulture)}" };
        if (requirements.RequiredCapabilities.Count > 0)
        {
            needs.Add("capabilities " + string.Join(", ", requirements.RequiredCapabilities));
        }

        if (requirements.LocalOnly)
        {
            needs.Add("local only");
        }

        if (requirements.Profile is { Length: > 0 } profile)
        {
            needs.Add($"profile '{profile}'");
        }

        return string.Join(", ", needs);
    }

    private void ReportFailure(ModelDefinition model, ConnectorResult result)
    {
        _health.ReportFailure(model, result.FailureKind, result.RetryAt);
        _metrics.RecordCooldown(model.Name, result.FailureKind);
    }

    private void RecordUsage(RouterRuntime runtime, ModelDefinition model, string connector, ChatUsage? reported, RoutingActivityEntry? activity, string? profile)
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

        // The same request at the reference model's prices, cache included: the reference model would have cached the
        // conversation too, so a cache discount is not a saving of routing.
        decimal referenceCost = reference?.Cost(input, output, cacheRead, cacheWrite) ?? cost;
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
            input, output, cacheRead, cacheWrite, cost, referenceCost, profile, model.ApiValue(input, output, cacheRead, cacheWrite)));
    }

    /// <summary>The model being tried, for the response-started callback.</summary>
    private sealed class CurrentAttempt
    {
        public ModelDefinition? Model { get; set; }

        public string? Connector { get; set; }

        /// <summary>When this attempt started (a <see cref="TimeProvider"/> timestamp), for the model's own timings.</summary>
        public long Started { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Served by {Model} via {Connector} (attempt {Attempt}).")]
    private static partial void LogServed(ILogger logger, string model, string connector, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {Model} failed ({Kind}): {Detail}. Trying the next candidate.")]
    private static partial void LogFailover(ILogger logger, string model, ModelFailureKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Model {Model} skipped: {Detail}.")]
    private static partial void LogSkipped(ILogger logger, string model, string detail);
}
