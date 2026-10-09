using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What the router knows about a conversation when a new turn arrives: the tier floor to keep, the model to prefer,
/// and whether the turn is a short follow-up.
/// </summary>
internal readonly record struct ConversationContext(int? Floor, string? PreferredModel, bool FollowUp);

/// <summary>
/// Remembers, per conversation, the highest tier recent turns needed and the model that answered last
/// (<see cref="ConversationRoutingOptions"/>). Keys are a hash of the client, the system prompt and the first user
/// message: no prompt text is kept. In memory only, and pruned.
/// </summary>
internal sealed class ConversationTracker
{
    private const int PruneAbove = 4096;

    private readonly ConcurrentDictionary<string, State> _conversations = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ConversationTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// The conversation a request belongs to. Chat clients resend the whole history, so the system prompt and the first
    /// user message are the same on every turn. <see langword="null"/> when the request has no user message.
    /// </summary>
    public static string? Identify(string client, ChatCompletionRequest chat)
    {
        ChatMessage? first = chat.Messages.FirstOrDefault(m => m.Role == "user");
        if (first is null)
        {
            return null;
        }

        string system = string.Join("\n", chat.Messages.Where(m => m.Role is "system" or "developer").Select(m => m.Content));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client + "\n" + system + "\n" + first.Content)));
    }

    /// <summary>A message short enough to be a follow-up ("yes", "continue", "try again") rather than a new request.</summary>
    public static bool IsFollowUp(string prompt, ConversationRoutingOptions options)
    {
        string trimmed = prompt.Trim();
        return trimmed.Length <= options.ShortFollowUpLength && !trimmed.Contains("```", StringComparison.Ordinal);
    }

    /// <summary>The floor and preferred model for a new turn of <paramref name="key"/>.</summary>
    public ConversationContext Lookup(string key, string prompt, ConversationRoutingOptions options)
    {
        bool followUp = IsFollowUp(prompt, options);
        if (!_conversations.TryGetValue(key, out State? state))
        {
            return new ConversationContext(null, null, followUp);
        }

        lock (state)
        {
            if (_time.GetUtcNow() - state.At > options.IdleTimeout)
            {
                return new ConversationContext(null, null, followUp);
            }

            int floor = followUp ? state.Peak : state.Peak - (state.TurnsBelow / options.DecayTurns);
            return new ConversationContext(floor >= 1 ? floor : null, options.StickToModel ? state.Model : null, followUp);
        }
    }

    /// <summary>
    /// Records a routed turn: the tier it required (after any floor), whether the floor raised it, and the model that
    /// answered. A turn that needed less than the conversation's peak counts towards lowering the floor, unless it was
    /// a short follow-up. A first turn starts the conversation afresh.
    /// </summary>
    public void Record(string key, int requiredTier, bool raisedByFloor, bool followUp, bool firstTurn, string? model, ConversationRoutingOptions options)
    {
        DateTimeOffset now = _time.GetUtcNow();
        State state = _conversations.GetOrAdd(key, _ => new State { Peak = requiredTier, At = now });
        lock (state)
        {
            if (firstTurn || now - state.At > options.IdleTimeout)
            {
                (state.Peak, state.TurnsBelow) = (requiredTier, 0);
            }
            else if (!raisedByFloor && requiredTier >= state.Peak)
            {
                (state.Peak, state.TurnsBelow) = (requiredTier, 0);
            }
            else if (!followUp)
            {
                state.TurnsBelow++;
            }

            state.Model = model ?? state.Model;
            state.At = now;
        }

        Prune(now, options.IdleTimeout);
    }

    /// <summary>Keeps a conversation alive and on its model during turns that are not classified (tool loops, explicit models).</summary>
    public void Touch(string key, string? model, ConversationRoutingOptions options)
    {
        if (!_conversations.TryGetValue(key, out State? state))
        {
            return;
        }

        lock (state)
        {
            state.Model = model ?? state.Model;
            state.At = _time.GetUtcNow();
        }
    }

    private void Prune(DateTimeOffset now, TimeSpan idle)
    {
        if (_conversations.Count <= PruneAbove)
        {
            return;
        }

        foreach ((string key, State state) in _conversations)
        {
            if (now - state.At > idle)
            {
                _conversations.TryRemove(key, out _);
            }
        }
    }

    private sealed class State
    {
        public int Peak;
        public int TurnsBelow;
        public string? Model;
        public DateTimeOffset At;
    }
}
