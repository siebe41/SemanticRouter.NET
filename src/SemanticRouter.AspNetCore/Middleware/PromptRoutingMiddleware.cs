using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Routes JSON chat requests: classifies the prompt, ranks the model catalog, and either annotates the request for
/// downstream endpoints or serves it through the chosen connectors. Accepts OpenAI chat-completions bodies on
/// <see cref="PromptRouterMiddlewareOptions.PathPrefixes"/> and Anthropic Messages bodies on
/// <see cref="PromptRouterMiddlewareOptions.AnthropicPathPrefixes"/>.
/// </summary>
internal sealed partial class PromptRoutingMiddleware
{
    private const int ReadChunkSize = 16 * 1024;
    private const int PreviewLength = 160;

    private readonly RequestDelegate _next;
    private readonly RouterRuntimeProvider _runtime;
    private readonly PromptDispatcher _dispatcher;
    private readonly PromptRouterMetrics _metrics;
    private readonly RoutingActivityLog _activity;
    private readonly MemoryIndex _memory;
    private readonly MemoryExposureLog _exposure;
    private readonly IToolCallAffinity? _affinity;
    private readonly ILogger _logger;

    public PromptRoutingMiddleware(
        RequestDelegate next,
        RouterRuntimeProvider runtime,
        PromptDispatcher dispatcher,
        PromptRouterMetrics metrics,
        RoutingActivityLog activity,
        MemoryIndex memory,
        MemoryExposureLog exposure,
        ILogger<PromptRoutingMiddleware> logger,
        IToolCallAffinity? affinity = null)
    {
        _next = next;
        _runtime = runtime;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _activity = activity;
        _memory = memory;
        _exposure = exposure;
        _affinity = affinity;
        _logger = logger;

        // Building the runtime validates the configuration, so a bad setup fails at start-up rather than on the first request.
        _ = runtime.Current;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        RouterRuntime runtime = _runtime.Current;
        PromptRouterMiddlewareOptions options = runtime.Configuration.Middleware;
        if (!ShouldIntercept(context.Request, runtime, out ChatProtocol protocol))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? client = runtime.GatewayKeys.Count > 0 ? IdentifyClient(context.Request, runtime.GatewayKeys) : null;
        if (client is null && runtime.GatewayKeys.Count > 0
            && (!DashboardAccess.IsLocal(context) || options.RequireGatewayKeyForLocalClients))
        {
            LogGatewayKeyMissing(_logger, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path);
            await ProtocolErrors.WriteAsync(
                context, protocol,
                StatusCodes.Status401Unauthorized,
                "Gateway key required",
                "This router requires a gateway key: send it as 'Authorization: Bearer <key>' or 'x-api-key: <key>' " +
                "(ANTHROPIC_AUTH_TOKEN, ANTHROPIC_API_KEY or OPENAI_API_KEY in most clients).",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using Activity? routeSpan = RouterTelemetry.Source.StartActivity("semantic_router.route");
        routeSpan?.SetTag("semantic_router.protocol", protocol.ToString());
        routeSpan?.SetTag("semantic_router.client", client);
        ReadOnlyMemory<byte>? body = await ReadBodyAsync(context.Request, options.MaxRequestBodyBytes, context.RequestAborted).ConfigureAwait(false);
        if (body is not { } bodyBytes)
        {
            await ProtocolErrors.WriteAsync(
                context, protocol,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large",
                $"Prompt routing buffers at most {options.MaxRequestBodyBytes} bytes.",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // Give downstream handlers a fresh, seekable copy of the body we consumed.
        context.Request.Body = new MemoryStream(bodyBytes.ToArray(), writable: false);

        bool parsed;
        string? prompt;
        ChatCompletionRequest chat;
        IReadOnlyList<MemoryReference> memory;
        using (RouterTelemetry.Source.StartActivity("semantic_router.parse"))
        {
            parsed = TryParse(context, runtime, protocol, bodyBytes, out prompt, out chat, out memory);
        }

        if (!parsed)
        {
            LogNoPrompt(_logger, context.Request.Path);
            if (options.Mode == PromptRouterMode.Dispatch)
            {
                await ProtocolErrors.WriteAsync(
                    context, protocol,
                    StatusCodes.Status400BadRequest,
                    "No prompt found",
                    protocol switch
                    {
                        ChatProtocol.Anthropic => "Expected an Anthropic Messages JSON body with 'messages'.",
                        ChatProtocol.Responses => "Expected an OpenAI Responses JSON body with 'input'.",
                        _ => "Expected an OpenAI chat-completions JSON body with 'messages'.",
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        string? mapped = MapModel(runtime, chat.Model);
        (SelectionStrategy? strategy, string? requestedModel) = ModelAliases.Resolve(mapped);
        if (requestedModel is not null
            && !runtime.Router.Models.Any(m => m.Enabled && string.Equals(m.Name, requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            // A vendor model name the catalog does not know (e.g. Claude Code's defaults): route as "auto".
            requestedModel = null;
        }

        string? affinityModel = options.PinToolCallingTurns && chat.IsToolResultContinuation && requestedModel is null
            ? _affinity?.Lookup(chat.AnsweredToolUseIds)
            : null;

        var routingRequest = new PromptRoutingRequest
        {
            Prompt = prompt,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            AdditionalCapabilities = ImpliedCapabilities(chat),
            RequestedModel = requestedModel,
            Strategy = strategy,
            AffinityModel = affinityModel,
        };

        RoutingPlan plan = await RouteAsync(runtime, routingRequest, options.FailOpen, context.RequestAborted).ConfigureAwait(false);
        PromptClassificationResult result = plan.Classification;

        context.Features.Set<IPromptRoutingFeature>(new PromptRoutingFeature(prompt, plan));
        if (options.EmitResponseHeaders)
        {
            WriteHeaders(context.Response.Headers, options.HeaderPrefix, plan);
        }

        _metrics.RecordDecision(result);
        LogDecision(
            _logger, result.RequiredTier, result.Reason, result.RouteName ?? "none",
            result.Similarity ?? float.NaN, result.Complexity.Score,
            plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : "none", result.Elapsed.TotalMilliseconds);

        RoutingActivityEntry entry = _activity.Add(new RoutingActivityEntry
        {
            Id = _activity.Reserve(),
            TraceId = Activity.Current?.TraceId.ToHexString(),
            Timestamp = DateTimeOffset.UtcNow,
            Protocol = protocol,
            Path = context.Request.Path.Value ?? string.Empty,
            Client = client,
            RequestedModel = chat.Model,
            PromptPreview = prompt.Length <= PreviewLength ? prompt : prompt[..PreviewLength] + "…",
            Tier = result.RequiredTier,
            Reason = result.Reason,
            Route = result.RouteName,
            Similarity = result.Similarity,
            Complexity = Math.Round(result.Complexity.Score, 3),
            HasTools = chat.HasTools,
            ToolContinuation = chat.IsToolResultContinuation,
            LocalOnly = plan.Requirements.LocalOnly,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            PlannedModel = plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : null,
            ClassificationMilliseconds = Math.Round(result.Elapsed.TotalMilliseconds, 2),
            Memory = memory,
        });

        routeSpan?.SetTag("semantic_router.request_id", entry.Id);
        routeSpan?.SetTag("semantic_router.tier", result.RequiredTier);
        routeSpan?.SetTag("semantic_router.reason", result.Reason.ToString());
        routeSpan?.SetTag("semantic_router.route", result.RouteName);
        routeSpan?.SetTag("semantic_router.planned_model", entry.PlannedModel);
        if (options.EmitResponseHeaders)
        {
            // Matches the activity entry (dashboard, export), so a client can quote it when something went wrong.
            context.Response.Headers[options.HeaderPrefix + "-Id"] = entry.Id.ToString(CultureInfo.InvariantCulture);
        }

        using (_logger.BeginScope(new Dictionary<string, object> { ["RouteRequestId"] = entry.Id }))
        {
            if (options.Mode == PromptRouterMode.Dispatch)
            {
                await _dispatcher.DispatchAsync(context, runtime, plan, chat, bodyBytes, entry).ConfigureAwait(false);
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }

        entry.TotalMilliseconds = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        routeSpan?.SetTag("semantic_router.outcome", entry.Outcome);
        routeSpan?.SetTag("semantic_router.served_by", entry.ServedBy);
        if (entry.Outcome == "failed")
        {
            routeSpan?.SetStatus(ActivityStatusCode.Error, "no model available");
        }

        RecordMemoryExposure(runtime, entry);
        _activity.Complete(entry);
    }

    /// <summary>Counts the request's memory notes against every model it was sent to (Dispatch mode only: in Annotate mode the router does not know).</summary>
    private void RecordMemoryExposure(RouterRuntime runtime, RoutingActivityEntry entry)
    {
        if (entry.Memory.Count == 0)
        {
            return;
        }

        foreach (string name in entry.SentTo.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ModelDefinition? model = runtime.Router.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            _exposure.Record(entry.Memory, name, model?.IsLocal ?? false, entry.Timestamp);
        }
    }

    /// <summary>
    /// Applies the configured model alias globs (first match wins) to the client's model name. Routing aliases and
    /// catalog model names are returned as they are, so an explicit choice always beats a pattern.
    /// </summary>
    internal static string? MapModel(RouterRuntime runtime, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        string trimmed = model.Trim();
        if (trimmed.StartsWith(ModelAliases.Auto, StringComparison.OrdinalIgnoreCase)
            || runtime.Router.Models.Any(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return model;
        }

        foreach ((string pattern, string target) in runtime.ModelAliases)
        {
            if (GlobMatches(pattern, model))
            {
                return target;
            }
        }

        return model;
    }

    internal static bool GlobMatches(string pattern, string value)
    {
        // Simple '*' glob, case-insensitive.
        string[] parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        int position = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            int index = i == 0
                ? (value.StartsWith(part, StringComparison.OrdinalIgnoreCase) ? 0 : -1)
                : value.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (i == parts.Length - 1 && !value.EndsWith(part, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            position = index + part.Length;
        }

        return true;
    }

    /// <summary>
    /// The name of the gateway key the client presented, or <see langword="null"/>. Keys are compared in constant time
    /// and are only read for this comparison: they are never logged or forwarded.
    /// </summary>
    internal static string? IdentifyClient(HttpRequest request, IReadOnlyDictionary<string, string> keys)
    {
        string? bearer = request.Headers.Authorization;
        bearer = bearer is not null && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..].Trim() : null;
        string? apiKey = request.Headers["x-api-key"];
        string? match = null;
        foreach ((string key, string name) in keys)
        {
            byte[] expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            foreach (string? presented in new[] { bearer, apiKey })
            {
                if (!string.IsNullOrEmpty(presented)
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        expected, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented))))
                {
                    match ??= name;
                }
            }
        }

        return match;
    }

    private static string[] ImpliedCapabilities(ChatCompletionRequest chat) =>
        (chat.HasTools, chat.HasImages) switch
        {
            (true, true) => ["tools", "vision"],
            (true, false) => ["tools"],
            (false, true) => ["vision"],
            _ => [],
        };

    private static bool ShouldIntercept(HttpRequest request, RouterRuntime runtime, out ChatProtocol protocol)
    {
        protocol = ChatProtocol.OpenAI;
        if (!HttpMethods.IsPost(request.Method) || !request.HasJsonContentType())
        {
            return false;
        }

        foreach (PathString prefix in runtime.AnthropicPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out PathString remaining))
            {
                if (remaining.HasValue && remaining.Value!.EndsWith("/count_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    // Token counting is served by its own endpoint and must not be routed.
                    return false;
                }

                protocol = ChatProtocol.Anthropic;
                return true;
            }
        }

        foreach (PathString prefix in runtime.ResponsesPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out PathString rest) && (!rest.HasValue || rest.Value == "/"))
            {
                protocol = ChatProtocol.Responses;
                return true;
            }
        }

        if (runtime.OpenAIPrefixes.Length == 0)
        {
            return runtime.Configuration.Middleware.Mode != PromptRouterMode.Dispatch;
        }

        foreach (PathString prefix in runtime.OpenAIPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        int initialCapacity = (int)Math.Min(request.ContentLength ?? ReadChunkSize, limit);
        var buffer = new ArrayBufferWriter<byte>(Math.Max(initialCapacity, 256));
        while (true)
        {
            Memory<byte> chunk = buffer.GetMemory(ReadChunkSize);
            int read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenMemory;
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                return null;
            }
        }
    }

    private bool TryParse(
        HttpContext context, RouterRuntime runtime, ChatProtocol protocol, ReadOnlyMemory<byte> body,
        out string prompt, out ChatCompletionRequest chat, out IReadOnlyList<MemoryReference> memory)
    {
        prompt = string.Empty;
        chat = new ChatCompletionRequest();
        memory = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string? extracted = runtime.Extractor.ExtractPrompt(context, document.RootElement);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return false;
            }

            prompt = extracted;
            chat = ChatCompletionRequest.Parse(document.RootElement, protocol);
            if (runtime.Configuration.Memory.TrackSharing)
            {
                _memory.EnsureFresh();
                memory = _memory.Match(document.RootElement);
            }

            return true;
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            return false;
        }
    }

    private async ValueTask<RoutingPlan> RouteAsync(RouterRuntime runtime, PromptRoutingRequest request, bool failOpen, CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.Router.RouteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (failOpen && !cancellationToken.IsCancellationRequested)
        {
            int fallbackTier = runtime.Configuration.FallbackTier;
            LogClassificationFailed(_logger, fallbackTier, ex);
            return runtime.Router.Plan(PromptClassificationResult.Bypass(fallbackTier, RoutingReason.ClassifierFailure), request);
        }
    }

    private static void WriteHeaders(IHeaderDictionary headers, string prefix, RoutingPlan plan)
    {
        PromptClassificationResult result = plan.Classification;
        headers[prefix] = result.RouteName ?? "none";
        headers[prefix + "-Tier"] = result.RequiredTier.ToString(CultureInfo.InvariantCulture);
        headers[prefix + "-Reason"] = result.Reason.ToString();
        headers[prefix + "-Complexity"] = result.Complexity.Score.ToString("0.000", CultureInfo.InvariantCulture);
        headers[prefix + "-Model"] = plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : "none";
        if (result.Similarity is { } similarity)
        {
            headers[prefix + "-Similarity"] = similarity.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    internal static Task WriteProblemAsync(HttpContext context, ChatProtocol protocol, int statusCode, string title, string detail) =>
        ProtocolErrors.WriteAsync(context, protocol, statusCode, title, detail, context.RequestAborted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a gateway request from {Remote} to {Path}: no valid gateway key.")]
    private static partial void LogGatewayKeyMissing(ILogger logger, string remote, PathString path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No prompt found in JSON request to {Path}; passing through.")]
    private static partial void LogNoPrompt(ILogger logger, PathString path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request body is not valid JSON; prompt routing skipped.")]
    private static partial void LogInvalidJson(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prompt classification failed; failing open to tier {Tier}.")]
    private static partial void LogClassificationFailed(ILogger logger, int tier, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Prompt needs tier {Tier} ({Reason}); route={Route} similarity={Similarity:0.000} complexity={Complexity:0.000} first choice={Model} in {ElapsedMs:0.0} ms")]
    private static partial void LogDecision(
        ILogger logger, int tier, RoutingReason reason, string route, float similarity, double complexity, string model, double elapsedMs);
}
