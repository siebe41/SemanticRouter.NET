using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>A problem with a configuration document, tied to the field it is about.</summary>
/// <param name="Path">
/// The field, as the dashboard's <c>data-bind</c> attributes name it: property names as in the configuration JSON,
/// dot-separated, with array indexes and dictionary keys (<c>Routes.1.Name</c>, <c>Connectors.local.Timeout</c>,
/// <c>Middleware.RateLimits.*.RequestsPerMinute</c>). <see langword="null"/> when the problem has no single field.
/// </param>
/// <param name="Message">What is wrong and how to fix it.</param>
public sealed record ConfigurationIssue(string? Path, string Message);

/// <summary>
/// Ties validation messages to the field they are about, so the dashboard can show each one next to its input, and
/// turns a document that does not parse into a message that names the field and the expected format.
/// </summary>
internal static partial class ConfigurationIssues
{
    private static readonly HashSet<string> RootProperties = PropertyNames(typeof(RouterConfiguration));
    private static readonly HashSet<string> ComplexityProperties = PropertyNames(typeof(ComplexityHeuristicsOptions));

    /// <summary>The field of each error in <paramref name="errors"/>, worked out against <paramref name="document"/>.</summary>
    public static IReadOnlyList<ConfigurationIssue> Locate(IEnumerable<string> errors, RouterConfiguration document) =>
        [.. errors.Select(e => new ConfigurationIssue(PathOf(e, document), e))];

