using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The automatic tuning endpoints of the admin API: what the router learned (with the evidence and the effect since),
/// running it now, undoing one change or all of them, applying or dismissing suggestions, and pausing.
/// </summary>
internal static class TuningApi
{
    private const int MaxChanges = 200;
    private static readonly TimeSpan EffectWindow = TimeSpan.FromDays(7);

    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json, Func<HttpContext, string> actor)
    {
        api.MapGet("/tuning", (RouterTuner tuner, RouterRuntimeProvider provider, RoutingActivityLog activity, RoutingFeedbackLog feedback, int? limit) =>
            Results.Json(Status(tuner, provider, activity, feedback, limit), json));

        api.MapGet("/tuning/changes", (RouterTuner tuner, RouterRuntimeProvider provider, RoutingActivityLog activity, RoutingFeedbackLog feedback, int? limit) =>
            Results.Json(Status(tuner, provider, activity, feedback, limit).Changes, json));

        // How often the judge's grades agree with the hand labels, and what weight that gives its labels in tuning.
        api.MapGet("/judge/agreement", (HttpContext context, RouterRuntimeProvider provider, RoutingActivityLog activity, IEvaluationSetStore labels) =>
            Results.Json(
                JudgeAgreement.Measure(provider.Current, activity, labels, (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()),
                json));

        api.MapPost("/tuning/run", async (RouterTuner tuner, RouterRuntimeProvider provider, CancellationToken cancellationToken) =>
        {
            try
            {
                TuningRun run = await tuner.RunAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(new RunResponse(run.At, run.Result, [.. run.Changes.Select(c => ChangeDto.From(c, null))], run.Before, run.After) { Saved = provider.Saved }, json);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                return Results.Problem($"Tuning could not run: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("ran automatic tuning");

        api.MapPost("/tuning/changes/{id:long}/undo", (HttpContext context, RouterTuner tuner, RouterRuntimeProvider provider, long id) =>
            Act(context, provider, () => tuner.Undo(id, actor(context)), json)).WithMetadata(DashboardWriteMetadata.Instance).Audited("undid a learned change");

        api.MapPost("/tuning/changes/{id:long}/apply", (HttpContext context, RouterTuner tuner, RouterRuntimeProvider provider, long id) =>
            Act(context, provider, () => tuner.ApplySuggestion(id, actor(context)), json)).WithMetadata(DashboardWriteMetadata.Instance).Audited("applied a suggested change");

        api.MapPost("/tuning/changes/{id:long}/dismiss", (HttpContext context, RouterTuner tuner, RouterRuntimeProvider provider, long id) =>
            Act(context, provider, () => tuner.Dismiss(id, actor(context)), json)).WithMetadata(DashboardWriteMetadata.Instance).Audited("dismissed a suggested change");

        api.MapPost("/tuning/undo-all", (HttpContext context, RouterTuner tuner, RouterRuntimeProvider provider) =>
        {
            (int undone, IReadOnlyList<string> skipped) = tuner.UndoAll(actor(context));
            DashboardAudit.Detail(context, $"{undone} undone");
            return Results.Json(new UndoAllResponse(undone, skipped) { Saved = provider.Saved }, json);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("undid every learned change");

        api.MapPost("/tuning/pause", (RouterTuner tuner) => Results.Json(tuner.SetPaused(true), json))
            .WithMetadata(DashboardWriteMetadata.Instance).Audited("paused automatic tuning");

        api.MapPost("/tuning/resume", (RouterTuner tuner) => Results.Json(tuner.SetPaused(false), json))
            .WithMetadata(DashboardWriteMetadata.Instance).Audited("resumed automatic tuning");
    }

    /// <remarks>
    /// The response's <c>saved</c> says whether the running configuration is saved afterwards: a learned change made
    /// while it holds changes applied without saving is applied without saving too, so it does not save those.
    /// </remarks>
    private static IResult Act(HttpContext context, RouterRuntimeProvider provider, Func<TuningChange> action, JsonSerializerOptions json)
    {
        try
        {
            TuningChange change = action();
            bool saved = provider.Saved;
            DashboardAudit.Detail(context, $"change {change.Id}{(saved || provider.Store is null ? string.Empty : ", not saved")}");
            return Results.Json(ChangeDto.From(change, null) with { Saved = saved }, json);
        }
        catch (TuningConflictException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);
        }
        catch (RouterConfigurationConflictException)
        {
            return Results.Problem("The configuration kept changing while this was applied. Try again.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static TuningResponse Status(RouterTuner tuner, RouterRuntimeProvider provider, RoutingActivityLog activity, RoutingFeedbackLog feedback, int? limit)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TuningChange[] changes = [.. tuner.Changes().Take(Math.Clamp(limit ?? 100, 1, MaxChanges))];
        TuningChange[] measured = [.. changes.Where(c => c.State is TuningChangeState.Applied or TuningChangeState.Undone && now - c.Timestamp >= TimeSpan.FromHours(1))];
        Func<TuningChange, Effect?> effect = _ => null;
        if (measured.Length > 0)
        {
            DateTimeOffset since = measured.Min(c => c.Timestamp) - EffectWindow;
            RoutingActivityEntry[] decisions = [.. activity.Between(since, now).Where(e => !e.ToolContinuation
                && e.Reason is not (RoutingReason.ModelOverride or RoutingReason.ExplicitCallout or RoutingReason.ConversationAffinity))];
            HashSet<long> judged = [.. feedback.Between(since, now).Select(f => f.EntryId)];
            effect = change => Measure(change, decisions, judged, now);
        }

        TuningState state = tuner.State;
        TuningOptions options = provider.Current.Configuration.Tuning;
        return new TuningResponse(
            options.Mode,
            state.Paused,
            state.LastRun,
            tuner.NextRun,
            state.LastResult,
            tuner.Changes().Count(c => c.State == TuningChangeState.Applied && now - c.Timestamp <= TimeSpan.FromDays(7)),
            tuner.Changes().Count(c => c.State == TuningChangeState.Suggested),
            [.. changes.Select(c => ChangeDto.From(c, effect(c)))]);
    }

    /// <summary>Feedback rate on the decisions the change touches, the same length of time before and after it.</summary>
    private static Effect Measure(TuningChange change, RoutingActivityEntry[] decisions, HashSet<long> judged, DateTimeOffset now)
    {
        TimeSpan window = TimeSpan.FromTicks(Math.Min(EffectWindow.Ticks, (now - change.Timestamp).Ticks));
        IEnumerable<RoutingActivityEntry> touched = change.Kind == TuningChangeKind.DefaultThreshold
            ? decisions
            : decisions.Where(e => string.Equals(e.Route, change.Route, StringComparison.OrdinalIgnoreCase) || e.Learned == change.Id);
        RoutingActivityEntry[] before = [.. touched.Where(e => e.Timestamp >= change.Timestamp - window && e.Timestamp < change.Timestamp)];
        RoutingActivityEntry[] after = [.. touched.Where(e => e.Timestamp >= change.Timestamp && e.Timestamp < change.Timestamp + window)];
        return new Effect(
            (int)Math.Round(window.TotalDays * 24),
            before.Length,
            before.Count(e => judged.Contains(e.Id)),
            after.Length,
            after.Count(e => judged.Contains(e.Id)),
            after.Count(e => e.Learned == change.Id));
    }
}

/// <summary>Automatic tuning at a glance, with the learned changes newest first.</summary>
/// <param name="Mode">Apply, Suggest or Off.</param>
/// <param name="Paused">Learning is paused.</param>
/// <param name="LastRun">When it last ran.</param>
/// <param name="NextRun">When it runs next by the clock.</param>
/// <param name="LastResult">What the last run did.</param>
/// <param name="ThisWeek">Changes applied in the last 7 days.</param>
/// <param name="Suggestions">Suggestions waiting.</param>
/// <param name="Changes">The changes, newest first.</param>
internal sealed record TuningResponse(
    TuningMode Mode,
    bool Paused,
    DateTimeOffset? LastRun,
    DateTimeOffset? NextRun,
    string? LastResult,
    int ThisWeek,
    int Suggestions,
    IReadOnlyList<ChangeDto> Changes);

/// <summary>A learned change for the dashboard: the stored change plus its label and effect.</summary>
internal sealed record ChangeDto(
    long Id,
    DateTimeOffset Timestamp,
    TuningChangeKind Kind,
    string? Route,
    float? OldValue,
    float? NewValue,
    float? Target,
    string? Utterance,
    string? OldModel,
    string? NewModel,
    string? Incumbent,
    string Summary,
    string Why,
    int Fixed,
    int Broken,
    IReadOnlyList<string> Examples,
    TuningChangeState State,
    long? Version,
    DateTimeOffset? UndoneAt,
    string? Actor,
    long? UndoVersion,
    Effect? Effect)
{
    /// <summary>
    /// After applying, undoing or dismissing it: whether the running configuration is saved. <see langword="false"/> when
    /// the change was applied without saving because unsaved changes were running (or there is no store). Not set in lists.
    /// </summary>
    public bool? Saved { get; init; }

    public static ChangeDto From(TuningChange c, Effect? effect) => new(
        c.Id, c.Timestamp, c.Kind, c.Route, c.OldValue, c.NewValue, c.Target, c.Utterance, c.OldModel, c.NewModel, c.Incumbent, c.Summary, c.Why, c.Fixed, c.Broken,
        c.Examples, c.State, c.Version, c.UndoneAt, c.Actor, c.UndoVersion, effect);
}

/// <summary>Decisions on what a change touches, and how many got feedback, over the same span before and after it.</summary>
/// <param name="Hours">The span on each side.</param>
/// <param name="DecisionsBefore">Decisions before.</param>
/// <param name="FeedbackBefore">Of those, with feedback.</param>
/// <param name="DecisionsAfter">Decisions after.</param>
/// <param name="FeedbackAfter">Of those, with feedback.</param>
/// <param name="Rerouted">Decisions after it that it routed differently.</param>
internal sealed record Effect(int Hours, int DecisionsBefore, int FeedbackBefore, int DecisionsAfter, int FeedbackAfter, int Rerouted);

/// <summary>What one run did (<see cref="TuningRun"/>), with the changes as the dashboard shows them.</summary>
internal sealed record RunResponse(DateTimeOffset At, string Result, IReadOnlyList<ChangeDto> Changes, TuningScore? Before, TuningScore? After)
{
    /// <summary>Whether the running configuration is saved after the run (see <see cref="ChangeDto.Saved"/>).</summary>
    public bool Saved { get; init; }
}

/// <summary>The result of undoing every automatic change.</summary>
internal sealed record UndoAllResponse(int Undone, IReadOnlyList<string> Skipped)
{
    /// <summary>Whether the running configuration is saved afterwards (see <see cref="ChangeDto.Saved"/>).</summary>
    public bool Saved { get; init; }
}
