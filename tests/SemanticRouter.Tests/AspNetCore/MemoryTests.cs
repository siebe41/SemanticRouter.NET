using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>A throw-away folder of notes, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sr-memory-" + Guid.NewGuid().ToString("N"));

    public TempFolder With(string relative, string content)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class MarkdownNoteParserTests
{
    [Fact]
    public void Reads_frontmatter_tags_and_links_but_not_code_or_attachments()
    {
        const string note = """
            ---
            title: Alice Smith
            aliases: [Ally, "A. Smith"]
            tags:
              - person
              - "#team"
            ---
            Works on [[Billing|the billing project]] and [[Roadmap#Q3]] with [Bob](../people/Bob.md).
            ![[diagram.png]] ![[Embedded note]]
            Status: #active #area/finance, not a heading:
            # Heading
            ```
            [[Not a link]] #notatag
            ```
            """;

        ParsedNote parsed = MarkdownNoteParser.Parse("people/alice.md", note);

        parsed.Title.Should().Be("Alice Smith");
        parsed.Aliases.Should().Equal("Ally", "A. Smith");
        parsed.Tags.Should().BeEquivalentTo("person", "team", "active", "area/finance");
        parsed.Links.Select(l => l.Target).Should().BeEquivalentTo("Billing", "Roadmap", "Embedded note", "../people/Bob.md");
    }

    [Fact]
    public void Title_defaults_to_the_file_name()
    {
        MarkdownNoteParser.Parse("folder/My Note.md", "no frontmatter here").Title.Should().Be("My Note");
    }
}

public sealed class MemorySourceTests
{
    [Fact]
    public async Task Markdown_source_skips_dot_folders_excluded_paths_and_other_extensions()
    {
        using TempFolder vault = new TempFolder()
            .With("Alice.md", "a")
            .With("people/Bob.md", "b")
            .With(".obsidian/workspace.md", "settings")
            .With("Templates/daily.md", "template")
            .With("notes.txt", "plain");

        IReadOnlyList<MemoryDocument> notes = await new MarkdownMemorySource("vault", vault.Root, ["Templates/*"]).LoadAsync(CancellationToken.None);
        notes.Select(n => n.Path).Should().BeEquivalentTo("Alice.md", "people/Bob.md");

        IReadOnlyList<MemoryDocument> withText = await new MarkdownMemorySource("vault", vault.Root, ["Templates/*"], extensions: ["md", ".txt"]).LoadAsync(CancellationToken.None);
        withText.Select(n => n.Path).Should().Contain("notes.txt");
    }

    [Fact]
    public async Task Claude_code_source_reads_user_memory_and_project_memory_folders()
    {
        using TempFolder claude = new TempFolder()
            .With("CLAUDE.md", "Always use tabs.")
            .With("projects/-home-me-app/memory/MEMORY.md", "- [Team](team.md)")
            .With("projects/-home-me-app/memory/team.md", "The team ships on Thursdays.")
            .With("projects/-home-me-app/session.jsonl", "{}")
            .With("projects/-home-me-app/notes.md", "not memory")
            .With("agents/reviewer.md", "an agent definition, not memory");

        var source = new ClaudeCodeMemorySource("claude", claude.Root);
        IReadOnlyList<MemoryDocument> notes = await source.LoadAsync(CancellationToken.None);

        notes.Select(n => n.Path).Should().BeEquivalentTo(
            "CLAUDE.md", "projects/-home-me-app/memory/MEMORY.md", "projects/-home-me-app/memory/team.md");
        notes.Select(n => n.Title).Should().BeEquivalentTo("CLAUDE.md (user)", "app · MEMORY", "app · team");
        source.Kind.Should().Be("ClaudeCode");
    }

    [Theory]
    [InlineData("-home-me-SemanticRouter", "SemanticRouter")]
    [InlineData("C--Users-me-source-repos-app", "source-repos-app")]
    [InlineData("-Users-me-app", "app")]
    [InlineData("-srv-app", "srv-app")]
    public void Claude_code_project_folders_get_readable_names(string folder, string expected)
    {
        ClaudeCodeMemorySource.ProjectName(folder).Should().Be(expected);
    }

    [Fact]
    public void Claude_code_memory_files_named_in_frontmatter_keep_that_name()
    {
        var document = new MemoryDocument
        {
            Path = "projects/-home-me-app/memory/prefs.md",
            Content = "---\nname: Testing preferences\ntype: feedback\n---\nRun the tests.",
        };

        ClaudeCodeMemorySource.TitleFor(document).Should().Be("Testing preferences");
    }

