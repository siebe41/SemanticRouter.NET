using Moq;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class ClassificationCacheTests
{
    private static readonly Dictionary<string, float[]> Table = new()
    {
        ["hello"] = [1f, 0f],
        ["design a system"] = [0f, 1f],
        ["hi friend"] = [0.95f, 0.1f],
        ["hi   friend "] = [0.95f, 0.1f],
        ["architect a platform"] = [0.1f, 0.95f],
        ["something else"] = [0.7f, 0.7f],
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (SemanticPromptClassifier Classifier, Mock<IEmbeddingProvider> Provider, Clock Clock) Create(Action<SemanticRouterOptions>? configure = null)
    {
        SemanticRouterOptions options = Options(Route("smalltalk", 1, "hello"), Route("reasoning", 4, "design a system"));
        configure?.Invoke(options);
        Mock<IEmbeddingProvider> provider = FromTable(Table);
        var clock = new Clock();
        return (new SemanticPromptClassifier(provider.Object, options, new SemanticPromptClassifierDependencies { Time = clock }), provider, clock);
    }

    private static int PromptEmbeddings(Mock<IEmbeddingProvider> provider) =>
        provider.Invocations.Count(i => i.Method.Name == nameof(IEmbeddingProvider.GenerateEmbeddingAsync));

    [Fact]
    public async Task A_repeated_prompt_is_answered_without_an_embedding_call()
    {
        (SemanticPromptClassifier classifier, Mock<IEmbeddingProvider> provider, _) = Create();

        PromptClassificationResult first = await classifier.ClassifyAsync("hi friend");
        PromptClassificationResult second = await classifier.ClassifyAsync("hi friend");
        PromptClassificationResult spaced = await classifier.ClassifyAsync("hi   friend ");

        PromptEmbeddings(provider).Should().Be(1, "whitespace differences normalise to the same prompt");
        first.Cached.Should().BeFalse();
        second.Cached.Should().BeTrue();
        spaced.Cached.Should().BeTrue();
        second.Should().BeEquivalentTo(first, o => o.Excluding(r => r.Cached).Excluding(r => r.Elapsed));
        classifier.CacheStatistics.Should().Be(new ClassificationCacheStatistics { Hits = 2, Misses = 1, Entries = 1, Capacity = 2048 });
        classifier.CacheStatistics!.HitRate.Should().BeApproximately(2 / 3d, 1e-9);
    }

    [Fact]
    public async Task Old_and_least_recently_used_decisions_are_dropped()
    {
        (SemanticPromptClassifier classifier, Mock<IEmbeddingProvider> provider, Clock clock) = Create(o =>
        {
            o.ClassificationCache.MaxEntries = 2;
            o.ClassificationCache.MaxAge = TimeSpan.FromMinutes(1);
        });

        await classifier.ClassifyAsync("hi friend");
        await classifier.ClassifyAsync("architect a platform");
        await classifier.ClassifyAsync("hi friend");            // hit: now the most recent
        await classifier.ClassifyAsync("something else");       // evicts "architect a platform"
        await classifier.ClassifyAsync("architect a platform"); // miss
        PromptEmbeddings(provider).Should().Be(4);

        clock.Now += TimeSpan.FromMinutes(2);
        (await classifier.ClassifyAsync("architect a platform")).Cached.Should().BeFalse("the decision expired");
        PromptEmbeddings(provider).Should().Be(5);
        classifier.CacheStatistics!.Entries.Should().Be(2);
    }

    [Fact]
    public async Task The_cache_can_be_turned_off()
    {
        (SemanticPromptClassifier classifier, Mock<IEmbeddingProvider> provider, _) = Create(o => o.ClassificationCache.Enabled = false);

        await classifier.ClassifyAsync("hi friend");
        PromptClassificationResult second = await classifier.ClassifyAsync("hi friend");

        PromptEmbeddings(provider).Should().Be(2);
        second.Cached.Should().BeFalse();
        classifier.CacheStatistics.Should().BeNull();
    }

    [Fact]
    public async Task The_index_reports_its_size_and_build_time()
    {
        (SemanticPromptClassifier classifier, _, _) = Create();
        classifier.IndexStatistics.Should().BeNull();

        await classifier.WarmUpAsync();

        classifier.IndexStatistics!.Utterances.Should().Be(2);
        classifier.IndexStatistics.Dimensions.Should().Be(2);
        classifier.IndexStatistics.BuildTime.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
