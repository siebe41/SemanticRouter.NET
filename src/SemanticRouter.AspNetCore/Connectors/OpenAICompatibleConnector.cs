using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Reaches an HTTP chat API: any OpenAI-compatible <c>/v1/chat/completions</c> endpoint, or the Anthropic Messages API
/// (<see cref="ConnectorType.Anthropic"/>). When the client speaks the backend's protocol the body is forwarded with
/// only <c>model</c> rewritten and the response is streamed back verbatim, so tool calls, images and SSE pass through
/// untouched. Otherwise the request is re-expressed in the backend's protocol and the reply is translated back,
/// including streaming and tool calls.
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

    private static readonly HashSet<string> NeverForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "x-api-key", "Cookie", "Host", "Content-Length", "Content-Type",
    };

    private readonly ConnectorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IToolCallAffinity? _affinity;
    private readonly ILogger _logger;
    private readonly SecretRedactor _redactor;
    private readonly Uri _requestUri;

    public OpenAICompatibleConnector(
        string name,
        ConnectorOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        IToolCallAffinity? affinity = null,
        SecretRedactor? redactor = null)
    {
        if (options.BaseAddress is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException($"SemanticRouter:Connectors:{name}:BaseAddress must be an absolute URI.");
        }

        Name = name;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _affinity = affinity;
        _logger = logger;
        _redactor = redactor ?? SecretRedactor.None;
        _requestUri = new Uri(options.BaseAddress, options.EffectiveRequestPath);
    }

    public string Name { get; }

    /// <summary>The protocol this connector speaks upstream.</summary>
    public ChatProtocol Protocol => _options.Protocol;

    public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        bool passThrough = request.Chat.Protocol == Protocol;
        bool reasoning = request.Model.Capabilities.Contains("reasoning", StringComparer.OrdinalIgnoreCase);
        DroppedFeatures.Report(context, DroppedFeatures.ForTranslation(request.Chat, Protocol, reasoning));
        ReadOnlyMemory<byte> body = passThrough
            ? RewriteModel(request.RawBody, request.Model.Model, stripRelayedThinking: Protocol == ChatProtocol.Anthropic)
            : Protocol switch
            {
                ChatProtocol.Anthropic => AnthropicRequestBuilder.Build(request.Chat, request.Model.Model, reasoning: reasoning),
                ChatProtocol.Responses => ResponsesRequestBuilder.Build(request.Chat, request.Model.Model, reasoning: reasoning),
                _ => OpenAIRequestBuilder.Build(request.Chat, request.Model.Model, reasoning: reasoning),
            };

        using var message = new HttpRequestMessage(HttpMethod.Post, _requestUri) { Content = new ReadOnlyMemoryContent(body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (passThrough)
        {
            foreach (string? accept in context.Request.Headers.Accept)
            {
                message.Headers.TryAddWithoutValidation("Accept", accept);
            }
        }

        AddCredentials(message);
        foreach (string header in _options.ForwardHeaders)
        {
            if (!NeverForwarded.Contains(header) && context.Request.Headers.TryGetValue(header, out var values))
            {
                message.Headers.Remove(header);
                message.Headers.TryAddWithoutValidation(header, values.ToArray());
            }
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
                // Expected operational failures (server down, timeout): one line, no stack trace.
                string error = ex is OperationCanceledException ? "timed out" : _redactor.Redact(ex.Message);
                LogUpstreamFailure(_logger, Name, message.RequestUri!, error);
                return ConnectorResult.Failed(
                    ModelFailureKind.Unavailable,
                    ex is OperationCanceledException ? "timed out" : $"unreachable ({error})");
            }
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                string advice = CredentialAdvice(Name, status, hasKey: !string.IsNullOrEmpty(_options.ApiKey));
                LogUpstreamRejectedCredentials(_logger, Name, status, advice);
                return ConnectorResult.Failed(ModelFailureKind.Unavailable, $"HTTP {status.ToString(CultureInfo.InvariantCulture)}: {advice}");
            }

            if (status is 404 or 408 or 429 || status >= 500)
            {
                DateTimeOffset? retryAt = status == 429 ? await ReadResetAsync(response, cancellationToken).ConfigureAwait(false) : null;
                string detail = $"HTTP {status.ToString(CultureInfo.InvariantCulture)}" +
                                (retryAt is { } reset ? $" (limit resets {reset.ToString("u", CultureInfo.InvariantCulture)})" : string.Empty);
                LogUpstreamStatus(_logger, Name, status);
                return ConnectorResult.Failed(status == 429 ? ModelFailureKind.QuotaExceeded : ModelFailureKind.Unavailable, detail, retryAt: retryAt);
            }

            if (passThrough && !response.IsSuccessStatusCode)
            {
                // Relay the backend's error in its own shape, but never a key it may have echoed back.
                string error = _redactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                context.Response.StatusCode = status;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                await context.Response.WriteAsync(error, cancellationToken).ConfigureAwait(false);
                return ConnectorResult.Completed;
            }

            if (passThrough)
            {
                await CopyResponseAsync(context, response, request.Model.Name, cancellationToken).ConfigureAwait(false);
                return ConnectorResult.Completed;
            }

            if (!response.IsSuccessStatusCode)
            {
                await RelayErrorAsync(context, response, request.Chat.Protocol, _redactor, cancellationToken).ConfigureAwait(false);
                return ConnectorResult.Completed;
            }

            IChatResponseWriter writer = ChatResponseWriterFactory.Create(context, request.Chat, request.Model, _affinity);
            if (Protocol == ChatProtocol.Anthropic && AnthropicRequestBuilder.UsesStructuredOutputTool(request.Chat))
            {
                writer = new StructuredOutputWriter(writer, AnthropicRequestBuilder.StructuredOutputTool);
            }

            Task relay = Protocol switch
            {
                ChatProtocol.Anthropic => AnthropicResponseReader.RelayAsync(response, writer, cancellationToken),
                ChatProtocol.Responses => ResponsesResponseReader.RelayAsync(response, writer, cancellationToken),
                _ => OpenAIResponseReader.RelayAsync(response, writer, cancellationToken),
            };
            try
            {
                await relay.ConfigureAwait(false);
            }
            catch (JsonException ex) when (!writer.HasStarted)
            {
                // A 200 whose body is not the JSON the protocol promises (an HTML page from a proxy, a truncated
                // reply): nothing reached the client yet, so the next model can answer instead.
                LogUnreadableReply(_logger, Name, response.Content.Headers.ContentType?.MediaType ?? "no content type", ex.Message);
                return ConnectorResult.Failed(ModelFailureKind.Unavailable, "unreadable reply");
            }

            return ConnectorResult.Completed;
        }
    }

    /// <summary>
    /// Sets <c>model</c> on a pass-through body. With <paramref name="stripRelayedThinking"/>, also removes thinking
    /// blocks the router itself relayed from another backend: Anthropic would reject their signature.
    /// </summary>
    internal static ReadOnlyMemory<byte> RewriteModel(ReadOnlyMemory<byte> body, string? model, bool stripRelayedThinking = false)
    {
        bool strip = stripRelayedThinking && body.Span.IndexOf(System.Text.Encoding.UTF8.GetBytes(ThinkingBlock.RouterSignature)) >= 0;
        if (string.IsNullOrEmpty(model) && !strip)
        {
            return body;
        }

        try
        {
            if (JsonNode.Parse(body.Span) is JsonObject json)
            {
                if (!string.IsNullOrEmpty(model))
                {
                    json["model"] = model;
                }

                if (strip && json["messages"] is JsonArray messages)
                {
                    foreach (JsonArray content in messages.OfType<JsonObject>().Select(m => m["content"]).OfType<JsonArray>())
                    {
                        foreach (JsonNode? block in content.Where(b => b is JsonObject o && o["signature"] is JsonValue signature && signature.TryGetValue(out string? value) && value == ThinkingBlock.RouterSignature).ToArray())
                        {
                            content.Remove(block);
                        }
                    }
                }

                return JsonSerializer.SerializeToUtf8Bytes(json, ConnectorJsonContext.Default.JsonObject);
            }
        }
        catch (JsonException)
        {
            // Not an object we can rewrite; forward the original bytes untouched.
        }

        return body;
    }

    private void AddCredentials(HttpRequestMessage message)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            if (Protocol == ChatProtocol.Anthropic)
            {
                message.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);
            }

            return;
        }

        if (Protocol == ChatProtocol.Anthropic)
        {
            message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    private async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response, string modelName, CancellationToken cancellationToken)
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
            bool sse = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            UsageCapture? capture = UsageCapture.Of(context);
            if ((_affinity is not null || capture is not null) && response.IsSuccessStatusCode)
            {
                IToolCallAffinity? affinity = _affinity;
                await PassThroughToolCallScanner.CopyAsync(
                    upstream, outgoing.Body, sse, Protocol, id => affinity?.Remember(id, modelName), cancellationToken,
                    capture is null ? null : capture.Observe).ConfigureAwait(false);
            }
            else
            {
                await upstream.CopyToAsync(outgoing.Body, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Relays a non-failover upstream error (e.g. 400) to a client that speaks another protocol.</summary>
    private static async Task RelayErrorAsync(
        HttpContext context, HttpResponseMessage response, ChatProtocol clientProtocol, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        string upstreamBody = redactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        string detail = ExtractErrorMessage(upstreamBody) ?? (upstreamBody.Length > 500 ? upstreamBody[..500] : upstreamBody);
        await ProtocolErrors.WriteAsync(
            context, clientProtocol, (int)response.StatusCode, "Upstream request rejected", detail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// When a 429's limit ends: <c>Retry-After</c>, Anthropic's <c>anthropic-ratelimit-*-reset</c> timestamps, or a
    /// reset time in the error message.
    /// </summary>
    internal static async Task<DateTimeOffset?> ReadResetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryAfter)
            && QuotaResetParser.TryParseRetryAfter(retryAfter.FirstOrDefault(), now, out DateTimeOffset fromHeader))
        {
            return fromHeader;
        }

        DateTimeOffset? latest = null;
        foreach (string header in new[] { "anthropic-ratelimit-requests-reset", "anthropic-ratelimit-tokens-reset", "anthropic-ratelimit-input-tokens-reset", "anthropic-ratelimit-output-tokens-reset" })
        {
            if (response.Headers.TryGetValues(header, out IEnumerable<string>? values)
                && DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset reset)
                && reset > now && (latest is null || reset > latest))
            {
                latest = reset;
            }
        }

        if (latest is not null)
        {
            return latest;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return QuotaResetParser.TryParse(body.Length > 2000 ? body[..2000] : body, now, null, out DateTimeOffset fromBody) ? fromBody : null;
    }

    /// <summary>What to fix when a server rejects the connector's credentials (names the setting, never the value).</summary>
    internal static string CredentialAdvice(string connector, int status, bool hasKey) => hasKey
        ? $"the server rejected the credentials; check SemanticRouter:Connectors:{connector}:ApiKey"
        : status == 401
            ? $"the server requires authentication; set SemanticRouter:Connectors:{connector}:ApiKey"
            : $"the server refused the request; check SemanticRouter:Connectors:{connector}:ApiKey and the server's permissions";

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetObject("error", out JsonElement error))
            {
                return error.GetString("message");
            }

            return root.GetString("message") ?? root.GetString("error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: the reply ({ContentType}) could not be read ({Error}); trying the next model.")]
    private static partial void LogUnreadableReply(ILogger logger, string connector, string contentType, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: request to {Uri} failed: {Error}")]
    private static partial void LogUpstreamFailure(ILogger logger, string connector, Uri uri, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: HTTP {Status}, {Advice}. Trying the next model.")]
    private static partial void LogUpstreamRejectedCredentials(ILogger logger, string connector, int status, string advice);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: upstream returned HTTP {Status}; trying the next model.")]
    private static partial void LogUpstreamStatus(ILogger logger, string connector, int status);
}

/// <summary>Writes error responses in the client's protocol.</summary>
internal static class ProtocolErrors
{
    public static async Task WriteAsync(HttpContext context, ChatProtocol protocol, int statusCode, string title, string detail, CancellationToken cancellationToken)
    {
        if (protocol == ChatProtocol.Responses)
        {
            // The OpenAI error envelope, which the OpenAI SDKs and Codex read.
            var error = new OpenAIErrorEnvelope(new OpenAIError($"{title}: {detail}", statusCode switch
            {
                401 => "authentication_error",
                403 => "permission_error",
                404 => "not_found_error",
                429 => "rate_limit_exceeded",
                >= 500 => "server_error",
                _ => "invalid_request_error",
            }));
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(error, ConnectorJsonContext.Default.OpenAIErrorEnvelope), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (protocol != ChatProtocol.Anthropic)
        {
            await Microsoft.AspNetCore.Http.Results.Problem(detail, statusCode: statusCode, title: title).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        string type = statusCode switch
        {
            400 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            503 or 529 => "overloaded_error",
            _ => "api_error",
        };

        var payload = new AnthropicErrorEnvelope("error", new AnthropicError(type, $"{title}: {detail}"));
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(payload, ConnectorJsonContext.Default.AnthropicErrorEnvelope), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AnthropicErrorEnvelope(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] AnthropicError Error);

internal sealed record AnthropicError(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

internal sealed record OpenAIErrorEnvelope([property: System.Text.Json.Serialization.JsonPropertyName("error")] OpenAIError Error);

internal sealed record OpenAIError(
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type);

[System.Text.Json.Serialization.JsonSerializable(typeof(OpenAIErrorEnvelope))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnthropicErrorEnvelope))]
internal sealed partial class ConnectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
