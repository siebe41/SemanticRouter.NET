namespace SemanticRouter.AspNetCore;

/// <summary>
/// Runs a piece of background work one at a time (a refresh that reads the state store), and lets its owner stop it:
/// <see cref="Dispose"/> starts no more and waits for the one under way, so nothing touches the store after the router
/// stopped and the store was closed.
/// </summary>
internal sealed class SingleFlight : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits, at most, for the work under way.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private Task? _running;
    private bool _stopped;

    /// <summary>Starts <paramref name="work"/> in the background, unless work is under way or this was stopped.</summary>
    public void TryStart(Action work)
    {
        lock (_gate)
        {
            if (_stopped || _running is { IsCompleted: false })
            {
                return;
            }

            _running = Task.Run(work);
        }
    }

    /// <summary>Starts no more work, and waits for the work under way (at most <see cref="StopWait"/>).</summary>
    public void Dispose()
    {
        Task? running;
        lock (_gate)
        {
            _stopped = true;
            running = _running;
        }

        if (running is { IsCompleted: false })
        {
            // Not Wait: work that failed has been reported (or not) by its owner, and must not fail the stop.
            ((IAsyncResult)running).AsyncWaitHandle.WaitOne(StopWait);
        }

        _ = running?.Exception; // observed
    }
}
