using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Corrections by meaning (#67): a short follow-up that no phrase matches is compared with example corrections and
/// example next steps, using the routing embedding model, after the request has gone on. Only candidates cost an
/// embedding call.
/// </summary>
public sealed class CorrectionDetectorTests : RouterHostTest
{
    private readonly ConceptEmbeddings _embeddings = new();

    private RoutingFeedbackLog Feedback => Services.GetRequiredService<RoutingFeedbackLog>();

    private Task<HttpClient> StartDispatchAsync(bool hashing = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                if (!hashing)
                {
                    s.AddSingleton<IEmbeddingProvider>(_embeddings);
                }
            });

    private static StringContent Chat(params string[] userTurns)
    {
        var messages = new List<object>();
        for (int i = 0; i < userTurns.Length; i++)
        {
            messages.Add(new { role = "user", content = userTurns[i] });
            if (i < userTurns.Length - 1)
            {
                messages.Add(new { role = "assistant", content = "answer " + i });
            }
        }

        return new StringContent(JsonSerializer.Serialize(new { model = "auto", messages }), Encoding.UTF8, "application/json");
    }

    /// <summary>A new conversation (its own opening, so it is not a retry) and a follow-up to its answer.</summary>
    private async Task FollowUpAsync(HttpClient client, string followUp)
    {
        string opening = "hello there " + Guid.NewGuid().ToString("N");
        (await client.PostAsync("/v1/chat/completions", Chat(opening))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/v1/chat/completions", Chat(opening, followUp))).StatusCode.Should().Be(HttpStatusCode.OK);
        await Services.GetRequiredService<CorrectionDetector>().Pending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_follow_up_that_means_a_correction_is_feedback_on_the_previous_turn()
    {
        HttpClient client = await StartDispatchAsync();

        await FollowUpAsync(client, ConceptEmbeddings.Correction);

        RoutingFeedback corrected = Feedback.Recent().Should().ContainSingle().Subject;
        (corrected.Kind, corrected.Tier, corrected.NeededTier).Should().Be((FeedbackKind.Corrected, 1, (int?)2));
        corrected.Detail.Should().EndWith("(by meaning)");
    }

    [Fact]
    public async Task A_next_step_or_an_unrelated_follow_up_is_not()
    {
        HttpClient client = await StartDispatchAsync();

        await FollowUpAsync(client, ConceptEmbeddings.NextStep);
        await FollowUpAsync(client, ConceptEmbeddings.Ambiguous);

        Feedback.Recent().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_candidates_cost_an_embedding_call()
    {
        HttpClient client = await StartDispatchAsync();
        string longRequest = "please rewrite the importer so that it streams the file instead of loading it into memory, and keep the progress reporting " + new string('x', 40);

        await FollowUpAsync(client, longRequest);
        await FollowUpAsync(client, "that's wrong, use the other table");
        await FollowUpAsync(client, "how does this work?");

        _embeddings.Asked.Should().NotContain([longRequest, "that's wrong, use the other table", "how does this work?"],
            "a long message is a new request, a known phrase needs no comparison and a question is not a correction");
        Feedback.Recent().Should().ContainSingle(f => f.Kind == FeedbackKind.Corrected).Which.Detail.Should().NotContain("by meaning");
    }

    [Fact]
    public async Task The_lexical_hashing_embeddings_are_never_used_for_it()
    {
        HttpClient client = await StartDispatchAsync(hashing: true);

        await FollowUpAsync(client, ConceptEmbeddings.Correction);

        Feedback.Recent().Should().BeEmpty("hashing embeddings match words, not meaning");
    }

    [Fact]
    public async Task The_examples_are_embedded_once_per_model()
    {
        var detector = new CorrectionDetector(null!, NullLogger<CorrectionDetector>.Instance);
        var options = new FeedbackOptions();

        (await detector.MatchesAsync(_embeddings, ConceptEmbeddings.Correction, options, CancellationToken.None)).Should().BeTrue();
        (await detector.MatchesAsync(_embeddings, ConceptEmbeddings.NextStep, options, CancellationToken.None)).Should().BeFalse();
        (await detector.MatchesAsync(_embeddings, ConceptEmbeddings.Ambiguous, options, CancellationToken.None)).Should().BeFalse("it must be clearly closer to the corrections");
        (await detector.MatchesAsync(_embeddings, ConceptEmbeddings.Correction, new FeedbackOptions { CorrectionSimilarity = 0.99f }, CancellationToken.None)).Should().BeFalse();

        _embeddings.Batches.Should().Be(2, "the corrections and the other examples, once");
    }

    [Theory]
    [InlineData("klopt niet helemaal", true)]
    [InlineData("how does this work?", false)]
    [InlineData("that's wrong", false)]
    [InlineData("", false)]
    public void Candidates_are_short_follow_ups_that_no_phrase_matched_and_not_questions(string prompt, bool expected) =>
        CorrectionDetector.IsCandidate(prompt).Should().Be(expected);

    /// <summary>
    /// Embeddings with three directions: the example corrections, the example next steps, and everything else. The test
    /// follow-ups sit near one of them, or between the first two.
    /// </summary>
    private sealed class ConceptEmbeddings : IEmbeddingProvider
    {
        public const string Correction = "zo wil ik het niet hebben";
        public const string NextStep = "doe nu de documentatie";
        public const string Ambiguous = "kijk er nog eens naar";

        public ConcurrentQueue<string> Asked { get; } = new();

        public int Batches { get; private set; }

        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            Asked.Enqueue(text);
            return ValueTask.FromResult(Vector(text));
        }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Batches += texts.Any(t => CorrectionDetector.CorrectionExamples.Contains(t) || CorrectionDetector.OtherExamples.Contains(t)) ? 1 : 0;
            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([.. texts.Select(Vector)]);
        }

        private static ReadOnlyMemory<float> Vector(string text) => text switch
        {
            _ when CorrectionDetector.CorrectionExamples.Contains(text) => new[] { 1f, 0f, 0f },
            _ when CorrectionDetector.OtherExamples.Contains(text) => new[] { 0f, 1f, 0f },
            Correction => new[] { 0.9f, 0.2f, 0.1f },
            NextStep => new[] { 0.2f, 0.9f, 0.1f },
            Ambiguous => new[] { 0.8f, 0.7f, 0f },
            _ => new[] { 0f, 0f, 1f },
        };
    }
}
