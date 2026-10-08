namespace SemanticRouter.AspNetCore;

/// <summary>Where one note has been sent since the router started (or since the log was cleared).</summary>
/// <param name="LocalContent">Requests that carried the note's text to a local model.</param>
/// <param name="LocalReference">Requests that only mentioned the note, to a local model.</param>
/// <param name="CloudContent">Requests that carried the note's text to a model that is not local.</param>
/// <param name="CloudReference">Requests that only mentioned the note, to a model that is not local.</param>
/// <param name="FirstShared">The first time.</param>
/// <param name="LastShared">The latest time.</param>
/// <param name="Models">Requests per model.</param>
public sealed record MemoryExposure(
    int LocalContent, int LocalReference, int CloudContent, int CloudReference,
    DateTimeOffset FirstShared, DateTimeOffset LastShared, IReadOnlyDictionary<string, int> Models);

/// <summary>
/// Counts, per memory note, the requests that carried it to each model, split by local and cloud. Kept in memory: it
/// starts empty on every restart, like the activity log.
/// </summary>
public sealed class MemoryExposureLog
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
                kv => new MemoryExposure(
                    kv.Value.LocalContent, kv.Value.LocalReference, kv.Value.CloudContent, kv.Value.CloudReference,
                    kv.Value.FirstShared, kv.Value.LastShared,
                    new Dictionary<string, int>(kv.Value.Models, StringComparer.OrdinalIgnoreCase)),
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
