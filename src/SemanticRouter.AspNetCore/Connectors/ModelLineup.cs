using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>A model a connector offers, and the catalog entry the router suggests for it.</summary>
/// <param name="Model">What the server reported.</param>
public sealed record ModelLineupEntry(DiscoveredModel Model)
{
    /// <summary>The suggested catalog entry. <see langword="null"/> for embedding models, which belong under <c>Embeddings</c>.</summary>
    public ModelDefinition? Suggestion { get; init; }

    /// <summary>Why the suggestion looks the way it does: the tier's basis, what is assumed, what to check.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether adding it is a sensible default (a local chat model, a known family); otherwise the user decides.</summary>
    public bool Recommended { get; init; }

    /// <summary>The catalog model that already uses this connector and model id, if any.</summary>
    public string? InCatalog { get; init; }

    /// <summary>The tier of <see cref="InCatalog"/>.</summary>
    public int? InCatalogTier { get; init; }
}

/// <summary>Everything a connector offers, for the dashboard's lineup and <c>semantic-router models</c>.</summary>
/// <param name="Connector">The connector name.</param>
/// <param name="Type">The connector type.</param>
public sealed record ModelLineupReport(string Connector, ConnectorType Type)
{
    /// <summary>Every model found, with a suggestion for each chat model.</summary>
    public IReadOnlyList<ModelLineupEntry> Entries { get; init; } = [];

    /// <summary>Whether the provider answered (for CLIs: the list is the preset's known model names).</summary>
    public bool Ok { get; init; }

    /// <summary>A failure, or how the list was obtained. Never contains a secret.</summary>
    public string? Note { get; init; }

    /// <summary>The server software, when recognised (<c>LM Studio</c>, <c>Ollama</c>).</summary>
    public string? Server { get; init; }

    /// <summary>Whether the server loads models on demand or only serves loaded ones.</summary>
    public ModelLoading Loading { get; init; }

    /// <summary>What <see cref="Loading"/> means for routing.</summary>
    public string? LoadingNote { get; init; }
}

/// <summary>
/// Turns what a connector offers into catalog suggestions, so a provider's whole lineup can be added at once and each
/// model put on the right tier: Claude's Haiku, Sonnet, Opus and Fable on four rungs; local models by size, with
/// <c>code</c>, <c>tools</c> and <c>vision</c> from the server's own report or the model's family; loaded models
/// preferred when the server loads the others on demand.
/// </summary>
/// <remarks>Suggestions are a starting point: prices and model families change, and the dashboard lets every field be edited before adding.</remarks>
public static partial class ModelLineup
{
    /// <summary>Claude families by capability: each gets its own tier on the default ladder.</summary>
    private static readonly Dictionary<string, (int Tier, string Name)> ClaudeFamilies = new(StringComparer.Ordinal)
    {
        ["haiku"] = (2, "Claude Haiku: fast and cheap; extraction, summaries, small code tasks"),
        ["sonnet"] = (3, "Claude Sonnet: everyday coding, agents and reasoning"),
        ["opus"] = (4, "Claude Opus: frontier; architecture, debugging, long agentic work"),
        ["fable"] = (5, "Claude Fable: Anthropic's most capable model; the hardest problems"),
        ["mythos"] = (5, "Claude Mythos: Fable-class (Project Glasswing)"),
    };

    /// <summary>Anthropic API list prices (USD per million tokens, input/output) and context windows, longest prefix first.</summary>
    private static readonly (string Prefix, int Context, decimal Input, decimal Output)[] AnthropicModels =
    [
        ("claude-fable-5", 1_000_000, 10m, 50m),
        ("claude-mythos-5", 1_000_000, 10m, 50m),
        ("claude-opus-5-5", 1_000_000, 4m, 20m),
        ("claude-opus-5", 1_000_000, 5m, 25m),
        ("claude-opus-4-8", 1_000_000, 5m, 25m),
        ("claude-opus-4-7", 1_000_000, 5m, 25m),
        ("claude-opus-4-6", 1_000_000, 5m, 25m),
        ("claude-sonnet-5", 1_000_000, 2m, 10m),
        ("claude-sonnet-4-6", 1_000_000, 3m, 15m),
        ("claude-haiku-5-5", 1_000_000, 0.10m, 0.50m),
        ("claude-haiku-4-5", 200_000, 1m, 5m),
    ];

