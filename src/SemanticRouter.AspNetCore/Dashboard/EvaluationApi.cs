using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The routing-quality endpoints of the admin API: evaluation sets, evaluation, threshold calibration, embedding
/// comparison, route health and "add as utterance". No model is called; only the embedding provider is.
/// </summary>
internal static class EvaluationApi
{
    private const int MaxCases = 5000;

    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json, Func<HttpContext, string> actor)
    {
        api.MapGet("/evaluation/set", (IEvaluationSetStore store) =>
        {
            IReadOnlyList<EvaluationCase> cases = store.Load();
            return Results.Json(new EvaluationSetResponse(store.Location, cases.Count, cases.Select(CaseDto.From).ToArray()), json);
        });

        api.MapPut("/evaluation/set", async (HttpContext context, IEvaluationSetStore store) =>
        {
            (IReadOnlyList<EvaluationCase>? cases, string? error) = await ReadCasesAsync(context).ConfigureAwait(false);
            if (cases is null)
            {
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }

            store.Save(cases);
            return Results.Json(new EvaluationSetResponse(store.Location, cases.Count, cases.Select(CaseDto.From).ToArray()), json);
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapPost("/evaluation/cases", (CaseDto label, IEvaluationSetStore store) =>
        {
            if (string.IsNullOrWhiteSpace(label.Prompt) || label.Tier < 0)
            {
                return Results.Problem("'prompt' and a non-negative 'tier' are required.", statusCode: StatusCodes.Status400BadRequest);
            }

            // A prompt labelled again replaces its earlier label.
            EvaluationCase added = label.ToCase();
            var cases = store.Load().Where(c => !string.Equals(c.Prompt, added.Prompt, StringComparison.Ordinal)).Append(added).ToArray();
            store.Save(cases);
            return Results.Json(new EvaluationSetResponse(store.Location, cases.Length, cases.Select(CaseDto.From).ToArray()), json);
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapGet("/evaluation/set/export", (IEvaluationSetStore store, bool? localOnly) =>
        {
            // Prompts labelled local-only stay on this machine unless the export asks for them.
            IEnumerable<EvaluationCase> cases = store.Load().Where(c => localOnly == true || !c.LocalOnly);
            return Results.File(Encoding.UTF8.GetBytes(EvaluationSet.ToJsonLines(cases)), "application/jsonl", "semantic-router.evaluation.jsonl");
        });

        api.MapPost("/evaluate", async (HttpContext context, RouterRuntimeProvider provider, IEvaluationSetStore store, bool? calibrate) =>
        {
            (IReadOnlyList<EvaluationCase>? cases, string? error) = await ReadCasesAsync(context, store).ConfigureAwait(false);
            if (cases is null)
            {
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Current;
            cases = LocalOnlyKept(cases, runtime.Configuration.Embeddings);
            var evaluator = new RoutingEvaluator(runtime.EmbeddingProvider, runtime.Configuration);
            PreparedEvaluation prepared;
            try
            {
                prepared = await evaluator.PrepareAsync(cases, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                return Results.Problem($"The embedding provider failed: {runtime.Redactor.Redact(ex.Message)}", statusCode: StatusCodes.Status502BadGateway);
            }

            EvaluationReport report = evaluator.Evaluate(prepared);
            CalibrationReport? calibration = calibrate == true ? evaluator.Calibrate(prepared) : null;
            return Results.Json(EvaluationResponse.From(report, calibration, prepared.EmbeddingTime, runtime.Configuration.Embeddings), json);
        });

        api.MapPost("/evaluate/compare", async (HttpContext context, RouterRuntimeProvider provider, IEvaluationSetStore store) =>
        {
            CompareRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<CompareRequest>(context.Request.Body, json, context.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.Problem($"Expected {{\"embeddings\": {{…}}, \"text\": optional set}}: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }

            if (request?.Embeddings is null)
            {
                return Results.Problem("'embeddings' (the provider settings to compare with) is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            IReadOnlyList<EvaluationCase> cases;
            try
            {
                cases = string.IsNullOrWhiteSpace(request.Text) ? store.Load() : EvaluationSet.Parse(request.Text);
            }
            catch (FormatException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            if (cases.Count == 0)
            {
                return Results.Problem("The evaluation set is empty.", statusCode: StatusCodes.Status400BadRequest);
            }

            // Build the other provider the way the runtime would: secret references resolved, "***" meaning the current key.
            RouterRuntime runtime = provider.Current;
            RouterConfiguration candidate = runtime.Configuration.Clone();
            candidate.Embeddings = request.Embeddings;
            candidate.UnmaskFrom(runtime.Configuration);
            if (RouterRuntimeProvider.Validate(candidate) is { Count: > 0 } errors && errors.FirstOrDefault(e => e.StartsWith("Embeddings", StringComparison.Ordinal)) is { } problem)
            {
                return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            }

            var warnings = new List<string>();
            RouterConfiguration resolved = candidate.ResolveSecrets(provider.Secrets, warnings);
            IEmbeddingProvider other = provider.CreateEmbeddingProvider(resolved.Embeddings);
            var redactor = new SecretRedactor(resolved.SecretValues().Concat(runtime.Resolved.SecretValues()));

            async Task<CompareSide> RunAsync(string label, IEmbeddingProvider embeddings, EmbeddingProviderOptions settings)
            {
                var evaluator = new RoutingEvaluator(embeddings, runtime.Configuration);
                IReadOnlyList<EvaluationCase> sent = LocalOnlyKept(cases, settings);
                try
                {
                    PreparedEvaluation prepared = await evaluator.PrepareAsync(sent, context.RequestAborted).ConfigureAwait(false);
                    EvaluationReport report = evaluator.Evaluate(prepared);
                    CalibrationReport calibration = evaluator.Calibrate(prepared);
                    return new CompareSide(label, Describe(settings), null, EvaluationResponse.From(report, calibration, prepared.EmbeddingTime, settings));
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
                {
                    return new CompareSide(label, Describe(settings), redactor.Redact(ex.Message), null);
                }
            }

            CompareSide current = await RunAsync("current", runtime.EmbeddingProvider, runtime.Configuration.Embeddings).ConfigureAwait(false);
            CompareSide compared = await RunAsync("candidate", other, candidate.Masked().Embeddings).ConfigureAwait(false);
            return Results.Json(new CompareResponse(current, compared, warnings), json);
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapGet("/routes/health", async (HttpContext context, RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.Current;
            try
            {
                RouteHealthReport report = await new RouteHealthAnalyzer(runtime.EmbeddingProvider, runtime.Configuration)
                    .AnalyzeAsync(context.RequestAborted).ConfigureAwait(false);
                return Results.Json(report, json);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                return Results.Problem($"The embedding provider failed: {runtime.Redactor.Redact(ex.Message)}", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/routes/{name}/utterances", async (HttpContext context, string name, AddUtteranceRequest request, RouterRuntimeProvider provider) =>
        {
            string utterance = request.Utterance?.Trim() ?? string.Empty;
            if (utterance.Length == 0)
            {
                return Results.Problem("'utterance' is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Current;
            RouterConfiguration next = runtime.Configuration.Clone();
            RouteDefinition? route = next.Routes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (route is null)
            {
                if (request.Tier is not { } tier)
                {
                    return Results.Problem($"Route '{name}' does not exist; give a 'tier' to create it.", statusCode: StatusCodes.Status404NotFound);
                }

                route = new RouteDefinition { Name = name.Trim(), Tier = tier, Description = request.Description, LocalOnly = request.LocalOnly ?? false };
                next.Routes.Add(route);
            }

            if (!route.Utterances.Contains(utterance, StringComparer.OrdinalIgnoreCase))
            {
                route.Utterances.Add(utterance);
            }

            IReadOnlyList<string> errors = RouterRuntimeProvider.Validate(next);
            if (errors.Count > 0)
            {
                return Results.Problem(string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime applied = provider.Apply(next, persist: true, "dashboard", actor(context), $"added an utterance to '{route.Name}'");
            PromptClassificationResult classification = await applied.Classifier.ClassifyAsync(utterance, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new AddUtteranceResponse(applied.Version, route.Name, route.Utterances.Count, classification.RequiredTier, classification.RouteName, classification.Reason, classification.Similarity), json);
        }).WithMetadata(DashboardWriteMetadata.Instance);
    }

    /// <summary>The set to use: the request body (cases, <c>{"text": …}</c> or raw JSON Lines), else the stored set.</summary>
    private static async Task<(IReadOnlyList<EvaluationCase>? Cases, string? Error)> ReadCasesAsync(HttpContext context, IEvaluationSetStore? fallback = null)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = (await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false)).Trim();
        IReadOnlyList<EvaluationCase> cases;
        try
        {
            if (body.Length == 0 || body == "{}")
            {
                if (fallback is null)
                {
                    return (null, "Expected an evaluation set: a JSON array of cases, {\"cases\": [...]}, {\"text\": \"…\"} or JSON Lines.");
                }

                cases = fallback.Load();
            }
            else if (body[0] == '{' && TryText(body, out string? text))
            {
                cases = EvaluationSet.Parse(text);
            }
            else
            {
                cases = EvaluationSet.Parse(body);
            }
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }

        if (cases.Count > MaxCases)
        {
            return (null, $"An evaluation set can have at most {MaxCases} prompts.");
        }

        return fallback is not null && cases.Count == 0
            ? (null, "The evaluation set is empty: add prompts on the Evaluate page, label activity entries, or send a set.")
            : (cases, null);
    }

    private static bool TryText(string body, out string text)
    {
        text = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("text", out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                text = value.GetString()!;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>The cases that may be embedded with <paramref name="settings"/>: local-only ones only when it keeps prompts local.</summary>
    private static IReadOnlyList<EvaluationCase> LocalOnlyKept(IReadOnlyList<EvaluationCase> cases, EmbeddingProviderOptions settings) =>
        settings.KeepsPromptsLocal ? cases : [.. cases.Where(c => !c.LocalOnly)];

    private static string Describe(EmbeddingProviderOptions settings) => settings.Provider switch
    {
        EmbeddingProviderKind.Hashing => $"Hashing ({settings.Dimensions} dimensions)",
        EmbeddingProviderKind.Onnx => $"ONNX {settings.ModelPath ?? settings.Model ?? "all-MiniLM-L6-v2"} (in-process)",
        _ => $"{settings.Model} at {settings.Endpoint}",
    };

    public sealed record CaseDto(string Prompt, int Tier, string? Route = null, bool LocalOnly = false, string? Note = null)
    {
        public static CaseDto From(EvaluationCase item) => new(item.Prompt, item.ExpectedTier, item.ExpectedRoute, item.LocalOnly, item.Note);

        public EvaluationCase ToCase() => new(Prompt.Trim(), Tier)
        {
            ExpectedRoute = string.IsNullOrWhiteSpace(Route) ? null : Route.Trim(),
            LocalOnly = LocalOnly,
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note,
        };
    }

    public sealed record EvaluationSetResponse(string? Location, int Count, IReadOnlyList<CaseDto> Cases);

    public sealed record ScoreDto(string Route, int Tier, float Similarity);

    public sealed record ResultDto(
        string Prompt, int ExpectedTier, int ActualTier, string? ExpectedRoute, string? ActualRoute, RoutingReason Reason,
        float? Similarity, double Complexity, bool LocalOnly, bool KeptLocal, bool Correct, bool? RouteCorrect, IReadOnlyList<ScoreDto> TopScores);

    public sealed record CalibrationDto(
        CalibrationPoint Current, CalibrationPoint Recommended, IReadOnlyList<CalibrationPoint> Sweep,
        IReadOnlyList<RouteThresholdRecommendation> Routes, int CorrectWithRoutes);

    public sealed record EvaluationResponse(
        int Total, int Correct, double Accuracy, int TooLow, int TooHigh, int LocalOnlyMissed, int RouteLabelled, int RouteCorrect,
        float DefaultThreshold, IReadOnlyList<int> Tiers, IReadOnlyList<IReadOnlyList<int>> Confusion, IReadOnlyList<TierAccuracy> PerTier,
        IReadOnlyList<ResultDto> Results, IReadOnlyList<ResultDto> Misroutes, CalibrationDto? Calibration, double EmbeddingMilliseconds, string Embeddings)
    {
        public static EvaluationResponse From(EvaluationReport report, CalibrationReport? calibration, TimeSpan embedding, EmbeddingProviderOptions settings)
        {
            ResultDto[] results = report.Results.Select(r => new ResultDto(
                r.Case.Prompt, r.Case.ExpectedTier, r.ActualTier, r.Case.ExpectedRoute, r.ActualRoute, r.Reason, r.Similarity,
                Math.Round(r.Complexity, 3), r.Case.LocalOnly, r.LocalOnly, r.Correct, r.RouteCorrect,
                r.TopScores.Select(s => new ScoreDto(s.Route.Name, s.Route.Tier, s.Similarity)).ToArray())).ToArray();
            return new EvaluationResponse(
                report.Total, report.Correct, report.Accuracy, report.TooLow, report.TooHigh, report.LocalOnlyMissed, report.RouteLabelled,
                report.RouteCorrect, report.DefaultThreshold, report.Tiers, report.Confusion, report.PerTier, results,
                results.Where(r => !r.Correct).ToArray(),
                calibration is null ? null : new CalibrationDto(calibration.Current, calibration.Recommended, calibration.Sweep, calibration.Routes, calibration.CorrectWithRoutes),
                Math.Round(embedding.TotalMilliseconds, 1), Describe(settings));
        }
    }

    public sealed record CompareRequest(EmbeddingProviderOptions? Embeddings, string? Text);

    public sealed record CompareSide(string Label, string Embeddings, string? Error, EvaluationResponse? Report);

    public sealed record CompareResponse(CompareSide Current, CompareSide Candidate, IReadOnlyList<string> Warnings);

    public sealed record AddUtteranceRequest(string? Utterance, int? Tier = null, string? Description = null, bool? LocalOnly = null);

    public sealed record AddUtteranceResponse(int Version, string Route, int Utterances, int Tier, string? MatchedRoute, RoutingReason Reason, float? Similarity);
}
