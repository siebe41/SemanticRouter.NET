using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>Why a connector probe failed.</summary>
public enum ConnectorProbeFailure
{
    /// <summary>The probe succeeded.</summary>
    None = 0,

    /// <summary>The server could not be reached (connection refused, DNS, timeout).</summary>
    Unreachable = 1,

    /// <summary>The server wants credentials and none are configured (HTTP 401 without an API key).</summary>
    AuthenticationRequired = 2,

    /// <summary>The server rejected the configured credentials (HTTP 401 or 403 with an API key).</summary>
    CredentialsRejected = 3,

    /// <summary>Any other unsuccessful HTTP status.</summary>
    HttpError = 4,

    /// <summary>The server answered with something that is not a model list.</summary>
    InvalidResponse = 5,
}

/// <summary>The outcome of <see cref="ConnectorProbe.ListModelsAsync"/>.</summary>
public sealed record ConnectorProbeResult
{
    /// <summary>What went wrong, or <see cref="ConnectorProbeFailure.None"/>.</summary>
    public required ConnectorProbeFailure Failure { get; init; }

    /// <summary>Model ids the server reported.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>The HTTP status, when the server answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>A one-line explanation that names the setting to fix. Never contains a credential.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the server answered with a model list.</summary>
    public bool Ok => Failure == ConnectorProbeFailure.None;

    /// <summary>
    /// Every model the server reported, with the details it gave: the listed <see cref="Models"/> first, then (LM Studio)
    /// downloaded models it does not list because they are not loaded.
    /// </summary>
    public IReadOnlyList<DiscoveredModel> Details { get; init; } = [];

    /// <summary>The server software, when <see cref="ConnectorProbe.DiscoverAsync"/> recognised it (<c>LM Studio</c>, <c>Ollama</c>).</summary>
    public string? Server { get; init; }

    /// <summary>Whether the server loads models on demand or only serves loaded ones.</summary>
    public ModelLoading Loading { get; init; }

    /// <summary>What <see cref="Loading"/> means for routing, in one or two sentences.</summary>
    public string? LoadingNote { get; init; }
}

/// <summary>
/// Checks an HTTP connector without calling a model: asks <c>GET /v1/models</c> with the connector's credentials and
/// headers. Used by the dashboard's Discover button and by start-up checks; a 401 or 403 is reported as an
/// authentication problem that names the configuration key, not as "unreachable".
/// </summary>
public static partial class ConnectorProbe
{
    /// <summary>Lists the models an HTTP connector's server offers.</summary>
    /// <param name="name">The connector name, used in messages (<c>SemanticRouter:Connectors:{name}:ApiKey</c>).</param>
    /// <param name="connector">The connector's settings, with secret references already resolved.</param>
    /// <param name="client">The client to send the request with.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public static async Task<ConnectorProbeResult> ListModelsAsync(
        string name, ConnectorOptions connector, HttpClient client, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(client);
        if (connector.BaseAddress is not { IsAbsoluteUri: true } baseAddress)
        {
            return new ConnectorProbeResult
            {
                Failure = ConnectorProbeFailure.Unreachable,
                Models = [],
                Message = $"SemanticRouter:Connectors:{name}:BaseAddress is not set.",
            };
        }