    [Fact]
    public void A_name_after_the_frontmatter_is_not_the_title()
    {
        // #305: the frontmatter pattern ran past its closing --- and took a name: line from the body.
        var document = new MemoryDocument { Path = "CLAUDE.md", Content = "---\ntitle: t\n---\nbody\nname: Body Name\n---\n" };

        ClaudeCodeMemorySource.TitleFor(document).Should().NotBe("Body Name");
    }

    [Fact]
    public void Knowledge_graph_source_reads_entities_and_relations_from_jsonl()
    {
        const string jsonl = """
            {"type":"entity","name":"Alice","entityType":"person","observations":["Prefers async standups over meetings","Owns the billing migration"]}
            {"type":"entity","name":"Billing","entityType":"project","observations":["Moves invoices to the new ledger"]}
            {"type":"relation","from":"Alice","to":"Billing","relationType":"owns"}
            not json
            """;

        IReadOnlyList<MemoryDocument> notes = KnowledgeGraphMemorySource.Parse(jsonl);

        MemoryDocument alice = notes.Single(n => n.Title == "Alice");
        alice.Path.Should().Be("person/Alice");
        alice.Content.Should().Be("Prefers async standups over meetings\nOwns the billing migration");
        alice.Tags.Should().Equal("person");
        alice.Links.Should().Equal("Billing");
        notes.Single(n => n.Title == "Billing").Links.Should().BeEmpty();
    }

    [Fact]
    public void Knowledge_graph_source_reads_a_single_json_document()
    {
        const string json = """{"entities":[{"name":"Alice","entityType":"person","observations":["x"]}],"relations":[{"from":"Alice","to":"Bob"}]}""";

        IReadOnlyList<MemoryDocument> notes = KnowledgeGraphMemorySource.Parse(json);

        notes.Should().ContainSingle().Which.Links.Should().Equal("Bob");
    }
}

