using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What a learned change touches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TuningChangeKind>))]
public enum TuningChangeKind
{
    /// <summary><see cref="SemanticRouterOptions.DefaultSimilarityThreshold"/>.</summary>
    DefaultThreshold,

    /// <summary>A route's own <see cref="RouteDefinition.SimilarityThreshold"/>.</summary>
    RouteThreshold,

    /// <summary>An utterance added to a route.</summary>
    Utterance,
}

/// <summary>Where a learned change stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TuningChangeState>))]
public enum TuningChangeState
{
    /// <summary>In the configuration.</summary>
    Applied,

    /// <summary>Waiting on the dashboard for someone to apply it.</summary>
    Suggested,

    /// <summary>Applied, then undone.</summary>
    Undone,

    /// <summary>A suggestion someone turned down.</summary>
    Dismissed,

    /// <summary>A suggestion that no longer fits the configuration, or that a newer one replaced.</summary>
    Expired,
}

/// <summary>
/// One change automatic tuning made or suggests: what (<see cref="Kind"/>, <see cref="Route"/>, the old and new
/// value or the utterance), why (<see cref="Why"/>, with the evidence), and when.
/// </summary>
public sealed record TuningChange
{
    /// <summary>Sequence number.</summary>
    public long Id { get; init; }

    /// <summary>When it was made or suggested.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>What it touches.</summary>
    public TuningChangeKind Kind { get; init; }

    /// <summary>The route, except for the default threshold.</summary>
    public string? Route { get; init; }

    /// <summary>The threshold before (for a route, <see langword="null"/>: it used the default).</summary>
    public float? OldValue { get; init; }

    /// <summary>The threshold after (for a route, <see langword="null"/>: it uses the default).</summary>
    public float? NewValue { get; init; }

    /// <summary>Where calibration says the threshold should end up; it gets there in steps.</summary>
    public float? Target { get; init; }

    /// <summary>The utterance added.</summary>
    public string? Utterance { get; init; }

    /// <summary>Why, in a sentence, with the evidence.</summary>
    public string Why { get; init; } = string.Empty;

    /// <summary>Labelled prompts the change (at its target) routes to their tier that were not.</summary>
    public int Fixed { get; init; }

    /// <summary>Labelled prompts it routes away from their tier.</summary>
    public int Broken { get; init; }

    /// <summary>The weighted gain, used to tell whether later evidence is much stronger.</summary>
    public double Gain { get; init; }

    /// <summary>A few of the prompts it fixes (previews).</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Where it stands.</summary>
    public TuningChangeState State { get; init; }

    /// <summary>The configuration version that applied it.</summary>
    public long? Version { get; init; }

    /// <summary>When it was undone (or dismissed).</summary>
    public DateTimeOffset? UndoneAt { get; init; }

    /// <summary>Who undid, applied or dismissed it.</summary>
    public string? Actor { get; init; }

    /// <summary>The configuration version that undid it.</summary>
    public long? UndoVersion { get; init; }

    /// <summary>Which setting it is, so changes to the same setting can be found: <c>threshold:</c>, <c>threshold:route</c> or <c>utterance:route:text</c>.</summary>
    [JsonIgnore]
    public string Setting => Kind switch
    {
        TuningChangeKind.DefaultThreshold => "threshold:",
        TuningChangeKind.RouteThreshold => "threshold:" + Route?.ToLowerInvariant(),
        _ => "utterance:" + Route?.ToLowerInvariant() + ":" + Utterance?.Trim().ToLowerInvariant(),
    };

    /// <summary>A short label: "threshold of complex-code 0.52 → 0.48", "default threshold 0.75 → 0.73", "utterance for code".</summary>
    [JsonIgnore]
    public string Summary => Kind switch
    {
        TuningChangeKind.DefaultThreshold => $"default threshold {Format(OldValue)} → {Format(NewValue)}",
        TuningChangeKind.RouteThreshold => $"threshold of {Route} {Format(OldValue)} → {Format(NewValue)}",
        _ => $"utterance for {Route}: \"{Preview(Utterance, 60)}\"",
    };

    internal static string Format(float? threshold) =>
        threshold is { } value ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "default";

    internal static string Preview(string? text, int length) =>
        text is null ? string.Empty : text.Length <= length ? text : text[..length] + "…";
}

/// <summary>Automatic tuning's own state: paused or not, and the last run.</summary>
public sealed record TuningState
{
    /// <summary>Learning is paused: nothing runs until it is resumed.</summary>
    public bool Paused { get; init; }

    /// <summary>When tuning last ran.</summary>
    public DateTimeOffset? LastRun { get; init; }

    /// <summary>The size of the evaluation set at the last run, to run early after new labels.</summary>
    public int LastLabelCount { get; init; }

    /// <summary>What the last run did, in a sentence.</summary>
    public string? LastResult { get; init; }
}

/// <summary>Keeps learned changes and tuning state across restarts.</summary>
public interface ITuningStore
{
    /// <summary>Every change, oldest first.</summary>
    IReadOnlyList<TuningChange> Changes();

    /// <summary>Adds or replaces a change (by <see cref="TuningChange.Id"/>).</summary>
    void Save(TuningChange change);

    /// <summary>The state; a new one when nothing was saved.</summary>
    TuningState LoadState();

    /// <summary>Saves the state.</summary>
    void SaveState(TuningState state);
}

/// <summary>The default <see cref="ITuningStore"/>: in memory, gone on restart.</summary>
public sealed class InMemoryTuningStore : ITuningStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<long, TuningChange> _changes = [];
    private TuningState _state = new();

    /// <inheritdoc />
    public IReadOnlyList<TuningChange> Changes()
    {
        lock (_gate)
        {
            return _changes.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public void Save(TuningChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _changes[change.Id] = change;
        }
    }

    /// <inheritdoc />
    public TuningState LoadState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    /// <inheritdoc />
    public void SaveState(TuningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = state;
        }
    }
}