    private static readonly string[] CoderNames = ["coder", "codestral", "devstral", "codellama", "codegemma", "starcoder", "code-"];
    private static readonly string[] ToolCallingFamilies = ["qwen3", "qwen2.5", "gpt-oss", "devstral", "mistral-small", "llama3.1", "llama-3.1", "llama3.3", "llama-3.3", "granite", "hermes"];
    private static readonly string[] ReasoningNames = ["qwq", "-r1", "r1-", "thinking", "gpt-oss", "magistral", "reasoning"];
    private static readonly string[] VisionNames = ["-vl", "vl-", "vision", "llava", "pixtral", "minicpm-v"];

    /// <summary>
    /// Model names a CLI preset accepts (CLIs have no model list to ask). Claude Code takes the <c>haiku</c>,
    /// <c>sonnet</c> and <c>opus</c> aliases, which follow Anthropic's latest model of each family, and full ids.
    /// </summary>
    public static IReadOnlyList<string> CliModelIds(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => ["haiku", "sonnet", "opus", "claude-fable-5-1"],
        CliPreset.Codex => ["gpt-6.1-sol", "gpt-6-luna"],
        CliPreset.GitHubCopilot => ["claude-sonnet-5.5", "gpt-6.1-sol"],
        _ => [],
    };

    /// <summary>Discovers what every configured connector offers, in name order.</summary>
    /// <param name="services">The application's services (the router must be registered).</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    public static async Task<IReadOnlyList<ModelLineupReport>> DiscoverAllAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        string[] names = services.GetRequiredService<RouterRuntimeProvider>().Current.Resolved.Connectors.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var reports = new List<ModelLineupReport>(names.Length);
        foreach (string name in names)
        {
            reports.Add(await DiscoverAsync(services, name, cancellationToken).ConfigureAwait(false));
        }

