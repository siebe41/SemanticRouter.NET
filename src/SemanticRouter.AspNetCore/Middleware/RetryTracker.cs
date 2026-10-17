using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>A routing decision as the trackers remember it, for feedback: which entry, on what tier, route and model.</summary>
/// <param name="EntryId">The activity entry of the decision.</param>
/// <param name="Tier">Its tier: the required tier, or for a retry the tier of the model that answered.</param>
/// <param name="Route">The route it matched.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Automatic">The router chose (not an explicit model or callout).</param>
internal sealed record DecisionRecord(long EntryId, int Tier, string? Route, string? Model, bool Automatic);

/// <summary>An earlier answer to the same prompt (<see cref="RetryTracker.Previous"/>).</summary>
/// <param name="Decision">The decision that answered it.</param>
/// <param name="Repeat">That request was itself a repeat: the prompt has been sent again and again, as a script does.</param>
internal sealed record RetryMatch(DecisionRecord Decision, bool Repeat);

/// <summary>
/// Remembers which decision answered each recent prompt, per client and conversation, for retry escalation
/// (<see cref="RetryEscalationOptions"/>) and retry feedback. The same prompt in another conversation is not a retry.
/// Keys are a hash of the client, the conversation and the prompt: no prompt text is kept. In memory only, and bounded: answers older than the retry window go, and beyond
/// <see cref="MaxEntries"/> the oldest go first.
/// </summary>
internal sealed class RetryTracker
{
    /// <summary>The most answers remembered: about 5 MB.</summary>
    public const int MaxEntries = 16_384;

    private const int PruneAbove = 2048;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RetryMatch Answer)> _answered = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TrackerPruning _pruning;

    public RetryTracker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _pruning = new TrackerPruning(MaxEntries, PruneAbove, _time);
    }

    /// <summary>Answers remembered now.</summary>
    internal int Count => _answered.Count;

    /// <summary>The pruning, for tests of its cost.</summary>
    internal TrackerPruning Pruning => _pruning;

    /// <summary>
    /// The decision that answered <paramref name="prompt"/> in <paramref name="conversation"/> within
    /// <paramref name="window"/>, or <see langword="null"/> when this is not a retry.
    /// </summary>
    public RetryMatch? Previous(string client, string prompt, TimeSpan window, string? conversation = null) =>
        _answered.TryGetValue(Key(client, conversation, prompt), out var answered) && _time.GetUtcNow() - answered.At <= window ? answered.Answer : null;

    /// <summary>The tier a retry of <paramref name="prompt"/> needs, or <see langword="null"/> when it is not a retry.</summary>
    /// <param name="client">Who is asking (gateway client name or remote address).</param>
    /// <param name="prompt">The prompt being routed.</param>
    /// <param name="window">How long after an answer a repeat counts.</param>
    /// <param name="highestTier">The highest tier an enabled model has: there is nothing to escalate to beyond it.</param>
    public int? MinimumTier(string client, string prompt, TimeSpan window, int highestTier) =>
        Previous(client, prompt, window) is { Decision: { } previous } && previous.Tier < highestTier ? previous.Tier + 1 : null;

    /// <summary>
    /// Records the decision that answered <paramref name="prompt"/> in <paramref name="conversation"/>; with
    /// <paramref name="repeat"/>, the request was itself a repeat of an answered prompt.
    /// </summary>
    public void Answered(string client, string prompt, DecisionRecord decision, TimeSpan window, string? conversation = null, bool repeat = false)
    {
        _answered[Key(client, conversation, prompt)] = (_time.GetUtcNow(), new RetryMatch(decision, repeat));
        _pruning.Written(_answered, answered => answered.At, window);
    }

    private static string Key(string client, string? conversation, string prompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client + "\n" + conversation + "\n" + prompt.Trim())));
}
