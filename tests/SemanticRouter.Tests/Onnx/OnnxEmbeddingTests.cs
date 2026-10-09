using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Onnx;

/// <summary>
/// In-process ONNX embeddings. The model tests download the pinned models once into a shared cache
/// (<c>SEMANTIC_ROUTER_TEST_MODELS</c>, or the temp directory), which CI keeps between runs.
/// </summary>
public sealed class OnnxEmbeddingTests : IDisposable
{
    private static readonly string ModelCache = Environment.GetEnvironmentVariable("SEMANTIC_ROUTER_TEST_MODELS") is { Length: > 0 } cache
        ? cache
        : Path.Combine(Path.GetTempPath(), "semantic-router-test-models");

    private readonly string _directory = Directory.CreateTempSubdirectory("router-onnx").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static OnnxModelInfo Fake(string model, string vocab) => new(
        "fake", "test/fake", "abc", "onnx/model.onnx", Sha(model), model.Length, Sha(vocab), EmbeddingPooling.Mean, 4, 0.5f, "fake");

    private static StubHttpMessageHandler Serving(string model, string vocab) => new((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("vocab.txt", StringComparison.Ordinal) ? vocab : model),
    });

    [Fact]
    public async Task Downloads_once_verifies_checksums_and_replaces_corrupt_files()
    {
        StubHttpMessageHandler http = Serving("model-bytes", "vocab-bytes");
        OnnxModelInfo info = Fake("model-bytes", "vocab-bytes");

        (string model, string vocab) = await OnnxModelDownloader.EnsureAsync(info, _directory, new HttpClient(http));
        await OnnxModelDownloader.EnsureAsync(info, _directory, new HttpClient(http));

        (await File.ReadAllTextAsync(model)).Should().Be("model-bytes");
        (await File.ReadAllTextAsync(vocab)).Should().Be("vocab-bytes");
        model.Should().Be(Path.Combine(_directory, "fake", "model.onnx"));
        http.Requests.Select(r => r.Request.RequestUri!.ToString()).Should().Equal(
            "https://huggingface.co/test/fake/resolve/abc/onnx/model.onnx", "https://huggingface.co/test/fake/resolve/abc/vocab.txt");

        await File.WriteAllTextAsync(model, "tampered");
        await OnnxModelDownloader.EnsureAsync(info, _directory, new HttpClient(http));
        (await File.ReadAllTextAsync(model)).Should().Be("model-bytes");
        http.Requests.Should().HaveCount(3, "only the corrupt file is fetched again");
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_refused_and_not_kept()
    {
        OnnxModelInfo info = Fake("expected", "vocab");

        Func<Task> act = () => OnnxModelDownloader.EnsureAsync(info, _directory, new HttpClient(Serving("something else", "vocab")));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*SHA-256*expected*");
        Directory.GetFiles(Path.Combine(_directory, "fake")).Should().BeEmpty("neither the bad file nor a temporary one stays behind");
    }

    [Fact]
    public void Known_models_are_pinned()
    {
        OnnxEmbeddingModels.Known.Should().OnlyContain(m => m.Revision.Length == 40 && m.ModelSha256.Length == 64 && m.VocabSha256.Length == 64);
        OnnxEmbeddingModels.Find(null)!.Name.Should().Be("all-MiniLM-L6-v2");
        OnnxEmbeddingModels.Find("BGE-small-en-v1.5")!.Pooling.Should().Be(EmbeddingPooling.Cls);
        OnnxEmbeddingModels.Find("nope").Should().BeNull();
    }

    [Fact]
    public async Task Unknown_models_and_a_missing_package_say_what_to_do()
    {
        var factory = new OnnxEmbeddingProviderFactory(new ServiceCollection().BuildServiceProvider(), Options.Create(new OnnxEmbeddingOptions { ModelsDirectory = _directory }));

        IEmbeddingProvider unknown = factory.Create(new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx, Model = "nope" });

        (await FluentActions.Awaiting(() => unknown.GenerateEmbeddingAsync("hi").AsTask()).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*'nope' is not a known ONNX model (all-MiniLM-L6-v2, bge-small-en-v1.5)*ModelPath*");

        IEmbeddingProvider missing = factory.Create(new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx, ModelPath = Path.Combine(_directory, "absent") });
        (await FluentActions.Awaiting(() => missing.GenerateEmbeddingAsync("hi").AsTask()).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*could not be loaded*");
    }

    [Fact]
    public async Task Without_the_package_the_onnx_choice_fails_with_advice_instead_of_breaking_the_router()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(o => o.Routes.Add(TestEmbeddings.Route("smalltalk", 1, "hello"))).Services
            .Configure<EmbeddingProviderOptions>(o => o.Provider = EmbeddingProviderKind.Onnx);
        await using ServiceProvider provider = services.BuildServiceProvider();

        IEmbeddingProvider embeddings = provider.GetRequiredService<RouterRuntimeProvider>().Current.EmbeddingProvider;

        (await FluentActions.Awaiting(() => embeddings.GenerateEmbeddingAsync("hi").AsTask()).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SemanticRouter.Embeddings.Onnx*UseOnnxEmbeddings*");
    }

    private static OnnxEmbeddingProviderFactory RealFactory() => new(
        new ServiceCollection().AddHttpClient().BuildServiceProvider(),
        Options.Create(new OnnxEmbeddingOptions { ModelsDirectory = ModelCache }));

    [Fact]
    public async Task MiniLM_embeds_meaning_not_just_shared_words()
    {
        using OnnxEmbeddingProviderFactory factory = RealFactory();
        IEmbeddingProvider provider = factory.Create(new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx });

        IReadOnlyList<ReadOnlyMemory<float>> vectors = await provider.GenerateEmbeddingsAsync(
            ["hello, how are you?", "hey there, how's it going", "why does this async code deadlock", ""]);

        vectors.Should().OnlyContain(v => v.Length == 384);
        VectorMath.Norm(vectors[0].Span).Should().BeApproximately(1f, 1e-4f);
        float greeting = VectorMath.CosineSimilarity(vectors[0].Span, vectors[1].Span);
        float unrelated = VectorMath.CosineSimilarity(vectors[0].Span, vectors[2].Span);
        // The values sentence-transformers' reference pipeline (Python tokenizers + onnxruntime) gives for these texts.
        greeting.Should().BeApproximately(0.539f, 0.002f, "the two greetings share almost no words but mean the same");
        unrelated.Should().BeApproximately(0.098f, 0.002f);
        vectors[0].Span[..3].ToArray().Should().BeEquivalentTo(new[] { 0.01194f, 0.04912f, 0.09315f }, o => o.Using<float>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-4f)).WhenTypeIs<float>());

        ReadOnlyMemory<float> single = await provider.GenerateEmbeddingAsync("hello, how are you?");
        single.ToArray().Should().Equal(vectors[0].ToArray(), "a text gets the same vector alone or in a batch");
    }

    [Fact]
    public async Task A_loaded_model_is_shared_by_every_configuration_version()
    {
        using OnnxEmbeddingProviderFactory factory = RealFactory();
        var options = new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx, Model = "bge-small-en-v1.5" };

        ReadOnlyMemory<float> first = await factory.Create(options).GenerateEmbeddingAsync("summarize this email");
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ReadOnlyMemory<float> second = await factory.Create(options).GenerateEmbeddingAsync("summarize this email");

        System.Diagnostics.Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(1), "the second provider reuses the loaded model");
        first.ToArray().Should().Equal(second.ToArray());
    }

    /// <summary>
    /// The shipped evaluation set (chat and agentic-client turns) against the template routes, with each ONNX model at
    /// the threshold <c>init</c> sets for it: CI fails if routing out of the box regresses below 90%.
    /// </summary>
    [Theory]
    [InlineData("all-MiniLM-L6-v2")]
    [InlineData("bge-small-en-v1.5")]
    public async Task With_no_embedding_server_the_onnx_model_routes_the_sample_prompts_to_their_tiers(string name)
    {
        RouterConfiguration template = RouterConfiguration.FromJson(RouterHost.Template());
        OnnxModelInfo model = OnnxEmbeddingModels.Find(name)!;
        template.DefaultSimilarityThreshold = model.SuggestedThreshold;
        using OnnxEmbeddingProviderFactory factory = RealFactory();
        IEmbeddingProvider embeddings = factory.Create(new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx, Model = name });
        var evaluator = new RoutingEvaluator(embeddings, template);
        IReadOnlyList<EvaluationCase> cases = EvaluationSet.Parse(RouterHost.EvaluationTemplate());

        EvaluationReport report = await evaluator.EvaluateAsync(cases);

        cases.Should().HaveCountGreaterThanOrEqualTo(80);
        report.Accuracy.Should().BeGreaterThanOrEqualTo(0.9, string.Join("; ", report.Misroutes.Select(m => $"{m.Case.Prompt}: {m.ActualTier}")));
        report.PerTier.Should().OnlyContain(t => t.Accuracy >= 0.75);
        report.TooLow.Should().BeLessThanOrEqualTo(report.Total / 10, "few prompts may go to a weaker model than they need");
    }
}
