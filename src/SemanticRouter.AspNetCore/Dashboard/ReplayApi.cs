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

            RouterRuntime runtime = provider.Current;
            string[] errors = [.. provider.ValidateChange(candidate), .. candidate.ReferenceErrors(runtime.Configuration, DashboardAccess.IsLocal(context))];
            if (errors.Length > 0)
            {
                return Results.Json(RouterDashboardEndpoints.Validation(errors, candidate), json, statusCode: StatusCodes.Status400BadRequest);
            }

            // As the runtime would build it: the machine policy applied, secret references resolved for the embeddings.
            provider.Policy.Enforce(candidate);
            var warnings = new List<string>();
            RouterConfiguration resolved = candidate.ResolveSecrets(provider.Secrets, warnings);
            var notes = new List<string>(warnings);

            // The running provider when the embedding settings are the same (or the application registered its own, which
            // every configuration uses); a provider of its own otherwise.
            bool custom = HasCustom<IEmbeddingProvider>(context.RequestServices);
            bool sameEmbeddings = custom || JsonSerializer.Serialize(candidate.Embeddings) == JsonSerializer.Serialize(runtime.Configuration.Embeddings);
            IEmbeddingProvider candidateEmbeddings = sameEmbeddings ? runtime.EmbeddingProvider : provider.CreateEmbeddingProvider(resolved.Embeddings);
            if (HasCustom<IPromptClassifier>(context.RequestServices) || HasCustom<IPromptRouter>(context.RequestServices)
                || HasCustom<IModelSelector>(context.RequestServices) || HasCustom<IRouteSelector>(context.RequestServices)
                || HasCustom<IComplexityAnalyzer>(context.RequestServices))
            {
                notes.Add("The application registers its own classifier, router, selector or complexity analyzer; the replay uses the built-in ones on both sides.");
            }

            DateTimeOffset until = DateTimeOffset.UtcNow;
            DateTimeOffset since = until - TimeSpan.FromDays(window);
            IReadOnlyList<RoutingActivityEntry> entries = activity.Between(since, until);
            IReadOnlyList<EvaluationCase> cases = evaluation == false ? [] : store.Load();
            var redactor = new SecretRedactor(resolved.SecretValues().Concat(runtime.Resolved.SecretValues()));
            try
            {
                ReplayReport report = await WhatIfReplay.RunAsync(
                    new ReplaySide(runtime.Configuration, runtime.EmbeddingProvider),
                    new ReplaySide(candidate, candidateEmbeddings),
                    entries,
                    cases,
                    context.RequestAborted).ConfigureAwait(false);
                return Results.Json(report with { Since = since, Until = until, Notes = notes }, json);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                return Results.Problem($"The replay could not embed the prompts: {redactor.Redact(ex.Message)}", statusCode: StatusCodes.Status502BadGateway);
            }
            finally
            {
                if (!sameEmbeddings)
                {
                    (candidateEmbeddings as IDisposable)?.Dispose();
                }
            }
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("replayed recent requests through a configuration"); // embeds prompts
    }

    /// <summary>Whether the application registered its own <typeparamref name="T"/>, which the runtime uses instead of building one.</summary>
    private static bool HasCustom<T>(IServiceProvider services)
        where T : class =>
        services.GetService<T>() is { } service && service is not IRuntimeFacade;
}
