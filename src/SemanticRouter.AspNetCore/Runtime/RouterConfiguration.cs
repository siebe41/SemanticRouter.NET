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
    };

    /// <summary>How to reach each provider, by connector name.</summary>
    public IDictionary<string, ConnectorOptions> Connectors { get; } = new Dictionary<string, ConnectorOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The embedding provider used to score routes.</summary>
    public EmbeddingProviderOptions Embeddings { get; set; } = new();

    /// <summary>The gateway (middleware) settings.</summary>
    public PromptRouterMiddlewareOptions Middleware { get; set; } = new();

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
        PromptRouterMiddlewareOptions middleware)
    {
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
                case ConnectorType.Cli when connector.MaxConcurrency < 1:
                    errors.Add($"Connector '{name}' MaxConcurrency must be at least 1.");
                    break;
                case ConnectorType.OpenAICompatible or ConnectorType.Anthropic when connector.BaseAddress is not { IsAbsoluteUri: true }:
                    errors.Add($"Connector '{name}' needs an absolute BaseAddress.");
                    break;
            }

            if (connector.Timeout < TimeSpan.Zero)
            {
                errors.Add($"Connector '{name}' Timeout must not be negative.");
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

        if (string.IsNullOrWhiteSpace(Middleware.HeaderPrefix))
        {
            errors.Add("Middleware:HeaderPrefix must not be empty.");
        }

        if (Middleware.Mode == PromptRouterMode.Dispatch)
        {
            if (!Middleware.PathPrefixes.Any(p => !string.IsNullOrWhiteSpace(p)))
            {
                errors.Add("Middleware:PathPrefixes must list at least one path in Dispatch mode, otherwise every JSON POST in the application would be proxied.");
            }

            if (!Models.Any(m => m.Enabled))
            {
                errors.Add("Models must define at least one enabled model in Dispatch mode.");
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
    }

    /// <summary>Returns a copy with secret values replaced by <see cref="MaskedSecret"/>.</summary>
    public RouterConfiguration Masked()
    {
        RouterConfiguration copy = Clone();
        foreach (ConnectorOptions connector in copy.Connectors.Values)
        {
            if (!string.IsNullOrEmpty(connector.ApiKey))
            {
                connector.ApiKey = MaskedSecret;
            }

            foreach (string header in connector.Headers.Keys.Where(IsSecretHeader).ToArray())
            {
                connector.Headers[header] = MaskedSecret;
            }
        }

        if (!string.IsNullOrEmpty(copy.Embeddings.ApiKey))
        {
            copy.Embeddings.ApiKey = MaskedSecret;
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
    }

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
