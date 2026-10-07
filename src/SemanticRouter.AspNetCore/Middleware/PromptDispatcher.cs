using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Serves a request by trying the plan's candidate models in order through their connectors. A model that fails before
/// producing output is put into cooldown and the next candidate is tried, up to
/// <see cref="ModelSelectionOptions.MaxAttempts"/>.
/// </summary>
internal sealed partial class PromptDispatcher
{
    private readonly ChatConnectorRegistry _connectors;
    private readonly IPromptRouter _router;
    private readonly IModelHealth _health;
    private readonly PromptRouterMetrics _metrics;
    private readonly PromptRouterMiddlewareOptions _middleware;
    private readonly int _maxAttempts;
    private readonly ILogger _logger;

    public PromptDispatcher(
        ChatConnectorRegistry connectors,
        IPromptRouter router,
        IModelHealth health,
        PromptRouterMetrics metrics,
        IOptions<PromptRouterMiddlewareOptions> middleware,
        IOptions<SemanticRouterOptions> routerOptions,
        ILogger<PromptDispatcher> logger)
    {
        _connectors = connectors;
        _router = router;
        _health = health;
        _metrics = metrics;
        _middleware = middleware.Value;
        _maxAttempts = Math.Max(1, routerOptions.Value.Selection.MaxAttempts);
        _logger = logger;
    }

    public async Task DispatchAsync(HttpContext context, RoutingPlan plan, ChatCompletionRequest chat, ReadOnlyMemory<byte> body)
    {
        var attempts = new List<string>();
        var cooledDuringRequest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tried = 0;

        foreach (ModelCandidate candidate in plan.Candidates)
        {
            if (tried >= _maxAttempts)
            {
                break;
            }

            ModelDefinition model = candidate.Model;
            if (cooledDuringRequest.Contains(model.Name))
            {
                attempts.Add($"{model.Name}: skipped (its account hit a limit earlier in this request)");
                continue;
            }

            if (!_connectors.TryGet(model.Connector, out IChatConnector connector))
            {
                attempts.Add($"{model.Name}: unknown connector '{model.Connector}'");
                continue;
            }

            tried++;
            if (_middleware.EmitResponseHeaders)
            {
                string prefix = _middleware.HeaderPrefix;
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
            switch (result.Outcome)
            {
                case ConnectorOutcome.Completed:
                    _health.ReportSuccess(model);
                    LogServed(_logger, model.Name, connector.Name, tried);
                    return;

                case ConnectorOutcome.Failed:
                    _health.ReportFailure(model, result.FailureKind);
                    if (result.AffectsAllConnectorModels)
                    {
                        foreach (ModelDefinition sibling in _router.Models.Where(m =>
                                     m != model && string.Equals(m.Connector, model.Connector, StringComparison.OrdinalIgnoreCase)))
                        {
                            _health.ReportFailure(sibling, result.FailureKind);
                            cooledDuringRequest.Add(sibling.Name);
                        }
                    }

                    LogFailover(_logger, model.Name, result.FailureKind, result.Detail ?? "failed");
                    attempts.Add($"{model.Name}: {result.Detail ?? "failed"}");
                    break;

                default:
                    LogSkipped(_logger, model.Name, result.Detail ?? "skipped");
                    attempts.Add($"{model.Name}: skipped ({result.Detail})");
                    break;
            }
        }

        string detail = plan.Candidates.Count == 0
            ? $"No configured model satisfies the request (tier {plan.Requirements.RequiredTier}" +
              (plan.Requirements.RequiredCapabilities.Count > 0 ? $", capabilities {string.Join(", ", plan.Requirements.RequiredCapabilities)}" : string.Empty) +
              (plan.Requirements.LocalOnly ? ", local only" : string.Empty) + ")."
            : "Every candidate model failed: " + string.Join("; ", attempts);

        await PromptRoutingMiddleware.WriteProblemAsync(
            context, StatusCodes.Status503ServiceUnavailable, "No model available", detail).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Served by {Model} via {Connector} (attempt {Attempt}).")]
    private static partial void LogServed(ILogger logger, string model, string connector, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {Model} failed ({Kind}): {Detail}. Trying the next candidate.")]
    private static partial void LogFailover(ILogger logger, string model, ModelFailureKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Model {Model} skipped: {Detail}.")]
    private static partial void LogSkipped(ILogger logger, string model, string detail);
}
