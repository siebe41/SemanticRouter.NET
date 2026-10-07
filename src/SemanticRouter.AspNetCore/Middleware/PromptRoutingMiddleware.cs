using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Classifies JSON prompt requests and either annotates them for downstream endpoints or dispatches them
/// to the selected upstream model endpoint.
/// </summary>
internal sealed partial class PromptRoutingMiddleware
{
    private const int ReadChunkSize = 16 * 1024;

    private readonly RequestDelegate _next;
    private readonly IPromptClassifier _classifier;
    private readonly IPromptExtractor _extractor;
    private readonly PromptDispatcher _dispatcher;
    private readonly PromptRouterMetrics _metrics;
    private readonly PromptRouterMiddlewareOptions _options;
    private readonly RouteTarget _fallbackTarget;
    private readonly PathString[] _pathPrefixes;
    private readonly ILogger _logger;

    public PromptRoutingMiddleware(
        RequestDelegate next,
        IPromptClassifier classifier,
        IPromptExtractor extractor,
        PromptDispatcher dispatcher,
        PromptRouterMetrics metrics,
        IOptions<PromptRouterMiddlewareOptions> options,
        IOptions<SemanticRouterOptions> routerOptions,
        IOptions<PromptDispatchOptions> dispatchOptions,
        ILogger<PromptRoutingMiddleware> logger)
    {
        _next = next;
        _classifier = classifier;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _options = options.Value;
        _fallbackTarget = routerOptions.Value.FallbackTarget;
        _logger = logger;
        _pathPrefixes = _options.PathPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p.StartsWith('/') ? p.TrimEnd('/') : "/" + p.TrimEnd('/')))
            .ToArray();

        ValidateConfiguration(dispatchOptions.Value);
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

        string? prompt = TryExtractPrompt(context, bodyBytes);
        if (prompt is null)
        {
            LogNoPrompt(_logger, context.Request.Path);
            if (_options.Mode == PromptRouterMode.Dispatch)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "No prompt found",
                    "Expected a JSON body with 'messages', 'prompt' or 'input'.").ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        PromptClassificationResult result = await ClassifyAsync(prompt, context.RequestAborted).ConfigureAwait(false);

        context.Features.Set<IPromptRoutingFeature>(new PromptRoutingFeature(prompt, result));
        if (_options.EmitResponseHeaders)
        {
            WriteHeaders(context.Response.Headers, result);
        }

        _metrics.RecordDecision(result);
        LogDecision(
            _logger, result.Target, result.Reason, result.RouteName ?? "none",
            result.Similarity ?? float.NaN, result.Complexity.Score, result.Elapsed.TotalMilliseconds);

        if (_options.Mode == PromptRouterMode.Dispatch)
        {
            await _dispatcher.DispatchAsync(context, result, bodyBytes).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

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

    private string? TryExtractPrompt(HttpContext context, ReadOnlyMemory<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string? prompt = _extractor.ExtractPrompt(context, document.RootElement);
            return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            return null;
        }
    }

    private async ValueTask<PromptClassificationResult> ClassifyAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await _classifier.ClassifyAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.FailOpen && !cancellationToken.IsCancellationRequested)
        {
            LogClassificationFailed(_logger, _fallbackTarget, ex);
            return PromptClassificationResult.Bypass(_fallbackTarget, RoutingReason.ClassifierFailure);
        }
    }

    private void WriteHeaders(IHeaderDictionary headers, PromptClassificationResult result)
    {
        string prefix = _options.HeaderPrefix;
        headers[prefix] = result.RouteName ?? "none";
        headers[prefix + "-Target"] = result.Target.ToString();
        headers[prefix + "-Reason"] = result.Reason.ToString();
        headers[prefix + "-Complexity"] = result.Complexity.Score.ToString("0.000", CultureInfo.InvariantCulture);
        if (result.Similarity is { } similarity)
        {
            headers[prefix + "-Similarity"] = similarity.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    private void ValidateConfiguration(PromptDispatchOptions dispatch)
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

        foreach (RouteTarget target in Enum.GetValues<RouteTarget>())
        {
            if (dispatch.Get(target).BaseAddress is not { IsAbsoluteUri: true })
            {
                throw new InvalidOperationException(
                    $"SemanticRouter:Endpoints:{target}:BaseAddress must be an absolute URI in Dispatch mode.");
            }
        }
    }

    internal static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail) =>
        Microsoft.AspNetCore.Http.Results.Problem(detail, statusCode: statusCode, title: title).ExecuteAsync(context);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No prompt found in JSON request to {Path}; passing through.")]
    private static partial void LogNoPrompt(ILogger logger, PathString path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request body is not valid JSON; prompt routing skipped.")]
    private static partial void LogInvalidJson(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prompt classification failed; failing open to {Target}.")]
    private static partial void LogClassificationFailed(ILogger logger, RouteTarget target, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Prompt routed to {Target} ({Reason}); route={Route} similarity={Similarity:0.000} complexity={Complexity:0.000} in {ElapsedMs:0.0} ms")]
    private static partial void LogDecision(
        ILogger logger, RouteTarget target, RoutingReason reason, string route, float similarity, double complexity, double elapsedMs);
}
