namespace SemanticRouter.Tests.Core;

public class TokenEstimatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("hello", 1)]
    [InlineData("hello world", 2)]
    [InlineData("Hello, world!", 4)]
    [InlineData("internationalization", 4)]
    public void Estimates_simple_inputs(string text, int expected)
    {
        TokenEstimator.Estimate(text).Should().Be(expected);
    }

    [Fact]
    public void Repeated_symbols_collapse_into_one_token()
    {
        TokenEstimator.Estimate("==========").Should().Be(1);
    }

    [Fact]
    public void Each_cjk_character_counts_as_a_token()
    {
        TokenEstimator.Estimate("你好世界").Should().Be(4);
    }

    [Fact]
    public void English_prose_is_roughly_four_characters_per_token()
    {
        const string prose =
            "The quick brown fox jumps over the lazy dog while the farmer watches from the porch, " +
            "sipping coffee and wondering whether the rain will hold off long enough to bring in the hay.";

        int estimate = TokenEstimator.Estimate(prose);

        // ~40 tokens with GPT-style tokenizers.
        estimate.Should().BeInRange(32, 48);
    }

    [Fact]
    public void Code_is_denser_than_prose()
    {
        const string code = "for (int i = 0; i < items.Length; i++) { total += items[i].Price * items[i].Qty; }";

        TokenEstimator.Estimate(code).Should().BeGreaterThan(code.Length / 4);
    }
}
