using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Counts failed dashboard sign-ins (a wrong token at <c>POST api/session</c> or as a bearer token) per client address
/// (an IPv6 address by its /64, <see cref="ClientAddressKey"/>). After <see cref="MaxFailures"/> within
/// <see cref="Window"/>, the address may not try again until the oldest of them is a window old, so a token cannot be
/// guessed by trying many. At most <see cref="MaxAddresses"/> addresses are kept; beyond that the ones that failed least
/// recently are forgotten first (<see cref="TrackerPruning"/>), so a stream of addresses costs constant work each.
/// </summary>
internal sealed class SignInThrottle(TimeProvider time, int maxFailures = SignInThrottle.MaxFailures)
{
    /// <summary>Failed attempts allowed within <see cref="Window"/>.</summary>
    public const int MaxFailures = 5;

    /// <summary>The most addresses kept.</summary>
    public const int MaxAddresses = 10_000;

    /// <summary>How long a failed attempt counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    // Addresses kept before those without a failure in the window are swept.
    private const int SweepAbove = 1000;

    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private readonly TrackerPruning _pruning = new(MaxAddresses, SweepAbove, time);

    /// <summary>The addresses kept, for tests.</summary>
    internal int Count => _failures.Count;

    /// <summary>The table's pruning, for tests of the cost per write.</summary>
    internal TrackerPruning Pruning => _pruning;

    /// <summary>How long until <paramref name="context"/>'s address may try again; <see langword="null"/> when it may now.</summary>
    public TimeSpan? RetryAfter(HttpContext context)
    {
        if (!_failures.TryGetValue(ClientAddressKey.KeyOf(context), out Failures? failures))
        {
            return null;
        }

        return failures.RetryAfter(time.GetUtcNow(), maxFailures);
    }

    /// <summary>Records a failed attempt from <paramref name="context"/>'s address.</summary>
    public void Failed(HttpContext context)
    {
        DateTimeOffset now = time.GetUtcNow();
        _failures.GetOrAdd(ClientAddressKey.KeyOf(context), static (_, at) => new Failures(at), now).Add(now);
        _pruning.Written(_failures, static f => f.Last, Window);
    }

    /// <summary>One address's failures within the window.</summary>
    private sealed class Failures(DateTimeOffset created)
    {
        private readonly Queue<DateTimeOffset> _times = new();
        private readonly Lock _gate = new();
        private long _lastTicks = created.UtcTicks;

        /// <summary>When the address last failed (or was first seen); read without the lock by the pruning.</summary>
        public DateTimeOffset Last => new(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);

        public void Add(DateTimeOffset now)
        {
            lock (_gate)
            {
                Expire(now);
                _times.Enqueue(now);
                Interlocked.Exchange(ref _lastTicks, now.UtcTicks);
            }
        }

        public TimeSpan? RetryAfter(DateTimeOffset now, int maxFailures)
        {
            lock (_gate)
            {
                Expire(now);
                return _times.Count >= maxFailures ? _times.Peek() + Window - now : null;
            }
        }

        private void Expire(DateTimeOffset now)
        {
            while (_times.Count > 0 && now - _times.Peek() >= Window)
            {
                _times.Dequeue();
            }
        }
    }
}
