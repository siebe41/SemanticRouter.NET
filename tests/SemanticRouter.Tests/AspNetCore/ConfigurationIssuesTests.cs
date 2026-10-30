using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Validation messages tied to the field the dashboard shows them next to, from the validator's own messages, and
/// values that do not fit their field named with the format they need.
/// </summary>
public sealed class ConfigurationIssuesTests
{
    /// <summary>The field of the one validation error of <paramref name="document"/> that contains <paramref name="fragment"/>.</summary>
    private static string? PathFor(RouterConfiguration document, string fragment)
    {
        IReadOnlyList<ConfigurationIssue> issues = ConfigurationIssues.Locate(document.GetDocumentValidationErrors(), document);
        return issues.Should().ContainSingle(i => i.Message.Contains(fragment, StringComparison.Ordinal), string.Join(" | ", issues.Select(i => i.Message)))
            .Subject.Path;
    }

    [Fact]
    public void An_unnamed_budget_points_at_its_name_and_at_the_limit_that_is_wrong()
    {
        var document = new RouterConfiguration();
        document.Usage.Budgets.Add(new BudgetDefinition { Name = "named", MaxTokens = 10 });
        document.Usage.Budgets.Add(new BudgetDefinition { Name = " ", MaxTokens = -1 });

        PathFor(document, "entries must have a Name").Should().Be("Usage.Budgets.1.Name");
        PathFor(document, "limits must not be negative").Should().Be("Usage.Budgets.1.MaxTokens");
        PathFor(document, "A budget must name exactly one of").Should().Be("Usage.Budgets.1.Model");
    }

    [Fact]
    public void An_unnamed_or_repeated_memory_source_points_at_the_name_to_change()
    {
        var unnamed = new RouterConfiguration();
        unnamed.Memory.Sources.Add(new MemorySourceOptions { Name = "notes", Path = "notes" });
        unnamed.Memory.Sources.Add(new MemorySourceOptions { Name = "", Path = "other" });
        var repeated = new RouterConfiguration();
        repeated.Memory.Sources.Add(new MemorySourceOptions { Name = "notes", Path = "a" });
        repeated.Memory.Sources.Add(new MemorySourceOptions { Name = "Notes", Path = "b" });

        PathFor(unnamed, "entries must have a Name").Should().Be("Memory.Sources.1.Name");
        PathFor(repeated, "listed more than once").Should().Be("Memory.Sources.1.Name", "the second one is the one to rename");
    }

    [Fact]
    public void A_repeated_tier_level_points_at_the_second_tier()
    {
        var document = new RouterConfiguration
        {
            Tiers = [new TierDefinition { Level = 1, Name = "fast" }, new TierDefinition { Level = 2, Name = "mid" }, new TierDefinition { Level = 1, Name = "again" }],
        };

        PathFor(document, "Tier level 1 is defined more than once").Should().Be("Tiers.2.Level");
    }

    [Fact]
    public void A_bad_complexity_tier_rule_points_at_the_score_or_the_tier()
    {
        var score = new RouterConfiguration { ComplexityTiers = [new ComplexityTierRule { MinScore = 0.5, Tier = 1 }, new ComplexityTierRule { MinScore = 1.5, Tier = 2 }] };
        var tier = new RouterConfiguration { ComplexityTiers = [new ComplexityTierRule { MinScore = 0.5, Tier = -1 }] };

        PathFor(score, "ComplexityTiers entries").Should().Be("ComplexityTiers.1.MinScore");
        PathFor(tier, "ComplexityTiers entries").Should().Be("ComplexityTiers.0.Tier");
    }

    [Fact]
    public void The_classification_cache_message_points_at_the_setting_that_is_wrong()
    {
        var entries = new RouterConfiguration();
        entries.ClassificationCache.MaxEntries = 0;
        var age = new RouterConfiguration();
        age.ClassificationCache.MaxAge = TimeSpan.Zero;

        PathFor(entries, "ClassificationCache needs").Should().Be("ClassificationCache.MaxEntries");
        PathFor(age, "ClassificationCache needs").Should().Be("ClassificationCache.MaxAge");
    }