    /// <summary>The field a validation message is about, or <see langword="null"/>.</summary>
    internal static string? PathOf(string error, RouterConfiguration document)
    {
        // Machine-policy findings say what the policy would turn off, about a model or a section.
        if (error.StartsWith("Not allowed: ", StringComparison.Ordinal))
        {
            string inner = error["Not allowed: ".Length..];
            return inner.Contains(" is turned off", StringComparison.Ordinal) && NamedPath(inner, document) is { } model
                ? model.Split('.')[0] + "." + model.Split('.')[1] + ".Enabled"
                : inner.StartsWith("Prompt previews", StringComparison.Ordinal) ? "State.StorePrompts"
                : inner.StartsWith("Activity is kept", StringComparison.Ordinal) ? "State.Retention.ActivityDays"
                : inner.StartsWith("The embeddings endpoint", StringComparison.Ordinal) ? "Embeddings.Endpoint"
                : PathOf(inner, document);
        }

        if (IndexedName().Match(error) is { Success: true } indexed)
        {
            // "Routes[2] must have a Name."
            return $"{indexed.Groups["list"].Value}.{indexed.Groups["index"].Value}.{indexed.Groups["property"].Value}";
        }

        if (NamedPath(error, document) is { } named)
        {
            return named;
        }

        if (ColonPath().Match(error) is { Success: true } colon)
        {
            return FromColonPath(colon.Groups["path"].Value, error, document);
        }

        if (DotPath().Match(error) is { Success: true } dot)
        {
            // "Selection.MaxAttempts must be at least 1.", "Complexity.CodeWeight must be between 0 and 1."
            return dot.Groups["path"].Value;
        }

        if (LeadingWord().Match(error) is { Success: true } word)
        {
            string name = word.Groups["word"].Value;
            if (name == nameof(SemanticRouterOptions.ClassificationCache))
            {
                return document.ClassificationCache.MaxEntries < 1 ? "ClassificationCache.MaxEntries" : "ClassificationCache.MaxAge";
            }

            if (name == nameof(SemanticRouterOptions.ComplexityTiers))
            {
                int bad = (document.ComplexityTiers ?? []).ToList().FindIndex(r => r.MinScore is < 0 or > 1 || double.IsNaN(r.MinScore) || r.Tier < 0);
                return bad < 0 ? "ComplexityTiers" : $"ComplexityTiers.{bad}.{(document.ComplexityTiers![bad].Tier < 0 ? "Tier" : "MinScore")}";
            }

            if (name == "Selection" && error.StartsWith("Selection cooldowns", StringComparison.Ordinal))
            {
                return document.Selection.QuotaCooldown < TimeSpan.Zero ? "Selection.QuotaCooldown" : "Selection.FailureCooldown";
            }

            if (name == "Selection" && error.StartsWith("Selection TimeZone", StringComparison.Ordinal))
            {
                return "Selection.TimeZone";
            }

            if (ComplexityProperties.Contains(name) && !RootProperties.Contains(name))
            {
                return "Complexity." + name;
            }

            if (RootProperties.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Messages about a named item: <c>Connector 'x' …</c>, <c>Route 'x' …</c>, <c>Budget 'x' …</c>, ….</summary>
    private static string? NamedPath(string error, RouterConfiguration document)
    {
        if (DuplicateName().Match(error) is { Success: true } duplicate)
        {
            // "Route name 'x' is used more than once": the second one is the one to rename.
            string name = duplicate.Groups["name"].Value;
            return duplicate.Groups["kind"].Value == "Route"
                ? LastIndex(document.Routes.Select(r => r.Name), name) is int r and >= 0 ? $"Routes.{r}.Name" : null
                : LastIndex(document.Models.Select(m => m.Name), name) is int m and >= 0 ? $"Models.{m}.Name" : null;
        }

        if (DuplicateTier().Match(error) is { Success: true } tier)
        {
            int level = int.Parse(tier.Groups["level"].Value, CultureInfo.InvariantCulture);
            int index = LastIndex((document.Tiers ?? []).Select(t => t.Level.ToString(CultureInfo.InvariantCulture)), level.ToString(CultureInfo.InvariantCulture));
            return index < 0 ? "Tiers" : $"Tiers.{index}.Level";
        }

        if (error.StartsWith("A budget ", StringComparison.Ordinal))
        {
            int unnamed = document.Usage.Budgets.ToList().FindIndex(b => string.IsNullOrWhiteSpace(b.Name));
            return unnamed < 0 ? "Usage.Budgets" : $"Usage.Budgets.{unnamed}.{BudgetProperty(error, document.Usage.Budgets[unnamed])}";
        }

        if (error.StartsWith("Profile names must not be empty", StringComparison.Ordinal) && Quoted().Matches(error) is { Count: > 0 } quoted)
        {
            // "… or contain '@' ('work@home')": the name is the last quoted text, and an empty one has no field of its own.
            string badProfile = quoted[^1].Groups["name"].Value;
            return badProfile.Length > 0 ? $"Profiles.{badProfile}" : "Profiles";
        }

        if (error.StartsWith("Middleware:ProfileRules name profile", StringComparison.Ordinal) && Quoted().Match(error) is { Success: true } ruleProfile)
        {
            int rule = document.Middleware.ProfileRules.ToList().FindIndex(r => string.Equals(r.Profile, ruleProfile.Groups["name"].Value, StringComparison.OrdinalIgnoreCase));
            return rule < 0 ? "Middleware.ProfileRules" : $"Middleware.ProfileRules.{rule}.Profile";
        }

        if (NamedItem().Match(error) is not { Success: true } item)
        {
            return null;
        }

        string kind = item.Groups["kind"].Value, key = item.Groups["name"].Value, rest = item.Groups["rest"].Value;
        switch (kind)
        {
            case "Connector":
                return $"Connectors.{key}" + (FirstProperty(rest, typeof(ConnectorOptions)) is { } connectorProperty ? "." + connectorProperty : string.Empty);

            case "Route":
            {
                int index = document.Routes.ToList().FindIndex(r => r.Name == key);
                string? property = rest.Contains("utterance", StringComparison.Ordinal) ? nameof(RouteDefinition.Utterances) : FirstProperty(rest, typeof(RouteDefinition));
                return index < 0 ? null : $"Routes.{index}" + (property is null ? string.Empty : "." + property);
            }

            case "Model":
            {
                int index = document.Models.ToList().FindIndex(m => m.Name == key);
                if (index < 0)
                {
                    return null;
                }

                ModelDefinition model = document.Models[index];
                string? property = rest.Contains("negative tier, context window or cost", StringComparison.Ordinal)
                    ? model.Tier < 0 ? nameof(ModelDefinition.Tier)
                    : model.ContextWindow < 0 ? nameof(ModelDefinition.ContextWindow)
                    : model.InputCostPerMillionTokens < 0 ? nameof(ModelDefinition.InputCostPerMillionTokens)
                    : model.OutputCostPerMillionTokens < 0 ? nameof(ModelDefinition.OutputCostPerMillionTokens)
                    : null
                    : rest.Contains("uses connector", StringComparison.Ordinal) ? nameof(ModelDefinition.Connector)
                    : FirstProperty(rest, typeof(ModelDefinition));
                return $"Models.{index}" + (property is null ? string.Empty : "." + property);
            }

            case "Profile":
            {
                RoutingProfile? profile = document.Profiles.TryGetValue(key, out RoutingProfile? found) ? found : null;
                string? property = rest.StartsWith(" names model", StringComparison.Ordinal) && Quoted().Match(rest) is { Success: true } m
                    ? profile?.DeniedModels.Contains(m.Groups["name"].Value) == true ? nameof(RoutingProfile.DeniedModels) : nameof(RoutingProfile.AllowedModels)
                    : rest.StartsWith(" names connector", StringComparison.Ordinal) && Quoted().Match(rest) is { Success: true } c
                        ? profile?.DeniedConnectors.Contains(c.Groups["name"].Value) == true ? nameof(RoutingProfile.DeniedConnectors) : nameof(RoutingProfile.AllowedConnectors)
                    : rest.Contains("strategy", StringComparison.Ordinal) ? nameof(RoutingProfile.Strategy)
                    : FirstProperty(rest, typeof(RoutingProfile));
                return $"Profiles.{key}" + (property is null ? string.Empty : "." + property);
            }

            case "Tier":
            {
                int index = (document.Tiers ?? []).ToList().FindIndex(t => t.Name == key);
                return index < 0 ? "Tiers" : $"Tiers.{index}.Level";
            }

            case "Budget":
            {
                int index = document.Usage.Budgets.ToList().FindIndex(b => b.Name == key);
                return index < 0 ? "Usage.Budgets" : $"Usage.Budgets.{index}.{BudgetProperty(rest, document.Usage.Budgets[index])}";
            }

            case "Memory source":
            {
                int index = rest.Contains("more than once", StringComparison.Ordinal)
                    ? LastIndex(document.Memory.Sources.Select(s => s.Name), key)
                    : document.Memory.Sources.ToList().FindIndex(s => s.Name == key);
                return index < 0 ? "Memory.Sources" : $"Memory.Sources.{index}.{(rest.Contains("Path", StringComparison.Ordinal) ? "Path" : "Name")}";
            }

            case "Virtual model":
                return $"Middleware.VirtualModels.{key}" + (rest.Contains("names profile", StringComparison.Ordinal) ? ".Profile" : string.Empty);

            default:
                return null;
        }
    }

    /// <summary><c>Connectors:x:BaseAddress</c>, <c>Memory:Sources:notes:Path</c>, <c>Middleware:RateLimits:*</c>, … as a field path.</summary>
    private static string FromColonPath(string path, string error, RouterConfiguration document)
    {
        string[] parts = path.Split(':');
        if (parts is ["Memory", "Sources", var source, ..])
        {
            // Memory sources are a list in the document, named in messages.
            int index = document.Memory.Sources.ToList().FindIndex(s => string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase));
            parts = [.. parts.Take(2), index < 0 ? source : index.ToString(CultureInfo.InvariantCulture), .. parts.Skip(3)];
        }

        if (parts is ["Middleware", "RateLimits", var client] && document.Middleware.RateLimits.TryGetValue(client, out RateLimitOptions? limit))
        {
            // "…:client needs RequestsPerMinute, TokensPerMinute or both" / "…:client limits must be at least 1".
            parts = [.. parts, limit.TokensPerMinute is < 1 && limit.RequestsPerMinute is not < 1 ? "TokensPerMinute" : "RequestsPerMinute"];
        }

        if (parts.Length == 2 && error.Length > path.Length && error[path.Length] == ' ' && parts[1] is "Budgets" or "Sources" && error.Contains("entries must have a Name", StringComparison.Ordinal))
        {
            int unnamed = parts[1] == "Budgets"
                ? document.Usage.Budgets.ToList().FindIndex(b => string.IsNullOrWhiteSpace(b.Name))
                : document.Memory.Sources.ToList().FindIndex(s => string.IsNullOrWhiteSpace(s.Name));
            if (unnamed >= 0)
            {
                parts = [.. parts, unnamed.ToString(CultureInfo.InvariantCulture), "Name"];
            }
        }

        return string.Join('.', parts);
    }

    private static string BudgetProperty(string message, BudgetDefinition budget) =>
        message.Contains("covers profile", StringComparison.Ordinal) ? nameof(BudgetDefinition.Profile)
        : message.Contains("covers connector", StringComparison.Ordinal) ? nameof(BudgetDefinition.Connector)
        : message.Contains("limits must not be negative", StringComparison.Ordinal) ? (budget.MaxTokens < 0 ? nameof(BudgetDefinition.MaxTokens) : nameof(BudgetDefinition.MaxCostUsd))
        : message.Contains("needs MaxTokens", StringComparison.Ordinal) ? nameof(BudgetDefinition.MaxTokens)
        : message.Contains("must have a Name", StringComparison.Ordinal) ? nameof(BudgetDefinition.Name)
        : nameof(BudgetDefinition.Model);

    /// <summary>The first word of <paramref name="text"/> that is a property of <paramref name="type"/>.</summary>
    private static string? FirstProperty(string text, Type type)
    {
        HashSet<string> names = PropertyNames(type);
        return PascalWord().Matches(text).Select(m => m.Value).FirstOrDefault(names.Contains);
    }

    private static int LastIndex(IEnumerable<string?> values, string value)
    {
        int found = -1, i = 0;
        foreach (string? candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                found = i;
            }

            i++;
        }

        return found;
    }

    private static HashSet<string> PropertyNames(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)];

