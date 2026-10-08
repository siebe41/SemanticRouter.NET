using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SemanticRouter;

/// <summary>A labelled prompt: the tier (and optionally the route) a correct configuration routes it to.</summary>
/// <param name="Prompt">The prompt text, as the classifier sees it (the latest user message).</param>
/// <param name="ExpectedTier">The tier the prompt should get.</param>
public sealed record EvaluationCase(string Prompt, int ExpectedTier)
{
    /// <summary>The route the prompt should match, when the label names one.</summary>
    public string? ExpectedRoute { get; init; }

    /// <summary>The prompt must stay on local models: a decision that is not local-only counts as wrong.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Free text, e.g. where the case came from.</summary>
    public string? Note { get; init; }
}

/// <summary>Reads and writes evaluation sets: a JSON array, <c>{"cases": [...]}</c>, or JSON Lines.</summary>
/// <remarks>
/// Each case is an object with <c>prompt</c> and <c>tier</c> (or <c>expectedTier</c>), and optionally <c>route</c>
/// (or <c>expectedRoute</c>), <c>localOnly</c> and <c>note</c>. In JSON Lines, blank lines and lines starting with
/// <c>#</c> or <c>//</c> are skipped.
/// </remarks>
public static class EvaluationSet
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Parses an evaluation set.</summary>
    /// <exception cref="FormatException">The text is not a valid set; the message names the case or line.</exception>
    public static IReadOnlyList<EvaluationCase> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (trimmed[0] == '[' || (trimmed[0] == '{' && IsDocument(trimmed)))
        {
            using JsonDocument document = ParseDocument(trimmed, "the set");
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cases", out JsonElement cases) && cases.ValueKind == JsonValueKind.Array)
            {
                root = cases;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                return [ReadCase(root, "case 1")];
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("An evaluation set is a JSON array of cases, {\"cases\": [...]}, or JSON Lines.");
            }

            var list = new List<EvaluationCase>();
            int index = 0;
            foreach (JsonElement item in root.EnumerateArray())
            {
                list.Add(ReadCase(item, $"case {++index}"));
            }

            return list;
        }

        var lines = new List<EvaluationCase>();
        int number = 0;
        foreach (string raw in trimmed.Split('\n'))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string where = "line " + number.ToString(CultureInfo.InvariantCulture);
            using JsonDocument document = ParseDocument(line, where);
            lines.Add(ReadCase(document.RootElement, where));
        }

        return lines;
    }

    /// <summary>Writes cases as JSON Lines (one object per line), the format <see cref="Parse"/> reads back.</summary>
    public static string ToJsonLines(IEnumerable<EvaluationCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var builder = new StringBuilder();
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (EvaluationCase item in cases)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, options))
            {
                writer.WriteStartObject();
                writer.WriteString("prompt", item.Prompt);
                writer.WriteNumber("tier", item.ExpectedTier);
                if (item.ExpectedRoute is { } route)
                {
                    writer.WriteString("route", route);
                }

                if (item.LocalOnly)
                {
                    writer.WriteBoolean("localOnly", true);
                }

                if (item.Note is { } note)
                {
                    writer.WriteString("note", note);
                }

                writer.WriteEndObject();
            }

            builder.Append(Encoding.UTF8.GetString(buffer.WrittenSpan)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Whether the whole text is one JSON value (rather than JSON Lines).</summary>
    private static bool IsDocument(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, DocumentOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonDocument ParseDocument(string text, string where)
    {
        try
        {
            return JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{where} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static EvaluationCase ReadCase(JsonElement item, string where)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{where} must be an object with 'prompt' and 'tier'.");
        }

        string? prompt = null;
        int? tier = null;
        string? route = null;
        string? note = null;
        bool localOnly = false;
        foreach (JsonProperty property in item.EnumerateObject())
        {
            switch (property.Name.ToUpperInvariant())
            {
                case "PROMPT" when property.Value.ValueKind == JsonValueKind.String:
                    prompt = property.Value.GetString();
                    break;
                case "TIER" or "EXPECTEDTIER" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int value):
                    tier = value;
                    break;
                case "ROUTE" or "EXPECTEDROUTE" when property.Value.ValueKind == JsonValueKind.String:
                    route = property.Value.GetString() is { Length: > 0 } name ? name : null;
                    break;
                case "LOCALONLY" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    localOnly = property.Value.GetBoolean();
                    break;
                case "NOTE" when property.Value.ValueKind == JsonValueKind.String:
                    note = property.Value.GetString();
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new FormatException($"{where} has no 'prompt'.");
        }

        if (tier is not { } expected || expected < 0)
        {
            throw new FormatException($"{where} has no valid 'tier'.");
        }

        return new EvaluationCase(prompt, expected) { ExpectedRoute = route, LocalOnly = localOnly, Note = note };
    }
}
