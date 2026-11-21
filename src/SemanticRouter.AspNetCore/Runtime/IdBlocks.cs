namespace SemanticRouter.AspNetCore;

/// <summary>
/// Hands out increasing ids from blocks a shared store reserves (<see cref="IRoutingActivityStore.ReserveIds"/>,
/// <see cref="IRoutingFeedbackStore.ReserveIds"/>), so logs sharing the store never hand out the same one. Not
/// thread-safe: its owner calls <see cref="TryNext"/> under its own lock, and <see cref="IdWait.Wait"/> outside it.
/// </summary>
/// <remarks>
/// The store is not asked while a caller waits in the normal case (#391): the first block is reserved when the log is
/// created (at start-up), and the next one in the background as soon as a block is taken, so a whole block's worth of
/// ids (<see cref="RoutingActivityLog.IdBlock"/>) covers the time a reservation can wait for another writer of the file.
/// If a block runs out before the next one arrives, the caller waits for it (<see cref="IdWait"/>) rather than count on
/// into ids another log may have reserved meanwhile. Ids count on from the last one only when the store returned -1 (a
/// store only one log uses, or one that failed), or when a reservation has not returned after <see cref="MaxWait"/>.
/// </remarks>
internal sealed class IdBlocks
{
    /// <summary>
    /// How long callers wait, at most, for a reservation once a block has run out: past a database's busy timeout, after
    /// which the store returns -1 itself.
    /// </summary>
    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(30);

    private readonly Func<int, long>? _reserve;
    private long _next;
    private long _end;
    private Task<long>? _ahead;
    private long _exhaustedAt;

    /// <summary>Starts at <paramref name="next"/>, or at the first id of a block <paramref name="reserve"/> reserves now.</summary>
    public IdBlocks(Func<int, long>? reserve, long next)
        : this(reserve, next, DefaultMaxWait)
    {
    }

    internal IdBlocks(Func<int, long>? reserve, long next, TimeSpan maxWait)
    {
        _reserve = reserve;
        _next = next;
        MaxWait = maxWait;
        Take(reserve is null ? -1 : ReserveOrFail(reserve));
    }

    /// <summary>How long callers wait, at most, for a reservation once a block has run out.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>The id <see cref="TryNext"/> returns, unless it takes a new block first.</summary>
    public long Upcoming => _next;

    /// <summary>
    /// The next id; or <see langword="false"/> when the block is used up and the next one is still being reserved:
    /// the caller then leaves its lock, waits (<paramref name="wait"/>) and asks again.
    /// </summary>
    public bool TryNext(out long id, out IdWait wait)
    {
        wait = default;
        if (_next >= _end)
        {
            if (_reserve is null)
            {
                Take(-1);
            }
            else if (_ahead is { IsCompleted: true } arrived)
            {
                // Cleared before it is read: a block that failed is read once, as "none", never again.
                _ahead = null;
                _exhaustedAt = 0;
                Take(arrived.IsCompletedSuccessfully ? arrived.Result : Failed(arrived));
            }
            else
            {
                _ahead ??= Task.Run(() => ReserveOrFail(_reserve));
                long now = Environment.TickCount64;
                if (_exhaustedAt == 0)
                {
                    _exhaustedAt = now;
                }

                long left = _exhaustedAt + (long)MaxWait.TotalMilliseconds - now;
                if (left > 0)
                {
                    id = -1;
                    wait = new IdWait(_ahead, TimeSpan.FromMilliseconds(left));
                    return false;
                }

                // The store has not answered for far longer than a database waits for a lock: taken as failed. Its
                // block, should it still arrive, is left unused.
                _ahead = null;
                _exhaustedAt = 0;
                Take(-1);
            }

            if (_next >= _end)
            {
                // The block that arrived lies behind ids already handed out (they counted on past a failure): ask again.
                return TryNext(out id, out wait);
            }
        }

        // The next block is asked for as soon as this one is taken, so it has a whole block's worth of time to arrive.
        if (_reserve is { } reserve && _ahead is null)
        {
            _ahead = Task.Run(() => ReserveOrFail(reserve));
        }

        id = _next++;
        return true;
    }

    /// <summary>Continues in the block that starts at <paramref name="first"/>; -1 (none): counts on for a block's worth.</summary>
    private void Take(long first)
    {
        if (first >= 0)
        {
            _next = Math.Max(_next, first);
            _end = first + RoutingActivityLog.IdBlock;
        }
        else
        {
            _end = _next + RoutingActivityLog.IdBlock;
        }
    }

    private static long ReserveOrFail(Func<int, long> reserve)
    {
        try
        {
            return reserve(RoutingActivityLog.IdBlock);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return -1; // a store must not throw; one that does is treated as one that failed
        }
    }

    private static long Failed(Task<long> task)
    {
        _ = task.Exception; // observed
        return -1;
    }
}

/// <summary>A reservation a caller of <see cref="IdBlocks.TryNext"/> waits for, outside its lock, before it asks again.</summary>
internal readonly record struct IdWait(Task? Pending, TimeSpan Timeout)
{
    /// <summary>Waits until the reservation returned or the time is up.</summary>
    public void Wait()
    {
        if (Pending is { IsCompleted: false } pending)
        {
            ((IAsyncResult)pending).AsyncWaitHandle.WaitOne(Timeout);
        }
    }
}
