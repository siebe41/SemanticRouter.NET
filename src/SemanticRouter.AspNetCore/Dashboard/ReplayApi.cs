using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What-if replay on the admin API (#373): which recent requests a configuration change would move, and what that would
/// do to tiers, cost and subscription calls. It applies nothing; a change is applied from the dashboard's draft, which
/// keeps it in the configuration history for a rollback.
/// </summary>
internal static class ReplayApi
{
    /// <summary>The longest window a replay reads.</summary>
    public const int MaxDays = 90;

    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json)
    {
        // The body is the candidate configuration document, as PUT api/config takes it ("***" keeps a stored secret).
        api.MapPost("/replay", async (HttpContext context, RouterRuntimeProvider provider, RoutingActivityLog activity, IEvaluationSetStore store, int? days, bool? evaluation) =>
        {
            int window = days ?? 7;
            if (window is < 1 or > MaxDays)
            {
                return Results.Problem($"'days' is between 1 and {MaxDays}.", statusCode: StatusCodes.Status400BadRequest);
            }

            (RouterConfiguration? candidate, IResult? unreadable) = await RouterDashboardEndpoints.ReadConfigurationAsync(context, provider).ConfigureAwait(false);
            if (candidate is null)
            {
                return unreadable!;
            }

            ReplayOutcome outcome = await RunAsync(
                context.RequestServices, provider, activity, store, candidate, DashboardAccess.IsLocal(context), window, evaluation != false, context.RequestAborted).ConfigureAwait(false);
            return outcome switch
            {
                { Errors.Count: > 0 } => Results.Json(RouterDashboardEndpoints.Validation(outcome.Errors, candidate), json, statusCode: StatusCodes.Status400BadRequest),
                { Report: { } report } => Results.Json(report, json),
                _ => Results.Problem(outcome.Problem, statusCode: StatusCodes.Status502BadGateway),
            };
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("replayed recent requests through a configuration"); // embeds prompts
    }

    /// <summary>
    /// Replays the last <paramref name="days"/> days of activity (and, with <paramref name="evaluation"/>, the evaluation
    /// set) through the running configuration and <paramref name="candidate"/>, held to the checks applying it would meet.
    /// Shared by <c>POST api/replay</c> and the MCP server's <c>propose_tuning</c>. Applies nothing.
    /// </summary>
    /// <param name="services">The request's services, to tell whether the application registered its own pieces.</param>
    /// <param name="provider">The runtime provider.</param>
    /// <param name="activity">The activity log to replay.</param>
    /// <param name="store">The evaluation set.</param>
    /// <param name="candidate">The configuration to compare with the running one, secrets unmasked.</param>
    /// <param name="local">Whether the caller is this machine, for the reference rules (<see cref="RouterConfiguration.ReferenceErrors"/>).</param>
    /// <param name="days">The window, 1 to <see cref="MaxDays"/>.</param>
    /// <param name="evaluation">Whether to replay the evaluation set too.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    public static async Task<ReplayOutcome> RunAsync(
        IServiceProvider services, RouterRuntimeProvider provider, RoutingActivityLog activity, IEvaluationSetStore store, RouterConfiguration candidate,
        bool local, int days, bool evaluation, CancellationToken cancellationToken)
    {
        RouterRuntime runtime = provider.Current;
        string[] errors = [.. provider.ValidateChange(candidate), .. candidate.ReferenceErrors(runtime.Configuration, local)];
        if (errors.Length > 0)
        {
            return new ReplayOutcome { Errors = errors };
        }

        // As the runtime would build it: the machine policy applied, secret references resolved for the embeddings.
        provider.Policy.Enforce(candidate);
        var warnings = new List<string>();
        RouterConfiguration resolved = candidate.ResolveSecrets(provider.Secrets, warnings);
        var notes = new List<string>(warnings);

        // The running provider when the embedding settings are the same (or the application registered its own, which
        // every configuration uses); a provider of its own otherwise.
        bool custom = HasCustom<IEmbeddingProvider>(services);
        bool sameEmbeddings = custom || JsonSerializer.Serialize(candidate.Embeddings) == JsonSerializer.Serialize(runtime.Configuration.Embeddings);
        IEmbeddingProvider candidateEmbeddings = sameEmbeddings ? runtime.EmbeddingProvider : provider.CreateEmbeddingProvider(resolved.Embeddings);
        if (HasCustom<IPromptClassifier>(services) || HasCustom<IPromptRouter>(services)
            || HasCustom<IModelSelector>(services) || HasCustom<IRouteSelector>(services)
            || HasCustom<IComplexityAnalyzer>(services))
        {
            notes.Add("The application registers its own classifier, router, selector or complexity analyzer; the replay uses the built-in ones on both sides.");
        }

        DateTimeOffset until = DateTimeOffset.UtcNow;
        DateTimeOffset since = until - TimeSpan.FromDays(days);
        IReadOnlyList<RoutingActivityEntry> entries = activity.Between(since, until);
        IReadOnlyList<EvaluationCase> cases = evaluation ? store.Load() : [];
        var redactor = new SecretRedactor(resolved.SecretValues().Concat(runtime.Resolved.SecretValues()));
        try
        {
            ReplayReport report = await WhatIfReplay.RunAsync(
                new ReplaySide(runtime.Configuration, runtime.EmbeddingProvider),
                new ReplaySide(candidate, candidateEmbeddings),
                entries,
                cases,
                cancellationToken).ConfigureAwait(false);
            return new ReplayOutcome { Report = report with { Since = since, Until = until, Notes = notes } };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            return new ReplayOutcome { Problem = $"The replay could not embed the prompts: {redactor.Redact(ex.Message)}" };
        }
        finally
        {
            if (!sameEmbeddings)
            {
                (candidateEmbeddings as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>Whether the application registered its own <typeparamref name="T"/>, which the runtime uses instead of building one.</summary>
    private static bool HasCustom<T>(IServiceProvider services)
        where T : class =>
        services.GetService<T>() is { } service && service is not IRuntimeFacade;
}

/// <summary>What a replay came to: the report, or why there is none.</summary>
internal sealed record ReplayOutcome
{
    /// <summary>The report, when the replay ran.</summary>
    public ReplayReport? Report { get; init; }

    /// <summary>Why the candidate would not be applied, as applying it would say; empty when it would.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Why the replay failed (the prompts could not be embedded), redacted.</summary>
    public string? Problem { get; init; }
}
