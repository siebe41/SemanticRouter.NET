using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The knowledge graph kept by the MCP memory server (<c>@modelcontextprotocol/server-memory</c>), which Claude
/// Desktop and Claude Code use as long-term memory. Its file holds one JSON object per line:
/// <c>{"type":"entity","name":…,"entityType":…,"observations":[…]}</c> and
/// <c>{"type":"relation","from":…,"to":…,"relationType":…}</c>. A single <c>{"entities":[…],"relations":[…]}</c>
/// document is read too. Each entity becomes a note in the folder of its entity type, its observations become the
/// note's lines (so they are recognised when sent to a model), and relations become links.
/// </summary>
internal sealed class KnowledgeGraphMemorySource : IMemorySource
{
    private static readonly string[] DefaultFileNames = ["memory.jsonl", "memory.json"];

    private readonly string _path;

    /// <summary>Creates a source reading the JSONL file at <paramref name="path"/> (or <c>memory.jsonl</c> in that folder).</summary>
    public KnowledgeGraphMemorySource(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Name = name;
        _path = MarkdownMemorySource.ExpandPath(path);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Kind => nameof(MemorySourceType.KnowledgeGraph);

    /// <inheritdoc />
    public string? Location => _path;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryDocument>> LoadAsync(CancellationToken cancellationToken)
    {
        string file = _path;
        if (Directory.Exists(file))
        {
            file = DefaultFileNames.Select(n => Path.Combine(_path, n)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"No memory.jsonl in '{_path}'.");
        }
        else if (!File.Exists(file))
        {
            throw new FileNotFoundException($"The file '{file}' does not exist.");
        }

        string text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        DateTimeOffset modified = File.GetLastWriteTimeUtc(file);
        return Parse(text, modified);
    }

    /// <summary>Parses the memory server's file (JSONL, or one JSON document).</summary>
    internal static IReadOnlyList<MemoryDocument> Parse(string text, DateTimeOffset? modified = null)
    {
        var entities = new List<(string Name, string Type, List<string> Observations)>();
        var relations = new List<(string From, string To)>();

        void Read(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (element.TryGetProperty("entities", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entity in list.EnumerateArray())
                {
                    ReadEntity(entity);
                }

                if (element.TryGetProperty("relations", out JsonElement rels) && rels.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement relation in rels.EnumerateArray())
                    {
                        ReadRelation(relation);
                    }
                }

                return;
            }

            string? type = Text(element, "type");
            if (type == "relation" || (type is null && element.TryGetProperty("from", out _)))
            {
                ReadRelation(element);
            }
            else
            {
                ReadEntity(element);
            }
        }

        void ReadEntity(JsonElement entity)
        {
            if (Text(entity, "name") is not { Length: > 0 } name)
            {
                return;
            }

            var observations = new List<string>();
            if (entity.TryGetProperty("observations", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                observations.AddRange(items.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!));
            }

            entities.Add((name, Text(entity, "entityType") ?? string.Empty, observations));
        }

        void ReadRelation(JsonElement relation)
        {
            if (Text(relation, "from") is { Length: > 0 } from && Text(relation, "to") is { Length: > 0 } to)
            {
                relations.Add((from, to));
            }
        }

        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') && TryParse(trimmed, out JsonElement whole) && whole.TryGetProperty("entities", out _))
        {
            Read(whole);
        }
        else
        {
            foreach (string line in text.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line) && TryParse(line, out JsonElement element))
                {
                    Read(element);
                }
            }
        }

        ILookup<string, string> links = relations.ToLookup(r => r.From, r => r.To, StringComparer.OrdinalIgnoreCase);
        return entities
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                string type = g.First().Type;
                string folder = type.Length > 0 ? type.Replace('/', '-') + "/" : string.Empty;
                return new MemoryDocument
                {
                    Path = folder + g.Key.Replace('/', '-'),
                    Content = string.Join('\n', g.SelectMany(e => e.Observations).Distinct()),
                    Title = g.Key,
                    Modified = modified,
                    Tags = type.Length > 0 ? [type] : [],
                    Links = links[g.Key].Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                };
            })
            .ToArray();
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryParse(string json, out JsonElement element)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