        return reports;
    }

    /// <summary>Discovers what the connector called <paramref name="connector"/> offers and suggests a catalog entry for each model.</summary>
    /// <param name="services">The application's services (the router must be registered).</param>
    /// <param name="connector">The connector name.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <exception cref="KeyNotFoundException">No connector has that name.</exception>
    public static async Task<ModelLineupReport> DiscoverAsync(IServiceProvider services, string connector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connector);
        RouterRuntime runtime = services.GetRequiredService<RouterRuntimeProvider>().Current;
        if (!runtime.Resolved.Connectors.TryGetValue(connector, out ConnectorOptions? options))
        {
            throw new KeyNotFoundException($"Connector '{connector}' is not configured.");
        }

        var report = new ModelLineupReport(connector, options.Type);
        if (runtime.Connectors.TryGet(connector, out IChatConnector? live) && live is IModelDiscovery discovery)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60)); // the first call may start a runtime (Copilot)
                IReadOnlyList<string> found = await discovery.ListModelsAsync(timeout.Token).ConfigureAwait(false);
                report = report with
                {
                    Ok = true,
                    Note = found.Count == 0 ? "The provider listed no models." : null,
                    Entries = Suggest(connector, options, found.Select(id => new DiscoveredModel(id)).ToArray(), ModelLoading.Unknown, runtime.Resolved.Models),
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TimeoutException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                report = report with { Note = ex is OperationCanceledException ? "The provider did not answer in time." : runtime.Redactor.Redact(ex.Message) };
            }

            return report;
        }

        if (options.Type == ConnectorType.Cli)
        {
            IReadOnlyList<string> ids = CliModelIds(options.Preset);
            return report with
            {
                Ok = ids.Count > 0,
                Note = ids.Count > 0
                    ? "A CLI has no model list to ask; these are the names its preset accepts. Any other model id the CLI knows works too."
                    : "A custom CLI has no model list to ask; add models by the names it accepts.",
                Entries = Suggest(connector, options, ids.Select(id => new DiscoveredModel(id)).ToArray(), ModelLoading.Unknown, runtime.Resolved.Models),
            };
        }

        if (options.BaseAddress is not { IsAbsoluteUri: true })
        {
            return report with { Note = $"SemanticRouter:Connectors:{connector}:BaseAddress is not set." };
        }

        IHttpClientFactory http = services.GetRequiredService<IHttpClientFactory>();
        ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync(
            connector, options, http.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + connector), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return report with
        {
            Ok = probe.Ok,
            Note = probe.Ok ? null : runtime.Redactor.Redact(probe.Message),
            Server = probe.Server,
            Loading = probe.Loading,
            LoadingNote = probe.LoadingNote,
            Entries = Suggest(connector, options, probe.Details, probe.Loading, runtime.Resolved.Models),
        };
    }

    /// <summary>
    /// Suggests a catalog entry for each model, with names unique against <paramref name="catalog"/> and each other,
    /// and marks the models the catalog already has (same connector, same model id).
    /// </summary>
    public static IReadOnlyList<ModelLineupEntry> Suggest(
        string connectorName, ConnectorOptions connector, IReadOnlyList<DiscoveredModel> models, ModelLoading loading, IEnumerable<ModelDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(catalog);
        ModelDefinition[] existing = catalog.ToArray();
        var taken = new HashSet<string>(existing.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        return models.Select(model =>
        {
            ModelLineupEntry entry = Suggest(connectorName, connector, model, loading, taken);
            ModelDefinition? inCatalog = existing.FirstOrDefault(m => string.Equals(m.Connector, connectorName, StringComparison.OrdinalIgnoreCase)
                                                                      && string.Equals(m.Model ?? string.Empty, model.Id, StringComparison.OrdinalIgnoreCase));
            return entry with { InCatalog = inCatalog?.Name, InCatalogTier = inCatalog?.Tier, Recommended = entry.Recommended && inCatalog is null };
        }).ToArray();
    }

    /// <summary>Suggests a catalog entry for one model. Its name is added to <paramref name="takenNames"/>.</summary>
    public static ModelLineupEntry Suggest(string connectorName, ConnectorOptions connector, DiscoveredModel model, ModelLoading loading, ISet<string> takenNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(takenNames);
        if (model.Kind == DiscoveredModelKind.Embedding)
        {
            return new ModelLineupEntry(model) { Reason = "An embedding model: use it under Embeddings, not as a chat model." };
        }

        string id = model.Id;
        string lower = id.ToLowerInvariant();
        bool localAddress = connector.Type == ConnectorType.OpenAICompatible && connector.BaseAddress is { } address && ConnectorProbe.IsLocalAddress(address);
        bool local = localAddress && model.Remote != true;
        bool subscription = connector.Type is ConnectorType.Cli or ConnectorType.Copilot;
        bool carriesTools = connector.Type switch
        {
            ConnectorType.Cli => connector.ToolCalling == CliToolCalling.Prompted,
            _ => true,
        };
        bool carriesImages = connector.Type is ConnectorType.OpenAICompatible or ConnectorType.Anthropic or ConnectorType.Responses;

        var suggestion = new ModelDefinition { Connector = connectorName, Model = id, IsLocal = local };
        var reasons = new List<string>();
        var capabilities = new List<string> { "chat" };
        bool recommended;

        if (ClaudeFamily().Match(lower) is { Success: true } claude)
        {
            (int tier, string family) = ClaudeFamilies[claude.Groups[1].Value];
            suggestion.Tier = tier;
            capabilities.AddRange(["code", "reasoning"]);
            if (carriesTools)
            {
                capabilities.Add("tools");
            }

            if (carriesImages)
            {
                capabilities.Add("vision");
            }

            reasons.Add($"{family} → tier {tier}");
            string normalized = lower.Replace('.', '-'); // Copilot writes claude-sonnet-5.5
            (string Prefix, int Context, decimal Input, decimal Output)[] known = AnthropicModels.Where(m => normalized.StartsWith(m.Prefix, StringComparison.Ordinal)).Take(1).ToArray();
            if (connector.Preset == CliPreset.ClaudeCode && connector.Type == ConnectorType.Cli)
            {
                suggestion.ContextWindow = 200_000;
                reasons.Add("context 200K, the conservative assumption for Claude Code; raise it if your plan has 1M");
            }
            else if (connector.Type == ConnectorType.Anthropic && known.Length == 1)
            {
                (_, int context, decimal input, decimal output) = known[0];
                suggestion.ContextWindow = model.MaxContextLength ?? context;
                suggestion.InputCostPerMillionTokens = input;
                suggestion.OutputCostPerMillionTokens = output;
                reasons.Add($"Anthropic list price ${input.ToString(CultureInfo.InvariantCulture)}/${output.ToString(CultureInfo.InvariantCulture)} per million tokens (check the current price)");
            }
            else
            {
                suggestion.ContextWindow = model.MaxContextLength ?? 0;
            }

            if (subscription && claude.Groups[1].Value is "fable" or "mythos")
            {
                suggestion.Enabled = false;
                reasons.Add("disabled: enable it if your plan includes this model");
            }

            recommended = true;
        }
        else if (local)
        {
            double? size = model.ParametersBillions ?? ConnectorProbe.ParametersFromId(id);
            bool coder = CoderNames.Any(lower.Contains);
            suggestion.Tier = size switch
            {
                null => coder ? 2 : 1,
                <= 9.5 => 1,
                <= 35 => 2,
                _ => 3,
            };
            reasons.Add(size is { } b
                ? $"{b.ToString("0.#", CultureInfo.InvariantCulture)}B parameters → tier {suggestion.Tier}"
                : $"size unknown → tier {suggestion.Tier}; raise it for a large model");
            if (coder)
            {
                capabilities.Add("code");
            }

            if (model.Capabilities.Contains("tools") || ToolCallingFamilies.Any(lower.Contains))
            {
                capabilities.Add("tools");
            }

            if (carriesImages && (model.Kind == DiscoveredModelKind.Vision || model.Capabilities.Contains("vision") || VisionNames.Any(lower.Contains)))
            {
                capabilities.Add("vision");
            }

            if (model.Capabilities.Contains("reasoning") || ReasoningNames.Any(lower.Contains))
            {
                capabilities.Add("reasoning");
            }

            if (model.Loaded is not null)
            {
                // The server reports load state, so the trained maximum is not what a request gets: LM Studio and Ollama
                // load with their own default context unless told otherwise.
                suggestion.ContextWindow = model.LoadedContextLength ?? 0;
                if (model.LoadedContextLength is null)
                {
                    reasons.Add(model.MaxContextLength is { } max
                        ? $"context unknown until loaded (supports up to {max.ToString("N0", CultureInfo.InvariantCulture)}): set it to the length you load it with"
                        : "context unknown until loaded");
                }
            }
            else
            {
                suggestion.ContextWindow = model.MaxContextLength ?? 0;
            }

            if (!model.Listed)
            {
                suggestion.Enabled = false;
                reasons.Add("disabled: the server does not serve it until it is loaded");
            }
            else if (model.Loaded == false && loading == ModelLoading.OnDemand)
            {
                suggestion.Priority = 1;
                reasons.Add("not loaded: its first request waits for it to load, so Priority 1 prefers loaded models");
            }

            recommended = model.Listed;
            if (model.Remote is null)
            {
                // IsLocal is a privacy rail: a proxy on localhost (LiteLLM, a gateway) can forward to a cloud API.
                reasons.Add("marked local because the server is on a local address; it is not LM Studio or Ollama, so make sure it does not forward prompts to a cloud API");
                recommended = false;
            }
        }
        else
        {
            suggestion.Tier = SmallModel().IsMatch(lower) ? 2 : 3;
            capabilities.Add("code");
            if (carriesTools)
            {
                capabilities.Add("tools");
            }

            suggestion.ContextWindow = model.MaxContextLength ?? 0;
            reasons.Add(suggestion.Tier == 2 ? "a small (\"mini\") model → tier 2" : "family not recognised → tier 3; review it");
            if (localAddress && model.Remote == true)
            {
                reasons.Add("not local: the server forwards it to a cloud service");
            }
            if (!subscription)
            {
                reasons.Add("set its prices: 0 makes it look free");
            }

            recommended = false;
        }

        foreach (string capability in capabilities.Distinct(StringComparer.Ordinal))
        {
            suggestion.Capabilities.Add(capability);
        }

        suggestion.Name = UniqueName(NameFor(connectorName, id), takenNames);
        suggestion.Description = Describe(id, connectorName, model, local, subscription);
        return new ModelLineupEntry(model) { Suggestion = suggestion, Reason = string.Join("; ", reasons) + ".", Recommended = recommended };
    }

    /// <summary>
    /// A readable catalog name: the connector (without a <c>-cli</c> suffix) and the model id's last segment, e.g.
    /// <c>lmstudio-qwen3-8b</c>, <c>ollama-llama3.2</c>, <c>claude-sonnet</c>, <c>claude-fable-5-1</c>.
    /// </summary>
    internal static string NameFor(string connectorName, string id)
    {
        string prefix = connectorName.EndsWith("-cli", StringComparison.OrdinalIgnoreCase) ? connectorName[..^4] : connectorName;
        string slug = id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (slug.EndsWith(":latest", StringComparison.Ordinal))
        {
            slug = slug[..^":latest".Length];
        }

        slug = NonNameCharacters().Replace(slug, "-").Trim('-');
        string first = prefix.Split('-')[0].ToLowerInvariant();
        if (slug.StartsWith(first + "-", StringComparison.Ordinal))
        {
            slug = slug[(first.Length + 1)..];
        }

        return slug.Length == 0 || slug.Equals(prefix, StringComparison.OrdinalIgnoreCase) ? prefix : $"{prefix}-{slug}";
    }

    private static string UniqueName(string name, ISet<string> taken)
    {
        string candidate = name;
        for (int n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{name}-{n.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static string Describe(string id, string connectorName, DiscoveredModel model, bool local, bool subscription)
    {
        var text = new StringBuilder(model.DisplayName is { Length: > 0 } display && display != id ? $"{display} ({id})" : id);
        text.Append(" via ").Append(connectorName);
        var facts = new List<string>();
        if (model.ParametersBillions is { } b)
        {
            facts.Add(b.ToString("0.#", CultureInfo.InvariantCulture) + "B");
        }

        if (model.Quantization is { Length: > 0 } q)
        {
            facts.Add(q);
        }

        if (local)
        {
            facts.Add("local");
        }
        else if (subscription)
        {
            facts.Add("your own subscription");
        }

        return facts.Count == 0 ? text.ToString() : text.Append(" (").Append(string.Join(", ", facts)).Append(')').ToString();
    }

    /// <summary>Serializes a suggestion the way the configuration document writes models (the dashboard adds it to the draft as is).</summary>
    internal static JsonElement ToConfigurationJson(ModelDefinition model) => JsonSerializer.SerializeToElement(model, ConfigurationJson);

    private static readonly JsonSerializerOptions ConfigurationJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [GeneratedRegex(@"(?<![a-z])(haiku|sonnet|opus|fable|mythos)(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeFamily();

    [GeneratedRegex(@"(?<![a-z])(nano|mini|lite|small|tiny)(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SmallModel();

    [GeneratedRegex(@"[^a-z0-9.]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonNameCharacters();
}