    /// <summary>
    /// The error and issue for a document whose JSON does not parse or whose values do not fit their fields, e.g.
    /// <c>Connectors.local.Timeout: "5m" is not a duration; use hh:mm:ss, e.g. 00:05:00</c>.
    /// </summary>
    /// <param name="exception">What the parser threw.</param>
    /// <param name="document">The parsed JSON, to quote the value; <see langword="null"/> when the text is not JSON at all.</param>
    /// <param name="pathPrefix">Prepended to the field path, e.g. <c>Connectors.local</c> when only a connector was sent.</param>
    public static ConfigurationIssue FromJsonException(JsonException exception, JsonNode? document, string? pathPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (document is null || exception.Path is not { Length: > 1 } jsonPath)
        {
            string where = exception.LineNumber is { } line ? $" (line {line + 1}, position {(exception.BytePositionInLine ?? 0) + 1})" : string.Empty;
            return new ConfigurationIssue(null, $"The configuration is not valid JSON{where}. Check for a missing comma, quote or bracket.");
        }

        string[] segments = Segments(jsonPath);
        string[] canonical = Canonical(segments);
        string path = string.Join('.', pathPrefix is null ? canonical : [pathPrefix, .. canonical]);
        JsonNode? value = document;
        foreach (string segment in segments)
        {
            value = value switch
            {
                JsonObject obj => obj.FirstOrDefault(p => string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < array.Count => array[i],
                _ => null,
            };
        }

        string shown = value is null ? "This value" : Clip(value.ToJsonString());
        string expected = ExpectedType(exception.Message) switch
        {
            { } t when t == typeof(TimeSpan) => $"{shown} is not a duration; use hh:mm:ss, e.g. 00:05:00",
            { } t when t == typeof(int) || t == typeof(long) || t == typeof(short) => $"{shown} is not a whole number",
            { } t when t == typeof(float) || t == typeof(double) || t == typeof(decimal) => $"{shown} is not a number; write it without quotes, e.g. 0.5",
            { } t when t == typeof(bool) => $"{shown} is not true or false",
            { } t when t == typeof(string) => $"{shown} should be text in quotes",
            { } t when t == typeof(Uri) => $"{shown} is not an address; use one like http://localhost:1234",
            { IsEnum: true } t => $"{shown} is not one of: {string.Join(", ", Enum.GetNames(t))}",
            { } t when typeof(System.Collections.IDictionary).IsAssignableFrom(t) || t.Name.StartsWith("IDictionary", StringComparison.Ordinal) => $"{shown} should be an object of names and values",
            { } t when t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t) => $"{shown} should be a list in [ ]",
            _ => $"{shown} is not valid here",
        };
        return new ConfigurationIssue(path, expected);
    }

