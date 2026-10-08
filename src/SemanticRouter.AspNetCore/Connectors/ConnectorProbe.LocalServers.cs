using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Discovery beyond <c>/v1/models</c> for local model servers. LM Studio (<c>GET /api/v0/models</c>) and Ollama
/// (<c>GET /api/tags</c>, <c>/api/ps</c>, <c>POST /api/show</c>) report which models are loaded, their context length,
/// size and capabilities, and from that whether the server loads models on demand.
/// </summary>
public static partial class ConnectorProbe
{
    private const int MaxOllamaShowRequests = 32;

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".internal", ".home.arpa", ".localdomain"];

    /// <summary>
    /// <see cref="ListModelsAsync"/>, and for an OpenAI-compatible connector on this machine or the local network also
    /// LM Studio's and Ollama's own model APIs: which models are loaded, with what context, how large they are, what
    /// they can do, and whether the server loads models on demand (<see cref="ConnectorProbeResult.Loading"/>). Those
    /// extra requests are best-effort: when they fail the result is what <see cref="ListModelsAsync"/> found.
    /// </summary>
    /// <param name="name">The connector name, used in messages.</param>
    /// <param name="connector">The connector's settings, with secret references already resolved.</param>
    /// <param name="client">The client to send the requests with.</param>
    /// <param name="timeout">How long to wait for each step.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public static async Task<ConnectorProbeResult> DiscoverAsync(
        string name, ConnectorOptions connector, HttpClient client, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ConnectorProbeResult listed = await ListModelsAsync(name, connector, client, timeout, cancellationToken).ConfigureAwait(false);
        if (!listed.Ok || connector.Type != ConnectorType.OpenAICompatible || connector.BaseAddress is not { } baseAddress || !IsLocalAddress(baseAddress))
        {
            return listed;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await LmStudioAsync(listed, connector, client, timeoutSource.Token).ConfigureAwait(false)
                   ?? await OllamaAsync(listed, connector, client, timeoutSource.Token).ConfigureAwait(false)
                   ?? listed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return listed;
        }
    }

    /// <summary>
    /// Whether a server address is on this machine or the local network (loopback, private and link-local addresses,
    /// single-label and <c>.local</c>/<c>.lan</c>/<c>.internal</c> host names). Prompts sent there stay on
    /// infrastructure you control.
    /// </summary>
    public static bool IsLocalAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri)
        {
            return false;
        }

        if (address.IsLoopback)
        {
            return true;
        }

        string host = address.IdnHost;
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip))
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
            }

            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return !host.Contains('.', StringComparison.Ordinal)
               || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
               || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LM Studio's REST API: <c>GET /api/v0/models</c> lists every downloaded model with its state.</summary>
    private static async Task<ConnectorProbeResult?> LmStudioAsync(
        ConnectorProbeResult listed, ConnectorOptions connector, HttpClient client, CancellationToken cancellationToken)
    {
        using JsonDocument? json = await GetJsonAsync(client, connector, "/api/v0/models", cancellationToken).ConfigureAwait(false);
        if (json is null || !json.RootElement.TryGetArray("data", out JsonElement data)
            || !data.EnumerateArray().Any(m => m.GetString("state") is not null || m.GetString("type") is not null))
        {
            return null;
        }

        var listedIds = new HashSet<string>(listed.Models, StringComparer.OrdinalIgnoreCase);
        var downloaded = new Dictionary<string, DiscoveredModel>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.GetString("id") is not { Length: > 0 } id)
            {
                continue;
            }

            var capabilities = new List<string>();
            if (item.TryGetArray("capabilities", out JsonElement caps))
            {
                capabilities.AddRange(caps.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => CatalogCapability(c.GetString()!)).OfType<string>());
            }

            DiscoveredModelKind kind = item.GetString("type") switch
            {
                "embeddings" => DiscoveredModelKind.Embedding,
                "vlm" => DiscoveredModelKind.Vision,
                _ => LooksLikeEmbedding(id) ? DiscoveredModelKind.Embedding : DiscoveredModelKind.Chat,
            };
            if (kind == DiscoveredModelKind.Vision && !capabilities.Contains("vision"))
            {
                capabilities.Add("vision");
            }

            string? state = item.GetString("state");
            downloaded[id] = new DiscoveredModel(id)
            {
                Kind = kind,
                Listed = listedIds.Contains(id),
                Loaded = state is null ? null : state.Equals("loaded", StringComparison.OrdinalIgnoreCase),
                Remote = false,
                MaxContextLength = item.GetInt("max_context_length"),
                LoadedContextLength = item.GetInt("loaded_context_length"),
                ParametersBillions = ParametersFromId(id),
                Capabilities = capabilities.Distinct(StringComparer.Ordinal).ToArray(),
                Quantization = item.GetString("quantization"),
            };
        }

        // Listed models first, in the server's order, then the ones it only reports as downloaded.
        var details = new List<DiscoveredModel>();
        foreach (DiscoveredModel model in listed.Details)
        {
            details.Add(downloaded.Remove(model.Id, out DiscoveredModel? rich)
                ? rich with { MaxContextLength = rich.MaxContextLength ?? model.MaxContextLength, DisplayName = model.DisplayName }
                : model);
        }

        details.AddRange(downloaded.Values.Select(m => m with { Listed = false }));

        DiscoveredModel[] cold = details.Where(m => m.Loaded == false).ToArray();
        (ModelLoading loading, string note) = cold.Any(m => m.Listed)
            ? (ModelLoading.OnDemand,
               "LM Studio's just-in-time loading is on: a model that is not loaded loads on its first request, which waits for it, and LM Studio may unload another model to make room.")
            : cold.Length > 0
                ? (ModelLoading.LoadedOnly,
                   "LM Studio's just-in-time loading is off: only loaded models answer. Load the others in LM Studio (or `lms load <model>`), or turn on just-in-time loading in the server settings.")
                : (ModelLoading.Unknown,
                   "Every downloaded model is loaded, so whether LM Studio's just-in-time loading is on cannot be told.");
        return listed with
        {
            Details = details,
            Server = "LM Studio",
            Loading = loading,
            LoadingNote = note,
            Message = listed.Message.TrimEnd('.') + $"; LM Studio, {details.Count(m => m.Loaded == true)} loaded.",
        };
    }

    /// <summary>
    /// Ollama's API: <c>GET /api/tags</c> lists pulled models with their size, <c>GET /api/ps</c> the loaded ones, and
    /// <c>POST /api/show</c> each model's capabilities and trained context length.
    /// </summary>
    private static async Task<ConnectorProbeResult?> OllamaAsync(
        ConnectorProbeResult listed, ConnectorOptions connector, HttpClient client, CancellationToken cancellationToken)
    {
        using JsonDocument? tags = await GetJsonAsync(client, connector, "/api/tags", cancellationToken).ConfigureAwait(false);
        if (tags is null || !tags.RootElement.TryGetArray("models", out JsonElement pulled))
        {
            return null;
        }

        var sizes = new Dictionary<string, (double? Billions, string? Quantization, bool Remote)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in pulled.EnumerateArray())
        {
            string? id = item.GetString("model") ?? item.GetString("name");
            if (id is null)
            {
                continue;
            }

            item.TryGetObject("details", out JsonElement details);

            // Cloud models ("gpt-oss:120b-cloud") are listed like local ones but run on ollama.com.
            bool remote = item.GetString("remote_host") is not null || item.GetString("remote_model") is not null || IsOllamaCloudId(id);
            sizes[id] = (ParseParameterSize(details.GetString("parameter_size")), details.GetString("quantization_level"), remote);
        }

        var loaded = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        using (JsonDocument? ps = await GetJsonAsync(client, connector, "/api/ps", cancellationToken).ConfigureAwait(false))
        {
            if (ps is not null && ps.RootElement.TryGetArray("models", out JsonElement running))
            {
                foreach (JsonElement item in running.EnumerateArray())
                {
                    if ((item.GetString("model") ?? item.GetString("name")) is { } id)
                    {
                        loaded[id] = item.GetInt("context_length");
                    }
                }
            }
        }

        DiscoveredModel[] models = listed.Details.Count > 0 ? [.. listed.Details] : sizes.Keys.Select(id => new DiscoveredModel(id)).ToArray();
        (string[] Capabilities, int? Context)?[] shown = await Task.WhenAll(models.Take(MaxOllamaShowRequests)
            .Select(m => ShowOllamaModelAsync(client, connector, m.Id, cancellationToken))).ConfigureAwait(false);

        DiscoveredModel[] enriched = models.Select((model, i) =>
        {
            (double? billions, string? quantization, bool remote) = sizes.TryGetValue(model.Id, out var size) ? size : (null, null, IsOllamaCloudId(model.Id));
            (string[] Capabilities, int? Context)? show = i < shown.Length ? shown[i] : null;
            string[] capabilities = show?.Capabilities ?? [];
            bool isLoaded = loaded.TryGetValue(model.Id, out int? loadedContext);
            return model with
            {
                Kind = capabilities.Contains("embedding") && !capabilities.Contains("completion") ? DiscoveredModelKind.Embedding
                    : capabilities.Contains("vision") ? DiscoveredModelKind.Vision
                    : model.Kind,
                Loaded = isLoaded,
                LoadedContextLength = isLoaded ? loadedContext : null,
                MaxContextLength = show?.Context ?? model.MaxContextLength,
                ParametersBillions = billions ?? model.ParametersBillions,
                Quantization = quantization,
                Remote = remote,
                Capabilities = capabilities.Select(CatalogCapability).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            };
        }).ToArray();

        return listed with
        {
            Details = enriched,
            Server = "Ollama",
            Loading = ModelLoading.OnDemand,
            LoadingNote = "Ollama loads a model on its first request, which waits for it, and unloads it after it has been idle for its keep-alive time " +
                          "(5 minutes by default); loading one may unload another (OLLAMA_MAX_LOADED_MODELS).",
            Message = listed.Message.TrimEnd('.') + $"; Ollama, {loaded.Count} loaded.",
        };
    }

    private static async Task<(string[] Capabilities, int? Context)?> ShowOllamaModelAsync(
        HttpClient client, ConnectorOptions connector, string id, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(connector.BaseAddress!, "/api/show"), connector);
            request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = id }, ChatJsonContext.Default.DictionaryStringString),
                Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string[] capabilities = json.RootElement.TryGetArray("capabilities", out JsonElement caps)
                ? caps.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToArray()
                : [];
            int? context = null;
            if (json.RootElement.TryGetObject("model_info", out JsonElement info))
            {
                // "<architecture>.context_length", e.g. "llama.context_length": 131072.
                context = info.EnumerateObject()
                    .Where(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt32(out _))
                    .Select(p => (int?)p.Value.GetInt32())
                    .FirstOrDefault();
            }

            return (capabilities, context);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, ConnectorOptions connector, string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(connector.BaseAddress!, path), connector);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsOllamaCloudId(string id) =>
        id.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) || id.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>A server's capability name in catalog terms: <c>tool_use</c> and <c>tools</c> → <c>tools</c>, and so on.</summary>
    private static string? CatalogCapability(string capability) => capability.ToLowerInvariant() switch
    {
        "tool_use" or "tools" or "function_calling" => "tools",
        "vision" or "image_input" => "vision",
        "thinking" or "reasoning" => "reasoning",
        _ => null,
    };

    /// <summary>Ollama's <c>parameter_size</c>: <c>8.0B</c> → 8, <c>567M</c> → 0.567.</summary>
    private static double? ParseParameterSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        double scale = char.ToUpperInvariant(text[^1]) switch { 'B' => 1, 'M' => 0.001, 'T' => 1000, _ => 0 };
        return scale > 0 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value * scale : null;
    }
}
