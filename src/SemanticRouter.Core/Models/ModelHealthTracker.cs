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
/// In-memory <see cref="IModelHealth"/> with time-based cooldowns. This is what makes "my subscription hit its
/// limit" fall through to the next model automatically. Thread-safe; register as a singleton.
/// </summary>
public sealed class ModelHealthTracker : IModelHealth
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModelSelectionOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates a tracker.</summary>
    public ModelHealthTracker(ModelSelectionOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new ModelSelectionOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsAvailable(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
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
        _cooldownUntil.TryRemove(model.Name, out _);
    }

    /// <inheritdoc />
    public void ReportFailure(ModelDefinition model, ModelFailureKind kind)
    {
        ArgumentNullException.ThrowIfNull(model);
        TimeSpan cooldown = kind == ModelFailureKind.QuotaExceeded ? _options.QuotaCooldown : _options.FailureCooldown;
        if (cooldown > TimeSpan.Zero)
        {
            _cooldownUntil[model.Name] = _time.GetUtcNow() + cooldown;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DateTimeOffset> GetCooldowns()
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _cooldownUntil.Where(kv => kv.Value > now).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
