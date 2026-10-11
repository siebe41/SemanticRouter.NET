using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Everything the router can be configured with, as one document: the routing rules and catalog of
/// <see cref="SemanticRouterOptions"/> plus the connectors, embeddings and middleware sections. Its JSON shape is the
/// <c>SemanticRouter</c> section of <c>appsettings.json</c>, so a document can be copied between the two. The
/// dashboard edits documents, and a <see cref="IRouterConfigurationStore"/> persists them.
/// </summary>
public sealed class RouterConfiguration : SemanticRouterOptions
{
    /// <summary>Sentinel the API uses in place of a secret value; sending it back keeps the stored value.</summary>
    public const string MaskedSecret = "***";

    private static readonly JsonSerializerOptions Json = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        NewLine = "\n", // the same file on every OS, so it diffs cleanly and survives a copy between machines
    };

    /// <summary>How to reach each provider, by connector name.</summary>
    public IDictionary<string, ConnectorOptions> Connectors { get; } = new Dictionary<string, ConnectorOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The embedding provider used to score routes.</summary>
    public EmbeddingProviderOptions Embeddings { get; set; } = new();

    /// <summary>The gateway (middleware) settings.</summary>
    public PromptRouterMiddlewareOptions Middleware { get; set; } = new();

    /// <summary>Usage accounting and budgets.</summary>
    public UsageOptions Usage { get; set; } = new();

    /// <summary>Memory sources the dashboard maps, and tracking of which notes reach which model.</summary>
    public MemoryOptions Memory { get; set; } = new();

    /// <summary>Automatic tuning of thresholds and utterances.</summary>
    public TuningOptions Tuning { get; set; } = new();

    /// <summary>When the router asks for a person's attention.</summary>
    public NotificationOptions Notifications { get; set; } = new();

    /// <summary>Serializes a document (secrets included).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a document. Comments and trailing commas are allowed.</summary>
    public static RouterConfiguration FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<RouterConfiguration>(json, Json) ?? new RouterConfiguration();
    }

    /// <summary>Deep-copies the document.</summary>
    public RouterConfiguration Clone() => FromJson(ToJson());

    /// <summary>Builds a document from separately bound option objects (the <c>appsettings.json</c> sections).</summary>
    public static RouterConfiguration Compose(
        SemanticRouterOptions routing,
        IReadOnlyDictionary<string, ConnectorOptions> connectors,
        EmbeddingProviderOptions embeddings,
        PromptRouterMiddlewareOptions middleware) => Compose(routing, connectors, embeddings, middleware, new UsageOptions());

    /// <summary>Builds a document from separately bound option objects, including the usage and memory sections.</summary>
    public static RouterConfiguration Compose(
        SemanticRouterOptions routing,
        IReadOnlyDictionary<string, ConnectorOptions> connectors,
        EmbeddingProviderOptions embeddings,
        PromptRouterMiddlewareOptions middleware,
        UsageOptions usage,
        MemoryOptions? memory = null) => Compose(routing, connectors, embeddings, middleware, usage, memory, tuning: null);

    /// <summary>Builds a document from separately bound option objects, including the usage, memory and tuning sections.</summary>
    public static RouterConfiguration Compose(
        SemanticRouterOptions routing,
        IReadOnlyDictionary<string, ConnectorOptions> connectors,
        EmbeddingProviderOptions embeddings,
        PromptRouterMiddlewareOptions middleware,
        UsageOptions usage,
        MemoryOptions? memory,
        TuningOptions? tuning) => Compose(routing, connectors, embeddings, middleware, usage, memory, tuning, notifications: null);

    /// <summary>Builds a document from separately bound option objects, including the usage, memory, tuning and notification sections.</summary>
    public static RouterConfiguration Compose(
        SemanticRouterOptions routing,
        IReadOnlyDictionary<string, ConnectorOptions> connectors,
        EmbeddingProviderOptions embeddings,
        PromptRouterMiddlewareOptions middleware,
        UsageOptions usage,
        MemoryOptions? memory,
        TuningOptions? tuning,
        NotificationOptions? notifications)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(middleware);

        // A JSON round-trip copies every bindable property, including ones added later, without a hand-written clone.
        RouterConfiguration document = FromJson(JsonSerializer.Serialize(routing, routing.GetType(), Json));
        foreach ((string name, ConnectorOptions connector) in connectors)
        {
            document.Connectors[name] = JsonSerializer.Deserialize<ConnectorOptions>(JsonSerializer.Serialize(connector, Json), Json)!;
        }

        document.Embeddings = JsonSerializer.Deserialize<EmbeddingProviderOptions>(JsonSerializer.Serialize(embeddings, Json), Json)!;
        document.Middleware = JsonSerializer.Deserialize<PromptRouterMiddlewareOptions>(JsonSerializer.Serialize(middleware, Json), Json)!;
        document.Usage = JsonSerializer.Deserialize<UsageOptions>(JsonSerializer.Serialize(usage, Json), Json)!;
        if (memory is not null)
        {
            document.Memory = JsonSerializer.Deserialize<MemoryOptions>(JsonSerializer.Serialize(memory, Json), Json)!;
        }

        if (tuning is not null)
        {
            document.Tuning = JsonSerializer.Deserialize<TuningOptions>(JsonSerializer.Serialize(tuning, Json), Json)!;
        }

        if (notifications is not null)
        {
            document.Notifications = JsonSerializer.Deserialize<NotificationOptions>(JsonSerializer.Serialize(notifications, Json), Json)!;
        }

        return document;
    }

    /// <summary>
    /// Validation errors for the whole document: routing rules, catalog, connectors and middleware. Empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetDocumentValidationErrors()
    {
        var errors = new List<string>(GetValidationErrors());

        foreach ((string name, ConnectorOptions connector) in Connectors)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add("Connectors must have a name.");
                continue;
            }

            switch (connector.Type)
            {
                case ConnectorType.Cli when connector.Preset == CliPreset.Custom && string.IsNullOrWhiteSpace(connector.Executable):
                    errors.Add($"Connector '{name}' is a Custom CLI and needs an Executable.");
                    break;
                case ConnectorType.Cli or ConnectorType.Copilot when connector.MaxConcurrency < 1:
                    errors.Add($"Connector '{name}' MaxConcurrency must be at least 1.");
                    break;
                case ConnectorType.OpenAICompatible or ConnectorType.Anthropic or ConnectorType.Responses when connector.BaseAddress is not { IsAbsoluteUri: true }:
                    errors.Add($"Connector '{name}' needs an absolute BaseAddress.");
                    break;
            }

            if (connector.Timeout < TimeSpan.Zero)
            {
                errors.Add($"Connector '{name}' Timeout must not be negative.");
            }

            if (connector.MaxQueue is < 0)
            {
                errors.Add($"Connector '{name}' MaxQueue must not be negative.");
            }
        }

        foreach (ModelDefinition model in Models.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Connector)))
        {
            if (!Connectors.ContainsKey(model.Connector))
            {
                errors.Add($"Model '{model.Name}' uses connector '{model.Connector}', which is not configured under Connectors.");
            }
        }

        if (Embeddings.Provider == EmbeddingProviderKind.OpenAICompatible)
        {
            if (Embeddings.Endpoint is not { IsAbsoluteUri: true })
            {
                errors.Add("Embeddings:Endpoint is required for the OpenAICompatible provider.");
            }

            if (string.IsNullOrWhiteSpace(Embeddings.Model))
            {
                errors.Add("Embeddings:Model is required for the OpenAICompatible provider.");
            }
        }
        else if (Embeddings.Dimensions <= 0)
        {
            errors.Add("Embeddings:Dimensions must be positive.");
        }

        if (Middleware.MaxRequestBodyBytes <= 0)
        {
            errors.Add("Middleware:MaxRequestBodyBytes must be positive.");
        }

        foreach ((string client, string profile) in Middleware.ClientProfiles)
        {
            if (!Profiles.ContainsKey(profile))
            {
                errors.Add($"Middleware:ClientProfiles:{client} names profile '{profile}', which is not configured.");
            }
        }

        foreach (ProfileRule rule in Middleware.ProfileRules)
        {
            if (!Profiles.ContainsKey(rule.Profile))
            {
                errors.Add($"Middleware:ProfileRules name profile '{rule.Profile}', which is not configured.");
            }
        }

        foreach ((string name, VirtualModel model) in Middleware.VirtualModels)
        {
            if (name.StartsWith(ModelAliases.Auto, StringComparison.OrdinalIgnoreCase) || name.Contains('@', StringComparison.Ordinal)
                || Models.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Virtual model '{name}' must not start with 'auto', contain '@' or reuse a catalog model's name.");
            }

            if (model.Profile is { Length: > 0 } profile && !Profiles.ContainsKey(profile))
            {
                errors.Add($"Virtual model '{name}' names profile '{profile}', which is not configured.");
            }
        }

        ConversationRoutingOptions conversation = Middleware.Conversation;
        if (conversation.Enabled && (conversation.DecayTurns < 1 || conversation.IdleTimeout <= TimeSpan.Zero || conversation.ShortFollowUpLength < 0))
        {
            errors.Add("Middleware:Conversation needs DecayTurns of at least 1, a positive IdleTimeout and a ShortFollowUpLength of 0 or more.");
        }

        ReliabilityOptions reliability = Middleware.Reliability;
        if (reliability.Enabled && (reliability.Window <= TimeSpan.Zero || reliability.Period <= TimeSpan.Zero || reliability.MinimumTurns < 1
            || !(reliability.MaxFailureRate is > 0 and <= 1) || !(reliability.PeerMargin >= 0)))
        {
            errors.Add("Middleware:Reliability needs a positive Window and Period, MinimumTurns of at least 1, a MaxFailureRate above 0 and at most 1, and a PeerMargin of 0 or more.");
        }

        HousekeepingOptions housekeeping = Middleware.Housekeeping;
        if (housekeeping.Tier < 0 || housekeeping.ResumedTier < 0)
        {
            errors.Add("Middleware:Housekeeping Tier and ResumedTier must not be negative.");
        }

        foreach (string pattern in housekeeping.Patterns)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Middleware:Housekeeping:Patterns has an invalid regular expression '{pattern}': {ex.Message}");
            }
        }

        JudgeOptions judge = Middleware.Judge;
        if (judge.Enabled)
        {
            if (string.IsNullOrWhiteSpace(judge.Model))
            {
                errors.Add("Middleware:Judge:Model must name the catalog model that grades answers.");
            }
            else if (!Models.Any(m => string.Equals(m.Name, judge.Model, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Middleware:Judge:Model names '{judge.Model}', which is not in the model catalog.");
            }

            if (!(judge.SampleRate is >= 0 and <= 1) || judge.DailyLimit < 0 || judge.FailingGrade is < 1 or > 4 || judge.MaxCharacters < 100)
            {
                errors.Add("Middleware:Judge needs a SampleRate between 0 and 1, a DailyLimit of 0 or more, a FailingGrade from 1 to 4 and MaxCharacters of at least 100.");
            }
        }

        if (Middleware.RetryEscalation.Enabled && Middleware.RetryEscalation.Window <= TimeSpan.Zero)
        {
            errors.Add("Middleware:RetryEscalation:Window must be positive.");
        }

        foreach ((string client, string key) in Middleware.GatewayKeys)
        {
            if (string.IsNullOrWhiteSpace(client))
            {
                errors.Add("Middleware:GatewayKeys entries must have a client name.");
            }
            else if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add($"Middleware:GatewayKeys:{client} must not be empty.");
            }
        }

        foreach ((string client, RateLimitOptions limit) in Middleware.RateLimits)
        {
            if (string.IsNullOrWhiteSpace(client))
            {
                errors.Add("Middleware:RateLimits entries must name a client (or \"*\" for every other client).");
            }
            else if (client != ClientRateLimiter.Everyone && !Middleware.GatewayKeys.ContainsKey(client))
            {
                errors.Add($"Middleware:RateLimits:{client} names no gateway client in Middleware:GatewayKeys; use \"*\" for every client without its own limit.");
            }

            if (limit.RequestsPerMinute is null && limit.TokensPerMinute is null)
            {
                errors.Add($"Middleware:RateLimits:{client} needs RequestsPerMinute, TokensPerMinute or both.");
            }

            if (limit.RequestsPerMinute is < 1 || limit.TokensPerMinute is < 1)
            {
                errors.Add($"Middleware:RateLimits:{client} limits must be at least 1.");
            }
        }

        foreach (BudgetDefinition budget in Usage.Budgets)
        {
            string label = string.IsNullOrWhiteSpace(budget.Name) ? "A budget" : $"Budget '{budget.Name}'";
            if (string.IsNullOrWhiteSpace(budget.Name))
            {
                errors.Add("Usage:Budgets entries must have a Name.");
            }

            int targets = new[] { budget.Model, budget.Connector, budget.Profile }.Count(t => !string.IsNullOrWhiteSpace(t));
            if (targets != 1)
            {
                errors.Add($"{label} must name exactly one of Model, Connector or Profile.");
            }
            else if (!string.IsNullOrWhiteSpace(budget.Profile) && !Profiles.ContainsKey(budget.Profile))
            {
                errors.Add($"{label} covers profile '{budget.Profile}', which is not configured.");
            }
            else if (!string.IsNullOrWhiteSpace(budget.Connector) && !Connectors.ContainsKey(budget.Connector))
            {
                errors.Add($"{label} covers connector '{budget.Connector}', which is not configured.");
            }
            else if (!string.IsNullOrWhiteSpace(budget.Model) && !Models.Any(m => string.Equals(m.Name, budget.Model, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{label} covers model '{budget.Model}', which is not in the catalog.");
            }

            if (budget.MaxTokens is null && budget.MaxCostUsd is null)
            {
                errors.Add($"{label} needs MaxTokens or MaxCostUsd.");
            }
            else if (budget.MaxTokens < 0 || budget.MaxCostUsd < 0)
            {
                errors.Add($"{label} limits must not be negative.");
            }
        }

        if (Usage.WarnAtPercent is < 1 or > 100)
        {
            errors.Add("Usage:WarnAtPercent must be between 1 and 100.");
        }

        if (!string.IsNullOrWhiteSpace(Usage.ReferenceModel) && !Models.Any(m => string.Equals(m.Name, Usage.ReferenceModel, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Usage:ReferenceModel '{Usage.ReferenceModel}' is not in the catalog (it may be disabled, but it must be listed).");
        }

        if (!string.IsNullOrWhiteSpace(Usage.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(Usage.TimeZone, out _))
        {
            errors.Add($"Usage:TimeZone '{Usage.TimeZone}' is not a known time zone.");
        }

        if (string.IsNullOrWhiteSpace(Middleware.HeaderPrefix))
        {
            errors.Add("Middleware:HeaderPrefix must not be empty.");
        }

        var memorySources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MemorySourceOptions source in Memory.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name))
            {
                errors.Add("Memory:Sources entries must have a Name.");
            }
            else if (!memorySources.Add(source.Name))
            {
                errors.Add($"Memory source '{source.Name}' is listed more than once.");
            }
            else if (source.Enabled && source.Type != MemorySourceType.ClaudeCode && string.IsNullOrWhiteSpace(source.Path))
            {
                errors.Add(source.Type == MemorySourceType.KnowledgeGraph
                    ? $"Memory source '{source.Name}' needs a Path (the memory server's JSONL file)."
                    : $"Memory source '{source.Name}' needs a Path (the folder of notes).");
            }
        }

        errors.AddRange(Tuning.GetValidationErrors());
        errors.AddRange(Notifications.GetValidationErrors());

        if (Memory.MinimumLineLength < 8)
        {
            errors.Add("Memory:MinimumLineLength must be at least 8, otherwise common lines would mark unrelated notes as shared.");
        }

        if (Middleware.Mode == PromptRouterMode.Dispatch)
        {
            if (!Middleware.PathPrefixes.Any(p => !string.IsNullOrWhiteSpace(p)))
            {
                errors.Add("Middleware:PathPrefixes must list at least one path in Dispatch mode, otherwise every JSON POST in the application would be proxied.");
            }
        }

        return errors;
    }

    /// <summary>Fills secrets that <paramref name="document"/> leaves empty from <paramref name="defaults"/> (same connector name).</summary>
    public static void InheritSecrets(RouterConfiguration document, RouterConfiguration defaults)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(defaults);

        foreach ((string name, ConnectorOptions connector) in document.Connectors)
        {
            if (!defaults.Connectors.TryGetValue(name, out ConnectorOptions? baseline))
            {
                continue;
            }

            if (string.IsNullOrEmpty(connector.ApiKey) || connector.ApiKey == MaskedSecret)
            {
                connector.ApiKey = baseline.ApiKey;
            }

            foreach ((string header, string value) in baseline.Headers)
            {
                if (!connector.Headers.TryGetValue(header, out string? current) || current == MaskedSecret)
                {
                    connector.Headers[header] = value;
                }
            }
        }

        if (string.IsNullOrEmpty(document.Embeddings.ApiKey) || document.Embeddings.ApiKey == MaskedSecret)
        {
            document.Embeddings.ApiKey = defaults.Embeddings.ApiKey;
        }

        foreach ((string client, string key) in defaults.Middleware.GatewayKeys)
        {
            if (document.Middleware.GatewayKeys.TryGetValue(client, out string? current) && (string.IsNullOrEmpty(current) || current == MaskedSecret))
            {
                document.Middleware.GatewayKeys[client] = key;
            }
        }

        foreach ((string name, WebhookOptions baseline) in defaults.Notifications.Webhooks)
        {
            if (document.Notifications.Webhooks.TryGetValue(name, out WebhookOptions? hook) && (string.IsNullOrEmpty(hook.Url) || hook.Url == MaskedSecret))
            {
                hook.Url = baseline.Url;
            }
        }
    }

    /// <summary>
    /// Returns a copy with secret values replaced by <see cref="MaskedSecret"/>. Values that are references
    /// (<c>${env:…}</c>, <c>${secret:…}</c>) are not secrets and are returned as they are.
    /// </summary>
    public RouterConfiguration Masked()
    {
        RouterConfiguration copy = Clone();
        foreach (SecretSlot slot in copy.Slots(secretsOnly: true))
        {
            string? value = slot.Get();
            if (!string.IsNullOrEmpty(value) && !SecretReference.ContainsReference(value))
            {
                slot.Set(MaskedSecret);
            }
        }

        return copy;
    }

    /// <summary>Replaces <see cref="MaskedSecret"/> placeholders in this document with the real values from <paramref name="current"/>.</summary>
    public void UnmaskFrom(RouterConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(current);
        foreach ((string name, ConnectorOptions connector) in Connectors)
        {
            current.Connectors.TryGetValue(name, out ConnectorOptions? existing);
            if (connector.ApiKey == MaskedSecret)
            {
                connector.ApiKey = existing?.ApiKey;
            }

            foreach (string header in connector.Headers.Where(h => h.Value == MaskedSecret).Select(h => h.Key).ToArray())
            {
                if (existing is not null && existing.Headers.TryGetValue(header, out string? value))
                {
                    connector.Headers[header] = value;
                }
                else
                {
                    connector.Headers.Remove(header);
                }
            }
        }

        if (Embeddings.ApiKey == MaskedSecret)
        {
            Embeddings.ApiKey = current.Embeddings.ApiKey;
        }

        foreach (string client in Middleware.GatewayKeys.Where(k => k.Value == MaskedSecret).Select(k => k.Key).ToArray())
        {
            if (current.Middleware.GatewayKeys.TryGetValue(client, out string? value))
            {
                Middleware.GatewayKeys[client] = value;
            }
            else
            {
                Middleware.GatewayKeys.Remove(client);
            }
        }

        foreach ((string name, WebhookOptions hook) in Notifications.Webhooks.Where(h => h.Value.Url == MaskedSecret).ToArray())
        {
            if (current.Notifications.Webhooks.TryGetValue(name, out WebhookOptions? existing))
            {
                hook.Url = existing.Url;
            }
            else
            {
                Notifications.Webhooks.Remove(name);
            }
        }
    }

    /// <summary>
    /// Returns a copy in which every <c>${env:…}</c> and <c>${secret:…}</c> reference is replaced by its value. This is
    /// what connectors and the embedding provider are built from; the document itself keeps the references.
    /// </summary>
    /// <param name="store">The secret store for <c>${secret:…}</c> references, if one is registered.</param>
    /// <param name="problems">Receives a message for each reference that could not be resolved.</param>
    public RouterConfiguration ResolveSecrets(IRouterSecretStore? store, ICollection<string>? problems = null)
    {
        RouterConfiguration copy = Clone();
        foreach (SecretSlot slot in copy.Slots(secretsOnly: false))
        {
            string? value = slot.Get();
            if (SecretReference.ContainsReference(value))
            {
                slot.Set(SecretReference.Resolve(value, store, slot.Setting, problems));
            }
        }

        return copy;
    }

    /// <summary>
    /// Moves every literal secret (one that is neither empty, masked nor a reference) into <paramref name="store"/> and
    /// replaces it in this document with a <c>${secret:…}</c> reference. Returns the settings that were moved.
    /// </summary>
    public IReadOnlyList<string> MoveSecretsToStore(IRouterSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var moved = new List<string>();
        foreach (SecretSlot slot in Slots(secretsOnly: true))
        {
            string? value = slot.Get();
            if (IsLiteralSecret(value))
            {
                store.SetSecret(slot.StoreName, value!);
                slot.Set(SecretReference.ForStore(slot.StoreName));
                moved.Add(slot.Setting);
            }
        }

        return moved;
    }

    /// <summary>The settings that hold a secret written out in full (rather than a reference).</summary>
    public IReadOnlyList<string> LiteralSecretSettings() =>
        Slots(secretsOnly: true).Where(s => IsLiteralSecret(s.Get())).Select(s => s.Setting).ToArray();

    /// <summary>Every non-empty secret value in this (resolved) document, for redaction.</summary>
    internal IEnumerable<string> SecretValues() =>
        Slots(secretsOnly: false)
            .Where(s => s.IsSecret || s.Setting.Contains(":EnvironmentVariables:", StringComparison.Ordinal))
            .Select(s => s.Get())
            .Where(v => !string.IsNullOrEmpty(v) && v != MaskedSecret)
            .Cast<string>();

    private static bool IsLiteralSecret(string? value) =>
        !string.IsNullOrEmpty(value) && value != MaskedSecret && !SecretReference.ContainsReference(value);

    /// <summary>
    /// The configuration values that may hold secrets or references to them. With <paramref name="secretsOnly"/>, only
    /// the ones that are masked and moved to the secret store: API keys, secret-looking header values, gateway keys and
    /// webhook URLs.
    /// </summary>
    private List<SecretSlot> Slots(bool secretsOnly)
    {
        var slots = new List<SecretSlot>();
        foreach ((string name, ConnectorOptions connector) in Connectors)
        {
            slots.Add(new SecretSlot($"Connectors:{name}:ApiKey", $"connectors/{name}/ApiKey", true, () => connector.ApiKey, v => connector.ApiKey = v));
            foreach (string header in connector.Headers.Keys.ToArray())
            {
                bool secret = IsSecretHeader(header);
                if (secret || !secretsOnly)
                {
                    slots.Add(new SecretSlot(
                        $"Connectors:{name}:Headers:{header}", $"connectors/{name}/headers/{header}", secret,
                        () => connector.Headers.TryGetValue(header, out string? v) ? v : null, v => connector.Headers[header] = v ?? string.Empty));
                }
            }

            if (!secretsOnly)
            {
                foreach (string variable in connector.EnvironmentVariables.Keys.ToArray())
                {
                    slots.Add(new SecretSlot(
                        $"Connectors:{name}:EnvironmentVariables:{variable}", $"connectors/{name}/env/{variable}", false,
                        () => connector.EnvironmentVariables.TryGetValue(variable, out string? v) ? v : null,
                        v => connector.EnvironmentVariables[variable] = v ?? string.Empty));
                }
            }
        }

        slots.Add(new SecretSlot("Embeddings:ApiKey", "embeddings/ApiKey", true, () => Embeddings.ApiKey, v => Embeddings.ApiKey = v));
        foreach (string client in Middleware.GatewayKeys.Keys.ToArray())
        {
            slots.Add(new SecretSlot(
                $"Middleware:GatewayKeys:{client}", $"gateway/{client}", true,
                () => Middleware.GatewayKeys.TryGetValue(client, out string? v) ? v : null, v => Middleware.GatewayKeys[client] = v ?? string.Empty));
        }

        foreach ((string name, WebhookOptions hook) in Notifications.Webhooks)
        {
            slots.Add(new SecretSlot($"Notifications:Webhooks:{name}:Url", $"notifications/webhooks/{name}", true, () => hook.Url, v => hook.Url = v));
        }

        return slots;
    }

    private sealed record SecretSlot(string Setting, string StoreName, bool IsSecret, Func<string?> Get, Action<string?> Set);

    private static bool IsSecretHeader(string name) =>
        name.Contains("key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("authorization", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where a <see cref="RouterConfiguration"/> is kept between restarts.</summary>
public interface IRouterConfigurationStore
{
    /// <summary>A human-readable location, e.g. a file path.</summary>
    string Location { get; }

    /// <summary>Loads the stored document, or <see langword="null"/> when nothing has been stored yet.</summary>
    RouterConfiguration? Load();

    /// <summary>Stores the document.</summary>
    void Save(RouterConfiguration configuration);
}

/// <summary>Keeps the configuration in a JSON file, created on first save.</summary>
public sealed class JsonFileRouterConfigurationStore : IRouterConfigurationStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>Creates a store at <paramref name="path"/> (relative paths resolve against the current directory).</summary>
    public JsonFileRouterConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public string Location => _path;

    /// <inheritdoc />
    public RouterConfiguration? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            string json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json) ? null : RouterConfiguration.FromJson(json);
        }
    }

    /// <inheritdoc />
    public void Save(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, configuration.ToJson());
            File.Move(temp, _path, overwrite: true);
        }
    }
}
