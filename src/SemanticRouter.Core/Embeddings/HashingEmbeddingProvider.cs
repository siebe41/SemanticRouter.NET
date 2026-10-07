namespace SemanticRouter;

/// <summary>
/// A deterministic, in-memory <see cref="IEmbeddingProvider"/> based on the feature-hashing trick.
/// Intended for unit tests, local development and CI, where a real embedding model is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// Text is lower-cased and split into words. Word unigrams, word bigrams and character trigrams are
/// hashed (FNV-1a, so results are stable across processes and platforms) into a fixed number of
/// buckets with a hash-derived sign, and the vector is L2-normalized. Texts that share vocabulary
/// and morphology therefore land close together, which is enough to exercise routing end-to-end.
/// </para>
/// <para>
/// It captures <em>lexical</em> overlap only: "reset my password" and "I forgot my login" will not
/// be close. Use a real embedding model in production.
/// </para>
/// </remarks>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Default vector size.</summary>
    public const int DefaultDimensions = 512;

    private const float UnigramWeight = 1.0f;
    private const float BigramWeight = 0.5f;
    private const float TrigramWeight = 0.35f;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "for", "from", "how", "i", "in", "is", "it",
        "me", "my", "of", "on", "or", "please", "that", "the", "this", "to", "was", "what", "with", "you", "your",
    };

    /// <summary>Creates a provider producing vectors of <paramref name="dimensions"/> floats.</summary>
    public HashingEmbeddingProvider(int dimensions = DefaultDimensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 8);
        Dimensions = dimensions;
    }

    /// <summary>The dimensionality of produced vectors.</summary>
    public int Dimensions { get; }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ReadOnlyMemory<float>>(Embed(text));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new ReadOnlyMemory<float>[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = Embed(texts[i]);
        }

        return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
    }

    /// <summary>Synchronously embeds <paramref name="text"/>.</summary>
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        float[] vector = new float[Dimensions];
        string? previous = null;

        foreach (string word in Tokenize(text))
        {
            bool isStopWord = StopWords.Contains(word);
            if (!isStopWord)
            {
                Accumulate(vector, Fnv1a("w:", word), UnigramWeight);
                AccumulateTrigrams(vector, word);
            }

            if (previous is not null)
            {
                Accumulate(vector, Fnv1a("b:", string.Concat(previous, " ", word)), BigramWeight);
            }

            previous = word;
        }

        VectorMath.Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool isWordChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (!isWordChar && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static void AccumulateTrigrams(float[] vector, string word)
    {
        // Pad with boundary markers so prefixes/suffixes ("^ru", "ng$") carry signal.
        string padded = string.Concat("^", word, "$");
        for (int i = 0; i + 3 <= padded.Length; i++)
        {
            Accumulate(vector, Fnv1a("c:", padded.AsSpan(i, 3)), TrigramWeight);
        }
    }

    private static void Accumulate(float[] vector, ulong hash, float weight)
    {
        int bucket = (int)(hash % (ulong)vector.Length);
        float sign = ((hash >> 63) & 1) == 0 ? 1f : -1f;
        vector[bucket] += sign * weight;
    }

    private static ulong Fnv1a(ReadOnlySpan<char> prefix, ReadOnlySpan<char> value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (char c in prefix)
        {
            hash = (hash ^ c) * prime;
        }

        foreach (char c in value)
        {
            hash = (hash ^ c) * prime;
        }

        // FNV's low bits avalanche poorly; apply the SplitMix64 finalizer before taking a modulus.
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;
        return hash;
    }
}
