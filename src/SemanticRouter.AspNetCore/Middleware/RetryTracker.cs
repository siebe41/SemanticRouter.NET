using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Remembers which tier answered each recent prompt, per client, for <see cref="RetryEscalationOptions"/>. Keys are a
/// hash of the client and the prompt: no prompt text is kept. In memory only.
/// </summary>
internal sealed class RetryTracker
{
    private const int PruneAbove = 2048;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int Tier)> _answered = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public RetryTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The tier a retry of <paramref name="prompt"/> needs, or <see langword="null"/> when it is not a retry.</summary>
    /// <param name="client">Who is asking (gateway client name or remote address).</param>
    /// <param name="prompt">The prompt being routed.</param>
    /// <param name="window">How long after an answer a repeat counts.</param>
    /// <param name="highestTier">The highest tier an enabled model has: there is nothing to escalate to beyond it.</param>
    public int? MinimumTier(string client, string prompt, TimeSpan window, int highestTier)
    {
        if (!_answered.TryGetValue(Key(client, prompt), out var answered) || _time.GetUtcNow() - answered.At > window)
        {
            return null;
        }

        return answered.Tier < highestTier ? answered.Tier + 1 : null;
    }

    /// <summary>Records that a model of <paramref name="tier"/> answered <paramref name="prompt"/>.</summary>
    public void Answered(string client, string prompt, int tier, TimeSpan window)
    {
        DateTimeOffset now = _time.GetUtcNow();
        _answered[Key(client, prompt)] = (now, tier);
        if (_answered.Count > PruneAbove)
        {
            foreach ((string key, (DateTimeOffset at, int _)) in _answered)
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
