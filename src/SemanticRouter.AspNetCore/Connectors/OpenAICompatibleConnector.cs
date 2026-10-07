using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Forwards the original chat-completions body (with <c>model</c> rewritten) to any OpenAI-compatible endpoint and
/// streams the response back verbatim, so tool calls, images and SSE all pass through untouched.
/// </summary>
/// <remarks>
/// Failover-eligible responses: connection failures and timeouts, 401/403/404 (misconfiguration), 408, 429 (quota)
/// and 5xx. Any other status, including 400, is relayed to the client.
/// </remarks>
internal sealed partial class OpenAICompatibleConnector : IChatConnector
{
    public const string HttpClientNamePrefix = "SemanticRouter.Connector.";

    // Hop-by-hop headers (RFC 9110 §7.6.1) must not be forwarded by a proxy.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Proxy-Connection",
    };

    private readonly ConnectorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public OpenAICompatibleConnector(string name, ConnectorOptions options, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        if (options.BaseAddress is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException($"SemanticRouter:Connectors:{name}:BaseAddress must be an absolute URI.");
        }

        Name = name;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name { get; }

    public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress!, _options.RequestPath))
        {
            Content = new ReadOnlyMemoryContent(RewriteModel(request.RawBody, request.Model.Model)),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (string? accept in context.Request.Headers.Accept)
        {
            message.Headers.TryAddWithoutValidation("Accept", accept);
        }

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        foreach ((string header, string value) in _options.Headers)
        {
            message.Headers.Remove(header);
            message.Headers.TryAddWithoutValidation(header, value);
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientNamePrefix + Name);
        HttpResponseMessage response;
        using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headersTimeout.CancelAfter(_options.EffectiveTimeout);
            try
            {
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                LogUpstreamFailure(_logger, Name, message.RequestUri!, ex);
                return ConnectorResult.Failed(
                    ModelFailureKind.Unavailable,
                    ex is OperationCanceledException ? "timed out" : $"unreachable ({ex.Message})");
            }
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            if (status is 401 or 403 or 404 or 408 or 429 || status >= 500)
            {
                string detail = $"HTTP {status.ToString(CultureInfo.InvariantCulture)}";
                LogUpstreamStatus(_logger, Name, status);
                return ConnectorResult.Failed(status == 429 ? ModelFailureKind.QuotaExceeded : ModelFailureKind.Unavailable, detail);
            }

            await CopyResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
            return ConnectorResult.Completed;
        }
    }

    internal static ReadOnlyMemory<byte> RewriteModel(ReadOnlyMemory<byte> body, string? model)
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
                return JsonSerializer.SerializeToUtf8Bytes(json, ConnectorJsonContext.Default.JsonObject);
            }
        }
        catch (JsonException)
        {
            // Not an object we can rewrite; forward the original bytes untouched.
        }

        return body;
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        HttpResponse outgoing = context.Response;
        outgoing.StatusCode = (int)response.StatusCode;

        foreach (KeyValuePair<string, HeaderStringValues> header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
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

        Stream upstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (upstream.ConfigureAwait(false))
        {
            await upstream.CopyToAsync(outgoing.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: request to {Uri} failed.")]
    private static partial void LogUpstreamFailure(ILogger logger, string connector, Uri uri, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: upstream returned HTTP {Status}; trying the next model.")]
    private static partial void LogUpstreamStatus(ILogger logger, string connector, int status);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class ConnectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
