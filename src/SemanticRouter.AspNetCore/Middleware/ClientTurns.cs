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
/// The patterns follow the clients' own prompts and are matched on the latest user message (injected
/// <c>&lt;system-reminder&gt;</c> blocks removed) and on the system prompt. They are deliberately specific: an ordinary
/// request that merely asks for a title or a summary is classified as usual.
/// </remarks>
internal static partial class ClientTurns
{
    private static readonly ConcurrentDictionary<string, Regex> Configured = new(StringComparer.Ordinal);

    private static readonly (ClientTurnKind Kind, Regex Pattern)[] BuiltIn =
    [
        (ClientTurnKind.Title, TitlePattern()),
        (ClientTurnKind.Topic, TopicPattern()),
        (ClientTurnKind.Summary, SummaryPattern()),
        (ClientTurnKind.CommandCheck, CommandCheckPattern()),
    ];

    /// <summary>The kind of client turn this is, or <see langword="null"/> for an ordinary request.</summary>
    /// <param name="chat">The request.</param>
    /// <param name="prompt">The latest user message, injected reminders removed.</param>
    /// <param name="firstTurn">Whether the request carries no earlier answer (a resumed session is only recognised on its first turn).</param>
    /// <param name="options">The settings.</param>
    public static ClientTurnKind? Recognise(ChatCompletionRequest chat, string prompt, bool firstTurn, HousekeepingOptions options)
    {
        if (string.Equals(prompt.Trim(), "warmup", StringComparison.OrdinalIgnoreCase))
        {
            return ClientTurnKind.Warmup;
        }

        // The summary opens the resumed session's first user message; the person's next request may follow it.
        if (firstTurn && chat.Messages.FirstOrDefault(m => m.Role == "user") is { } opening && ResumedPattern().IsMatch(WithoutReminders(opening.Content).TrimStart()))
        {
            return ClientTurnKind.Resumed;
        }

        string system = string.Join("\n", chat.Messages.Where(m => m.Role is "system" or "developer").Select(m => m.Content));
        foreach ((ClientTurnKind kind, Regex pattern) in BuiltIn)
        {
            if (pattern.IsMatch(prompt) || (system.Length > 0 && pattern.IsMatch(system)))
            {
                return kind;
            }
        }

        foreach (string configured in options.Patterns)
        {
            Regex pattern = Configured.GetOrAdd(configured, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
            if (pattern.IsMatch(prompt) || (system.Length > 0 && pattern.IsMatch(system)))
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

    private static string WithoutReminders(string text) => ReminderBlock().Replace(text, string.Empty);

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex ReminderBlock();

    // Claude Code: "Please write a 5-10 word title for the following conversation", "Summarize this coding conversation
    // in under 50 characters"; titles "for this session/conversation" in other clients.
    [GeneratedRegex(@"write an? \d+\s*[-–]\s*\d+[ -]word title|summari[sz]e this (coding )?conversation in under \d+ (characters|words)|generate an? (short|concise|brief)[^.\n]{0,40}title for (this|the following) (conversation|session|chat)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitlePattern();

    // Claude Code: "Analyze if this message indicates a new conversation topic. If it does, extract a 2-3 word title …"
    [GeneratedRegex(@"indicates a new conversation topic|isNewTopic", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    // Claude Code's compaction ("Your task is to create a detailed summary of the conversation so far …") and Codex's
    // ("CONTEXT CHECKPOINT COMPACTION … a handoff summary for another LLM", formerly "a short memento message for the next agent").
    [GeneratedRegex(@"create a detailed summary of the conversation so far|context checkpoint compaction|handoff summary for another (llm|model|agent)|memento message for the next agent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();

    // Claude Code's shell command check: a <policy_spec> and "extract the command prefix".
    [GeneratedRegex(@"<policy_spec>|extract the command prefix|process bash commands that an ai coding agent wants to run", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandCheckPattern();

    // Claude Code, after compaction: "This session is being continued from a previous conversation that ran out of context."
    [GeneratedRegex(@"^this session is being continued from a previous conversation", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResumedPattern();
}
