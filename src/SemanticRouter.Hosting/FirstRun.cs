using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// The configuration a router has before anyone set it up: the built-in template, and in a container the API
/// connectors its environment holds keys for.
/// </summary>
internal static class FirstRun
{
    /// <summary>The API keys a container may be started with, and the connector each one gets.</summary>
    private static readonly (string Variable, string Connector, ConnectorType Type, Uri Address)[] ApiKeys =
    [
        ("OPENAI_API_KEY", "openai", ConnectorType.OpenAICompatible, new Uri("https://api.openai.com")),
        ("ANTHROPIC_API_KEY", "anthropic", ConnectorType.Anthropic, new Uri("https://api.anthropic.com")),
    ];

    /// <summary>
    /// Whether <paramref name="configurationFile"/> still holds the built-in template, as the router or an <c>init</c>
    /// that found nothing wrote it: no models, no connectors, and every other setting the template's, apart from the
    /// embeddings <c>init</c> picks. Compared as configurations, so formatting and property order do not matter. A file
    /// that cannot be read is not the template.
    /// </summary>
    /// <param name="configurationFile">The configuration file.</param>
    /// <param name="exactly">Count a different embeddings choice as a change: the file is the template as the router writes it, so no <c>init</c> ran.</param>
    public static bool IsUntouchedTemplate(string configurationFile, bool exactly = false)
    {
        try
        {
            RouterConfiguration? stored = new JsonFileRouterConfigurationStore(configurationFile).Load();
            return stored is { Models.Count: 0, Connectors.Count: 0 }
                   && JsonNode.DeepEquals(Comparable(stored, exactly), Comparable(RouterConfiguration.FromJson(RouterHost.Template()), exactly));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static JsonNode? Comparable(RouterConfiguration configuration, bool exactly)
    {
        JsonNode? json = JsonNode.Parse(configuration.ToJson());
        if (json is JsonObject document && !exactly)
        {
            document.Remove(nameof(RouterConfiguration.Embeddings));
            document.Remove(nameof(RouterConfiguration.DefaultSimilarityThreshold));
        }

        return json;
    }

    /// <summary>
    /// With <see cref="HostSettings.SeedVariable"/> on (the container image sets it), adds a connector for each of
    /// <c>OPENAI_API_KEY</c> and <c>ANTHROPIC_API_KEY</c> that is set. The key stays in the environment: the connector
    /// refers to it (<c>${env:OPENAI_API_KEY}</c>). Models are left to the dashboard's Discover models, which shows what
    /// the key can use and what each model costs. Returns the names of the connectors added.
    /// </summary>
    public static IReadOnlyList<string> SeedFromEnvironment(RouterConfiguration document, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment(HostSettings.SeedVariable)?.Trim().ToUpperInvariant() is not ("1" or "TRUE" or "YES"))
        {
            return [];
        }

        var added = new List<string>();
        foreach ((string variable, string connector, ConnectorType type, Uri address) in ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(environment(variable)) || document.Connectors.ContainsKey(connector))
            {
                continue;
            }

            document.Connectors[connector] = new ConnectorOptions { Type = type, BaseAddress = address, ApiKey = SecretReference.ForEnvironment(variable) };
            added.Add(connector);
        }

        return added;
    }

    /// <summary>The line the router prints when it wrote the template (<paramref name="created"/>) or seeded connectors into it.</summary>
    public static string Describe(string configurationFile, bool created, IReadOnlyList<string> seeded)
    {
        string what = created ? $"Created {configurationFile} from the built-in template" : $"Updated {configurationFile}, which still held the built-in template";
        if (seeded.Count == 0)
        {
            return $"{what}: no models yet. Run `semantic-router init` to add the ones on this machine, or set them up on the dashboard.";
        }

        string variables = string.Join(" and ", seeded.Select(c => ApiKeys.First(k => k.Connector == c).Variable));
        return $"{what}, with the {string.Join(" and ", seeded)} connector{(seeded.Count == 1 ? string.Empty : "s")} for {variables} " +
               $"(the key stays in the environment). Choose {(seeded.Count == 1 ? "its" : "their")} models on the dashboard with Discover models.";
    }
}
