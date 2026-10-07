using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Routes JSON chat requests: classifies the prompt, ranks the model catalog, and either annotates the request for
/// downstream endpoints or serves it through the chosen connectors.
/// </summary>
internal sealed partial class PromptRoutingMiddleware
{
    private const int ReadChunkSize = 16 * 1024;

    private readonly RequestDelegate _next;
    private readonly IPromptRouter _router;
    private readonly IPromptExtractor _extractor;
    private readonly PromptDispatcher _dispatcher;
    private readonly PromptRouterMetrics _metrics;
    private readonly PromptRouterMiddlewareOptions _options;
    private readonly int _fallbackTier;
    private readonly PathString[] _pathPrefixes;
    private readonly ILogger _logger;

    public PromptRoutingMiddleware(
        RequestDelegate next,
        IPromptRouter router,
        IPromptExtractor extractor,
        PromptDispatcher dispatcher,
        ChatConnectorRegistry connectors,
        PromptRouterMetrics metrics,
        IOptions<PromptRouterMiddlewareOptions> options,
        IOptions<SemanticRouterOptions> routerOptions,
        ILogger<PromptRoutingMiddleware> logger)
    {
        _next = next;
        _router = router;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _options = options.Value;
        _fallbackTier = routerOptions.Value.FallbackTier;
        _logger = logger;
        _pathPrefixes = _options.PathPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p.StartsWith('/') ? p.TrimEnd('/') : "/" + p.TrimEnd('/')))
            .ToArray();

        ValidateConfiguration(router.Models, connectors);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldIntercept(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        ReadOnlyMemory<byte>? body = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (body is not { } bodyBytes)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large",
                $"Prompt routing buffers at most {_options.MaxRequestBodyBytes} bytes.").ConfigureAwait(false);
            return;
        }

        // Give downstream handlers a fresh, seekable copy of the body we consumed.
        context.Request.Body = new MemoryStream(bodyBytes.ToArray(), writable: false);

        if (!TryParse(context, bodyBytes, out string? prompt, out ChatCompletionRequest chat))
        {
            LogNoPrompt(_logger, context.Request.Path);
            if (_options.Mode == PromptRouterMode.Dispatch)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "No prompt found",
                    "Expected an OpenAI chat-completions JSON body with 'messages'.").ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        (SelectionStrategy? strategy, string? requestedModel) = ModelAliases.Resolve(chat.Model);
        var routingRequest = new PromptRoutingRequest
        {
            Prompt = prompt,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            AdditionalCapabilities = ImpliedCapabilities(chat),
            RequestedModel = requestedModel,
            Strategy = strategy,
        };

        RoutingPlan plan = await RouteAsync(routingRequest, context.RequestAborted).ConfigureAwait(false);
        PromptClassificationResult result = plan.Classification;

        context.Features.Set<IPromptRoutingFeature>(new PromptRoutingFeature(prompt, plan));
        if (_options.EmitResponseHeaders)
        {
            WriteHeaders(context.Response.Headers, plan);
        }

        _metrics.RecordDecision(result);
        LogDecision(
            _logger, result.RequiredTier, result.Reason, result.RouteName ?? "none",
            result.Similarity ?? float.NaN, result.Complexity.Score,
            plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : "none", result.Elapsed.TotalMilliseconds);

        if (_options.Mode == PromptRouterMode.Dispatch)
        {
            await _dispatcher.DispatchAsync(context, plan, chat, bodyBytes).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static string[] ImpliedCapabilities(ChatCompletionRequest chat) =>
        (chat.HasTools, chat.HasImages) switch
        {
            (true, true) => ["tools", "vision"],
            (true, false) => ["tools"],
            (false, true) => ["vision"],
            _ => [],
        };

    private bool ShouldIntercept(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || !request.HasJsonContentType())
        {
            return false;
        }

        if (_pathPrefixes.Length == 0)
        {
            return true;
        }

        foreach (PathString prefix in _pathPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        long limit = _options.MaxRequestBodyBytes;
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

    private bool TryParse(HttpContext context, ReadOnlyMemory<byte> body, out string prompt, out ChatCompletionRequest chat)
    {
        prompt = string.Empty;
        chat = new ChatCompletionRequest();
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string? extracted = _extractor.ExtractPrompt(context, document.RootElement);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return false;
            }

            prompt = extracted;
            chat = ChatCompletionRequest.Parse(document.RootElement);
            return true;
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            return false;
        }
    }

    private async ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _router.RouteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.FailOpen && !cancellationToken.IsCancellationRequested)
        {
            LogClassificationFailed(_logger, _fallbackTier, ex);
            return _router.Plan(PromptClassificationResult.Bypass(_fallbackTier, RoutingReason.ClassifierFailure), request);
        }
    }

    private void WriteHeaders(IHeaderDictionary headers, RoutingPlan plan)
    {
        PromptClassificationResult result = plan.Classification;
        string prefix = _options.HeaderPrefix;
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

    private void ValidateConfiguration(IReadOnlyList<ModelDefinition> models, ChatConnectorRegistry connectors)
    {
        if (_options.MaxRequestBodyBytes <= 0)
        {
            throw new InvalidOperationException("SemanticRouter:Middleware:MaxRequestBodyBytes must be positive.");
        }

        if (string.IsNullOrWhiteSpace(_options.HeaderPrefix))
        {
            throw new InvalidOperationException("SemanticRouter:Middleware:HeaderPrefix must not be empty.");
        }

        if (_options.Mode != PromptRouterMode.Dispatch)
        {
            return;
        }

        if (_pathPrefixes.Length == 0)
        {
            throw new InvalidOperationException(
                "SemanticRouter:Middleware:PathPrefixes must list at least one path in Dispatch mode, " +
                "otherwise every JSON POST in the application would be proxied.");
        }

        if (!models.Any(m => m.Enabled))
        {
            throw new InvalidOperationException("SemanticRouter:Models must define at least one enabled model in Dispatch mode.");
        }

        foreach (ModelDefinition model in models.Where(m => m.Enabled))
        {
            if (!connectors.TryGet(model.Connector, out _))
            {
                throw new InvalidOperationException(
                    $"Model '{model.Name}' uses connector '{model.Connector}', which is not configured under SemanticRouter:Connectors.");
            }
        }
    }

    internal static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail) =>
        Microsoft.AspNetCore.Http.Results.Problem(detail, statusCode: statusCode, title: title).ExecuteAsync(context);

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