        var uri = new Uri(baseAddress, "/v1/models");
        string shown = AddressDisplay.Show(uri)!;
        bool hasKey = !string.IsNullOrEmpty(connector.ApiKey);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, uri, connector);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            string statusText = status.ToString(CultureInfo.InvariantCulture);
            if (status is 401 or 403)
            {
                return new ConnectorProbeResult
                {
                    Failure = hasKey || status == 403 ? ConnectorProbeFailure.CredentialsRejected : ConnectorProbeFailure.AuthenticationRequired,
                    Models = [],
                    StatusCode = status,
                    Message = $"{shown} answered HTTP {statusText}: {OpenAICompatibleConnector.CredentialAdvice(name, status, hasKey)}.",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectorProbeResult
                {
                    Failure = ConnectorProbeFailure.HttpError,
                    Models = [],
                    StatusCode = status,
                    Message = $"{shown} answered HTTP {statusText}.",
                };
            }

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false));
            if (!json.RootElement.TryGetArray("data", out JsonElement data))
            {
                return new ConnectorProbeResult
                {
                    Failure = ConnectorProbeFailure.InvalidResponse,
                    Models = [],
                    StatusCode = status,
                    Message = $"{shown} did not return a model list.",
                };
            }

            DiscoveredModel[] details = data.EnumerateArray().Select(ReadListedModel).OfType<DiscoveredModel>().ToArray();
            return new ConnectorProbeResult
            {
                Failure = ConnectorProbeFailure.None,
                Models = details.Select(d => d.Id).ToArray(),
                StatusCode = status,
                Message = $"{shown}: {details.Length} model(s).",
                Details = details,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ConnectorProbeResult
            {
                Failure = ConnectorProbeFailure.Unreachable,
                Models = [],
                Message = $"{shown} is not reachable ({(ex is OperationCanceledException ? "timed out" : ex.Message.TrimEnd('.'))}). {StartHint(name, baseAddress)}",
            };
        }
        catch (JsonException)
        {
            return new ConnectorProbeResult
            {
                Failure = ConnectorProbeFailure.InvalidResponse,
                Models = [],
                Message = $"{shown} did not return JSON.",
            };
        }
    }

    /// <summary>
    /// What to do about a server that does not answer: for LM Studio and Ollama on this machine (by their default port
    /// or the connector's name), how to start them; otherwise the address to check, in the configuration and on the
    /// dashboard.
    /// </summary>
    internal static string StartHint(string name, Uri address)
    {
        string setting = $"SemanticRouter:Connectors:{name}:BaseAddress (dashboard: Providers → Connectors → {name} → Base address)";
        if (!IsLocalAddress(address))
        {
            return $"Check the address, {setting}, and this machine's network or proxy.";
        }

        if (address.Port == InitDefaults.LmStudioPort || name.Contains("lmstudio", StringComparison.OrdinalIgnoreCase) || name.Contains("lm-studio", StringComparison.OrdinalIgnoreCase))
        {
            return "Start LM Studio's local server (in LM Studio: Developer → Start server, or run `lms server start`) with a model loaded.";
        }

        return address.Port == InitDefaults.OllamaPort || name.Contains("ollama", StringComparison.OrdinalIgnoreCase)
            ? "Start Ollama (open the Ollama app, or run `ollama serve`)."
            : $"Start the server, or correct the address: {setting}.";
    }

    /// <summary>The ports LM Studio and Ollama listen on unless told otherwise.</summary>
    private static class InitDefaults
    {
        public const int LmStudioPort = 1234;
        public const int OllamaPort = 11434;
    }

    /// <summary>The headers every request to the connector's server carries: its credentials and extra headers.</summary>
    internal static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, ConnectorOptions connector)
    {
        var request = new HttpRequestMessage(method, uri);
        bool hasKey = !string.IsNullOrEmpty(connector.ApiKey);
        if (connector.Type == ConnectorTypes.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", connector.AnthropicVersion);
            if (hasKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", connector.ApiKey);
            }
        }
        else if (hasKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.ApiKey);
        }

        foreach ((string header, string value) in connector.Headers)
        {
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, value);
        }

        return request;
    }

    /// <summary>
    /// One entry of a <c>/v1/models</c> list. Besides the id, APIs differ in what they add: Anthropic sends
    /// <c>max_input_tokens</c> and <c>display_name</c>, OpenRouter <c>context_length</c>, LM Studio and vLLM
    /// <c>max_context_length</c> or <c>max_model_len</c>.
    /// </summary>
    private static DiscoveredModel? ReadListedModel(JsonElement item)
    {
        if (item.GetString("id") is not { Length: > 0 } id)
        {
            return null;
        }

        int? context = item.GetInt("max_input_tokens") ?? item.GetInt("context_length") ?? item.GetInt("max_context_length")
            ?? item.GetInt("max_model_len") ?? item.GetInt("context_window");
        return new DiscoveredModel
        {
            Id = id,
            Kind = LooksLikeEmbedding(id) ? DiscoveredModelKind.Embedding : DiscoveredModelKind.Chat,
            MaxContextLength = context,
            ParametersBillions = ParametersFromId(id),
            DisplayName = item.GetString("display_name") ?? item.GetString("name"),
        };
    }

    /// <summary>Whether a model id names an embedding model (<c>text-embedding-nomic-embed-text-v1.5</c>, <c>bge-m3</c>).</summary>
    public static bool LooksLikeEmbedding(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Contains("embed", StringComparison.OrdinalIgnoreCase)
               || id.Contains("bge-", StringComparison.OrdinalIgnoreCase)
               || id.Contains("minilm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The parameter count a model id states, in billions: <c>qwen3-8b</c> → 8, <c>qwen3:30b-a3b</c> → 30 (the active
    /// <c>a3b</c> is not the size), <c>qwen3-0.6b</c> → 0.6. <see langword="null"/> when the id does not say.
    /// </summary>
    public static double? ParametersFromId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Match match = ParameterSize().Match(id.ToLowerInvariant());
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0
            ? size
            : null;
    }

    // A number followed by "b", not preceded by a letter or digit ("a3b" is MoE active parameters, "e4b" effective) and
    // not followed by a letter ("8bit", "7base").
    [GeneratedRegex(@"(?<![a-z0-9.])(\d+(?:\.\d+)?)b(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterSize();
}
