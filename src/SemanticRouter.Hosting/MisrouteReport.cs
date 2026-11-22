using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SemanticRouter.Host;

/// <summary>What the reporter says went wrong, from the form's first question.</summary>
internal enum MisrouteDirection
{
    /// <summary>Not answered, or not one of the form's options.</summary>
    Unknown = 0,

    /// <summary>A stronger model than needed.</summary>
    TooHigh = 1,

    /// <summary>A weaker model than needed.</summary>
    TooLow = 2,

    /// <summary>The right tier, but the wrong route.</summary>
    WrongRoute = 3,

    /// <summary>Something else.</summary>
    Other = 4,
}

/// <summary>
/// A <b>Routed to the wrong tier</b> issue (<c>.github/ISSUE_TEMPLATE/misroute.yml</c>), as GitHub renders the form's
/// answers in the issue body: one <c>### Label</c> heading per field, then its value (a <c>render: text</c> field inside
/// a code fence, an unanswered one as <c>_No response_</c>). Anyone can file an issue, so the body is untrusted: it is
/// only ever read as data, every field is cut to a length limit, and control and invisible formatting characters are
/// dropped. Nothing here is written back without <see cref="UntrustedText"/>.
/// </summary>
internal sealed partial record MisrouteReport
{
    /// <summary>The longest body read; GitHub allows 65,536 characters.</summary>
    public const int MaxBodyLength = 65_536;

    /// <summary>The longest prompt or paraphrase kept.</summary>
    public const int MaxPromptLength = 4_000;

    /// <summary>The longest decision kept.</summary>
    public const int MaxDecisionLength = 4_000;

    /// <summary>The longest of any other field kept.</summary>
    public const int MaxFieldLength = 500;

    // The form's labels (misroute.yml), by field id. MisrouteTriageTests checks them against the form.
    public const string DirectionLabel = "What went wrong";
    public const string DecisionLabel = "The router's decision";
    public const string AnsweredByLabel = "Model that answered";
    public const string ExpectedLabel = "What the prompt really needed";
    public const string EmbeddingsLabel = "Embedding provider, model and threshold";
    public const string ClientLabel = "Client";
    public const string PromptLabel = "Prompt, paraphrase or description";
    public const string VersionLabel = "Router version";

