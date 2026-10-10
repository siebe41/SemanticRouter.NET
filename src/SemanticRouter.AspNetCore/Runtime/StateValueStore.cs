namespace SemanticRouter.AspNetCore;

/// <summary>
/// Small named values that should outlast a restart: what the router noticed about its connectors
/// (<see cref="ConnectorIncidents"/>) and how many answers the judge graded today (<see cref="AnswerJudge"/>). Without
/// one (the default), they are kept in memory.
/// </summary>
public interface IStateValueStore
{
    /// <summary>The value stored under <paramref name="key"/>, or <see langword="null"/>.</summary>
    string? Load(string key);

    /// <summary>Stores (or replaces) a value. Must not throw for storage problems: log them and carry on.</summary>
    void Save(string key, string value);
}
