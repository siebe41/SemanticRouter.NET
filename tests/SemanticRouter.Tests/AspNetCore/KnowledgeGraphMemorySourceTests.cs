using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Loading the MCP memory server's knowledge graph from disk: a file, or a folder holding <c>memory.jsonl</c> (or
/// <c>memory.json</c>). Parsing is in <c>MemorySourceTests</c>.
/// </summary>
public sealed class KnowledgeGraphMemorySourceTests
{
    private const string Graph = """
        {"type":"entity","name":"Alice","entityType":"person","observations":["Owns the billing migration"]}
        {"type":"relation","from":"Alice","to":"Billing","relationType":"owns"}
        """;

    [Fact]
    public async Task A_file_loads_with_its_modification_time()
    {
        using TempFolder folder = new TempFolder().With("graph.jsonl", Graph);
        string file = Path.Combine(folder.Root, "graph.jsonl");
        var modified = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        var source = new KnowledgeGraphMemorySource("graph", file);

        IReadOnlyList<MemoryDocument> notes = await source.LoadAsync(CancellationToken.None);

        (source.Name, source.Kind, source.Location).Should().Be(("graph", "KnowledgeGraph", file));
        MemoryDocument alice = notes.Should().ContainSingle().Subject;
        (alice.Path, alice.Content, alice.Modified).Should().Be(("person/Alice", "Owns the billing migration", new DateTimeOffset(modified)));
        alice.Links.Should().Equal("Billing");
    }

    [Fact]
    public async Task A_folder_loads_its_memory_jsonl_before_memory_json()
    {
        using TempFolder both = new TempFolder()
            .With("memory.jsonl", Graph)
            .With("memory.json", """{"entities":[{"name":"Bob","entityType":"person","observations":["x"]}]}""");
        using TempFolder jsonOnly = new TempFolder()
            .With("memory.json", """{"entities":[{"name":"Bob","entityType":"person","observations":["x"]}]}""");

        IReadOnlyList<MemoryDocument> fromJsonl = await new KnowledgeGraphMemorySource("graph", both.Root).LoadAsync(CancellationToken.None);
        IReadOnlyList<MemoryDocument> fromJson = await new KnowledgeGraphMemorySource("graph", jsonOnly.Root).LoadAsync(CancellationToken.None);

        fromJsonl.Select(n => n.Title).Should().Equal("Alice");
        fromJson.Select(n => n.Title).Should().Equal("Bob");
    }

    [Fact]
    public async Task A_missing_file_or_a_folder_without_a_graph_says_where_it_looked()
    {
        using var empty = new TempFolder();
        string missing = Path.Combine(empty.Root, "nowhere.jsonl");

        Func<Task> folder = () => new KnowledgeGraphMemorySource("graph", empty.Root).LoadAsync(CancellationToken.None);
        Func<Task> file = () => new KnowledgeGraphMemorySource("graph", missing).LoadAsync(CancellationToken.None);

        (await folder.Should().ThrowAsync<FileNotFoundException>()).WithMessage($"No memory.jsonl in '{empty.Root}'.");
        (await file.Should().ThrowAsync<FileNotFoundException>()).WithMessage($"The file '{missing}' does not exist.");
    }

    [Theory]
    [InlineData(null, "memory.jsonl")]
    [InlineData("graph", "")]
    [InlineData(" ", "memory.jsonl")]
    [InlineData("graph", "  ")]
    public void A_name_and_a_path_are_required(string? name, string? path)
    {
        FluentActions.Invoking(() => new KnowledgeGraphMemorySource(name!, path!)).Should().Throw<ArgumentException>();
    }
}