public sealed class MemoryFingerprintTests
{
    [Theory]
    [InlineData("Alice prefers async standups over meetings.")]
    [InlineData("     12→Alice prefers async standups over meetings.")]
    [InlineData("12\tAlice  prefers async   standups over meetings.  ")]
    public void Tool_line_numbers_and_whitespace_do_not_change_the_fingerprint(string line)
    {
        MemoryIndex.TryFingerprint("Alice prefers async standups over meetings.", 24, out ulong expected).Should().BeTrue();
        MemoryIndex.TryFingerprint(line, 24, out ulong actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Short_lines_are_not_fingerprints()
    {
        MemoryIndex.TryFingerprint("## Overview", 24, out _).Should().BeFalse();
    }
}

/// <summary>The memory map end to end: scanning a vault, spotting notes in requests, and recording where they went.</summary>
public sealed class MemoryMapTests : RouterHostTest, IDisposable
{
    private const string AliceLine1 = "Alice prefers async standups over meetings.";
    private const string AliceLine2 = "She owns the billing migration project since March.";
    private const string Boilerplate = "Generated from the weekly template, edit freely.";

    private readonly TempFolder _vault = new TempFolder()
        .With("people/Alice.md", $"---\naliases: [Ally]\n---\n{AliceLine1}\n{AliceLine2}\nSee [[Billing]].\n{Boilerplate}\n")
        .With("Billing.md", $"The billing migration moves invoices to the ledger service.\nOwner: [[Ally]]. Open question: [[Nonexistent]].\n{Boilerplate}\n")
        .With("Weekly 1.md", $"Week one notes go here for the whole team.\n{Boilerplate}\n")
        .With("Weekly 2.md", $"Week two notes go here for the whole team.\n{Boilerplate}\n")
        .With(".obsidian/workspace.md", "settings");

    public void Dispose() => _vault.Dispose();

    private Task<HttpClient> StartWithVaultAsync() =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s => s.Configure<MemoryOptions>(m => m.Sources.Add(new MemorySourceOptions { Name = "vault", Path = _vault.Root })));

    private static StringContent Chat(string content) =>
        Json(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content } } }));

    private static async Task<JsonNode> MemoryAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("/router/api/memory"))!;

    [Fact]
    public async Task Maps_notes_links_and_missing_link_targets()
    {
        HttpClient client = await StartWithVaultAsync();

        JsonNode map = await MemoryAsync(client);

        map["sources"]![0]!["notes"]!.GetValue<int>().Should().Be(4);
        JsonArray nodes = map["nodes"]!.AsArray();
        string[] ids = nodes.Select(n => n!["id"]!.GetValue<string>()).ToArray();
        ids.Should().Contain(["vault:people/Alice.md", "vault:Billing.md", "vault:?nonexistent"]);
        ids.Should().NotContain(id => id.Contains(".obsidian", StringComparison.Ordinal));
        nodes.Single(n => n!["id"]!.GetValue<string>() == "vault:?nonexistent")!["missing"]!.GetValue<bool>().Should().BeTrue();

        // Alice → Billing, Billing → Alice (through the alias "Ally"), Billing → Nonexistent.
        int Index(string id) => Array.IndexOf(ids, id);
        map["links"]!.AsArray().Select(l => (l![0]!.GetValue<int>(), l[1]!.GetValue<int>())).Should().BeEquivalentTo(new[]
        {
            (Index("vault:people/Alice.md"), Index("vault:Billing.md")),
            (Index("vault:Billing.md"), Index("vault:people/Alice.md")),
            (Index("vault:Billing.md"), Index("vault:?nonexistent")),
        });
        map.ToJsonString().Should().NotContain(AliceLine1, "note text never leaves the router");
    }

    [Fact]
    public async Task Records_note_content_sent_to_a_cloud_model()
    {
        HttpClient client = await StartWithVaultAsync();
        await MemoryAsync(client);
        ClassifyAs(tier: 4);

        // As Claude Code's Read tool returns a file: numbered lines.
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat($"Summarize this:\n     1→{AliceLine1}\n     2→{AliceLine2}\n     3→{Boilerplate}"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();
        entry.SentTo.Should().Equal("cloud");
        MemoryReference alice = entry.Memory.Should().ContainSingle("the shared template line identifies no single note").Subject;
        alice.Should().Be(new MemoryReference
        {
            Id = "vault:people/Alice.md",
            Title = "Alice",
            Source = "vault",
            Kind = MemoryShareKind.Content,
            Lines = 2,
            TotalLines = 2,
        });

        JsonNode exposure = (await MemoryAsync(client))["exposure"]!["vault:people/Alice.md"]!;
        exposure["cloudContent"]!.GetValue<int>().Should().Be(1);
        exposure["localContent"]!.GetValue<int>().Should().Be(0);
        exposure["models"]!["cloud"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task The_state_database_names_the_notes_a_request_quoted_but_never_holds_their_text()
    {
        using var state = new TempFolder();
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                s.Configure<MemoryOptions>(m => m.Sources.Add(new MemorySourceOptions { Name = "vault", Path = _vault.Root }));
                new SemanticRouterBuilder(s).UseSqliteState(Path.Combine(state.Root, "state.db"));
                s.Configure<StateOptions>(o => o.StorePrompts = PromptStorage.Off);
            });
        await MemoryAsync(client);
        ClassifyAs(tier: 4);

        await client.PostAsync("/v1/chat/completions", Chat($"Summarize this:\n     1→{AliceLine1}\n     2→{AliceLine2}"));
        IRoutingActivityStore store = Services.GetRequiredService<IRoutingActivityStore>();
        for (int i = 0; i < 500 && store.Recent(1).Count == 0; i++)
        {
            await Task.Delay(10);
        }

        store.Recent(1).Should().ContainSingle().Which.Memory.Should().ContainSingle().Which.Id.Should().Be("vault:people/Alice.md");
        string files = DurableStateHostTests.ReadDatabaseFiles(state.Root, "state.db");
        files.Should().Contain("vault:people/Alice.md", "the entry names the note it quoted");
        files.Should().NotContain("async standups").And.NotContain("billing migration", "note text is never stored");
        await DisposeAsync();
        SqliteFiles.Release(state.Root);
    }

    [Fact]
    public async Task One_distinctive_line_is_enough_to_count_as_content()
    {
        HttpClient client = await StartWithVaultAsync();
        await MemoryAsync(client);

        await client.PostAsync("/v1/chat/completions", Chat("Remember this:\n" + AliceLine2));

        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Memory.Should().ContainSingle()
            .Which.Should().Be(new MemoryReference
            {
                Id = "vault:people/Alice.md",
                Title = "Alice",
                Source = "vault",
                Kind = MemoryShareKind.Content,
                Lines = 1,
                TotalLines = 2,
            });
    }

    [Fact]
    public async Task Records_references_by_path_and_wikilink_to_a_local_model()
    {
        HttpClient client = await StartWithVaultAsync();
        await MemoryAsync(client);

        await client.PostAsync("/v1/chat/completions", Chat("Open notes/vault/Billing.md and compare it with [[Ally|her]] notes."));

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();
        entry.SentTo.Should().Equal("local");
        entry.Memory.Select(m => (m.Id, m.Kind)).Should().BeEquivalentTo(new[]
        {
            ("vault:Billing.md", MemoryShareKind.Reference),
            ("vault:people/Alice.md", MemoryShareKind.Reference),
        });

        JsonNode exposure = (await MemoryAsync(client))["exposure"]!;
        exposure["vault:Billing.md"]!["localReference"]!.GetValue<int>().Should().Be(1);
        exposure["vault:Billing.md"]!["cloudReference"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task A_wikilink_may_name_a_note_by_its_file_name_when_it_has_another_title()
    {
        // #294: links between notes resolve a file name, as Obsidian does, but a request's [[file name]] was not spotted.
        using var vault = new TempFolder().With("plans.md", "---\ntitle: Roadmap for Q3\n---\nShip the ledger service before the quarter ends.\n");
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s => s.Configure<MemoryOptions>(m => m.Sources.Add(new MemorySourceOptions { Name = "notes", Path = vault.Root })));
        await MemoryAsync(client);

        await client.PostAsync("/v1/chat/completions", Chat("What does [[plans]] say about the ledger?"));

        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Memory.Select(m => (m.Id, m.Kind))
            .Should().Equal(("notes:plans.md", MemoryShareKind.Reference));
    }

    [Fact]
    public async Task Exposure_can_be_cleared()
    {
        HttpClient client = await StartWithVaultAsync();
        await MemoryAsync(client);
        await client.PostAsync("/v1/chat/completions", Chat("Look at Billing.md"));

        (await client.PostAsync("/router/api/memory/exposure/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await MemoryAsync(client))["exposure"]!.AsObject().Count.Should().Be(0);
    }

    [Fact]
    public async Task Requests_without_memory_sources_carry_no_notes()
    {
        HttpClient client = await StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
        });

        await client.PostAsync("/v1/chat/completions", Chat(AliceLine1 + "\n" + AliceLine2));

        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Memory.Should().BeEmpty();
        (await MemoryAsync(client))["nodes"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Event_stream_announces_routed_requests()
    {
        HttpClient client = await StartWithVaultAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using HttpResponseMessage stream = await client.GetAsync("/router/api/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));
        (await reader.ReadLineAsync(timeout.Token)).Should().Be(": connected");

        await client.PostAsync("/v1/chat/completions", Chat("hello there"));

        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && !line.StartsWith("data: ", StringComparison.Ordinal))
        {
        }

        JsonNode entry = JsonNode.Parse(line!["data: ".Length..])!;
        entry["promptPreview"]!.GetValue<string>().Should().Be("hello there");
    }

    [Fact]
    public void Activity_entries_keep_their_models_and_notes_through_a_json_round_trip()
    {
        // The SQLite activity store keeps entries as JSON; the memory columns must survive a restart.
        var entry = new RoutingActivityEntry
        {
            Id = 7,
            Memory = [new MemoryReference { Id = "vault:a.md", Title = "a", Source = "vault", Kind = MemoryShareKind.Content, Lines = 2, TotalLines = 3 }],
        };
        entry.SentTo.Add("cloud");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        RoutingActivityEntry copy = JsonSerializer.Deserialize<RoutingActivityEntry>(JsonSerializer.Serialize(entry, json), json)!;

        copy.SentTo.Should().Equal("cloud");
        copy.Memory.Should().Equal(entry.Memory);
    }

    [Fact]
    public void Validation_requires_a_path_except_for_claude_code()
    {
        var config = new RouterConfiguration();
        config.Memory.Sources.Add(new MemorySourceOptions { Name = "vault" });
        config.Memory.Sources.Add(new MemorySourceOptions { Name = "graph", Type = MemorySourceType.KnowledgeGraph });
        config.Memory.Sources.Add(new MemorySourceOptions { Name = "claude", Type = MemorySourceType.ClaudeCode });
        config.Memory.Sources.Add(new MemorySourceOptions { Name = "Claude", Type = MemorySourceType.ClaudeCode });

        IReadOnlyList<string> errors = config.GetDocumentValidationErrors();

        errors.Should().Contain(e => e.Contains("'vault' needs a Path", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("'graph' needs a Path", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("'Claude' is listed more than once", StringComparison.Ordinal));
        errors.Should().NotContain(e => e.Contains("'claude' needs", StringComparison.Ordinal));
    }
}
