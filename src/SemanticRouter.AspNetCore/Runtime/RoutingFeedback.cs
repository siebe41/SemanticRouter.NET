using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>A sign, noticed after the fact, that a routing decision was not good enough.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FeedbackKind>))]
public enum FeedbackKind
{
    /// <summary>The same client sent the same prompt again shortly after it was answered.</summary>
    Retried,

    /// <summary>The next message in the conversation corrected the answer ("that's wrong", "still failing", …).</summary>
    Corrected,

    /// <summary>The person chose a model or tier with a callout right after a turn the router chose.</summary>
    Overridden,

    /// <summary>The tools the model called kept failing (tool results marked as errors on consecutive tool turns).</summary>
    ToolErrors,

    /// <summary>The model answered with nothing (no output tokens).</summary>
    EmptyReply,

    /// <summary>The judge (<see cref="JudgeOptions"/>) graded the answer low.</summary>
    Judged,
}

/// <summary>
/// One piece of feedback about a routing decision (<see cref="EntryId"/>, an activity entry), with the automatic label
/// it implies: the decision needed at least <see cref="NeededTier"/>. No prompt text is kept.
/// </summary>
public sealed record RoutingFeedback
{
    /// <summary>Sequence number, assigned by <see cref="RoutingFeedbackLog"/>.</summary>
    public long Id { get; init; }

    /// <summary>When it was noticed.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The activity entry (routing decision) it is about.</summary>
    public long EntryId { get; init; }

    /// <summary>What was noticed.</summary>
    public FeedbackKind Kind { get; init; }

    /// <summary>The conversation's short hash, when known.</summary>
    public string? Conversation { get; init; }

    /// <summary>The gateway client, when known.</summary>
    public string? Client { get; init; }

    /// <summary>The route the decision matched.</summary>
    public string? Route { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>The tier the decision required.</summary>
    public int Tier { get; init; }

    /// <summary>
    /// The automatic label: the decision needed at least this tier. <see langword="null"/> when the feedback says
    /// nothing about the tier (for example a callout to a cheaper model).
    /// </summary>
    public int? NeededTier { get; init; }

    /// <summary>A short explanation, e.g. "asked for @opus" or "3 tool errors on 2 turns".</summary>
    public string? Detail { get; init; }
}

/// <summary>Keeps feedback beyond memory and across restarts.</summary>
public interface IRoutingFeedbackStore
{
    /// <summary>
    /// Stores feedback. Its id is new: a store should refuse (and log) feedback whose id it already holds rather than
    /// replace it. Must not throw for storage problems: log them and carry on.
    /// </summary>
    void Add(RoutingFeedback feedback);

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive ids that no log sharing this store has handed out or will, and
    /// returns the first. -1 (the default) for a store that only one log uses, or when it failed: the log then counts
    /// on from the newest id it knows.
    /// </summary>
    long ReserveIds(int count) => -1;

    /// <summary>The newest first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<RoutingFeedback> Recent(int limit);

    /// <summary>Feedback with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/>, oldest first.</summary>
    IReadOnlyList<RoutingFeedback> Between(DateTimeOffset since, DateTimeOffset until, int limit);
}

/// <summary>
/// Feedback about routing decisions: a bounded in-memory log, optionally backed by an <see cref="IRoutingFeedbackStore"/>.
/// One piece of feedback per decision and kind.
/// </summary>
public sealed class RoutingFeedbackLog
{
    private readonly int _capacity;
    private readonly IRoutingFeedbackStore? _store;
    private readonly LinkedList<RoutingFeedback> _recent = new();
    private readonly HashSet<(long, FeedbackKind)> _seen = [];
    private readonly Lock _lock = new();
    private long _nextId; // the last id handed out
    private long _blockEnd;

    /// <summary>Creates a log keeping the latest <paramref name="capacity"/> items, seeded from <paramref name="store"/>.</summary>
    public RoutingFeedbackLog(int capacity = 2000, IRoutingFeedbackStore? store = null)
    {
        _capacity = capacity;
        _store = store;
        if (store is not null)
        {
            foreach (RoutingFeedback feedback in store.Recent(capacity).Reverse())
            {
                _recent.AddFirst(feedback);
                _seen.Add((feedback.EntryId, feedback.Kind));
                _nextId = Math.Max(_nextId, feedback.Id);
            }
        }
    }

    /// <summary>Records feedback; returns <see langword="false"/> when the decision already has feedback of that kind.</summary>
    public bool Add(RoutingFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        RoutingFeedback stored;
        lock (_lock)
        {
            if (!_seen.Add((feedback.EntryId, feedback.Kind)))
            {
                return false;
            }

            if (_nextId + 1 >= _blockEnd)
            {
                // Logs sharing a store take blocks of ids from it; without one (or when it failed) this log counts on.
                long first = _store?.ReserveIds(RoutingActivityLog.IdBlock) ?? -1;
                _nextId = first >= 0 ? first - 1 : _nextId;
                _blockEnd = _nextId + 1 + RoutingActivityLog.IdBlock;
            }

            stored = feedback with { Id = ++_nextId };
            _recent.AddFirst(stored);
            while (_recent.Count > _capacity)
            {
                RoutingFeedback dropped = _recent.Last!.Value;
                _recent.RemoveLast();
                _seen.Remove((dropped.EntryId, dropped.Kind));
            }
        }

        _store?.Add(stored);
        return true;
    }

    /// <summary>The newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<RoutingFeedback> Recent(int limit = 100)
    {
        lock (_lock)
        {
            return [.. _recent.Take(limit)];
        }
    }

    /// <summary>Feedback in a time range, oldest first: from the store when there is one, otherwise from memory.</summary>
    public IReadOnlyList<RoutingFeedback> Between(DateTimeOffset since, DateTimeOffset until, int limit = 100_000)
    {
        if (_store is not null)
        {
            return _store.Between(since, until, limit);
        }

        lock (_lock)
        {
            return [.. _recent.Where(f => f.Timestamp >= since && f.Timestamp < until).Reverse().Take(limit)];
        }
    }
}
