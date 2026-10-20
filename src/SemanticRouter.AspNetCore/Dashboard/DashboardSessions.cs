using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Dashboard sessions ended on the server. Signing out revokes the session's id; signing out every session revokes
/// everything issued until then. A revocation is kept only until the session it names would have lapsed anyway
/// (issued + <see cref="RouterDashboardOptions.SessionLifetime"/>), in memory and, with the
/// state database, in <see cref="IStateValueStore"/>, so it survives a restart and reaches other instances sharing the
/// file within a second. Revocations are never dropped early: past <see cref="MaxRevoked"/> ids, every session issued
/// up to the newest of them is revoked instead.
/// </summary>
internal sealed class DashboardSessions
{
    internal const string StoreKey = "dashboard-sessions";

    /// <summary>Most revoked ids kept, so signing in and out in a loop cannot grow the set without bound.</summary>
    internal const int MaxRevoked = 10_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(1);

    private readonly IStateValueStore? _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly Dictionary<string, long> _revoked = new(StringComparer.Ordinal); // id -> issued, Unix milliseconds
    private readonly Lock _gate = new();
    private long? _before; // every session issued at or before this (Unix milliseconds) is revoked
    private long _loadedAt = long.MinValue;

    /// <param name="store">Where revocations outlast a restart; <see langword="null"/> keeps them in memory.</param>
    /// <param name="time">The clock.</param>
    /// <param name="lifetime">How long a session lasts, with any clock skew allowed: how long a revocation is kept.</param>
    public DashboardSessions(IStateValueStore? store, TimeProvider time, TimeSpan lifetime)
    {
        _store = store;
        _time = time;
        _lifetime = lifetime;
        lock (_gate)
        {
            Reload();
        }
    }

    /// <summary>Revoked ids kept now (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _revoked.Count;
            }
        }
    }

    /// <summary>Whether the session <paramref name="id"/>, issued at <paramref name="issued"/> (Unix milliseconds), was ended.</summary>
    public bool IsRevoked(string id, long issued)
    {
        lock (_gate)
        {
            if (_store is not null && _time.GetUtcNow().ToUnixTimeMilliseconds() - _loadedAt >= ReloadInterval.TotalMilliseconds)
            {
                Reload();
            }

            return issued <= _before || _revoked.ContainsKey(id);
        }
    }

    /// <summary>Ends the session <paramref name="id"/>, issued at <paramref name="issued"/> (Unix milliseconds).</summary>
    public void Revoke(string id, long issued)
    {
        lock (_gate)
        {
            Reload();
            _revoked[id] = issued;
            Save();
        }
    }

    /// <summary>Ends every session issued until now.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            Reload();
            _before = Math.Max(_before ?? long.MinValue, _time.GetUtcNow().ToUnixTimeMilliseconds());
            _revoked.Clear();
            Save();
        }
    }

    /// <summary>Adds what the store holds (another instance's sign-outs, or the last run's) to what is kept here. Never forgets.</summary>
    private void Reload()
    {
        _loadedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (_store?.Load(StoreKey) is { } saved)
        {
            try
            {
                if (JsonSerializer.Deserialize<Stored>(saved, Json) is { } stored)
                {
                    if (stored.Before is long before)
                    {
                        _before = Math.Max(_before ?? long.MinValue, before);
                    }

                    foreach ((string id, long issued) in stored.Revoked ?? [])
                    {
                        _revoked.TryAdd(id, issued);
                    }
                }
            }
            catch (JsonException)
            {
                // Unreadable: what is kept in memory still applies, and the next sign-out rewrites it.
            }
        }

        Prune();
    }

    private void Save()
    {
        Prune();
        if (_revoked.Count > MaxRevoked)
        {
            _before = Math.Max(_before ?? long.MinValue, _revoked.Values.Max());
            _revoked.Clear();
        }

        _store?.Save(StoreKey, JsonSerializer.Serialize(new Stored(_before, _revoked), Json));
    }

    /// <summary>Forgets revocations of sessions that have lapsed anyway.</summary>
    private void Prune()
    {
        long oldest = _time.GetUtcNow().ToUnixTimeMilliseconds() - (long)_lifetime.TotalMilliseconds;
        foreach (string id in _revoked.Where(kv => kv.Value <= oldest).Select(kv => kv.Key).ToArray())
        {
            _revoked.Remove(id);
        }

        if (_before <= oldest)
        {
            _before = null;
        }
    }

    private sealed record Stored(long? Before, Dictionary<string, long>? Revoked);
}
