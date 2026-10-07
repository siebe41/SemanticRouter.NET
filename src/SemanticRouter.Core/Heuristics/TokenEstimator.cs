namespace SemanticRouter;

/// <summary>
/// Fast, tokenizer-free estimate of how many BPE tokens a text will consume.
/// </summary>
/// <remarks>
/// <para>
/// Real tokenizers are model specific and expensive to ship. For routing we only need an estimate
/// that is within roughly ±20% for English prose and code, so this scanner approximates common
/// BPE behaviour in a single allocation-free pass:
/// </para>
/// <list type="bullet">
///   <item>a word costs one token plus one per additional six characters (long identifiers split);</item>
///   <item>each punctuation or symbol run of a repeated character costs one token (<c>{</c>, <c>);</c>, <c>====</c>);</item>
///   <item>each CJK / kana / hangul character costs one token;</item>
///   <item>whitespace is free (BPE merges it into the following token).</item>
/// </list>
/// </remarks>
public static class TokenEstimator
{
    private const int CharactersPerAdditionalToken = 6;

    /// <summary>Estimates the token count of <paramref name="text"/>.</summary>
    public static int Estimate(ReadOnlySpan<char> text)
    {
        int tokens = 0;
        int wordLength = 0;
        char previousSymbol = '\0';

        foreach (char c in text)
        {
            if (IsIdeographic(c))
            {
                tokens += FlushWord(ref wordLength) + 1;
                previousSymbol = '\0';
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                wordLength++;
                previousSymbol = '\0';
            }
            else if (char.IsWhiteSpace(c))
            {
                tokens += FlushWord(ref wordLength);
                previousSymbol = '\0';
            }
            else
            {
                tokens += FlushWord(ref wordLength);
                if (c != previousSymbol)
                {
                    tokens++;
                }

                previousSymbol = c;
            }
        }

        return tokens + FlushWord(ref wordLength);
    }

    private static int FlushWord(ref int wordLength)
    {
        if (wordLength == 0)
        {
            return 0;
        }

        int tokens = 1 + ((wordLength - 1) / CharactersPerAdditionalToken);
        wordLength = 0;
        return tokens;
    }

    // CJK radicals through Hangul syllables, plus CJK compatibility ideographs. These scripts
    // are not whitespace delimited and BPE vocabularies typically spend ~1 token per character.
    private static bool IsIdeographic(char c) =>
        c is (>= '⺀' and <= '힯') or (>= '豈' and <= '﫿');
}