    /// <summary>The type the parser expected, from its message ("… could not be converted to System.TimeSpan. Path: …").</summary>
    private static Type? ExpectedType(string message)
    {
        if (ConvertedTo().Match(message) is not { Success: true } match)
        {
            return null;
        }

        string name = match.Groups["type"].Value;
        if (NullableOf().Match(name) is { Success: true } nullable)
        {
            name = nullable.Groups["inner"].Value;
        }

        int generic = name.IndexOf('`', StringComparison.Ordinal);
        if (generic > 0)
        {
            // Collections: the open generic is enough to tell a list from a dictionary.
            string open = name[..(generic + 2)];
            return Type.GetType(open) ?? typeof(object).Assembly.GetType(open);
        }

        // Type.GetType without an assembly name looks only in this assembly and CoreLib; Uri is in neither.
        return Type.GetType(name)
               ?? typeof(Uri).Assembly.GetType(name)
               ?? typeof(RouterConfiguration).Assembly.GetType(name)
               ?? typeof(SemanticRouterOptions).Assembly.GetType(name);
    }

    /// <summary><c>$.Routes[1].Name</c> or <c>$['Connectors']['my box'].Timeout</c> as its segments.</summary>
    private static string[] Segments(string jsonPath) =>
        [.. PathSegment().Matches(jsonPath).Select(m => m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["index"].Value)];

