using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>How much of each prompt the activity log keeps.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptStorage>))]
public enum PromptStorage
{
    /// <summary>
    /// The first 160 characters, with credentials (API keys, tokens, private keys) masked as <c>***</c>, and the same masking
    /// in a failed CLI run's output and the judge's reason. Default.
    /// </summary>
    Redacted = 0,

    /// <summary>The first 160 characters as they are.</summary>
    On = 1,

    /// <summary>
    /// No prompt text: the activity log, its exports and the live view show none, and tuning learns nothing from them.
    /// Nor what models wrote about it: a failed CLI run's output (in the activity log and the logs) and the judge's reason.
    /// </summary>
    Off = 2,
}

/// <summary>What the router keeps about requests, and for how long (<c>SemanticRouter:State</c>).</summary>
public sealed class StateOptions
{
    /// <summary>How much of each prompt the activity log keeps. Default <see cref="PromptStorage.Redacted"/>.</summary>
    public PromptStorage StorePrompts { get; set; } = PromptStorage.Redacted;

    /// <summary>How long the state database keeps each kind of record.</summary>
    public StateRetentionOptions Retention { get; set; } = new();
}

/// <summary>
/// How long the state database (<c>UseSqliteState</c>) keeps records. Unset values keep the store's own defaults:
/// activity and feedback 30 days and 10,000 entries, usage 400 days, 200 configuration versions, audit log 365 days.
/// </summary>
public sealed class StateRetentionOptions
{
    /// <summary>Days of activity (with its prompt previews) and feedback kept.</summary>
    public int? ActivityDays { get; set; }

    /// <summary>Most activity entries kept.</summary>
    public int? ActivityEntries { get; set; }

    /// <summary>Days of usage records kept. Budgets need the current period; the dashboard shows up to a year.</summary>
    public int? UsageDays { get; set; }

    /// <summary>Most configuration versions kept for history and rollback.</summary>
    public int? ConfigurationVersions { get; set; }

    /// <summary>
    /// Days of the audit log (who changed what through the dashboard and admin API, sign-ins) kept. Not capped by the
    /// machine policy's <c>MaxRetentionDays</c>, which is about prompt text: the audit log holds none.
    /// </summary>
    public int? AuditDays { get; set; }
}

/// <summary>Takes the retention settings of each applied configuration (implemented by durable state stores).</summary>
internal interface IStateRetention
{
    void Apply(StateRetentionOptions retention);
}
