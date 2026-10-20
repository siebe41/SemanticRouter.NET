namespace SemanticRouter.AspNetCore;

/// <summary>Where the dashboard keeps its evaluation set (labelled prompts) between runs.</summary>
public interface IEvaluationSetStore
{
    /// <summary>A human-readable location (a file path), or <see langword="null"/> when the set lives in memory.</summary>
    string? Location { get; }

    /// <summary>The stored cases, in order.</summary>
    IReadOnlyList<EvaluationCase> Load();

    /// <summary>Replaces the stored cases.</summary>
    void Save(IReadOnlyList<EvaluationCase> cases);
}

/// <summary>Keeps the evaluation set in memory: it is lost on restart.</summary>
internal sealed class InMemoryEvaluationSetStore : IEvaluationSetStore
{
    private IReadOnlyList<EvaluationCase> _cases = [];

    /// <inheritdoc />
    public string? Location => null;

    /// <inheritdoc />
    public IReadOnlyList<EvaluationCase> Load() => Volatile.Read(ref _cases);

    /// <inheritdoc />
    public void Save(IReadOnlyList<EvaluationCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        Volatile.Write(ref _cases, cases.ToArray());
    }
}

/// <summary>Keeps the evaluation set in a JSON Lines file, created on first save.</summary>
internal sealed class JsonLinesEvaluationSetStore : IEvaluationSetStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>Creates a store at <paramref name="path"/> (relative paths resolve against the current directory).</summary>
    public JsonLinesEvaluationSetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public string? Location => _path;

    /// <inheritdoc />
    /// <exception cref="FormatException">The file is not a valid evaluation set.</exception>
    public IReadOnlyList<EvaluationCase> Load()
    {
        lock (_gate)
        {
            return File.Exists(_path) ? EvaluationSet.Parse(File.ReadAllText(_path)) : [];
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<EvaluationCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, EvaluationSet.ToJsonLines(cases));
            OwnerOnly.Restrict(temp);
            File.Move(temp, _path, overwrite: true);
        }
    }
}
