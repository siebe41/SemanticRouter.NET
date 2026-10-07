using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Forwards a classified request to the selected upstream endpoint and streams the response back
/// (server-sent events included), with optional local → cloud failover.
/// </summary>
internal sealed partial class PromptDispatcher
{
    public const string LocalClientName = "SemanticRouter.Local";
    public const string CloudClientName = "SemanticRouter.Cloud";

    // Hop-by-hop headers (RFC 9110 §7.6.1) must not be forwarded by a proxy.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Proxy-Connection",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PromptDispatchOptions _endpoints;
    private readonly PromptRouterMiddlewareOptions _middlewareOptions;
    private readonly PromptRouterMetrics _metrics;
    private readonly ILogger _logger;

    public PromptDispatcher(
        IHttpClientFactory httpClientFactory,
        IOptions<PromptDispatchOptions> endpoints,
        IOptions<PromptRouterMiddlewareOptions> middlewareOptions,
        PromptRouterMetrics metrics,
        ILogger<PromptDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _endpoints = endpoints.Value;
        _middlewareOptions = middlewareOptions.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task DispatchAsync(HttpContext context, PromptClassificationResult result, ReadOnlyMemory<byte> body)
    {
        RouteTarget target = result.Target;
        HttpResponseMessage? response = await TrySendAsync(context, target, body).ConfigureAwait(false);

        try
        {
            bool localFailed = response is null || (int)response.StatusCode >= StatusCodes.Status500InternalServerError;
            if (target == RouteTarget.Local && localFailed && _middlewareOptions.FailoverToCloud && !result.IsPinned
                && !context.RequestAborted.IsCancellationRequested)
            {
                LogFailover(_logger, response is null ? "unreachable" : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                response?.Dispose();
                _metrics.RecordFailover();

                target = RouteTarget.Cloud;
                if (_middlewareOptions.EmitResponseHeaders)
                {
                    context.Response.Headers[_middlewareOptions.HeaderPrefix + "-Target"] = target.ToString();
                    context.Response.Headers[_middlewareOptions.HeaderPrefix + "-Failover"] = "true";
                }

                response = await TrySendAsync(context, target, body).ConfigureAwait(false);
            }

            if (response is null)
            {
                await PromptRoutingMiddleware.WriteProblemAsync(
                    context,
                    StatusCodes.Status502BadGateway,
                    "Upstream model unavailable",
                    $"The {target} model endpoint could not be reached.").ConfigureAwait(false);
                return;
            }

            await CopyResponseAsync(context, response).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<HttpResponseMessage?> TrySendAsync(HttpContext context, RouteTarget target, ReadOnlyMemory<byte> body)
    {
        RouteEndpointOptions endpoint = _endpoints.Get(target);
        HttpRequest incoming = context.Request;
        string path = endpoint.RequestPath ?? (incoming.PathBase + incoming.Path + incoming.QueryString).ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress!, path))
        {
            Content = new ReadOnlyMemoryContent(RewriteModel(body, endpoint.Model)),
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(incoming.ContentType, out var contentType)
            ? contentType
            : new MediaTypeHeaderValue("application/json");

        foreach (string? accept in incoming.Headers.Accept)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        foreach ((string name, string value) in endpoint.Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpClient client = _httpClientFactory.CreateClient(target == RouteTarget.Local ? LocalClientName : CloudClientName);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !context.RequestAborted.IsCancellationRequested)
        {
            LogUpstreamFailure(_logger, target, request.RequestUri!, ex);
            return null;
        }
    }

    private static ReadOnlyMemory<byte> RewriteModel(ReadOnlyMemory<byte> body, string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return body;
        }

        try
        {
            if (JsonNode.Parse(body.Span) is JsonObject json)
            {
                json["model"] = model;
                return JsonSerializer.SerializeToUtf8Bytes(json, JsonContext.Default.JsonObject);
            }
        }
        catch (JsonException)
        {
            // Not an object we can rewrite; forward the original bytes untouched.
        }

        return body;
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
    {
        HttpResponse outgoing = context.Response;
        outgoing.StatusCode = (int)response.StatusCode;

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            // "Server" holds space-separated product tokens, which a parsed header splits into separate values.
            outgoing.Headers[header.Key] = string.Equals(header.Key, "Server", StringComparison.OrdinalIgnoreCase)
                ? string.Join(' ', header.Value)
                : header.Value.ToArray();
        }

        // Stream tokens to the client as they arrive (SSE / NDJSON) instead of buffering the whole reply.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        Stream upstream = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
        await using (upstream.ConfigureAwait(false))
        {
            await upstream.CopyToAsync(outgoing.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local model endpoint failed ({Status}); failing over to the cloud endpoint.")]
    private static partial void LogFailover(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dispatch to {Target} endpoint {Uri} failed.")]
    private static partial void LogUpstreamFailure(ILogger logger, RouteTarget target, Uri uri, Exception exception);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
