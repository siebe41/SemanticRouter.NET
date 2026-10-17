using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// Configurations written from an earlier release's template: brought up to the current template where nobody changed
/// them, kept where someone did, and replaced by <c>init</c> while still untouched.
/// </summary>
public sealed class TemplateMigrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-template").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    public static TheoryData<int> Templates() => [.. Enumerable.Range(1, TemplateMigration.Current)];

    private string ConfigurationFile => Path.Combine(_directory, "semantic-router.json");

    private HostSettings Settings(params string[] args) => HostSettings.Parse(["--config-dir", _directory, .. args], _ => null);

    /// <summary>The document a release's <c>init --no-detect</c> or first start wrote: its template, without a stamp.</summary>
    private static RouterConfiguration AsReleased(int template)
    {
        RouterConfiguration document = RouterConfiguration.FromJson(TemplateMigration.Template(template));
        document.TemplateVersion = null;
        return document;
    }

    private static string Normalized(RouterConfiguration document)
    {
        RouterConfiguration copy = document.Clone();
        copy.TemplateVersion = null;
        return copy.ToJson();
    }

    [Fact]
    public void Every_released_template_is_kept_and_the_current_one_is_stamped()
    {
        RouterConfiguration.FromJson(RouterHost.Template()).TemplateVersion.Should().Be(TemplateMigration.Current);
        for (int version = 1; version < TemplateMigration.Current; version++)
        {
            Normalized(AsReleased(version)).Should().NotBe(Normalized(AsReleased(version + 1)), $"template {version + 1} must differ from {version}");
            TemplateMigration.EvaluationTemplate(version).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_template_does_not_change_without_a_reminder_to_number_it()
    {
        // What the template says, without its comments, formatting or stamp.
        JsonObject template = JsonNode.Parse(RouterHost.Template(), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        template.Remove(nameof(RouterConfiguration.TemplateVersion));
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template.ToJsonString())));

        fingerprint.Should().Be(
            "4e1d86e1645bce9c0fcf2acd84a0d38b8281185b06dd5edab8fd936947a0510a",
            "installs that already have this template get a change to it only when its TemplateVersion is raised. If this " +
            "template was released, raise TemplateVersion and TemplateMigration.Current and keep the released files in " +
            "Templates/Previous (CONTRIBUTING.md); either way, update this fingerprint to {0}",
            fingerprint);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void An_untouched_template_becomes_the_current_one(int template)
    {
        TemplateUpgrade upgrade = TemplateMigration.Upgrade(AsReleased(template))!;

        upgrade.From.Should().Be(template, "an unstamped document is recognised by what it holds");
        upgrade.Document.TemplateVersion.Should().Be(TemplateMigration.Current);
        Normalized(upgrade.Document).Should().Be(Normalized(AsReleased(TemplateMigration.Current)));
        upgrade.Kept.Should().BeEmpty();
        upgrade.Document.GetDocumentValidationErrors().Should().BeEmpty();
    }

    [Fact]
    public void Template_1_gets_the_opus_alias_fix_and_the_quick_answers_route()
    {
        RouterConfiguration document = AsReleased(1);
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "lmstudio", Model = "qwen3-8b", Tier = 1 });
        document.Embeddings.Provider = EmbeddingProviderKind.Onnx;

        TemplateUpgrade upgrade = TemplateMigration.Upgrade(document)!;

        RouterConfiguration upgraded = upgrade.Document;
        upgraded.Middleware.ModelAliases.Should().Equal(
            new Dictionary<string, string>
            {
                ["opus-pinned"] = "family:opus",
                ["*haiku*"] = "auto:cheapest",
                ["*sonnet*"] = "auto",
                ["*opus*"] = "auto",
                ["claude-*"] = "auto",
                ["gpt-*"] = "auto",
            },
            "opus-pinned goes before *opus*, which it also matches: the first matching alias wins");
        upgraded.Routes.Select(r => r.Name).Should().Equal(
            "smalltalk", "rewrite-summarize", "simple-code", "quick-answers", "deep-reasoning", "complex-code", "agentic-coding", "agent-housekeeping", "continuation");
        upgraded.Models.Should().ContainSingle(m => m.Name == "small");
        upgraded.Connectors.Keys.Should().Equal("lmstudio");
        upgraded.Embeddings.Provider.Should().Be(EmbeddingProviderKind.Onnx, "settings the template did not change are not touched");
        upgrade.Describe().Should().StartWith("template 1 → 2: ")
            .And.Contain("Routes[quick-answers] added")
            .And.Contain("Routes[smalltalk].Utterances: 2 added")
            .And.Contain("Routes[simple-code].Utterances: 10 added")
            .And.Contain("Middleware.ModelAliases.opus-pinned added")
            .And.Contain("Middleware.ModelAliases.*opus*: \"family:opus\" → \"auto\"");
    }

    [Fact]
    public void What_someone_changed_is_kept_and_said_so()
    {
        RouterConfiguration document = AsReleased(1);
        document.Middleware.ModelAliases["*opus*"] = "family:sonnet";
        RouteDefinition smalltalk = document.Routes.Single(r => r.Name == "smalltalk");
        smalltalk.Tier = 2;
        smalltalk.Utterances.Remove("tell me a joke");
        document.Routes.Single(r => r.Name == "simple-code").Utterances.Add("format this json"); // as tuning learns them
        document.Routes.Remove(document.Routes.Single(r => r.Name == "agent-housekeeping"));

        TemplateUpgrade upgrade = TemplateMigration.Upgrade(document)!;

        RouterConfiguration upgraded = upgrade.Document;
        upgraded.Middleware.ModelAliases["*opus*"].Should().Be("family:sonnet");
        upgraded.Middleware.ModelAliases.Keys.First().Should().Be("opus-pinned");
        upgrade.Kept.Should().ContainSingle().Which.Should().Be("Middleware.ModelAliases.*opus* (the template changed it from \"family:opus\" to \"auto\")");

        RouteDefinition merged = upgraded.Routes.Single(r => r.Name == "smalltalk");
        merged.Tier.Should().Be(2);
        merged.Utterances.Should().Contain("what can you help me with?", "the template's new utterances are added").And.NotContain("tell me a joke");
        upgraded.Routes.Single(r => r.Name == "simple-code").Utterances.Should().Contain("format this json").And.Contain("make this field read-only");
        upgraded.Routes.Should().NotContain(r => r.Name == "agent-housekeeping", "a route someone removed stays removed");
        upgraded.Routes.Should().Contain(r => r.Name == "quick-answers");
    }

    [Fact]
    public void A_stamped_document_is_upgraded_once()
    {
        TemplateUpgrade first = TemplateMigration.Upgrade(AsReleased(1))!;
        RouterConfiguration reverted = first.Document.Clone();
        reverted.Middleware.ModelAliases["*opus*"] = "family:opus"; // chosen after the upgrade: it sticks

        TemplateMigration.Upgrade(reverted).Should().BeNull();

        RouterConfiguration newer = AsReleased(TemplateMigration.Current);
        newer.TemplateVersion = TemplateMigration.Current + 1;
        TemplateMigration.Upgrade(newer).Should().BeNull("a document from a newer release is left alone");
    }

    [Fact]
    public void An_unstamped_document_from_the_current_template_is_only_stamped()
    {
        // Written by 0.3.0–0.5.0, with Opus asked for on purpose: it differs from template 1 in far more than that one alias.
        RouterConfiguration document = AsReleased(TemplateMigration.Current);
        document.Middleware.ModelAliases["*opus*"] = "family:opus";

        TemplateUpgrade upgrade = TemplateMigration.Upgrade(document)!;

        upgrade.From.Should().Be(TemplateMigration.Current);
        upgrade.Changes.Should().BeEmpty();
        upgrade.Document.Middleware.ModelAliases["*opus*"].Should().Be("family:opus");
        upgrade.Document.TemplateVersion.Should().Be(TemplateMigration.Current);
    }

    [Fact]
    public void A_configuration_written_by_hand_is_left_alone()
    {
        // Two routes of its own, one named like the template's, no model aliases: not a template someone edited.
        RouterConfiguration document = RouterConfiguration.FromJson("""
            {
              "Routes": [
                { "Name": "smalltalk", "Tier": 1, "Utterances": [ "hi there" ] },
                { "Name": "architecture", "Tier": 4, "Utterances": [ "design a system" ] }
              ]
            }
            """);

        TemplateMigration.Origin(document).Should().BeNull();
        TemplateMigration.Upgrade(document).Should().BeNull("a template's new routes and aliases are not for it");

        RouterConfiguration trimmed = AsReleased(1);
        while (trimmed.Routes.Count > 4)
        {
            trimmed.Routes.RemoveAt(trimmed.Routes.Count - 1);
        }

        TemplateMigration.Origin(trimmed).Should().Be(1, "half of the template's routes are still there");
    }

    [Fact]
    public async Task The_router_upgrades_a_file_from_0_2_1_on_start_and_records_it_in_the_history()
    {
        RouterConfiguration document = AsReleased(1);
        document.Connectors["local"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "local", Model = "m", Tier = 1 });
        new JsonFileRouterConfigurationStore(ConfigurationFile).Save(document);
        string evaluation = Path.Combine(_directory, "semantic-router.evaluation.jsonl");
        await File.WriteAllTextAsync(evaluation, TemplateMigration.EvaluationTemplate(1));
        HostSettings settings = Settings("--urls", "http://127.0.0.1:0");
        var output = new StringWriter();

        await using (RunningRouter? router = await RouterHost.StartAsync(settings, output, b => b.Logging.ClearProviders()))
        {
            RouterRuntimeProvider provider = router!.App.Services.GetRequiredService<RouterRuntimeProvider>();
            provider.Current.Configuration.Middleware.ModelAliases["*opus*"].Should().Be("auto");
            provider.Current.Configuration.Routes.Should().Contain(r => r.Name == "quick-answers");
            RouterConfigurationVersion latest = provider.History!.List(1)[0];
            latest.Source.Should().Be("upgrade");
            latest.Note.Should().StartWith("template 1 → 2: ").And.Contain("Routes[quick-answers] added");
        }

        output.ToString().Should().Contain($"Updated {ConfigurationFile} to the current template (template 1 → 2:")
            .And.Contain($"Updated {evaluation} to the current sample prompts");
        new JsonFileRouterConfigurationStore(ConfigurationFile).Load()!.TemplateVersion.Should().Be(TemplateMigration.Current);
        (await File.ReadAllTextAsync(evaluation)).Should().Be(RouterHost.EvaluationTemplate());

        var again = new StringWriter();
        await using (RunningRouter? router = await RouterHost.StartAsync(settings, again, b => b.Logging.ClearProviders()))
        {
            router.Should().NotBeNull();
        }

        again.ToString().Should().NotContain("Updated", "the file is stamped now");
    }

    [Fact]
    public void An_evaluation_set_someone_changed_is_kept()
    {
        string evaluation = Path.Combine(_directory, "semantic-router.evaluation.jsonl");
        string mine = TemplateMigration.EvaluationTemplate(1) + "{\"prompt\":\"mine\",\"tier\":1}\n";
        File.WriteAllText(evaluation, mine);

        TemplateMigration.UpgradeEvaluationSet(evaluation).Should().BeNull();
        File.ReadAllText(evaluation).Should().Be(mine);

        File.WriteAllText(evaluation, TemplateMigration.EvaluationTemplate(1).ReplaceLineEndings("\r\n"));
        TemplateMigration.UpgradeEvaluationSet(evaluation).Should().NotBeNull("line endings do not make it someone's own");
        TemplateMigration.UpgradeEvaluationSet(Path.Combine(_directory, "none.jsonl")).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task Init_replaces_an_untouched_template_from_any_release(int template)
    {
        new JsonFileRouterConfigurationStore(ConfigurationFile).Save(AsReleased(template));
        FirstRun.IsUntouchedTemplate(ConfigurationFile, exactly: true).Should().BeTrue();
        var output = new StringWriter();

        (await InitCommand.RunAsync(Settings("init", "--no-detect", "--no-cli"), NothingRunning(), output)).Should().Be(0, output.ToString());

        output.ToString().Should().Contain("it held only the built-in template");
        RouterConfiguration written = new JsonFileRouterConfigurationStore(ConfigurationFile).Load()!;
        written.TemplateVersion.Should().Be(TemplateMigration.Current);
        Normalized(written).Should().Be(Normalized(AsReleased(TemplateMigration.Current)));
    }

    private static InitProbes NothingRunning() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"))), string.Empty, Windows: false);
}
