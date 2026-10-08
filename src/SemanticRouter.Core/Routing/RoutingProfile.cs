using System.Globalization;

namespace SemanticRouter;

/// <summary>
/// A named routing policy (<see cref="SemanticRouterOptions.Profiles"/>): which models a context may use, how aggressive
/// routing is, and whether prompts must stay on this machine. A profile only ever narrows: it removes models and raises
/// requirements, it never adds a model the catalog does not have.
/// </summary>
public sealed class RoutingProfile
{
    /// <summary>When not empty, only these catalog models may serve (names, <c>*</c> globs allowed).</summary>
    public IList<string> AllowedModels { get; } = new List<string>();

    /// <summary>These catalog models never serve (names, <c>*</c> globs allowed).</summary>
    public IList<string> DeniedModels { get; } = new List<string>();

    /// <summary>When not empty, only models on these connectors may serve.</summary>
    public IList<string> AllowedConnectors { get; } = new List<string>();

    /// <summary>Models on these connectors never serve.</summary>
    public IList<string> DeniedConnectors { get; } = new List<string>();

    /// <summary>Overrides <see cref="ModelSelectionOptions.Strategy"/> (a strategy the client asks for still wins).</summary>
    public SelectionStrategy? Strategy { get; set; }

    /// <summary>Raises the required tier to at least this.</summary>
    public int? MinTier { get; set; }

    /// <summary>Lowers the required tier to at most this, so cheaper models are acceptable for every prompt.</summary>
    public int? MaxTier { get; set; }

    /// <summary>Only <see cref="ModelDefinition.IsLocal"/> models serve, including on failover.</summary>
    public bool LocalOnly { get; set; }

    /// <summary>Free-form description for the dashboard.</summary>
    public string? Description { get; set; }

    /// <summary>Whether <paramref name="model"/> may serve requests under this profile.</summary>
    public bool Allows(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return (!LocalOnly || model.IsLocal)
               && (AllowedModels.Count == 0 || AllowedModels.Any(p => Glob.IsMatch(p, model.Name)))
               && !DeniedModels.Any(p => Glob.IsMatch(p, model.Name))
               && (AllowedConnectors.Count == 0 || AllowedConnectors.Any(c => string.Equals(c, model.Connector, StringComparison.OrdinalIgnoreCase)))
               && !DeniedConnectors.Any(c => string.Equals(c, model.Connector, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tier after this profile's floor and ceiling.</summary>
    public int ClampTier(int tier)
    {
        if (MaxTier is { } max && tier > max)
        {
            tier = max;
        }

        return MinTier is { } min && tier < min ? min : tier;
    }
}

/// <summary>Case-insensitive <c>*</c> globs, as used by model aliases and profiles.</summary>
public static class Glob
{
    /// <summary>Whether <paramref name="value"/> matches <paramref name="pattern"/>; <c>*</c> matches any run of characters.</summary>
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);
        string[] parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        if (!value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) || !value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase)
            || value.Length < parts[0].Length + parts[^1].Length)
        {
            return false;
        }

        int position = parts[0].Length;
        int end = value.Length - parts[^1].Length;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            int found = value.IndexOf(parts[i], position, end - position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            position = found + parts[i].Length;
        }

        return true;
    }
}

/// <summary>A daily time window such as <c>08:00-18:00</c>, or <c>22:00-06:00</c> across midnight.</summary>
public readonly record struct TimeWindow(TimeOnly Start, TimeOnly End)
{
    /// <summary>Parses <c>HH:mm-HH:mm</c>.</summary>
    public static bool TryParse(string? text, out TimeWindow window)
    {
        window = default;
        string[] parts = (text ?? string.Empty).Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && TimeOnly.TryParseExact(parts[0], ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly start)
            && TimeOnly.TryParseExact(parts[1], ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly end)
            && start != end)
        {
            window = new TimeWindow(start, end);
            return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="time"/> falls inside the window (start inclusive, end exclusive).</summary>
    public bool Contains(TimeOnly time) => Start < End ? time >= Start && time < End : time >= Start || time < End;
}
