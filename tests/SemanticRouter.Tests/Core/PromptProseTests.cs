using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

/// <summary>Only the prose of a prompt is compared with the utterances; pasted code and logs are left out.</summary>
public class PromptProseTests
{
    private const string Function = """
        def parse_duration(text):
            units = {"d": 86400, "h": 3600, "m": 60, "s": 1}
            total = 0
            number = ""
            for ch in text.strip().lower():
                if ch.isdigit():
                    number += ch
                elif ch in units:
                    total += int(number) * units[ch]
                    number = ""
            return total
        """;

    [Theory]
    [InlineData("write a unit test for this function")]
    [InlineData("why does `Task.Result` deadlock in `LoadAsync`?")]
    [InlineData("first line\nsecond line\n\nthird line")]
    [InlineData("- add a test\n- fix the build\n- update the docs\n- bump the version\n- tag it\n- write the notes\n- publish\n- announce")]
    [InlineData("Dear team,\nthe release is late,\nbecause the build broke (again),\nand nobody noticed,\nuntil Friday,\nwhen we tried,\nto ship it,\nso sorry.")]
    [InlineData("")]
    public void Prompts_without_code_are_used_as_they_are(string prompt) =>
        PromptProse.Extract(prompt).Should().BeSameAs(prompt);

    [Fact]
    public void Fenced_blocks_are_left_out_and_the_prose_around_them_kept()
    {
        string prompt = $"write a unit test for this function\n\n```python\n{Function}\n```\nuse pytest, please";

        PromptProse.Extract(prompt).Should().Be("write a unit test for this function\n\nuse pytest, please");
    }

