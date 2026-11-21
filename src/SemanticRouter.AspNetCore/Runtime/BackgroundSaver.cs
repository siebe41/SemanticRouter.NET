namespace SemanticRouter.AspNetCore;

/// <summary>
/// Saves one piece of state off the caller's thread: <see cref="Request"/> marks it changed and returns at once, and
/// one background task at a time takes a snapshot and saves it, again for as long as it changed meanwhile. So a
/// request never waits for the store or holds a lock while it writes, the saves are in order, the last one is of the
/// latest state, and a burst of changes costs a few saves rather than one each.
/// </summary>
/// <param name="snapshot">The state to save, as it is now. Takes the owner's lock itself; never called under it.</param>
/// <param name="save">Saves a snapshot. Must not throw for storage problems; an exception is passed to <paramref name="failed"/>.</param>
/// <param name="failed">Told of an exception from <paramref name="snapshot"/> or <paramref name="save"/>, if anyone wants to know.</param>
internal sealed class BackgroundSaver(Func<string> snapshot, Action<string> save, Action<Exception>? failed = null)
{
    // One save at a time, a snapshot each, so a save never replaces a newer one.
    private readonly Lock _saving = new();
    private int _changed;
    private int _running;

    /// <summary>The state changed: it is saved in the background soon.</summary>
    public void Request()
    {
        Volatile.Write(ref _changed, 1);
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(Run);
        }
    }

    /// <summary>Saves the state now, on this thread, after a background save that is under way.</summary>
    public void SaveNow()
    {
        Volatile.Write(ref _changed, 0);
        SaveOnce();
    }

    /// <summary>Waits until every change requested so far is saved, at most <paramref name="timeout"/>. Returns whether it is.</summary>
    public bool Flush(TimeSpan timeout) =>
        SpinWait.SpinUntil(() => Volatile.Read(ref _running) == 0 && Volatile.Read(ref _changed) == 0, timeout);

    private void Run()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _changed, 0) == 1)
            {
                SaveOnce();
            }

            Volatile.Write(ref _running, 0);

            // A change requested after the last look found this running and started nothing: look again.
            if (Volatile.Read(ref _changed) == 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private void SaveOnce()
    {
        lock (_saving)
        {
            try
            {
                save(snapshot());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // On a pool thread, an exception that escaped would be lost and leave this running for good.
                failed?.Invoke(ex);
            }
        }
    }
}
