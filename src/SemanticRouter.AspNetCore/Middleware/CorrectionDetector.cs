using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Recognises corrections by meaning, for the wording and languages <see cref="Corrections"/> has no phrase for: a short
/// follow-up to an answer is compared, with the routing embedding model, against example corrections and against example
/// follow-ups that are not corrections, in several languages. It counts when it is close to a correction
/// (<see cref="FeedbackOptions.CorrectionSimilarity"/>) and clearly closer to the corrections than to the rest
/// (<see cref="FeedbackOptions.CorrectionMargin"/>). Only candidates cost an embedding call, and the check runs after the
/// request has gone on, so it never delays an answer.
/// </summary>
internal sealed partial class CorrectionDetector(RouterRuntimeProvider provider, ILogger<CorrectionDetector> logger)
{
    /// <summary>The longest follow-up that is checked: corrections are short; a longer message is a new request.</summary>
    internal const int MaxCandidateLength = 120;

    /// <summary>Corrections of the previous answer, in the languages people most often write to the router in.</summary>
    internal static readonly string[] CorrectionExamples =
    [
        "that's wrong", "it still doesn't work", "no, that is not what I asked", "you broke the build", "same error as before", "that didn't fix it",
        "dat klopt niet", "het werkt nog steeds niet", "nee, dat bedoelde ik niet", "zelfde fout", "dat heeft het niet opgelost",
        "das ist falsch", "es funktioniert immer noch nicht", "nein, das meinte ich nicht", "gleicher Fehler", "das hat es nicht behoben",
        "c'est faux", "ça ne marche toujours pas", "non, ce n'est pas ce que je voulais", "même erreur", "ça n'a rien corrigé",
        "eso está mal", "sigue sin funcionar", "no, eso no es lo que pedí", "el mismo error", "eso no lo arregló",
        "è sbagliato", "ancora non funziona", "no, non è quello che ho chiesto", "stesso errore",
        "está errado", "ainda não funciona", "não, não foi isso que pedi", "o mesmo erro",
    ];

    /// <summary>Follow-ups that are not corrections: the next step, thanks, a go-ahead.</summary>
    internal static readonly string[] OtherExamples =
    [
        "now add a test", "thanks, looks good", "next, update the README", "great, commit it", "can you also handle the empty case", "ok go ahead",
        "voeg nu een test toe", "bedankt, ziet er goed uit", "doe nu hetzelfde voor de tests", "ga verder",
        "füge jetzt einen Test hinzu", "danke, sieht gut aus", "mach weiter", "jetzt die Dokumentation",
        "ajoute maintenant un test", "merci, c'est bien", "continue avec la suite", "maintenant la documentation",
        "ahora añade una prueba", "gracias, se ve bien", "sigue con el siguiente paso", "ahora la documentación",
        "ora aggiungi un test", "grazie, va bene", "agora adicione um teste", "obrigado, ficou bom",
        "explain what this function does", "leg uit wat deze code doet", "erklär mir die Fehlermeldung", "explique ce que fait cette fonction", "explica qué hace esta función",
    ];

    private readonly ConditionalWeakTable<IEmbeddingProvider, Task<Examples>> _examples = [];

    /// <summary>The check started last, for tests to wait on.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Whether <paramref name="prompt"/> is worth checking: short, not a question ("how does this work?" means much the
    /// same as "this doesn't work" to an embedding model), and not already a known phrase.
    /// </summary>
    public static bool IsCandidate(string prompt)
    {
        string text = prompt.Trim();
        return text.Length is > 0 and <= MaxCandidateLength && !text.EndsWith('?') && !Corrections.Matches(text);
    }

    /// <summary>Checks a follow-up in the background and calls <paramref name="corrected"/> when it reads as a correction.</summary>
    public void CheckInBackground(string prompt, Action corrected)
    {
        RouterRuntime runtime = provider.Current;
        FeedbackOptions options = runtime.Configuration.Middleware.Feedback;
        if (options.CorrectionSimilarity <= 0 || runtime.EmbeddingProvider is HashingEmbeddingProvider || !IsCandidate(prompt))
        {
            return;
        }

        IEmbeddingProvider embeddings = runtime.EmbeddingProvider;
        Pending = Task.Run(async () =>
        {
            try
            {
                if (await MatchesAsync(embeddings, prompt, options, CancellationToken.None).ConfigureAwait(false))
                {
                    corrected();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        });
    }

    /// <summary>Whether <paramref name="prompt"/> is close in meaning to a correction, and clearly closer to the corrections than to the rest.</summary>
    internal async Task<bool> MatchesAsync(IEmbeddingProvider embeddings, string prompt, FeedbackOptions options, CancellationToken cancellationToken)
    {
        Examples examples = await ExamplesFor(embeddings).ConfigureAwait(false);
        ReadOnlyMemory<float> vector = await embeddings.GenerateEmbeddingAsync(prompt.Trim(), cancellationToken).ConfigureAwait(false);
        float correction = examples.Corrections.Max(e => VectorMath.CosineSimilarity(e.Span, vector.Span));
        float other = examples.Others.Max(e => VectorMath.CosineSimilarity(e.Span, vector.Span));
        return correction >= options.CorrectionSimilarity && correction - other >= options.CorrectionMargin;
    }

    /// <summary>The examples' embeddings for <paramref name="embeddings"/>, computed once per embedding model; a failure is tried again next time.</summary>
    private Task<Examples> ExamplesFor(IEmbeddingProvider embeddings)
    {
        Task<Examples> task = _examples.GetValue(embeddings, Embed);
        if (task.IsFaulted || task.IsCanceled)
        {
            _examples.Remove(embeddings);
            task = _examples.GetValue(embeddings, Embed);
        }

        return task;

        static async Task<Examples> Embed(IEmbeddingProvider embeddings) => new(
            await embeddings.GenerateEmbeddingsAsync(CorrectionExamples, CancellationToken.None).ConfigureAwait(false),
            await embeddings.GenerateEmbeddingsAsync(OtherExamples, CancellationToken.None).ConfigureAwait(false));
    }

    private sealed record Examples(IReadOnlyList<ReadOnlyMemory<float>> Corrections, IReadOnlyList<ReadOnlyMemory<float>> Others);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking whether a follow-up corrects the previous answer failed; it is not counted.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
