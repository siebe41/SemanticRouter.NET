using System.Collections.Concurrent;
using System.Text.Json;

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

/// <summary>
/// In-memory <see cref="IWarmCacheStore"/>, bounded: expired entries are pruned as it grows, and beyond 32,768
/// conversations the ones whose caches expire first go.
/// </summary>
public sealed class WarmCacheStore : IWarmCacheStore
{
    private const int PruneAbove = 4096;
    private const int MaxConversations = 32_768;

    private readonly ConcurrentDictionary<string, WarmCache[]> _caches = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TrackerPruning _pruning;

    /// <summary>Creates an empty store on the system clock.</summary>
    public WarmCacheStore()
        : this(null)
    {
    }

    /// <summary>Creates an empty store.</summary>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    public WarmCacheStore(TimeProvider? time)
    {
        _time = time ?? TimeProvider.System;
        _pruning = new TrackerPruning(MaxConversations, PruneAbove, _time);
    }

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

        // Kept while any of its caches is warm: past the latest expiry the conversation goes.
        _pruning.Written(_caches, caches => caches.Max(c => c.ExpiresAt), TimeSpan.Zero);
    }

    /// <inheritdoc />
    public void Forget(string conversation, string? model = null) => Remove(conversation, model);

    /// <summary>Like <see cref="Forget"/>; returns whether there was anything to forget.</summary>
    internal bool Remove(string conversation, string? model = null)
    {
        if (model is null)
        {
            return _caches.TryRemove(conversation, out _);
        }

        if (_caches.TryGetValue(conversation, out WarmCache[]? caches) && caches.Any(c => Same(c, model)))
        {
            _caches[conversation] = [.. caches.Where(c => !Same(c, model))];
            return true;
        }

        return false;
    }

    private static bool Same(WarmCache cache, string model) => string.Equals(cache.Model, model, StringComparison.OrdinalIgnoreCase);
}

/// <summary>How long the prompt cache entries a turn writes live, when the router can tell.</summary>
internal static class PromptCacheLifetime
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>
    /// The lifetime of the cache entries <paramref name="chat"/> writes through <paramref name="connector"/>. On an
    /// Anthropic connector it is set by the markers: the longest <c>ttl</c> among the client's own markers, or else the
    /// connector's <see cref="ConnectorOptions.CacheTtl"/> when the router adds them (<see cref="PromptCaching.Auto"/>).
    /// <see langword="null"/> when it is not known: other backends decide for themselves how long they cache.
    /// </summary>
    public static TimeSpan? Of(ChatCompletionRequest chat, ConnectorOptions connector)
    {
        if (connector.Type != ConnectorTypes.Anthropic)
        {
            return null;
        }

        if (chat.HasCacheMarkers)
        {
            IEnumerable<string?> markers = chat.Tools.Select(t => t.CacheControl)
                .Concat(chat.Turns.SelectMany(t => t.Blocks).Select(b => b.CacheControl))
                .Append(chat.CacheControl);
            return markers.Any(m => m is not null && Ttl(m) == "1h") ? OneHour : FiveMinutes;
        }

        return connector.PromptCaching == PromptCaching.Auto ? (connector.CacheTtl == "1h" ? OneHour : FiveMinutes) : null;
    }

    private static string? Ttl(string marker)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(marker);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("ttl", out JsonElement ttl) && ttl.ValueKind == JsonValueKind.String
                ? ttl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
