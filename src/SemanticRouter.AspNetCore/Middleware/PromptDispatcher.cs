using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Serves a request by trying the plan's candidate models in order through their connectors. A model that fails before
/// producing output is put into cooldown and the next candidate is tried, up to
/// <see cref="ModelSelectionOptions.MaxAttempts"/>.
/// </summary>
internal sealed partial class PromptDispatcher
{
    private readonly IModelHealth _health;
    private readonly PromptRouterMetrics _metrics;
    private readonly ILogger _logger;

    public PromptDispatcher(IModelHealth health, PromptRouterMetrics metrics, ILogger<PromptDispatcher> logger)
    {
        _health = health;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task DispatchAsync(
        HttpContext context, RouterRuntime runtime, RoutingPlan plan, ChatCompletionRequest chat, ReadOnlyMemory<byte> body, RoutingActivityEntry? activity = null)
    {
        PromptRouterMiddlewareOptions middleware = runtime.Configuration.Middleware;
        int maxAttempts = Math.Max(1, runtime.Configuration.Selection.MaxAttempts);
        var attempts = new List<string>();
        var cooledDuringRequest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tried = 0;

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

            ConnectorResult result = await connector
                .SendAsync(new ConnectorRequest(chat, model, body), context, context.RequestAborted)
                .ConfigureAwait(false);

            _metrics.RecordAttempt(model.Name, result.Outcome);
            if (result.Outcome != ConnectorOutcome.Skipped)
            {
                activity?.SentTo.Add(model.Name);
            }

            switch (result.Outcome)
            {
                case ConnectorOutcome.Completed:
                    _health.ReportSuccess(model);
                    LogServed(_logger, model.Name, connector.Name, tried);
                    if (activity is not null)
                    {
                        activity.ServedBy = model.Name;
                        activity.Connector = connector.Name;
                        activity.Attempt = tried;
                        activity.Degraded = !candidate.MeetsRequiredTier;
                        activity.Outcome = "served";
                        activity.Detail = attempts.Count > 0 ? string.Join("; ", attempts) : null;
                    }

                    return;

                case ConnectorOutcome.Failed:
                    _health.ReportFailure(model, result.FailureKind);
                    if (result.AffectsAllConnectorModels)
                    {
                        foreach (ModelDefinition sibling in runtime.Router.Models.Where(m =>
                                     m != model && string.Equals(m.Connector, model.Connector, StringComparison.OrdinalIgnoreCase)))
                        {
                            _health.ReportFailure(sibling, result.FailureKind);
                            cooledDuringRequest.Add(sibling.Name);
                        }
                    }

                    string failure = runtime.Redactor.Redact(result.Detail ?? "failed");
                    LogFailover(_logger, model.Name, result.FailureKind, failure);
                    attempts.Add($"{model.Name}: {failure}");
                    break;

                default:
                    string reason = runtime.Redactor.Redact(result.Detail ?? "skipped");
                    LogSkipped(_logger, model.Name, reason);
                    attempts.Add($"{model.Name}: skipped ({reason})");
                    break;
            }
        }

        string detail = plan.Candidates.Count == 0
            ? $"No configured model satisfies the request (tier {plan.Requirements.RequiredTier}" +
              (plan.Requirements.RequiredCapabilities.Count > 0 ? $", capabilities {string.Join(", ", plan.Requirements.RequiredCapabilities)}" : string.Empty) +
              (plan.Requirements.LocalOnly ? ", local only" : string.Empty) + ")."
            : "Every candidate model failed: " + string.Join("; ", attempts);

        if (activity is not null)
        {
            activity.Outcome = "failed";
            activity.Detail = detail;
        }

        await PromptRoutingMiddleware.WriteProblemAsync(
            context, chat.Protocol, StatusCodes.Status503ServiceUnavailable, "No model available", detail).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Served by {Model} via {Connector} (attempt {Attempt}).")]
    private static partial void LogServed(ILogger logger, string model, string connector, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {Model} failed ({Kind}): {Detail}. Trying the next candidate.")]
    private static partial void LogFailover(ILogger logger, string model, ModelFailureKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Model {Model} skipped: {Detail}.")]
    private static partial void LogSkipped(ILogger logger, string model, string detail);
}