    [Fact]
    public void A_profile_rule_naming_an_unknown_profile_points_at_the_rule()
    {
        var document = new RouterConfiguration();
        document.Middleware.ProfileRules.Add(new ProfileRule { Profile = "work" });
        document.Middleware.ProfileRules.Add(new ProfileRule { Profile = "ghost" });
        document.Profiles["work"] = new RoutingProfile();

        PathFor(document, "name profile 'ghost'").Should().Be("Middleware.ProfileRules.1.Profile");
    }

    [Fact]
    public void A_bad_profile_name_points_at_that_profile_not_at_the_quoted_at_sign()
    {
        // "Profile names must not be empty or contain '@' ('work@home')."
        var at = new RouterConfiguration();
        at.Profiles["work@home"] = new RoutingProfile();
        var empty = new RouterConfiguration();
        empty.Profiles[string.Empty] = new RoutingProfile();

        PathFor(at, "Profile names must not be empty").Should().Be("Profiles.work@home");
        PathFor(empty, "Profile names must not be empty").Should().Be("Profiles");
    }

    [Fact]
    public void An_unknown_selection_time_zone_points_at_it()
    {
        var document = new RouterConfiguration();
        document.Selection.TimeZone = "Mars/Olympus_Mons";

        PathFor(document, "Selection TimeZone").Should().Be("Selection.TimeZone");
    }

    [Theory]
    [InlineData("""{"DefaultSimilarityThreshold":"high"}""", "DefaultSimilarityThreshold", "\"high\" is not a number; write it without quotes, e.g. 0.5")]
    [InlineData("""{"ClassificationCache":{"Enabled":"yes"}}""", "ClassificationCache.Enabled", "\"yes\" is not true or false")]
    [InlineData("""{"Routes":[{"Name":5}]}""", "Routes.0.Name", "5 should be text in quotes")]
    [InlineData("""{"Connectors":{"local":{"BaseAddress":5}}}""", "Connectors.local.BaseAddress", "5 is not an address; use one like http://localhost:1234")]
    [InlineData("""{"Connectors":[1]}""", "Connectors", "[1] should be an object of names and values")]
    [InlineData("""{"Routes":{"a":1}}""", "Routes", "{\"a\":1} should be a list in [ ]")]
    public void A_value_that_does_not_fit_its_field_says_the_format_it_needs(string json, string path, string message)
    {
        JsonException exception = FluentActions.Invoking(() => RouterConfiguration.FromJson(json)).Should().Throw<JsonException>().Which;

        ConfigurationIssue issue = ConfigurationIssues.FromJsonException(exception, JsonNode.Parse(json));

        issue.Should().Be(new ConfigurationIssue { Path = path, Message = message });
    }

    [Fact]
    public void An_enum_value_lists_the_names_it_can_be()
    {
        const string json = """{"Memory":{"Sources":[{"Name":"notes","Type":"Telepathy"}]}}""";
        JsonException exception = FluentActions.Invoking(() => RouterConfiguration.FromJson(json)).Should().Throw<JsonException>().Which;

        ConfigurationIssue issue = ConfigurationIssues.FromJsonException(exception, JsonNode.Parse(json));

        issue.Path.Should().Be("Memory.Sources.0.Type");
        issue.Message.Should().Be($"\"Telepathy\" is not one of: {string.Join(", ", Enum.GetNames<MemorySourceType>())}");
    }

    [Fact]
    public void A_memory_source_the_policy_turns_off_points_at_its_own_switch()
    {
        // #304: the path was rebuilt from its first two parts, which dropped the source's index.
        var document = new RouterConfiguration();
        document.Memory.Sources.Add(new MemorySourceOptions { Name = "a", Path = "a" });
        document.Memory.Sources.Add(new MemorySourceOptions { Name = "b", Path = "b" });

        ConfigurationIssues.PathOf("Not allowed: Memory source 'b' is turned off: your organization's policy.", document)
            .Should().Be("Memory.Sources.1.Enabled");
    }
}