    /// <summary>
    /// The path with property names spelt as the document's properties are (the parser matches them ignoring case), so
    /// <c>connectors.local.timeout</c> names the field <c>Connectors.local.Timeout</c>. Dictionary keys and indexes stay.
    /// </summary>
    private static string[] Canonical(string[] segments)
    {
        var canonical = new string[segments.Length];
        Type? type = typeof(RouterConfiguration);
        for (int i = 0; i < segments.Length; i++)
        {
            canonical[i] = segments[i];
            if (type is null)
            {
                continue;
            }

            Type? item = ItemType(type);
            if (item is not null)
            {
                type = item;
                continue;
            }

            PropertyInfo? property = type.GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            canonical[i] = property?.Name ?? segments[i];
            type = property is null ? null : Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        }

        return canonical;

        // The value type of a dictionary or the element type of a list; null for anything else (strings included).
        static Type? ItemType(Type type)
        {
            if (type == typeof(string))
            {
                return null;
            }

            Type[] interfaces = type.IsInterface ? [type, .. type.GetInterfaces()] : type.GetInterfaces();
            Type? dictionary = interfaces.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionary is not null)
            {
                return dictionary.GetGenericArguments()[1];
            }

            Type? list = interfaces.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return list?.GetGenericArguments()[0];
        }
    }

    private static string Clip(string json) => json.Length <= 60 ? json : json[..57] + "…";

    [GeneratedRegex(@"^(?<list>Routes|Models)\[(?<index>\d+)\] must have an? (?<property>[A-Za-z]+)")]
    private static partial Regex IndexedName();

    [GeneratedRegex(@"^(?<kind>Route|Model) name '(?<name>.*)' is used more than once")]
    private static partial Regex DuplicateName();

    [GeneratedRegex(@"^Tier level (?<level>-?\d+) is defined more than once")]
    private static partial Regex DuplicateTier();

    [GeneratedRegex(@"^(?<kind>Connector|Route|Model|Profile|Tier|Budget|Memory source|Virtual model) '(?<name>[^']*)'(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex NamedItem();

    [GeneratedRegex(@"'(?<name>[^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^(?<path>[A-Z][A-Za-z]*(?::[^\s:]+)+)(?=[:\s.]|$)")]
    private static partial Regex ColonPath();

    [GeneratedRegex(@"^(?<path>[A-Z][A-Za-z]*(?:\.[A-Z][A-Za-z]*)+)(?=\s)")]
    private static partial Regex DotPath();

    [GeneratedRegex(@"^(?<word>[A-Z][A-Za-z]+)\b")]
    private static partial Regex LeadingWord();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]*)*\b")]
    private static partial Regex PascalWord();

    [GeneratedRegex(@"could not be converted to (?<type>\S+?)\.? Path:")]
    private static partial Regex ConvertedTo();

    [GeneratedRegex(@"^System\.Nullable`1\[(?<inner>[^\]]+)\]$")]
    private static partial Regex NullableOf();

    [GeneratedRegex(@"\.(?<name>[^.\[\]']+)|\['(?<quoted>(?:[^']|'')*)'\]|\[(?<index>\d+)\]")]
    private static partial Regex PathSegment();
}
