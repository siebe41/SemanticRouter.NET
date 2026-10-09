using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>Where the local model-family table is (<c>UseModelFamilyFile</c>).</summary>
/// <param name="Path">The file.</param>
public sealed record ModelFamilyFile(string Path);

/// <summary>A size band of a <see cref="ModelFamily"/>: models up to <see cref="UpTo"/> billion parameters get <see cref="Tier"/>.</summary>
public sealed class ModelFamilyBand
{
    /// <summary>The largest size in the band, in billions of parameters; <see langword="null"/> for every larger model.</summary>
    public double? UpTo { get; set; }

    /// <summary>The tier models in the band get.</summary>
    public int Tier { get; set; }
}

/// <summary>
/// What is known about a model family, for discovery: its tier (fixed, by size, or relative to the size rule), the
/// capabilities it has, and notes (<see cref="ModelFamilyTable"/>).
/// </summary>
public sealed class ModelFamily
{
    /// <summary>The family's name, e.g. <c>Qwen3 Coder</c>. A local table entry with the same name replaces the built-in one.</summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>
    /// Patterns matched against the model id, lower case, anywhere in it; <c>*</c> stands for anything. The longest
    /// matching pattern of all families wins, so <c>qwen3-coder</c> beats <c>qwen3</c>.
    /// </summary>
    public IList<string> Match { get; } = new List<string>();

    /// <summary>A fixed tier, whatever the size (typical for hosted models, whose size is not published).</summary>
    public int? Tier { get; set; }

    /// <summary>Tiers by size, smallest band first. Used when the size is known and <see cref="Tier"/> is not set.</summary>
    public IList<ModelFamilyBand> Bands { get; } = new List<ModelFamilyBand>();

    /// <summary>Added to the tier the size rule gives, when neither <see cref="Tier"/> nor <see cref="Bands"/> decide (at least 1).</summary>
    public int Adjust { get; set; }

    /// <summary>Capabilities the family has (<c>code</c>, <c>tools</c>, <c>vision</c>, <c>reasoning</c>).</summary>
    public IList<string> Capabilities { get; } = new List<string>();

    /// <summary>Capabilities it does not have, even when the server reports them.</summary>
    public IList<string> Without { get; } = new List<string>();

    /// <summary>The smallest size, in billions of parameters, at which its tool calls are reliable; smaller models get no <c>tools</c>.</summary>
    public double? ToolsFrom { get; set; }

    /// <summary>Anything worth knowing, shown with the suggestion.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// The model-family table discovery uses before the size rule: tiers and capabilities per family (Qwen, Llama,
/// Mistral, DeepSeek, Gemma, Phi, gpt-oss, Gemini, …). A versioned table is built in; a <see cref="FileName"/> file in
/// the configuration directory adds families or replaces built-in ones by name, without a code change.
/// </summary>
public sealed class ModelFamilyTable
{
    /// <summary>The local table's file name, next to the configuration file.</summary>
    public const string FileName = "model-families.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
    };

    private readonly (ModelFamily Family, Regex Pattern, int Length)[] _patterns;

    private ModelFamilyTable(int version, IReadOnlyList<ModelFamily> families, string source, string? warning)
    {
        Version = version;
        Families = families;
        Source = source;
        Warning = warning;
        _patterns = [.. families.SelectMany(f => f.Match.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => (f, Compile(m), m.Replace("*", string.Empty, StringComparison.Ordinal).Length)))];
    }

    /// <summary>The built-in table, as shipped.</summary>
    public static ModelFamilyTable BuiltIn { get; } = LoadBuiltIn();

    /// <summary>The built-in table's version.</summary>
    public int Version { get; }

    /// <summary>Every family, local ones first.</summary>
    public IReadOnlyList<ModelFamily> Families { get; }

    /// <summary>Where the table came from: <c>built-in v1</c>, or that plus the local file.</summary>
    public string Source { get; }

    /// <summary>Why the local file was not (fully) used, when it was not.</summary>
    public string? Warning { get; }

    /// <summary>The local table's path next to a configuration file.</summary>
    public static string FileNextTo(string configurationFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!, FileName);
    }

    /// <summary>The built-in table with the local file's families on top, when <paramref name="localFile"/> exists.</summary>
    public static ModelFamilyTable Load(string? localFile)
    {
        if (localFile is null || !File.Exists(localFile))
        {
            return BuiltIn;
        }

        try
        {
            (int _, List<ModelFamily> local, List<string> problems) = Parse(File.ReadAllText(localFile));
            var names = new HashSet<string>(local.Select(f => f.Family), StringComparer.OrdinalIgnoreCase);
            ModelFamily[] families = [.. local, .. BuiltIn.Families.Where(f => !names.Contains(f.Family))];
            return new ModelFamilyTable(
                BuiltIn.Version,
                families,
                $"{BuiltIn.Source} + {localFile} ({local.Count} {(local.Count == 1 ? "family" : "families")})",
                problems.Count > 0 ? $"{localFile}: {string.Join("; ", problems)}" : null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ModelFamilyTable(BuiltIn.Version, BuiltIn.Families, BuiltIn.Source, $"{localFile} could not be read ({ex.Message}); using the built-in table.");
        }
    }

    /// <summary>The family a model id belongs to (the longest matching pattern), or <see langword="null"/>.</summary>
    public ModelFamily? Find(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        string id = modelId.ToLowerInvariant();
        ModelFamily? best = null;
        int longest = -1;
        foreach ((ModelFamily family, Regex pattern, int length) in _patterns)
        {
            if (length > longest && pattern.IsMatch(id))
            {
                best = family;
                longest = length;
            }
        }

        return best;
    }

    private static ModelFamilyTable LoadBuiltIn()
    {
        using Stream stream = typeof(ModelFamilyTable).Assembly.GetManifestResourceStream("SemanticRouter.AspNetCore.model-families.json")
            ?? throw new InvalidOperationException("The built-in model-family table is missing.");
        using var reader = new StreamReader(stream);
        (int version, List<ModelFamily> families, List<string> problems) = Parse(reader.ReadToEnd());
        return problems.Count > 0
            ? throw new InvalidOperationException("The built-in model-family table is invalid: " + string.Join("; ", problems))
            : new ModelFamilyTable(version, families, $"built-in v{version}", null);
    }

    private static (int Version, List<ModelFamily> Families, List<string> Problems) Parse(string json)
    {
        TableDocument document = JsonSerializer.Deserialize<TableDocument>(json, Json) ?? new TableDocument();
        var families = new List<ModelFamily>();
        var problems = new List<string>();
        foreach (ModelFamily family in document.Families)
        {
            string name = string.IsNullOrWhiteSpace(family.Family) ? "(unnamed)" : family.Family;
            if (string.IsNullOrWhiteSpace(family.Family) || !family.Match.Any(m => !string.IsNullOrWhiteSpace(m)))
            {
                problems.Add($"family {name} needs a name and at least one match pattern; skipped");
            }
            else if (family.Tier is < 0 || family.Bands.Any(b => b.Tier < 0))
            {
                problems.Add($"family {name} has a negative tier; skipped");
            }
            else
            {
                families.Add(family);
            }
        }

        return (document.Version, families, problems);
    }

    private static Regex Compile(string pattern) =>
        new(string.Join(".*", pattern.Trim().ToLowerInvariant().Split('*').Select(Regex.Escape)), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private sealed class TableDocument
    {
        public int Version { get; set; }

        public List<ModelFamily> Families { get; set; } = [];
    }
}
