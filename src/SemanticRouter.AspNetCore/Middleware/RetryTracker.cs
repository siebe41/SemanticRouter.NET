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

/// <summary>
/// Remembers which decision answered each recent prompt, per client, for retry escalation
/// (<see cref="RetryEscalationOptions"/>) and retry feedback. Keys are a hash of the client and the prompt: no prompt
/// text is kept. In memory only.
/// </summary>
internal sealed class RetryTracker
{
    private const int PruneAbove = 2048;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, DecisionRecord Decision)> _answered = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public RetryTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The decision that answered <paramref name="prompt"/> within <paramref name="window"/>, or <see langword="null"/> when this is not a retry.</summary>
    public DecisionRecord? Previous(string client, string prompt, TimeSpan window) =>
        _answered.TryGetValue(Key(client, prompt), out var answered) && _time.GetUtcNow() - answered.At <= window ? answered.Decision : null;

    /// <summary>The tier a retry of <paramref name="prompt"/> needs, or <see langword="null"/> when it is not a retry.</summary>
    /// <param name="client">Who is asking (gateway client name or remote address).</param>
    /// <param name="prompt">The prompt being routed.</param>
    /// <param name="window">How long after an answer a repeat counts.</param>
    /// <param name="highestTier">The highest tier an enabled model has: there is nothing to escalate to beyond it.</param>
    public int? MinimumTier(string client, string prompt, TimeSpan window, int highestTier) =>
        Previous(client, prompt, window) is { } previous && previous.Tier < highestTier ? previous.Tier + 1 : null;

    /// <summary>Records the decision that answered <paramref name="prompt"/>.</summary>
    public void Answered(string client, string prompt, DecisionRecord decision, TimeSpan window)
    {
        DateTimeOffset now = _time.GetUtcNow();
        _answered[Key(client, prompt)] = (now, decision);
        if (_answered.Count > PruneAbove)
        {
            foreach ((string key, (DateTimeOffset at, DecisionRecord _)) in _answered)
            {
                if (now - at > window)
                {
                    _answered.TryRemove(key, out _);
                }
            }
        }
    }

    private static string Key(string client, string prompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client + "\n" + prompt.Trim())));
}
