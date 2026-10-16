using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Counts failed dashboard sign-ins (a wrong token at <c>POST api/session</c> or as a bearer token) per client address.
/// After <see cref="MaxFailures"/> within <see cref="Window"/>, the address may not try again until the oldest of them
/// is a window old, so a token cannot be guessed by trying many.
/// </summary>
internal sealed class SignInThrottle(TimeProvider time)
{
    /// <summary>Failed attempts allowed within <see cref="Window"/>.</summary>
    public const int MaxFailures = 5;

    /// <summary>How long a failed attempt counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    // Addresses kept before expired ones are swept, so a stream of addresses cannot grow the table without end.
    private const int SweepAbove = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    /// <summary>How long until <paramref name="context"/>'s address may try again; <see langword="null"/> when it may now.</summary>
    public TimeSpan? RetryAfter(HttpContext context)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_failures.TryGetValue(AddressOf(context), out Queue<DateTimeOffset>? failures))
            {
                return null;
            }

            Expire(failures, now);
            return failures.Count >= MaxFailures ? failures.Peek() + Window - now : null;
        }
    }

    /// <summary>Records a failed attempt from <paramref name="context"/>'s address.</summary>
    public void Failed(HttpContext context)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (_failures.Count > SweepAbove)
            {
                foreach ((string address, Queue<DateTimeOffset> failures) in _failures.ToArray())
                {
                    Expire(failures, now);
                    if (failures.Count == 0)
                    {
                        _failures.Remove(address);
                    }
                }
            }

            string key = AddressOf(context);
            if (!_failures.TryGetValue(key, out Queue<DateTimeOffset>? queue))
            {
                _failures[key] = queue = new Queue<DateTimeOffset>();
            }

            Expire(queue, now);
            queue.Enqueue(now);
        }
    }

    private static void Expire(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        while (failures.Count > 0 && now - failures.Peek() >= Window)
        {
            failures.Dequeue();
        }
    }

    private static string AddressOf(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "local";
}
