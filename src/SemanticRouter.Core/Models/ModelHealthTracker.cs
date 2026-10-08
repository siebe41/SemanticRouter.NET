using System.Collections.Concurrent;

namespace SemanticRouter;

/// <summary>Why a model attempt failed.</summary>
public enum ModelFailureKind
{
    /// <summary>The model failed in a way that may be transient (5xx, crash, timeout, unreachable).</summary>
    Unavailable = 0,

    /// <summary>Quota or rate limit exhausted (HTTP 429, subscription usage limit). Cooled down for longer.</summary>
    QuotaExceeded = 1,
}

/// <summary>Tracks which models are temporarily unavailable.</summary>
public interface IModelHealth
{
    /// <summary>Whether <paramref name="model"/> should currently be tried.</summary>
    bool IsAvailable(ModelDefinition model);

    /// <summary>Records a successful call, clearing any cooldown.</summary>
    void ReportSuccess(ModelDefinition model);

    /// <summary>Records a failure, putting the model into cooldown.</summary>
    void ReportFailure(ModelDefinition model, ModelFailureKind kind);

    /// <summary>The models currently cooling down and when each becomes available again.</summary>
    IReadOnlyDictionary<string, DateTimeOffset> GetCooldowns();
}

/// <summary>
/// Keeps cooldowns somewhere that outlives the process (a database file, a cache shared by several router instances).
/// Cooldowns are stored with their absolute expiry, so one that started before a restart ends when it was meant to.
/// </summary>
public interface ICooldownStore
{
    /// <summary>Every stored cooldown, by model name. Expired ones may be included; the caller ignores them.</summary>
    IReadOnlyDictionary<string, DateTimeOffset> LoadCooldowns();

    /// <summary>Stores (or replaces) the cooldown of <paramref name="model"/>.</summary>
    void SaveCooldown(string model, DateTimeOffset until);

    /// <summary>Removes the cooldown of <paramref name="model"/>.</summary>
    void RemoveCooldown(string model);

    /// <summary>Removes every cooldown.</summary>
    void ClearCooldowns();
}

/// <summary>
/// <see cref="IModelHealth"/> with time-based cooldowns. This is what makes "my subscription hit its limit" fall
/// through to the next model automatically. Cooldowns live in memory and, when an <see cref="ICooldownStore"/> is
/// given, are written through to it and loaded from it on start, so a restart does not forget a weekly limit. When
/// the store is shared by several instances, <see cref="RefreshInterval"/> picks up the others' cooldowns.
/// Thread-safe; register as a singleton.
/// </summary>
public sealed class ModelHealthTracker : IModelHealth
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly ICooldownStore? _store;
    private volatile ModelSelectionOptions _options;
    private long _lastRefreshTicks;

    /// <summary>Creates a tracker.</summary>
    public ModelHealthTracker(ModelSelectionOptions? options = null, TimeProvider? timeProvider = null, ICooldownStore? store = null)
    {
        _options = options ?? new ModelSelectionOptions();
        _time = timeProvider ?? TimeProvider.System;
        _store = store;
        Refresh();
    }

    /// <summary>
    /// How often cooldowns are re-read from the store, to see ones recorded by other router instances. Default 5
    /// seconds; <see cref="TimeSpan.Zero"/> reads only at start-up.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Replaces the cooldown durations, e.g. after configuration is reloaded. Existing cooldowns keep their expiry.</summary>
    public void Configure(ModelSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Clears every cooldown so all models are tried again immediately.</summary>
    public void Clear()
    {
        _cooldownUntil.Clear();
        _store?.ClearCooldowns();
    }

    /// <inheritdoc />
    public bool IsAvailable(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        RefreshIfDue();
        if (!_cooldownUntil.TryGetValue(model.Name, out DateTimeOffset until))
        {
            return true;
        }

        if (_time.GetUtcNow() >= until)
        {
            _cooldownUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(model.Name, until));
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void ReportSuccess(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_cooldownUntil.ContainsKey(model.Name))
        {
            // Only a model that was cooling down costs a write; ordinary successes stay in memory. The store goes
            // first so a concurrent refresh cannot bring the cooldown back.
            _store?.RemoveCooldown(model.Name);
            _cooldownUntil.TryRemove(model.Name, out _);
        }
    }

    /// <inheritdoc />
    public void ReportFailure(ModelDefinition model, ModelFailureKind kind)
    {
        ArgumentNullException.ThrowIfNull(model);
        TimeSpan cooldown = kind == ModelFailureKind.QuotaExceeded ? _options.QuotaCooldown : _options.FailureCooldown;
        if (cooldown > TimeSpan.Zero)
        {
            DateTimeOffset until = _time.GetUtcNow() + cooldown;
            _store?.SaveCooldown(model.Name, until);
            _cooldownUntil[model.Name] = until;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DateTimeOffset> GetCooldowns()
    {
        RefreshIfDue();
        DateTimeOffset now = _time.GetUtcNow();
        return _cooldownUntil.Where(kv => kv.Value > now).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshIfDue()
    {
        if (_store is null || RefreshInterval <= TimeSpan.Zero)
        {
            return;
        }

        long now = _time.GetUtcNow().UtcTicks;
        long last = Interlocked.Read(ref _lastRefreshTicks);
        if (now - last >= RefreshInterval.Ticks && Interlocked.CompareExchange(ref _lastRefreshTicks, now, last) == last)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_store is null)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        Interlocked.Exchange(ref _lastRefreshTicks, now.UtcTicks);
        IReadOnlyDictionary<string, DateTimeOffset> stored = _store.LoadCooldowns();
        foreach ((string model, DateTimeOffset until) in stored)
        {
            if (until > now)
            {
                _cooldownUntil[model] = until;
            }
        }

        // A cooldown another instance cleared (success, reset) ends here too.
        foreach (string model in _cooldownUntil.Keys)
        {
            if (!stored.ContainsKey(model))
            {
                _cooldownUntil.TryRemove(model, out _);
            }
        }
    }
}
