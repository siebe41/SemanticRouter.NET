using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter;

/// <summary>Settings for the classification cache (<see cref="SemanticRouterOptions.ClassificationCache"/>).</summary>
public sealed class ClassificationCacheOptions
{
    /// <summary>Reuse decisions for prompts classified recently. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Most decisions kept; the least recently used go first. Default 2048.</summary>
    public int MaxEntries { get; set; } = 2048;

    /// <summary>How long a decision is reused. Default 10 minutes.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>How the classification cache is doing.</summary>
public sealed record ClassificationCacheStatistics
{
    /// <summary>Prompts answered from the cache.</summary>
    public required long Hits { get; init; }

    /// <summary>Prompts classified (embedded) because they were not cached.</summary>
    public required long Misses { get; init; }

    /// <summary>Decisions currently kept.</summary>
    public required int Entries { get; init; }

    /// <summary>Most decisions kept.</summary>
    public required int Capacity { get; init; }

    /// <summary><see cref="Hits"/> / (<see cref="Hits"/> + <see cref="Misses"/>).</summary>
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}

/// <summary>
/// A least-recently-used cache of classification decisions, keyed by a SHA-256 of the normalised prompt (whitespace
/// runs collapsed, ends trimmed). It lives in memory only and belongs to one classifier, so one configuration version:
/// a new configuration starts with an empty cache. Thread-safe.
/// </summary>
internal sealed class ClassificationCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _time;
    private long _hits;
    private long _misses;

    public ClassificationCache(ClassificationCacheOptions options, TimeProvider time)
    {
        _capacity = Math.Max(1, options.MaxEntries);
        _maxAge = options.MaxAge;
        _time = time;
    }

    public ClassificationCacheStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new ClassificationCacheStatistics { Hits = _hits, Misses = _misses, Entries = _entries.Count, Capacity = _capacity };
            }
        }
    }

    public static string KeyOf(string prompt)
    {
        var normalised = new StringBuilder(prompt.Length);
        bool space = false;
        foreach (char c in prompt.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && normalised.Length > 0)
            {
                normalised.Append(' ');
            }

            space = false;
            normalised.Append(c);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised.ToString())));
    }

    public bool TryGet(string key, out PromptClassificationResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                if (_time.GetUtcNow() - node.Value.Stored <= _maxAge)
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    _hits++;
                    result = node.Value.Result;
                    return true;
                }

                _order.Remove(node);
                _entries.Remove(key);
            }

            _misses++;
            result = null!;
            return false;
        }
    }

    public void Add(string key, PromptClassificationResult result)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
            {
                _order.Remove(existing);
            }

            _entries[key] = _order.AddFirst(new Entry(key, result, _time.GetUtcNow()));
            while (_entries.Count > _capacity)
            {
                LinkedListNode<Entry> oldest = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }

    private sealed record Entry(string Key, PromptClassificationResult Result, DateTimeOffset Stored);
}
