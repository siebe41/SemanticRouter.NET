using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// References to secrets inside configuration values, so the configuration file never has to hold the secret itself:
/// <c>${env:OPENAI_API_KEY}</c> reads an environment variable, <c>${secret:connectors/openai/ApiKey}</c> reads the
/// registered <see cref="IRouterSecretStore"/>. A reference can stand alone or sit inside a value
/// (<c>"Bearer ${env:TOKEN}"</c>). References are resolved each time a configuration is applied.
/// </summary>
public static partial class SecretReference
{
    /// <summary>The prefix of an environment variable reference.</summary>
    public const string EnvironmentScheme = "env";

    /// <summary>The prefix of a secret store reference.</summary>
    public const string StoreScheme = "secret";

    /// <summary>Whether <paramref name="value"/> contains at least one reference.</summary>
    public static bool ContainsReference(string? value) => !string.IsNullOrEmpty(value) && Pattern().IsMatch(value);

    /// <summary>A reference to an environment variable.</summary>
    public static string ForEnvironment(string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        return "${" + EnvironmentScheme + ":" + variable + "}";
    }

    /// <summary>A reference to an entry of the secret store.</summary>
    public static string ForStore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "${" + StoreScheme + ":" + name + "}";
    }

    /// <summary>
    /// Replaces every reference in <paramref name="value"/> with what it points at. A reference that cannot be resolved
    /// (unset variable, missing store entry, no store registered) becomes an empty string and is described in
    /// <paramref name="problems"/>, which names <paramref name="setting"/> so the message says what to fix.
    /// </summary>
    public static string? Resolve(string? value, IRouterSecretStore? store, string setting, ICollection<string>? problems = null)
    {
        if (!ContainsReference(value))
        {
            return value;
        }

        return Pattern().Replace(value!, match =>
        {
            string scheme = match.Groups["scheme"].Value;
            string name = match.Groups["name"].Value.Trim();
            string? resolved;
            string? problem = null;
            if (string.Equals(scheme, EnvironmentScheme, StringComparison.OrdinalIgnoreCase))
            {
                resolved = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(resolved))
                {
                    problem = $"{setting} refers to the environment variable {name}, which is not set.";
                }
            }
            else if (store is null)
            {
                resolved = null;
                problem = $"{setting} refers to the secret store entry '{name}', but no secret store is registered (see SemanticRouterBuilder.UseSecretFile).";
            }
            else
            {
                resolved = store.GetSecret(name);
                if (string.IsNullOrEmpty(resolved))
                {
                    problem = $"{setting} refers to the secret store entry '{name}', which {store.Location} does not hold.";
                }
            }

            if (problem is not null)
            {
                problems?.Add(problem);
            }

            return resolved ?? string.Empty;
        });
    }

    /// <summary>The references in <paramref name="value"/>: scheme (<c>env</c> or <c>secret</c>, lower case) and name.</summary>
    internal static IEnumerable<(string Scheme, string Name)> References(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : Pattern().Matches(value).Select(m => (m.Groups["scheme"].Value.ToLowerInvariant(), m.Groups["name"].Value));

    [GeneratedRegex(@"\$\{(?<scheme>env|secret):(?<name>[^}]+)\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
