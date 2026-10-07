namespace SemanticRouter.Tests.Core;

public class HeuristicComplexityAnalyzerTests
{
    private readonly HeuristicComplexityAnalyzer _analyzer = new();

    [Fact]
    public void Empty_prompt_has_zero_complexity()
    {
        _analyzer.Analyze("   ").Should().BeSameAs(ComplexityAnalysis.Empty);
    }

    [Theory]
    [InlineData("hi there!")]
    [InlineData("thanks, that's great")]
    [InlineData("translate 'good morning' to Spanish")]
    public void Simple_prompts_score_low(string prompt)
    {
        ComplexityAnalysis analysis = _analyzer.Analyze(prompt);

        analysis.Score.Should().BeLessThan(0.2);
        analysis.ContainsCode.Should().BeFalse();
    }

    [Fact]
    public void Detects_fenced_code_blocks()
    {
        const string prompt = """
            Why does this throw?

            ```csharp
            var x = items.First();
            Console.WriteLine(x);
            ```

            ~~~python
            print(1)
            ~~~
            """;

        ComplexityAnalysis analysis = _analyzer.Analyze(prompt);

        analysis.CodeBlockCount.Should().Be(2);
        analysis.CodeLineCount.Should().Be(3);
        analysis.ContainsCode.Should().BeTrue();
        analysis.CodeSignal.Should().BeGreaterThanOrEqualTo(0.8);
    }

    [Fact]
    public void Detects_unfenced_code_from_code_like_lines()
    {
        const string prompt = """
            public int Add(int a, int b) {
                return a + b;
            }
            """;

        ComplexityAnalysis analysis = _analyzer.Analyze(prompt);

        analysis.CodeBlockCount.Should().Be(0);
        analysis.ContainsCode.Should().BeTrue();
        analysis.CodeSignal.Should().Be(0.5);
    }

    [Fact]
    public void A_single_prose_sentence_ending_in_a_brace_is_not_code()
    {
        _analyzer.Analyze("Tell me about the history of curly braces {").ContainsCode.Should().BeFalse();
    }

    [Fact]
    public void Counts_structural_items_and_questions()
    {
        const string prompt = """
            # Requirements
            1. Parse the file
            2) Validate rows
            - log errors
            * retry failures
            What should the batch size be? Should it be configurable?
            """;

        ComplexityAnalysis analysis = _analyzer.Analyze(prompt);

        analysis.StructuralItemCount.Should().Be(5);
        analysis.QuestionCount.Should().Be(2);
        analysis.StructureSignal.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Reasoning_keywords_match_whole_words_only()
    {
        _analyzer.Analyze("Please analyze the trade-offs step by step").ReasoningKeywordCount.Should().Be(3);
        _analyzer.Analyze("The reprove and redesign meeting").ReasoningKeywordCount.Should().Be(0);
    }

    [Fact]
    public void Custom_reasoning_keywords_replace_the_defaults()
    {
        var analyzer = new HeuristicComplexityAnalyzer(new ComplexityHeuristicsOptions { ReasoningKeywords = ["actuarial"] });

        analyzer.Analyze("Run an actuarial projection").ReasoningKeywordCount.Should().Be(1);
        analyzer.Analyze("Analyze step by step").ReasoningKeywordCount.Should().Be(0);
    }

    [Fact]
    public void Long_multi_part_code_reasoning_prompt_scores_high()
    {
        string prompt = $$"""
            Analyze this service and explain why it deadlocks, step by step. Compare the trade-offs of the fixes.

            1. It uses a lock
            2. It retries on failure
            3. It caches results

            ```csharp
            public async Task RunAsync()
            {
                lock (_gate) { _cache.Clear(); }
                await Task.Delay(10);
            }
            ```

            {{string.Join(" ", Enumerable.Repeat("The service handles many concurrent requests under load.", 30))}}

            What is wrong? How would you fix it? What would you test?
            """;

        ComplexityAnalysis analysis = _analyzer.Analyze(prompt);

        analysis.Score.Should().BeGreaterThan(0.8);
        analysis.ContainsCode.Should().BeTrue();
        analysis.ReasoningKeywordCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Length_signal_saturates_at_the_configured_token_count()
    {
        var analyzer = new HeuristicComplexityAnalyzer(new ComplexityHeuristicsOptions { TokenSaturation = 100 });
        string prompt = string.Join(" ", Enumerable.Repeat("word", 500));

        analyzer.Analyze(prompt).LengthSignal.Should().Be(1.0);
    }

    [Fact]
    public void Score_is_bounded_and_monotonic_in_signals()
    {
        ComplexityAnalysis plain = _analyzer.Analyze("Summarize this text.");
        ComplexityAnalysis withReasoning = _analyzer.Analyze("Summarize this text and analyze the implications step by step.");

        withReasoning.Score.Should().BeGreaterThan(plain.Score);
        withReasoning.Score.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Zero_weights_disable_a_signal()
    {
        var analyzer = new HeuristicComplexityAnalyzer(new ComplexityHeuristicsOptions
        {
            LengthWeight = 0, CodeWeight = 0, StructureWeight = 0, ReasoningWeight = 0,
        });

        analyzer.Analyze("Analyze step by step:\n```\ncode();\n```").Score.Should().Be(0);
    }
}
