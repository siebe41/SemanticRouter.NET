using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// <c>semantic-router triage-misroute</c>: a "Routed to the wrong tier" issue body, re-run on the built-in template. The
/// sample report in <c>tests/fixtures/misroute</c> uses the Hashing embeddings, so these need no network or model.
/// </summary>
public sealed partial class MisrouteTriageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-triage").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "misroute", "too-high.issue.md");

    private HostSettings Settings(params string[] args) => HostSettings.Parse([.. args, "--config-dir", _directory], _ => null);

    private async Task<(int Exit, string Output, string Error)> RunAsync(string body, params string[] args)
    {
        string file = Path.Combine(_directory, "body.md");
        await File.WriteAllTextAsync(file, body);
        return await RunFileAsync(file, args);
    }

    private async Task<(int Exit, string Output, string Error)> RunFileAsync(string file, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = await MisrouteTriage.RunAsync(Settings(["triage-misroute", file, .. args]), TextReader.Null, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static string Body(
        string direction = "Too high: a stronger model than needed (wasted quota or cost)",
        string decision = "Reason: SemanticMatch\nTier: 4\nRoute: complex-code · similarity 0.548",
        string expected = "Tier 2. A small change.",
        string embeddings = "Hashing, threshold 0.3",
        string? prompt = "review this pull request: it only renames one variable",
        string version = "0.6.1") => $"""
        ### What went wrong

        {direction}

        ### The router's decision

        ```text
        {decision}
        ```

        ### Model that answered

        claude-code-opus

        ### What the prompt really needed

        {expected}

        ### Embedding provider, model and threshold

        {embeddings}

        ### Client

        Claude Code

        ### Prompt, paraphrase or description

        ```text
        {prompt ?? "_No response_"}
        ```

        ### Router version

        {version}

        ### Before you submit

        - [X] This report contains no keys, tokens or private prompt text.
        """;

    [Fact]
    public void The_parser_knows_every_label_of_the_issue_form()
    {
        // The form's fields as `id:` followed by `label:`, as misroute.yml writes them.
        string form = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "issue-forms", "misroute.yml"));
        Dictionary<string, string> fields = FormField().Matches(form)
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["label"].Value.Trim().Trim('"'));

        fields.Remove("no-secrets").Should().BeTrue("the form ends with its checkbox, which the triage does not read");
        fields.Should().BeEquivalentTo(MisrouteReport.Labels);
    }

    [Fact]
    public async Task The_sample_report_reproduces_and_proposes_its_evaluation_line()
    {
        (int exit, string comment, string error) = await RunFileAsync(Fixture, "--issue", "412");

        exit.Should().Be(0, error);
        comment.Should().StartWith(MisrouteTriage.Marker);
        comment.Should().Contain("on template 2 (release `0.6.1`), Hashing at threshold 0.30");
        comment.Should().Contain("- Reported: tier 4, `SemanticMatch`, route `complex-code`, similarity 0.548");
        comment.Should().Contain("- Re-run: tier 4, `SemanticMatch`, route `complex-code`, similarity 0.548");
        comment.Should().Contain("- **Reproduced:** the template makes the same decision for the paraphrase.");
        comment.Should().Contain("""
            ```jsonl
            {"prompt":"review this pull request: it only renames one variable","tier":2,"note":"misroute report #412"}
            ```
            """.ReplaceLineEndings("\n"));
        comment.Should().Contain("tier 2 (the tier the report says it needed)");
        comment.Should().Contain("The template routes the new line to tier 4, not 2: with it, the set records this misroute.");
        comment.Should().NotContain("**Notes**", "the sample report is complete and runs as reported");
    }

    [Fact]
    public async Task Json_has_the_reproduction_and_both_evaluation_runs()
    {
        (int exit, string output, _) = await RunFileAsync(Fixture, "--json");

        exit.Should().Be(0);
        JsonNode json = JsonNode.Parse(output)!;
        json["reproduction"]!.GetValue<string>().Should().Be("reproduced");
        json["template"]!.GetValue<int>().Should().Be(2);
        json["rerun"]!["tier"]!.GetValue<int>().Should().Be(4);
        json["rerun"]!["route"]!.GetValue<string>().Should().Be("complex-code");
        json["expectedTier"]!.GetValue<int>().Should().Be(2);
        json["expectedTierSource"]!.GetValue<string>().Should().Be("stated");
        json["evaluationLine"]!.GetValue<string>().Should().Be("""{"prompt":"review this pull request: it only renames one variable","tier":2,"note":"misroute report"}""");
        json["newLineCorrect"]!.GetValue<bool>().Should().BeFalse();
        int without = json["without"]!["total"]!.GetValue<int>();
        without.Should().Be(EvaluationSet.Parse(RouterHost.EvaluationTemplate()).Count);
        json["with"]!["total"]!.GetValue<int>().Should().Be(without + 1);
        json["with"]!["correct"]!.GetValue<int>().Should().Be(json["without"]!["correct"]!.GetValue<int>(), "the new line is routed wrongly, as reported");
    }

    [Fact]
    public async Task Standard_input_is_read_with_a_dash()
    {
        var output = new StringWriter();

        int exit = await MisrouteTriage.RunAsync(Settings("triage-misroute", "-"), new StringReader(Body()), output, TextWriter.Null);

        exit.Should().Be(0);
        output.ToString().Should().Contain("**Reproduced:**");
    }

    [Fact]
    public async Task Text_that_is_not_the_form_is_refused()
    {
        (int exit, string output, string error) = await RunAsync("Hello @octocat, routing is broken.\n\n## Steps\n\n1. run it");

        exit.Should().Be(1);
        output.Should().BeEmpty("nothing is posted for an issue that is not a misroute report");
        error.Should().Contain("not a \"Routed to the wrong tier\" report");
    }

    [Fact]
    public async Task Missing_sections_are_named_and_nothing_is_rerun_without_a_prompt()
    {
        (int exit, string comment, _) = await RunAsync(Body(prompt: null).Replace("### Model that answered\n\nclaude-code-opus\n", string.Empty, StringComparison.Ordinal));

        exit.Should().Be(0);
        comment.Should().Contain("- The form leaves out: *Model that answered*.");
        comment.Should().Contain("**Not re-run:** the report has no prompt or paraphrase.");
        comment.Should().NotContain("Proposed evaluation line");
        comment.Should().Contain("| Without the line |").And.NotContain("| With the line |");
    }

    [Fact]
    public async Task Mentions_html_links_and_fences_in_the_report_stay_inside_code()
    {
        const string Hostile = "@octocat <img src=x onerror=alert(1)> [click](https://evil.example) ![i](https://evil.example/i.png) #1 <!-- x -->";
        string body = Body(
            decision: $"Reason: SemanticMatch\nTier: 4\nRoute: `{Hostile}` · similarity 0.5",
            expected: $"Tier 2 {Hostile}",
            embeddings: $"Some{Hostile}, model, threshold 0.4",
            prompt: $"{Hostile}\nthen ``` and ```` to close the fence early",
            version: $"main {Hostile}");

        (int exit, string comment, _) = await RunAsync(body, "--embeddings", "Hashing");

        exit.Should().Be(0);
        string outsideCode = OutsideCode(comment.Replace(MisrouteTriage.Marker, string.Empty, StringComparison.Ordinal))
            .Replace("<sub>", string.Empty, StringComparison.Ordinal).Replace("</sub>", string.Empty, StringComparison.Ordinal);
        outsideCode.Should().NotContainAny(["@", "<", "](", "#1", "evil.example"]);
        comment.Should().Contain("`````jsonl\n{\"prompt\":\"@octocat", "the line's fence is longer than any run of backticks in it");
        comment.Should().Contain("`main @octocat <img src=x onerror=alert(1)>", "the text is kept, inside code");
    }

    [Fact]
    public void A_heading_inside_a_code_fence_is_not_a_section()
    {
        MisrouteReport report = MisrouteReport.Parse(Body(prompt: "first line\n### Router version\n\n9.9.9"));

        report.Prompt.Should().Be("first line\n### Router version\n\n9.9.9");
        report.Version.Should().Be("0.6.1");
    }

    [Fact]
    public void Control_and_invisible_characters_are_dropped_and_fields_are_capped()
    {
        string huge = new('x', MisrouteReport.MaxBodyLength);
        MisrouteReport report = MisrouteReport.Parse(Body(
            expected: "Tier​ 2‮ (local)\u0007",
            prompt: "a\u0000b‮c " + huge));

        report.Truncated.Should().BeTrue();
        report.Expected.Should().Be("Tier 2 (local)");
        report.Prompt.Should().StartWith("abc x").And.EndWith("…");
        report.Prompt!.Length.Should().Be(MisrouteReport.MaxPromptLength + 1);
        report.Missing.Should().Contain(MisrouteReport.VersionLabel, "the body was cut before it");
    }

    [Fact]
    public void An_unanswered_field_and_a_repeated_heading_are_read_as_the_form_means()
    {
        MisrouteReport report = MisrouteReport.Parse(Body(prompt: null) + "\n\n### Router version\n\n9.9.9\n");

        report.Prompt.Should().BeNull("GitHub writes _No response_ for an empty optional field");
        report.Version.Should().Be("0.6.1", "only the first section of a label counts");
        report.Missing.Should().BeEmpty();
    }

    [Theory]
    [InlineData("X-Prompt-Route-Reason: SemanticMatch\nX-Prompt-Route-Tier: 4\nX-Prompt-Route: complex-code\nX-Prompt-Route-Similarity: 0.8100")]
    [InlineData("""{"requiredTier":4,"reason":"SemanticMatch","route":"complex-code","similarity":0.81,"routes":[{"route":"simple-code","tier":2,"similarity":0.7}]}""")]
    [InlineData("Reason: SemanticMatch\nTier: 4\nRoute: complex-code · similarity 0.810\nPlanned model: claude-code-opus")]
    public void The_decision_is_read_from_the_dashboard_line_the_headers_or_the_classify_json(string decision)
    {
        MisrouteReport report = MisrouteReport.Parse(Body(decision: decision));

        report.ReportedTier.Should().Be(4);
        report.ReportedReason.Should().Be(RoutingReason.SemanticMatch);
        report.ReportedRoute.Should().Be("complex-code");
        report.ReportedSimilarity.Should().BeApproximately(0.81f, 0.0001f);
    }

    [Fact]
    public void Embeddings_and_a_route_threshold_are_read_from_the_dashboard_line()
    {
        MisrouteReport report = MisrouteReport.Parse(Body(embeddings: "Onnx, all-MiniLM-L6-v2, threshold 0.5 (route simple-code)"));

        report.EmbeddingProvider.Should().Be("Onnx");
        report.EmbeddingModel.Should().Be("all-MiniLM-L6-v2");
        report.Threshold.Should().Be(0.5f);
        report.ThresholdRoute.Should().Be("simple-code");
    }

    [Theory]
    [InlineData("Tier 2. It is a rename.", "Too high", 2, nameof(ExpectedTierSource.Stated))]
    [InlineData("T1, it is a greeting", "Too high", 1, nameof(ExpectedTierSource.Stated))]
    [InlineData("The local coder / mini models do this fine", "Too high", 2, nameof(ExpectedTierSource.TierName))]
    [InlineData("Something cheaper", "Too high", 3, nameof(ExpectedTierSource.Direction))]
    [InlineData("A stronger model", "Too low", 5, nameof(ExpectedTierSource.Direction))]
    [InlineData("Tier 9 please", "Other", null, nameof(ExpectedTierSource.None))]
    public void The_expected_tier_is_stated_named_or_one_step_from_the_reported_one(string expected, string direction, int? tier, string source)
    {
        MisrouteReport report = MisrouteReport.Parse(Body(expected: expected, direction: direction));

        MisrouteTriage.ExpectedTier(report, RouterConfiguration.FromJson(RouterHost.Template())).Should().Be((tier, Enum.Parse<ExpectedTierSource>(source)));
    }

    [Theory]
    [InlineData("0.2.1", 1)]
    [InlineData("v0.1.0", 1)]
    [InlineData("0.3.0", 2)]
    [InlineData("0.6.1 (desktop)", 2)]
    public void A_release_uses_the_template_it_shipped(string version, int template)
    {
        var notes = new List<string>();

        MisrouteTriage.TemplateFor(MisrouteReport.Release(version), notes).Should().Be(template);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void A_report_without_a_release_uses_the_current_template_and_says_so()
    {
        var notes = new List<string>();

        MisrouteTriage.TemplateFor(MisrouteReport.Release("main at abc1234"), notes).Should().Be(TemplateMigration.Current);
        notes.Should().ContainSingle().Which.Should().Contain("names no release version");
        MisrouteTriage.Releases[^1].Template.Should().Be(TemplateMigration.Current, "a new template needs a line naming the release that ships it");
    }

    [Fact]
    public async Task An_old_release_is_rerun_on_its_own_template()
    {
        (int exit, string comment, _) = await RunAsync(Body(version: "0.2.0"), "--embeddings", "Hashing");

        exit.Should().Be(0);
        comment.Should().Contain("on template 1 (release `0.2.0`)");
        comment.Should().Contain($"| Without the line | {EvaluationSet.Parse(TemplateMigration.EvaluationTemplate(1)).Count} |");
    }

    [Fact]
    public void Embeddings_that_cannot_run_here_are_replaced_by_the_default_onnx_model_with_a_note()
    {
        var notes = new List<string>();
        MisrouteReport report = MisrouteReport.Parse(Body(embeddings: "OpenAICompatible, nomic-embed-text @octocat, threshold 0.62"));

        MisrouteTriage.ChooseEmbeddings(report, choice: null, notes).Should().Be(("Onnx", "all-MiniLM-L6-v2"));
        notes.Should().ContainSingle().Which.Should().Contain("`OpenAICompatible, nomic-embed-text @octocat, threshold 0.62`");
        MisrouteTriage.ChooseEmbeddings(report, "hashing", notes).Should().Be(("Hashing", (string?)null));
        MisrouteTriage.ChooseEmbeddings(report, "Onnx:bge-small-en-v1.5", notes).Should().Be(("Onnx", "bge-small-en-v1.5"));
    }

    [Theory]
    [InlineData(new[] { "triage-misroute" }, "needs the issue body")]
    [InlineData(new[] { "triage-misroute", "body.md", "--embeddings", "OpenAICompatible" }, "--embeddings takes Hashing, Onnx or Onnx:<model>")]
    [InlineData(new[] { "triage-misroute", "body.md", "--embeddings", "Onnx:unknown" }, "--embeddings takes")]
    [InlineData(new[] { "triage-misroute", "body.md", "--issue", "12; rm -rf /" }, "--issue needs an issue number.")]
    [InlineData(new[] { "triage-misroute", "body.md", "--router-version", "latest" }, "--router-version needs a release version")]
    public void The_command_line_is_checked(string[] args, string error)
    {
        Settings(args).Error.Should().Contain(error);
    }

    [Fact]
    public void The_command_line_takes_a_file_or_a_dash_and_its_options()
    {
        HostSettings settings = Settings("triage-misroute", "-", "--json", "--issue", "375", "--router-version", "0.5.0", "--embeddings", "onnx:bge-small-en-v1.5");

        settings.Error.Should().BeNull();
        settings.Command.Should().Be(HostSettings.TriageCommand);
        (settings.TriageInput, settings.Json, settings.TriageIssue, settings.TriageRouterVersion, settings.TriageEmbeddings)
            .Should().Be(("-", true, 375, "0.5.0", "onnx:bge-small-en-v1.5"));
    }

    /// <summary>The Markdown without its code blocks and code spans: what GitHub renders as Markdown and HTML.</summary>
    private static string OutsideCode(string markdown)
    {
        string withoutBlocks = FencedBlock().Replace(markdown, string.Empty);
        return CodeSpan().Replace(withoutBlocks, string.Empty);
    }

    [GeneratedRegex(@"^\s*id:\s*(?<id>[\w-]+)\s*\n\s*attributes:\s*\n\s*label:\s*(?<label>.+)$", RegexOptions.Multiline)]
    private static partial Regex FormField();

    [GeneratedRegex(@"^(?<fence>`{3,})[^\n]*\n[\s\S]*?\n\k<fence>$", RegexOptions.Multiline)]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex CodeSpan();
}
