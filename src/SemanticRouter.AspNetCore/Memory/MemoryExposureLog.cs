namespace SemanticRouter.AspNetCore;

/// <summary>Where one note has been sent since the router started (or since the log was cleared).</summary>
internal sealed record MemoryExposure
{
    /// <summary>Requests that carried the note's text to a local model.</summary>
    public required int LocalContent { get; init; }

    /// <summary>Requests that only mentioned the note, to a local model.</summary>
    public required int LocalReference { get; init; }

    /// <summary>Requests that carried the note's text to a model that is not local.</summary>
    public required int CloudContent { get; init; }

    /// <summary>Requests that only mentioned the note, to a model that is not local.</summary>
    public required int CloudReference { get; init; }

    /// <summary>The first time.</summary>
    public required DateTimeOffset FirstShared { get; init; }

    /// <summary>The latest time.</summary>
    public required DateTimeOffset LastShared { get; init; }

    /// <summary>Requests per model.</summary>
    public required IReadOnlyDictionary<string, int> Models { get; init; }
}

/// <summary>
/// Counts, per memory note, the requests that carried it to each model, split by local and cloud. Kept in memory: it
/// starts empty on every restart, like the activity log.
/// </summary>
internal sealed class MemoryExposureLog
{
    private const int MaxNotes = 20_000;

    private readonly Dictionary<string, Counter> _notes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Records that <paramref name="references"/> were sent to <paramref name="model"/>.</summary>
    public void Record(IEnumerable<MemoryReference> references, string model, bool isLocal, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentException.ThrowIfNullOrEmpty(model);
        lock (_gate)
        {
            foreach (MemoryReference reference in references)
            {
                if (!_notes.TryGetValue(reference.Id, out Counter? counter))
                {
                    if (_notes.Count >= MaxNotes)
                    {
                        continue;
                    }

                    _notes[reference.Id] = counter = new Counter { FirstShared = at };
                }

                bool content = reference.Kind == MemoryShareKind.Content;
                switch (isLocal, content)
                {
                    case (true, true): counter.LocalContent++; break;
                    case (true, false): counter.LocalReference++; break;
                    case (false, true): counter.CloudContent++; break;
                    default: counter.CloudReference++; break;
                }

                counter.LastShared = at;
                counter.Models[model] = counter.Models.GetValueOrDefault(model) + 1;
            }
        }
    }

    /// <summary>Every note that has been sent somewhere, by note id.</summary>
    public IReadOnlyDictionary<string, MemoryExposure> Snapshot()
    {
        lock (_gate)
        {
            return _notes.ToDictionary(
                kv => kv.Key,
                kv => new MemoryExposure
                {
                    LocalContent = kv.Value.LocalContent,
                    LocalReference = kv.Value.LocalReference,
                    CloudContent = kv.Value.CloudContent,
                    CloudReference = kv.Value.CloudReference,
                    FirstShared = kv.Value.FirstShared,
                    LastShared = kv.Value.LastShared,
                    Models = new Dictionary<string, int>(kv.Value.Models, StringComparer.OrdinalIgnoreCase),
                },
                StringComparer.Ordinal);
        }
    }

    /// <summary>Forgets everything recorded.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _notes.Clear();
        }
    }

    private sealed class Counter
    {
        public int LocalContent;
        public int LocalReference;
        public int CloudContent;
        public int CloudReference;
        public DateTimeOffset FirstShared;
        public DateTimeOffset LastShared;
        public Dictionary<string, int> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
