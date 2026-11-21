namespace SemanticRouter.AspNetCore;

/// <summary>
/// Hands out increasing ids from blocks a shared store reserves (<see cref="IRoutingActivityStore.ReserveIds"/>,
/// <see cref="IRoutingFeedbackStore.ReserveIds"/>), so logs sharing the store never hand out the same one. Not
/// thread-safe: its owner calls it under its own lock.
/// </summary>
/// <remarks>
/// The store is never asked while a caller waits (#391): the first block is reserved when the log is created (at
/// start-up), and each next one in the background once half of the current one is used. If a block runs out before the
/// next one arrives, or the store fails, ids count on from the last one, as for a store that only one log uses; the
/// reserved block is taken as soon as it arrives.
/// </remarks>
internal sealed class IdBlocks
{
    private readonly Func<int, long>? _reserve;
    private long _next;
    private long _end;
    private Task<long>? _ahead;

    /// <summary>Starts at <paramref name="next"/>, or at the first id of a block <paramref name="reserve"/> reserves now.</summary>
    public IdBlocks(Func<int, long>? reserve, long next)
    {
        _reserve = reserve;
        _next = next;
        Take(reserve is null ? -1 : ReserveOrFail(reserve));
    }

    /// <summary>The id <see cref="Next"/> returns, unless it takes a new block first.</summary>
    public long Upcoming => _next;

    /// <summary>The next id.</summary>
    public long Next()
    {
        if (_next >= _end && _ahead is { IsCompleted: true } arrived)
        {
            // Cleared before it is read: a block that failed is read once, as "none", never again.
            _ahead = null;
            Take(arrived.IsCompletedSuccessfully ? arrived.Result : Failed(arrived));
        }

        if (_reserve is { } reserve && _ahead is null && (_next >= _end || _end - _next <= RoutingActivityLog.IdBlock / 2))
        {
            _ahead = Task.Run(() => ReserveOrFail(reserve));
        }

        return _next++;
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
