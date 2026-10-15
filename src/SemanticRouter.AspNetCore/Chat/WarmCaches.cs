using System.Collections.Concurrent;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Remembers, per conversation, the prompt caches its turns left on models (<see cref="WarmCache"/>), so a later turn
/// can weigh staying on a warm cache against moving (<see cref="PromptRoutingRequest.WarmCaches"/>). Keys are the
/// conversation hash: no prompt text is kept.
/// </summary>
public interface IWarmCacheStore
{
    /// <summary>The caches <paramref name="conversation"/> has on models, expired ones included.</summary>
    IReadOnlyList<WarmCache> Lookup(string conversation);

    /// <summary>Records (or replaces) the conversation's cache on <see cref="WarmCache.Model"/>.</summary>
    void Remember(string conversation, WarmCache cache);

    /// <summary>Forgets the conversation's cache on <paramref name="model"/>, or every one of its caches when it is <see langword="null"/>.</summary>
    void Forget(string conversation, string? model = null);
}

/// <summary>In-memory <see cref="IWarmCacheStore"/>; expired entries are pruned as it grows.</summary>
public sealed class WarmCacheStore : IWarmCacheStore
{
    private const int PruneAbove = 4096;

    private readonly ConcurrentDictionary<string, WarmCache[]> _caches = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates an empty store.</summary>
    public WarmCacheStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public IReadOnlyList<WarmCache> Lookup(string conversation) =>
        _caches.TryGetValue(conversation, out WarmCache[]? caches) ? caches : [];

    /// <inheritdoc />
    public void Remember(string conversation, WarmCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _caches.AddOrUpdate(
            conversation,
            _ => [cache],
            (_, caches) => [.. caches.Where(c => !Same(c, cache.Model)), cache]);
        Prune();
    }

    /// <inheritdoc />
    public void Forget(string conversation, string? model = null)
    {
        if (model is null)
        {
            _caches.TryRemove(conversation, out _);
            return;
        }

        if (_caches.TryGetValue(conversation, out WarmCache[]? caches) && caches.Any(c => Same(c, model)))
        {
            _caches[conversation] = [.. caches.Where(c => !Same(c, model))];
        }
    }

    private static bool Same(WarmCache cache, string model) => string.Equals(cache.Model, model, StringComparison.OrdinalIgnoreCase);

    private void Prune()
    {
        if (_caches.Count <= PruneAbove)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach ((string key, WarmCache[] caches) in _caches)
        {
            if (caches.All(c => c.ExpiresAt <= now))
            {
                _caches.TryRemove(key, out _);
            }
        }
    }
}
