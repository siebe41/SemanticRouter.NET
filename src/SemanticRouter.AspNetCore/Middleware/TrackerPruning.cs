using System.Collections.Concurrent;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Keeps an in-memory tracker's dictionary bounded without walking it on every request. The size is looked at only
/// every <see cref="CheckEvery"/> writes. Above <c>sweepAbove</c> entries, those past their age go, in a sweep that runs
/// at most once per <see cref="SweepInterval"/>. Above <see cref="Capacity"/>, the least recently used go as well,
/// until a quarter of the room is free again. Either way the work per write stays constant, however much traffic there
/// is. One instance per dictionary; thread-safe.
/// </summary>
internal sealed class TrackerPruning(int capacity, int sweepAbove, TimeProvider time)
{
    private const int CheckEvery = 64;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private int _writes;
    private int _busy;
    private long _lastSweepTicks;
    private long _visited;

    /// <summary>The most entries kept, give or take the writes between two checks.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>Entries looked at by sweeps and evictions so far, for tests of the cost per write.</summary>
    internal long Visited => Interlocked.Read(ref _visited);

    /// <summary>For tests: called with the key of an entry the pruning read as one to go, before it is removed.</summary>
    internal Action<string>? Removing { get; set; }

    /// <summary>Call after each write to <paramref name="entries"/>.</summary>
    /// <param name="entries">The tracker's dictionary.</param>
    /// <param name="lastUsed">When an entry was last written or used.</param>
    /// <param name="maxAge">Entries unused for longer than this are no longer needed.</param>
    public void Written<TValue>(ConcurrentDictionary<string, TValue> entries, Func<TValue, DateTimeOffset> lastUsed, TimeSpan maxAge)
    {
        // Count takes every bucket lock, so it is not read on every write; one thread prunes at a time.
        if (Interlocked.Increment(ref _writes) % CheckEvery != 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            int count = entries.Count;
            DateTimeOffset now = time.GetUtcNow();
            if (count > Capacity)
            {
                Prune(entries, lastUsed, now - maxAge, keep: Capacity - (Capacity / 4));
            }
            else if (count > sweepAbove && now.UtcTicks - Interlocked.Read(ref _lastSweepTicks) >= SweepInterval.Ticks)
            {
                Prune(entries, lastUsed, now - maxAge, keep: int.MaxValue);
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Prune<TValue>(ConcurrentDictionary<string, TValue> entries, Func<TValue, DateTimeOffset> lastUsed, DateTimeOffset oldest, int keep)
    {
        Interlocked.Exchange(ref _lastSweepTicks, time.GetUtcNow().UtcTicks);
        var live = new List<(KeyValuePair<string, TValue> Entry, DateTimeOffset At)>();
        long visited = 0;
        foreach (KeyValuePair<string, TValue> entry in entries)
        {
            visited++;
            DateTimeOffset at = lastUsed(entry.Value);
            if (at < oldest)
            {
                // Only as it was read: an entry written again since stays.
                Remove(entries, entry, at);
            }
            else
            {
                live.Add((entry, at));
            }
        }

        Interlocked.Add(ref _visited, visited);
        if (live.Count <= keep)
        {
            return;
        }

        live.Sort((a, b) => a.At.CompareTo(b.At));
        for (int i = 0; i < live.Count - keep; i++)
        {
            Remove(entries, live[i].Entry, live[i].At);
        }
    }

    /// <summary>
    /// Removes <paramref name="entry"/> unless it was written after <paramref name="seen"/>. A value replaced on every
    /// write is compared with the one read (<see cref="ConcurrentDictionary{TKey, TValue}.TryRemove(KeyValuePair{TKey, TValue})"/>).
    /// One changed in place compares equal however it was changed, so it decides under its own lock (<see cref="IPrunableEntry"/>).
    /// </summary>
    private void Remove<TValue>(ConcurrentDictionary<string, TValue> entries, KeyValuePair<string, TValue> entry, DateTimeOffset seen)
    {
        Removing?.Invoke(entry.Key);
        if (entry.Value is IPrunableEntry changedInPlace && !changedInPlace.TryRetire(seen))
        {
            return;
        }

        entries.TryRemove(entry);
    }
}

/// <summary>
/// A tracker's entry that is changed in place rather than replaced, so the dictionary cannot tell whether it was written
/// again after <see cref="TrackerPruning"/> read it: the pruning asks the entry instead. A writer that finds the entry
/// retired writes to a fresh one in its place.
/// </summary>
internal interface IPrunableEntry
{
    /// <summary>
    /// Holding the lock its writers hold: when the entry was last used no later than <paramref name="seen"/>, marks it
    /// retired, so no write changes it any more, and returns <see langword="true"/> (also when it was retired already).
    /// Otherwise it was written again since: it is left as it is and the result is <see langword="false"/>.
    /// </summary>
    bool TryRetire(DateTimeOffset seen);
}
