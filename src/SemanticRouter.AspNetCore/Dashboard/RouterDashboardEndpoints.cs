using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter;
using SemanticRouter.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Options for <see cref="RouterDashboardEndpoints.MapRouterDashboard"/>.</summary>
public sealed class RouterDashboardOptions
{
    /// <summary>
    /// Only answer dashboard and admin requests from the loopback interface. Default <see langword="true"/>: the admin
    /// API can change where prompts go and can read connector settings, so keep it on unless the application
    /// authenticates these endpoints itself.
    /// </summary>
    public bool RequireLocalClient { get; set; } = true;

    /// <summary>Entries shown by the activity endpoint when the client gives no limit. Default 100.</summary>
    public int DefaultActivityLimit { get; set; } = 100;
}

/// <summary>Maps the configuration dashboard and its admin API.</summary>
public static class RouterDashboardEndpoints
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Maps the dashboard (a single-page app) at <paramref name="prefix"/> and its admin API under
    /// <c>{prefix}/api</c>: configuration (read, validate, apply, persist, reset), live status, recent routing activity,
    /// prompt classification, cooldown reset and model discovery. See <c>docs/dashboard.md</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapRouterDashboard(
        this IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string prefix = "/router",
        Action<RouterDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new RouterDashboardOptions();
        configure?.Invoke(options);

        prefix = "/" + prefix.Trim('/');
        RouteGroupBuilder group = endpoints.MapGroup(prefix);
        if (options.RequireLocalClient)
        {
            group.AddEndpointFilter(async (context, next) =>
            {
                IPAddress? remote = context.HttpContext.Connection.RemoteIpAddress;
                if (remote is not null && !IPAddress.IsLoopback(remote))
                {
                    return Results.Problem(
                        "The router dashboard only answers requests from this machine (RouterDashboardOptions.RequireLocalClient).",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return await next(context).ConfigureAwait(false);
            });
        }

        group.MapGet("/", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix));
        group.MapGet("/index.html", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix));
        group.MapGet("/app.css", (HttpContext context) => Asset(context, "app.css", "text/css; charset=utf-8", prefix));
        group.MapGet("/app.js", (HttpContext context) => Asset(context, "app.js", "text/javascript; charset=utf-8", prefix));

        RouteGroupBuilder api = group.MapGroup("/api");
        api.MapGet("/status", (RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity) =>
            Results.Json(BuildStatus(provider, health, activity), ApiJson));

        api.MapGet("/config", (RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.Current;
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), null);
        });

        api.MapGet("/config/baseline", (RouterRuntimeProvider provider) =>
            ConfigEnvelope(provider.Current, provider, provider.Baseline.Masked(), null));

        api.MapPost("/config/validate", async (HttpContext context, RouterRuntimeProvider provider) =>
        {
            RouterConfiguration? candidate = await ReadConfigurationAsync(context, provider).ConfigureAwait(false);
            if (candidate is null)
            {
                return Results.Problem("Expected a configuration document.", statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(new ValidationResult(RouterRuntimeProvider.Validate(candidate)), ApiJson);
        });

        api.MapPut("/config", async (HttpContext context, RouterRuntimeProvider provider, bool? persist) =>
        {
            RouterConfiguration? candidate = await ReadConfigurationAsync(context, provider).ConfigureAwait(false);
            if (candidate is null)
            {
                return Results.Problem("Expected a configuration document.", statusCode: StatusCodes.Status400BadRequest);
            }

            IReadOnlyList<string> errors = RouterRuntimeProvider.Validate(candidate);
            if (errors.Count > 0)
            {
                return Results.Json(new ValidationResult(errors), ApiJson, statusCode: StatusCodes.Status400BadRequest);
            }

            bool shouldPersist = persist ?? true;
            RouterRuntime runtime = provider.Apply(candidate, shouldPersist);
            string? warning = shouldPersist && provider.Store is null
                ? "Applied in memory only: no configuration store is registered (see SemanticRouterBuilder.UseConfigurationFile)."
                : null;
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), warning);
        });

        api.MapPost("/config/reset", (RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.ResetToBaseline();
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), "Reverted to the application's configuration (the stored document was left in place).");
        });

        api.MapGet("/activity", (RoutingActivityLog activity, int? limit, long? after) =>
            Results.Json(new ActivityResponse(activity.Recent(limit ?? options.DefaultActivityLimit, after), activity.NextId - 1), ApiJson));

        api.MapPost("/classify", async (ClassifyPromptRequest request, RouterRuntimeProvider provider, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.Problem("'prompt' is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Current;
            (SelectionStrategy? strategy, string? requestedModel) = ModelAliases.Resolve(PromptRoutingMiddleware.MapModel(runtime, request.Model));
            RoutingPlan plan = await runtime.Router.RouteAsync(
                new PromptRoutingRequest { Prompt = request.Prompt, Strategy = strategy, RequestedModel = requestedModel }, cancellationToken).ConfigureAwait(false);
            return Results.Ok(PromptClassificationResponse.From(plan));
        });

        api.MapPost("/health/reset", (IModelHealth health) =>
        {
            if (health is ModelHealthTracker tracker)
            {
                tracker.Clear();
                return Results.Ok(new { cleared = true });
            }

            return Results.Problem("The registered IModelHealth does not support clearing cooldowns.", statusCode: StatusCodes.Status501NotImplemented);
        });

        api.MapPost("/embeddings/test", async (EmbeddingTestRequest request, RouterRuntimeProvider provider, CancellationToken cancellationToken) =>
        {
            RouterRuntime runtime = provider.Current;
            long started = Stopwatch.GetTimestamp();
            try
            {
                ReadOnlyMemory<float> vector = await runtime.EmbeddingProvider
                    .GenerateEmbeddingAsync(string.IsNullOrWhiteSpace(request.Text) ? "hello" : request.Text, cancellationToken).ConfigureAwait(false);
                return Results.Json(new EmbeddingTestResponse(true, vector.Length, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), null), ApiJson);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                return Results.Json(new EmbeddingTestResponse(false, 0, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), ex.Message), ApiJson);
            }
        });

        api.MapPost("/connectors/{name}/discover", async (string name, RouterRuntimeProvider provider, IHttpClientFactory http, CancellationToken cancellationToken) =>
        {
            if (!provider.Current.Configuration.Connectors.TryGetValue(name, out ConnectorOptions? connector))
            {
                return Results.Problem($"Connector '{name}' is not configured.", statusCode: StatusCodes.Status404NotFound);
            }

            if (connector.Type == ConnectorType.Cli)
            {
                return Results.Json(new DiscoverResponse(CliModelHints(connector.Preset), "CLI presets accept the vendor's model aliases; these are common values."), ApiJson);
            }

            if (connector.BaseAddress is not { IsAbsoluteUri: true })
            {
                return Results.Problem("The connector has no BaseAddress.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connector.BaseAddress, "/v1/models"));
                if (!string.IsNullOrEmpty(connector.ApiKey))
                {
                    if (connector.Type == ConnectorType.Anthropic)
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", connector.ApiKey);
                        request.Headers.TryAddWithoutValidation("anthropic-version", connector.AnthropicVersion);
                    }
                    else
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.ApiKey);
                    }
                }

                foreach ((string header, string value) in connector.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header, value);
                }

                HttpClient client = http.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + name);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Results.Json(new DiscoverResponse([], $"HTTP {(int)response.StatusCode} from {request.RequestUri}"), ApiJson);
                }

                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                string[] ids = json.RootElement.TryGetArray("data", out JsonElement data)
                    ? data.EnumerateArray().Select(m => m.GetString("id")).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToArray()
                    : [];
                return Results.Json(new DiscoverResponse(ids, null), ApiJson);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return Results.Json(new DiscoverResponse([], ex.Message), ApiJson);
            }
        });

        return group;
    }

    private static IResult Asset(HttpContext context, string name, string contentType, string prefix)
    {
        Stream? stream = typeof(RouterDashboardEndpoints).Assembly.GetManifestResourceStream("dashboard/" + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "no-cache";
        if (name == "index.html")
        {
            using var reader = new StreamReader(stream);
            string html = reader.ReadToEnd().Replace("__ROUTER_PREFIX__", prefix, StringComparison.Ordinal);
            return Results.Content(html, contentType);
        }

        return Results.Stream(stream, contentType);
    }

    private static async Task<RouterConfiguration?> ReadConfigurationAsync(HttpContext context, RouterRuntimeProvider provider)
    {
        using var reader = new StreamReader(context.Request.Body);
        string raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonNode? document = node is JsonObject obj && obj.TryGetPropertyValue("config", out JsonNode? inner) ? inner : node;
            if (document is not JsonObject)
            {
                return null;
            }

            RouterConfiguration candidate = RouterConfiguration.FromJson(document.ToJsonString());
            candidate.UnmaskFrom(provider.Current.Configuration);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult ConfigEnvelope(RouterRuntime runtime, RouterRuntimeProvider provider, RouterConfiguration masked, string? warning)
    {
        var envelope = new JsonObject
        {
            ["version"] = runtime.Version,
            ["source"] = runtime.Source.ToString(),
            ["createdAt"] = runtime.CreatedAt,
            ["store"] = provider.Store?.Location,
            ["warning"] = warning,
            ["config"] = JsonNode.Parse(masked.ToJson()),
        };
        return Results.Content(envelope.ToJsonString(), "application/json");
    }

    private static StatusResponse BuildStatus(RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity)
    {
        RouterRuntime runtime = provider.Current;
        RouterConfiguration config = runtime.Configuration;
        IReadOnlyDictionary<string, DateTimeOffset> cooldowns = health.GetCooldowns();
        IReadOnlyList<RoutingActivityEntry> recent = activity.Recent(500);

        return new StatusResponse(
            runtime.Version,
            runtime.Source.ToString(),
            runtime.CreatedAt,
            runtime.WarmedUp,
            runtime.WarmUpError,
            provider.Store?.Location,
            (typeof(RouterDashboardEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev").Split('+')[0],
            new EmbeddingsStatus(config.Embeddings.Provider.ToString(), config.Embeddings.Model, config.Embeddings.Endpoint?.ToString(), config.Embeddings.Dimensions),
            config.EffectiveTiers.Select(t => new TierStatus(t.Level, t.Name, t.Description)).ToArray(),
            config.Models.Select(m => new ModelStatus(
                m.Name, m.Connector, m.Model, m.Tier, m.Capabilities.ToArray(), m.IsLocal, m.Enabled, m.Priority, m.ContextWindow,
                m.InputCostPerMillionTokens, m.OutputCostPerMillionTokens,
                cooldowns.TryGetValue(m.Name, out DateTimeOffset until) ? until : null,
                config.Connectors.TryGetValue(m.Connector, out ConnectorOptions? c) ? c.Type.ToString() : null)).ToArray(),
            config.Connectors.Select(kv => new ConnectorStatus(
                kv.Key, kv.Value.Type.ToString(), kv.Value.Type == ConnectorType.Cli ? kv.Value.Preset.ToString() : null,
                kv.Value.Type == ConnectorType.Cli ? kv.Value.Executable : kv.Value.BaseAddress?.ToString(),
                kv.Value.Type == ConnectorType.Cli ? kv.Value.ToolCalling.ToString() : "Native",
                !string.IsNullOrEmpty(kv.Value.ApiKey),
                config.Models.Count(m => string.Equals(m.Connector, kv.Key, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            config.Routes.Select(r => new RouteStatus(r.Name, r.Tier, r.Utterances.Count, r.LocalOnly, r.RequiredCapabilities.ToArray(), r.SimilarityThreshold ?? config.DefaultSimilarityThreshold)).ToArray(),
            new GatewayStatus(
                config.Middleware.Mode.ToString(),
                config.Middleware.PathPrefixes.ToArray(),
                config.Middleware.EffectiveAnthropicPathPrefixes.ToArray(),
                config.Middleware.ModelAliases.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                config.Selection.Strategy.ToString()),
            new ActivitySummary(
                recent.Count,
                recent.Count(e => e.Outcome == "served"),
                recent.Count(e => e.Outcome == "failed"),
                recent.Count(e => e.Degraded),
                recent.Where(e => e.ServedBy is not null).GroupBy(e => e.ServedBy!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                recent.GroupBy(e => e.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
                recent.GroupBy(e => e.Reason.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)));
    }

    private static string[] CliModelHints(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => ["sonnet", "opus", "haiku"],
        CliPreset.Codex => ["gpt-6.1-sol", "gpt-6-luna"],
        CliPreset.GitHubCopilot => ["claude-sonnet-5.5", "gpt-6.1-sol"],
        _ => [],
    };

    /// <summary>Validation outcome.</summary>
    /// <param name="Errors">Problems; empty when valid.</param>
    public sealed record ValidationResult(IReadOnlyList<string> Errors)
    {
        /// <summary>Whether the document can be applied.</summary>
        public bool Valid => Errors.Count == 0;
    }

    /// <summary>Recent routing activity.</summary>
    public sealed record ActivityResponse(IReadOnlyList<RoutingActivityEntry> Entries, long Latest);

    /// <summary>Request for the embeddings probe.</summary>
    public sealed record EmbeddingTestRequest(string? Text);

    /// <summary>Result of the embeddings probe.</summary>
    public sealed record EmbeddingTestResponse(bool Ok, int Dimensions, double Milliseconds, string? Error);

    /// <summary>Models a connector reports.</summary>
    public sealed record DiscoverResponse(IReadOnlyList<string> Models, string? Note);

    /// <summary>Live status of the router.</summary>
    public sealed record StatusResponse(
        int Version,
        string Source,
        DateTimeOffset CreatedAt,
        bool WarmedUp,
        string? WarmUpError,
        string? Store,
        string LibraryVersion,
        EmbeddingsStatus Embeddings,
        IReadOnlyList<TierStatus> Tiers,
        IReadOnlyList<ModelStatus> Models,
        IReadOnlyList<ConnectorStatus> Connectors,
        IReadOnlyList<RouteStatus> Routes,
        GatewayStatus Gateway,
        ActivitySummary Activity);

    /// <summary>Embedding provider in use.</summary>
    public sealed record EmbeddingsStatus(string Provider, string? Model, string? Endpoint, int Dimensions);

    /// <summary>A tier on the ladder.</summary>
    public sealed record TierStatus(int Level, string Name, string? Description);

    /// <summary>A catalog model and its health.</summary>
    public sealed record ModelStatus(
        string Name, string Connector, string? Model, int Tier, IReadOnlyList<string> Capabilities, bool IsLocal, bool Enabled, int Priority,
        int ContextWindow, decimal InputCostPerMillionTokens, decimal OutputCostPerMillionTokens, DateTimeOffset? CoolingDownUntil, string? ConnectorType);

    /// <summary>A connector.</summary>
    public sealed record ConnectorStatus(string Name, string Type, string? Preset, string? Target, string ToolCalling, bool HasApiKey, int ModelCount);

    /// <summary>A route.</summary>
    public sealed record RouteStatus(string Name, int Tier, int Utterances, bool LocalOnly, IReadOnlyList<string> RequiredCapabilities, float Threshold);

    /// <summary>Gateway settings.</summary>
    public sealed record GatewayStatus(string Mode, IReadOnlyList<string> PathPrefixes, IReadOnlyList<string> AnthropicPathPrefixes, IReadOnlyDictionary<string, string> ModelAliases, string Strategy);

    /// <summary>Counts over the activity log.</summary>
    public sealed record ActivitySummary(
        int Total, int Served, int Failed, int Degraded,
        IReadOnlyDictionary<string, int> ByModel, IReadOnlyDictionary<string, int> ByTier, IReadOnlyDictionary<string, int> ByReason);
}
