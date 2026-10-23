using System.Collections.Concurrent;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Remembers which catalog model issued each tool call, so that the follow-up request carrying the tool results is
/// served by the same model (a tool-calling turn should not change models halfway through).
/// </summary>
public interface IToolCallAffinity
{
    /// <summary>Records that <paramref name="modelName"/> issued the tool call <paramref name="toolUseId"/>.</summary>
    void Remember(string toolUseId, string modelName);

    /// <summary>Returns the model that issued any of <paramref name="toolUseIds"/>, or <see langword="null"/> when unknown.</summary>
    string? Lookup(IEnumerable<string> toolUseIds);
}

/// <summary>In-memory <see cref="IToolCallAffinity"/> with a bounded size (oldest entries are evicted first).</summary>
public sealed class ToolCallAffinity : IToolCallAffinity
{
    private readonly ConcurrentDictionary<string, string> _models = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;

    /// <summary>Creates a cache that keeps the latest 4096 tool-call ids.</summary>
    public ToolCallAffinity()
        : this(4096)
    {
    }

    /// <summary>Creates a cache that keeps the latest <paramref name="capacity"/> tool-call ids.</summary>
    public ToolCallAffinity(int capacity)
    {
        _capacity = Math.Max(16, capacity);
    }

    /// <inheritdoc />
    public void Remember(string toolUseId, string modelName)
    {
        if (string.IsNullOrEmpty(toolUseId))
        {
            return;
        }

        if (_models.TryAdd(toolUseId, modelName))
        {
            _order.Enqueue(toolUseId);
            while (_order.Count > _capacity && _order.TryDequeue(out string? oldest))
            {
                _models.TryRemove(oldest, out _);
            }
        }
        else
        {
            _models[toolUseId] = modelName;
        }
    }

    /// <inheritdoc />
    public string? Lookup(IEnumerable<string> toolUseIds)
    {
        ArgumentNullException.ThrowIfNull(toolUseIds);
        foreach (string id in toolUseIds)
        {
            if (_models.TryGetValue(id, out string? model))
            {
                return model;
            }
        }

        return null;
    }
}
