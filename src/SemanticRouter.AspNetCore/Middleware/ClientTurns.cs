using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>What kind of request a client made for itself.</summary>
internal enum ClientTurnKind
{
    /// <summary>A title for the session or conversation.</summary>
    Title,

    /// <summary>Whether a message starts a new topic.</summary>
    Topic,

    /// <summary>A summary of the conversation so far, to compact it.</summary>
    Summary,

    /// <summary>A warm-up ping.</summary>
    Warmup,

    /// <summary>A check of a shell command the agent wants to run.</summary>
    CommandCheck,

    /// <summary>A pattern from <see cref="HousekeepingOptions.Patterns"/>.</summary>
    Configured,

    /// <summary>A session resumed from a summary of an earlier one.</summary>
    Resumed,
}

/// <summary>
/// Recognises the requests agentic clients (Claude Code, Codex) make for themselves by their fixed wording: session
/// titles, topic checks, compaction summaries, warm-up pings and command checks go to a small model; a session resumed
/// from a compaction summary goes to the tier of the work it continues (<see cref="HousekeepingOptions"/>).
/// </summary>
/// <remarks>
/// The patterns follow the clients' own prompts (<see cref="ClientTurnTable"/>: built in, overridable by a
/// <c>client-turns.json</c> next to the configuration) and are matched on the latest user message (injected
/// <c>&lt;system-reminder&gt;</c> blocks removed) and on the system prompt. They are deliberately specific: an ordinary
/// request that merely asks for a title or a summary is classified as usual.
/// </remarks>
internal static partial class ClientTurns
{
    private static readonly ConcurrentDictionary<string, Regex> Configured = new(StringComparer.Ordinal);

    /// <summary>The kind of client turn this is, or <see langword="null"/> for an ordinary request.</summary>
    /// <param name="chat">The request.</param>
    /// <param name="prompt">The latest user message, injected reminders removed.</param>
    /// <param name="firstTurn">Whether the request carries no earlier answer (a resumed session is only recognised on its first turn).</param>
    /// <param name="options">The settings.</param>
    /// <param name="table">The patterns; the built-in ones when <see langword="null"/>.</param>
    public static ClientTurnKind? Recognise(ChatCompletionRequest chat, string prompt, bool firstTurn, HousekeepingOptions options, ClientTurnTable? table = null)
    {
        string? system = null;
        string? opening = null;
        string trimmed = prompt.Trim();
        foreach ((ClientTurnPattern entry, Regex pattern) in (table ?? ClientTurnTable.BuiltIn).Patterns)
        {
            bool matched = entry.Kind switch
            {
                ClientTurnKind.Warmup => Matches(pattern, trimmed),

                // The summary opens the resumed session's first user message; the person's next request may follow it.
                ClientTurnKind.Resumed => firstTurn && Matches(pattern, opening ??= OpeningMessage(chat)),
                _ => Matches(pattern, prompt) || Matches(pattern, system ??= SystemPrompt(chat)),
            };
            if (matched)
            {
                return entry.Kind;
            }
        }

        foreach (string configured in options.Patterns)
        {
            Regex pattern = Configured.GetOrAdd(configured, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
            if (Matches(pattern, prompt) || Matches(pattern, system ??= SystemPrompt(chat)))
            {
                return ClientTurnKind.Configured;
            }
        }

        return null;
    }

    /// <summary>The decision for a recognised turn: housekeeping on <see cref="HousekeepingOptions.Tier"/>, a resumed session on <see cref="HousekeepingOptions.ResumedTier"/>.</summary>
    public static PromptClassificationResult Decide(ClientTurnKind kind, HousekeepingOptions options, int estimatedTokens) =>
        PromptClassificationResult.Bypass(
            kind == ClientTurnKind.Resumed ? options.ResumedTier : options.Tier,
            kind == ClientTurnKind.Resumed ? RoutingReason.ResumedSession : RoutingReason.ClientHousekeeping,
            new ComplexityAnalysis { EstimatedTokens = estimatedTokens });

    /// <summary>Whether <paramref name="pattern"/> matches; a pattern that runs out of time does not.</summary>
    private static bool Matches(Regex pattern, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string SystemPrompt(ChatCompletionRequest chat) =>
        string.Join("\n", chat.Messages.Where(m => m.Role is "system" or "developer").Select(m => m.Content));

    private static string OpeningMessage(ChatCompletionRequest chat) =>
        chat.Messages.FirstOrDefault(m => m.Role == "user") is { } opening ? ReminderBlock().Replace(opening.Content, string.Empty).TrimStart() : string.Empty;

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex ReminderBlock();
}