    /// <summary>The form's labels, by the field id the form gives them.</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["direction"] = DirectionLabel,
        ["decision"] = DecisionLabel,
        ["answered-by"] = AnsweredByLabel,
        ["expected"] = ExpectedLabel,
        ["embeddings"] = EmbeddingsLabel,
        ["client"] = ClientLabel,
        ["prompt"] = PromptLabel,
        ["version"] = VersionLabel,
    };

    /// <summary>The fields the form requires (all but the prompt).</summary>
    private static readonly string[] Required = [DirectionLabel, DecisionLabel, AnsweredByLabel, ExpectedLabel, EmbeddingsLabel, ClientLabel, VersionLabel];

    /// <summary>How many of the form's headings the body has; 0 means it is not this form.</summary>
    public int Sections { get; init; }

    /// <summary>The body was longer than <see cref="MaxBodyLength"/> and was cut.</summary>
    public bool Truncated { get; init; }

    /// <summary>Required fields the body leaves empty or out, by label.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    public MisrouteDirection Direction { get; init; }

    /// <summary>The decision as pasted.</summary>
    public string? Decision { get; init; }

    /// <summary>The tier the router gave the prompt, from the decision.</summary>
    public int? ReportedTier { get; init; }

    /// <summary>Why, from the decision.</summary>
    public RoutingReason? ReportedReason { get; init; }

    /// <summary>The route it matched, from the decision; <see langword="null"/> for none.</summary>
    public string? ReportedRoute { get; init; }

    /// <summary>The route's similarity, from the decision.</summary>
    public float? ReportedSimilarity { get; init; }

    public string? AnsweredBy { get; init; }

    /// <summary>What the prompt really needed, in the reporter's words.</summary>
    public string? Expected { get; init; }

    public string? Embeddings { get; init; }

    /// <summary>The embedding provider named first in <see cref="Embeddings"/> (<c>Onnx</c>, <c>Hashing</c>, …).</summary>
    public string? EmbeddingProvider { get; init; }

    /// <summary>The embedding model named second in <see cref="Embeddings"/>.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>The similarity threshold in <see cref="Embeddings"/>.</summary>
    public float? Threshold { get; init; }

    /// <summary>The route whose own threshold <see cref="Threshold"/> is, when the line says so (<c>threshold 0.5 (route x)</c>).</summary>
    public string? ThresholdRoute { get; init; }

    public string? Client { get; init; }

    /// <summary>The prompt, paraphrase or description; <see langword="null"/> when left out.</summary>
    public string? Prompt { get; init; }

    /// <summary>The router version as written.</summary>
    public string? Version { get; init; }

    /// <summary>The first <c>major.minor.patch</c> in <see cref="Version"/>.</summary>
    public Version? ReleaseVersion { get; init; }

    /// <summary>Reads an issue body. Never throws on content; anything it does not recognise is left empty.</summary>
    public static MisrouteReport Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        bool truncated = body.Length > MaxBodyLength;
        string text = (truncated ? body[..MaxBodyLength] : body).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Dictionary<string, string> sections = Split(text);

        string? Field(string label, int max, bool multiline) =>
            sections.TryGetValue(label, out string? raw) && Value(raw) is { } value ? UntrustedText.Clean(value, max, multiline) is { Length: > 0 } clean ? clean : null : null;

        string? decision = Field(DecisionLabel, MaxDecisionLength, multiline: true);
        string? embeddings = Field(EmbeddingsLabel, MaxFieldLength, multiline: false);
        string? version = Field(VersionLabel, MaxFieldLength, multiline: false);
        var parsed = new MisrouteReport
        {
            Sections = sections.Count,
            Truncated = truncated,
            Direction = ParseDirection(Field(DirectionLabel, MaxFieldLength, multiline: false)),
            Decision = decision,
            AnsweredBy = Field(AnsweredByLabel, MaxFieldLength, multiline: false),
            Expected = Field(ExpectedLabel, MaxFieldLength, multiline: false),
            Embeddings = embeddings,
            Client = Field(ClientLabel, MaxFieldLength, multiline: false),
            Prompt = Field(PromptLabel, MaxPromptLength, multiline: true),
            Version = version,
            ReleaseVersion = Release(version),
            Missing = [.. Required.Where(label => !sections.TryGetValue(label, out string? raw) || Value(raw) is null)],
        };

        return ParseEmbeddings(ParseDecision(parsed, decision), embeddings);
    }

    /// <summary>The first <c>major.minor.patch</c> in <paramref name="text"/> (<c>0.6.1</c>, <c>v0.6.1-preview</c>), if any.</summary>
    public static Version? Release(string? text) =>
        text is not null && ReleasePattern().Match(text) is { Success: true } release && System.Version.TryParse(release.Value, out Version? version) ? version : null;

    /// <summary>
    /// The body's sections, by the form's label. A heading inside a code fence is the fence's text, not a new section, and
    /// only the first section of each label counts.
    /// </summary>
    private static Dictionary<string, string> Split(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var value = new StringBuilder();
        int fence = 0;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            int ticks = trimmed.Length - trimmed.TrimStart('`').Length;
            if (fence == 0 && ticks >= 3)
            {
                fence = ticks;
            }
            else if (fence > 0 && ticks >= fence && trimmed.Trim('`').Trim().Length == 0)
            {
                fence = 0;
            }
            else if (fence == 0 && line.StartsWith("### ", StringComparison.Ordinal))
            {
                Close();
                string heading = line[4..].Trim();
                current = Labels.Values.FirstOrDefault(l => string.Equals(l, heading, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (current is not null)
            {
                value.Append(line).Append('\n');
            }
        }

        Close();
        return sections;

        void Close()
        {
            if (current is not null)
            {
                sections.TryAdd(current, value.ToString());
            }

            current = null;
            value.Clear();
        }
    }

    /// <summary>
    /// A section's answer without its code fence (also one a cut body left open); <see langword="null"/> when empty or
    /// <c>_No response_</c>.
    /// </summary>
    private static string? Value(string raw)
    {
        string[] lines = raw.Trim('\n').Split('\n');
        if (lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            lines = lines.Length >= 2 && lines[^1].Trim().Length > 0 && lines[^1].Trim().Trim('`').Length == 0 ? lines[1..^1] : lines[1..];
        }

        string value = string.Join('\n', lines).Trim();
        return value.Length == 0 || value == "_No response_" ? null : value;
    }

    private static MisrouteDirection ParseDirection(string? value) => value switch
    {
        null => MisrouteDirection.Unknown,
        _ when value.StartsWith("Too high", StringComparison.OrdinalIgnoreCase) => MisrouteDirection.TooHigh,
        _ when value.StartsWith("Too low", StringComparison.OrdinalIgnoreCase) => MisrouteDirection.TooLow,
        _ when value.StartsWith("Wrong route", StringComparison.OrdinalIgnoreCase) => MisrouteDirection.WrongRoute,
        _ when value.StartsWith("Other", StringComparison.OrdinalIgnoreCase) => MisrouteDirection.Other,
        _ => MisrouteDirection.Unknown,
    };

    /// <summary>
    /// Tier, reason, route and similarity from the dashboard's decision line (<c>Tier: 4</c>), the response headers
    /// (<c>X-Prompt-Route-Tier: 4</c>) or the classify endpoint's JSON (<c>"requiredTier": 4</c>). The first of each wins.
    /// </summary>
    private static MisrouteReport ParseDecision(MisrouteReport report, string? decision)
    {
        if (decision is null)
        {
            return report;
        }

        int? tier = null;
        RoutingReason? reason = null;
        string? route = null;
        float? similarity = null;
        bool routeSeen = false;
        foreach ((string key, string value) in Fields(decision))
        {
            switch (key)
            {
                case "TIER" or "X-PROMPT-ROUTE-TIER" or "REQUIREDTIER" when tier is null:
                    tier = int.TryParse(LeadingNumber().Match(value).Value, NumberStyles.None, CultureInfo.InvariantCulture, out int t) && t <= 99 ? t : null;
                    break;
                case "REASON" or "X-PROMPT-ROUTE-REASON" when reason is null:
                    reason = Enum.TryParse(WordPattern().Match(value).Value, ignoreCase: true, out RoutingReason r) && Enum.IsDefined(r) ? r : null;
                    break;
                case "ROUTE" or "X-PROMPT-ROUTE" when !routeSeen:
                    routeSeen = true;
                    string[] parts = value.Split('·', 2);
                    string named = parts[0].Trim();
                    route = named.Length is 0 or > 100 || named is "(none)" or "none" or "null" ? null : named;
                    if (parts.Length == 2 && SimilarityPattern().Match(parts[1]) is { Success: true } inline)
                    {
                        similarity ??= Number(inline.Groups[1].Value);
                    }

                    break;
                case "X-PROMPT-ROUTE-SIMILARITY" or "SIMILARITY" when similarity is null:
                    similarity = Number(value);
                    break;
            }
        }

        return report with { ReportedTier = tier, ReportedReason = reason, ReportedRoute = route, ReportedSimilarity = similarity };
    }

    /// <summary>
    /// The decision's <c>key: value</c> pairs, keys in upper case: one per line, then the pairs of JSON pasted on one
    /// line (<c>{"requiredTier":4,"reason":"SemanticMatch"}</c>). The caller keeps the first of each.
    /// </summary>
    private static IEnumerable<(string Key, string Value)> Fields(string decision)
    {
        foreach (string raw in decision.Split('\n'))
        {
            Match field = FieldPattern().Match(raw.Trim().TrimEnd(','));
            if (field.Success)
            {
                yield return (field.Groups["key"].Value.Trim('"').Trim().ToUpperInvariant(), field.Groups["value"].Value.Trim().Trim('"').Trim());
            }
        }

        foreach (Match pair in JsonPairPattern().Matches(decision))
        {
            yield return (pair.Groups["key"].Value.ToUpperInvariant(), pair.Groups["value"].Value.Trim('"'));
        }
    }

    /// <summary>Provider, model and threshold from a line such as <c>Onnx, all-MiniLM-L6-v2, threshold 0.40</c>.</summary>
    private static MisrouteReport ParseEmbeddings(MisrouteReport report, string? embeddings)
    {
        if (embeddings is null)
        {
            return report;
        }

        string[] parts = embeddings.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? provider = parts.Length > 0 && !parts[0].StartsWith("threshold", StringComparison.OrdinalIgnoreCase) ? WordPattern().Match(parts[0]).Value : null;
        string? model = parts.Length > 1 && !parts[1].StartsWith("threshold", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        Match threshold = ThresholdPattern().Match(embeddings);
        return report with
        {
            EmbeddingProvider = provider is { Length: > 0 } ? provider : null,
            EmbeddingModel = model,
            Threshold = threshold.Success ? Number(threshold.Groups["value"].Value) : null,
            ThresholdRoute = threshold.Success && threshold.Groups["route"].Success ? threshold.Groups["route"].Value : null,
        };
    }

    private static float? Number(string text) =>
        float.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float value) && value is >= 0 and <= 1 ? value : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex ReleasePattern();

    [GeneratedRegex(@"^(?<key>""?[A-Za-z][A-Za-z -]{0,40}""?)\s*:\s*(?<value>.*)$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"""(?<key>requiredTier|reason|route|similarity)""\s*:\s*(?<value>""[^""\n]{0,100}""|[0-9.]{1,20}|null)")]
    private static partial Regex JsonPairPattern();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,40}")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"similarity\s+([0-9]*\.?[0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SimilarityPattern();

    [GeneratedRegex(@"threshold\s*:?\s*(?<value>[0-9]*\.?[0-9]+)(?:\s*\(route\s+(?<route>[A-Za-z0-9_.-]{1,64})\))?", RegexOptions.IgnoreCase)]
    private static partial Regex ThresholdPattern();
}