    [Fact]
    public void Several_small_fences_and_tilde_fences_are_all_left_out()
    {
        string prompt = "rename `a` to `b` in\n```js\nlet a = 1;\n```\nand in\n~~~\nconsole.log(a);\n~~~\n  ```\n  a++;\n  ```\nplease";

        PromptProse.Extract(prompt).Should().Be("rename `a` to `b` in\nand in\nplease");
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end()
    {
        PromptProse.Extract("explain this\r\n```\r\nint x = 1;\r\nint y = 2;").Should().Be("explain this");
    }

    [Fact]
    public void A_fence_is_closed_only_by_as_many_of_the_same_character()
    {
        string prompt = "what does this print?\n````md\n```\nnot the end\n~~~\n````\nthanks";

        PromptProse.Extract(prompt).Should().Be("what does this print?\nthanks");
    }

    [Fact]
    public void Triple_backticks_inside_a_line_are_inline_code()
    {
        string prompt = "why does ```x = 1``` fail?\nit says ```SyntaxError```";

        PromptProse.Extract(prompt).Should().BeSameAs(prompt);
    }

    [Fact]
    public void A_long_unfenced_paste_is_left_out()
    {
        string prompt = $"write a unit test for this function\n\n{Function}\nthanks!";

        PromptProse.Extract(prompt).Should().Be("write a unit test for this function\n\nthanks!");
    }

    [Fact]
    public void Pasted_logs_and_stack_traces_are_left_out()
    {
        string log = string.Join('\n', Enumerable.Range(0, 9).Select(i => $"2024-05-01 12:00:0{i} ERROR [worker-{i}] connection to db:5432 refused"));
        string trace = "System.InvalidOperationException: Sequence contains no elements\n" + string.Join('\n', Enumerable.Range(0, 8).Select(i => $"   at App.Service{i}.Run() in /src/Service{i}.cs:line {i + 10}"));

        PromptProse.Extract($"why does the worker keep failing?\n{log}").Should().Be("why does the worker keep failing?");
        PromptProse.Extract($"what causes this?\n{trace}").Should().Be("what causes this?\nSystem.InvalidOperationException: Sequence contains no elements");
    }

    [Fact]
    public void A_short_unfenced_snippet_stays_part_of_the_question()
    {
        string prompt = "why is this slow?\nfor (int i = 0; i < n; i++) {\n    total += Lookup(i);\n}\nit takes seconds";

        PromptProse.Extract(prompt).Should().BeSameAs(prompt);
    }

    [Theory]
    [InlineData("```csharp\nvar x = 1;\nvar y = 2;\n```")]
    [InlineData("fix:\n```\nvar x = 1;\n```")]
    [InlineData("```\nunclosed")]
    public void A_prompt_that_is_only_code_is_used_whole(string prompt) =>
        PromptProse.Extract(prompt).Should().BeSameAs(prompt, "fewer than two words of prose say less than the code");

    [Fact]
    public async Task Code_pasted_under_an_instruction_does_not_drown_it()
    {
        SemanticRouterOptions options = Options(
            Route("simple-code", 2, "write a unit test for this function"),
            Route("smalltalk", 1, "hello, how are you?"));
        options.DefaultSimilarityThreshold = 0.5f;
        var embeddings = new HashingEmbeddingProvider(512);
        string fenced = $"write a unit test for this function\n```python\n{Function}\n```";
        string unfenced = $"write a unit test for this function:\n{Function}";

        PromptClassificationResult withProse = await new SemanticPromptClassifier(embeddings, options).ClassifyAsync(fenced);
        PromptClassificationResult pasted = await new SemanticPromptClassifier(embeddings, options).ClassifyAsync(unfenced);
        options.EmbedProseOnly = false;
        PromptClassificationResult whole = await new SemanticPromptClassifier(embeddings, options).ClassifyAsync(fenced);

        withProse.Should().Match<PromptClassificationResult>(r => r.RouteName == "simple-code" && r.RequiredTier == 2 && r.Similarity > 0.99f);
        pasted.RouteName.Should().Be("simple-code");
        withProse.Complexity.ContainsCode.Should().BeTrue("complexity still reads the whole prompt");
        whole.Reason.Should().Be(RoutingReason.NoMatchFallback, "embedded with its code, the prompt matches nothing");
    }

    [Fact]
    public async Task Evaluation_embeds_prompts_the_way_routing_does()
    {
        SemanticRouterOptions options = Options(Route("simple-code", 2, "write a unit test for this function"));
        options.DefaultSimilarityThreshold = 0.5f;
        options.FallbackTier = 3;
        var evaluator = new RoutingEvaluator(new HashingEmbeddingProvider(512), options);

        EvaluationReport report = await evaluator.EvaluateAsync([new EvaluationCase { Prompt = $"write a unit test for this function\n```python\n{Function}\n```", ExpectedTier = 2 }]);

        report.Correct.Should().Be(1);
    }

    private static readonly string Records = string.Join('\n', Enumerable.Range(1, 12).Select(i =>
        $"{1000 + i}, customer record, account number {4400 + i}, social security number {i:000}-55-0199, invoices for customer id {i}"));

    private static SemanticRouterOptions WithInternalRecords()
    {
        RouteDefinition records = Route(
            "internal-records", 1,
            "look up the customer record for account number", "find the social security number for this customer", "list the invoices for customer id");
        records.LocalOnly = true;
        records.SimilarityThreshold = 0.35f;
        SemanticRouterOptions options = Options(records, Route("rewrite-summarize", 1, "summarize these lines for me"));
        options.DefaultSimilarityThreshold = 0.5f;
        return options;
    }

    [Fact]
    public async Task Private_data_pasted_under_an_instruction_still_matches_a_local_only_route()
    {
        SemanticRouterOptions options = WithInternalRecords();
        var embeddings = new CountingEmbeddings(new HashingEmbeddingProvider(512));
        var classifier = new SemanticPromptClassifier(embeddings, options);
        string prompt = $"summarize these lines for me\n{Records}";
        PromptProse.Extract(prompt).Should().Be("summarize these lines for me", "the records are a pasted block");

        PromptClassificationResult result = await classifier.ClassifyAsync(prompt);
        PromptClassificationResult prose = await new SemanticPromptClassifier(new HashingEmbeddingProvider(512), options).ClassifyAsync("summarize these lines for me");

        prose.LocalOnly.Should().BeFalse("the instruction alone matches no local-only route");
        result.LocalOnly.Should().BeTrue("the whole prompt, records included, matches internal-records");
        (result.RouteName, result.RequiredTier).Should().Be(("rewrite-summarize", 1), "route, tier and score come from the prose");
        embeddings.Calls.Should().Equal(["batch 4", "batch 2"], "the utterances, then the prose and the whole prompt in one call");
    }

    [Fact]
    public async Task The_whole_prompt_is_embedded_again_only_when_something_was_left_out_and_a_route_is_local_only()
    {
        var embeddings = new CountingEmbeddings(new HashingEmbeddingProvider(512));
        SemanticRouterOptions options = WithInternalRecords();
        options.Routes[0].LocalOnly = false;
        await new SemanticPromptClassifier(embeddings, options).ClassifyAsync($"summarize these lines for me\n{Records}");
        embeddings.Calls.Should().Equal(["batch 4", "one"], "no route is local-only");

        embeddings.Calls.Clear();
        await new SemanticPromptClassifier(embeddings, WithInternalRecords()).ClassifyAsync("summarize these lines for me");
        embeddings.Calls.Should().Equal(["batch 4", "one"], "nothing was left out");
    }

    [Fact]
    public async Task Evaluation_keeps_the_local_only_match_of_the_whole_prompt()
    {
        var evaluator = new RoutingEvaluator(new HashingEmbeddingProvider(512), WithInternalRecords());

        EvaluationReport report = await evaluator.EvaluateAsync([new EvaluationCase { Prompt = $"summarize these lines for me\n{Records}", ExpectedTier = 1, LocalOnly = true }]);

        report.LocalOnlyMissed.Should().Be(0);
        report.Results.Single().LocalOnly.Should().BeTrue();
    }

    private sealed class CountingEmbeddings(IEmbeddingProvider inner) : IEmbeddingProvider
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls.Add("one");
            return inner.GenerateEmbeddingAsync(text, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Calls.Add($"batch {texts.Count}");
            return inner.GenerateEmbeddingsAsync(texts, cancellationToken);
        }
    }
}
