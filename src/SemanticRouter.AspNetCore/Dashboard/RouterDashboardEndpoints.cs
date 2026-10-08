using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter;
using SemanticRouter.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Options for <see cref="RouterDashboardEndpoints.MapRouterDashboard"/>. Bound from <c>SemanticRouter:Dashboard</c>
/// when routing is registered from configuration; the <c>configure</c> callback of <c>MapRouterDashboard</c> runs after.
/// See <c>docs/dashboard.md</c> for the supported setups.
/// </summary>
public sealed class RouterDashboardOptions
{
    /// <summary>Shortest token accepted, so a token cannot be guessed.</summary>
    public const int MinimumTokenLength = 16;

    /// <summary>
    /// Only answer dashboard and admin requests from the loopback interface. Default <see langword="true"/>. To reach
    /// the dashboard from other machines, turn this off and set <see cref="AdminToken"/> (or, behind a reverse proxy
    /// that authenticates callers itself, <see cref="AllowUnauthenticatedRemoteClients"/>).
    /// </summary>
    public bool RequireLocalClient { get; set; } = true;

    /// <summary>
    /// Token that remote clients present (as <c>Authorization: Bearer {token}</c> or by signing in on the dashboard) for
    /// full access. At least 16 characters; <c>${env:…}</c> and <c>${secret:…}</c> references are resolved.
    /// </summary>
    public string? AdminToken { get; set; }

    /// <summary>Token for view-only access: status, activity, the masked configuration and the prompt tester.</summary>
    public string? ReadOnlyToken { get; set; }

    /// <summary>
    /// Serve remote clients without a token. Only for a reverse proxy that authenticates every request itself; ignored
    /// when a token is configured. Default <see langword="false"/>.
    /// </summary>
    public bool AllowUnauthenticatedRemoteClients { get; set; }

    /// <summary>
    /// Require the token from loopback clients too. Turn this on when a reverse proxy on this machine forwards remote
    /// traffic, which otherwise arrives from loopback and would be trusted. Default <see langword="false"/>.
    /// </summary>
    public bool RequireTokenForLocalClients { get; set; }

    /// <summary>
    /// Addresses the dashboard is reached at besides <c>localhost</c>, e.g. <c>https://router.home.lan</c>. Changes
    /// are accepted from these origins, and loopback requests may name them in the Host header.
    /// </summary>
    public IList<string> PublicOrigins { get; } = new List<string>();

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
        RouterDashboardOptions options = CopyOf(endpoints.ServiceProvider.GetService<IOptions<RouterDashboardOptions>>()?.Value);
        configure?.Invoke(options);

        prefix = "/" + prefix.Trim('/');
        DashboardAccess access = CreateAccess(endpoints.ServiceProvider, options, prefix);
        RouteGroupBuilder group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(access.FilterAsync);

        group.MapGet("/", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/index.html", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/app.css", (HttpContext context) => Asset(context, "app.css", "text/css; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/app.js", (HttpContext context) => Asset(context, "app.js", "text/javascript; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);

        RouteGroupBuilder api = group.MapGroup("/api");
        api.MapGet("/session", (HttpContext context) =>
                Results.Json(new SessionResponse(DashboardAccess.RoleOf(context)?.ToString(), access.TokenRequired(context), access.TokensConfigured), ApiJson))
            .WithMetadata(DashboardAnonymousMetadata.Instance);

        api.MapPost("/session", (HttpContext context, SignInRequest request) =>
            {
                if (access.RoleForToken(request.Token) is not { } role)
                {
                    return Results.Problem("That token is not valid for this router.", statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed");
                }

                access.SignIn(context, role, prefix);
                return Results.Json(new SessionResponse(role.ToString(), access.TokenRequired(context), access.TokensConfigured), ApiJson);
            })
            .WithMetadata(DashboardAnonymousMetadata.Instance);

        api.MapDelete("/session", (HttpContext context) =>
            {
                DashboardAccess.SignOut(context, prefix);
                return Results.NoContent();
            })
            .WithMetadata(DashboardAnonymousMetadata.Instance);

        api.MapGet("/status", (RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity, BudgetGate budgets) =>
            Results.Json(BuildStatus(provider, health, activity, budgets), ApiJson));

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

        api.MapPut("/config", async (HttpContext context, RouterRuntimeProvider provider, bool? persist, string? note) =>
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
            RouterRuntime runtime = provider.Apply(candidate, shouldPersist, "admin API", Actor(context), note);
            var warnings = new List<string>(runtime.ApplyNotes);
            if (shouldPersist && provider.Store is null)
            {
                warnings.Insert(0, "Applied in memory only: no configuration store is registered (see SemanticRouterBuilder.UseConfigurationFile).");
            }

            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), warnings.Count > 0 ? string.Join(" ", warnings) : null);
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapPost("/config/reset", (HttpContext context, RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.ResetToBaseline(Actor(context));
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), "Reverted to the application's configuration (the stored document was left in place).");
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapGet("/config/history", (RouterRuntimeProvider provider, int? limit) =>
            provider.History is { } history
                ? Results.Json(new HistoryResponse(history.List(Math.Clamp(limit ?? 50, 1, 500))), ApiJson)
                : Results.Json(new HistoryResponse([]), ApiJson));

        api.MapGet("/config/history/{id:long}", (RouterRuntimeProvider provider, long id) =>
        {
            RouterConfiguration? document = provider.History?.GetVersion(id);
            if (document is null)
            {
                return Results.Problem($"Version {id} is not in the history.", statusCode: StatusCodes.Status404NotFound);
            }

            var envelope = new JsonObject { ["id"] = id, ["config"] = JsonNode.Parse(document.Masked().ToJson()) };
            return Results.Content(envelope.ToJsonString(), "application/json");
        });

        api.MapPost("/config/history/{id:long}/rollback", (HttpContext context, RouterRuntimeProvider provider, long id) =>
        {
            RouterConfiguration? document = provider.History?.GetVersion(id);
            if (document is null)
            {
                return Results.Problem($"Version {id} is not in the history.", statusCode: StatusCodes.Status404NotFound);
            }

            IReadOnlyList<string> errors = RouterRuntimeProvider.Validate(document);
            if (errors.Count > 0)
            {
                return Results.Json(new ValidationResult(errors), ApiJson, statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Apply(document, persist: true, "rollback", Actor(context), $"to version {id}");
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), $"Rolled back to version {id}.");
        }).WithMetadata(DashboardWriteMetadata.Instance);

        api.MapGet("/activity", (RoutingActivityLog activity, int? limit, long? after) =>
            Results.Json(new ActivityResponse(activity.Recent(limit ?? options.DefaultActivityLimit, after), activity.NextId - 1), ApiJson));

        api.MapGet("/activity/series", (RoutingActivityLog activity, string? window) =>
        {
            (TimeSpan span, TimeSpan bucket) = ActivityReports.Window(window);
            DateTimeOffset until = Align(DateTimeOffset.UtcNow, bucket) + bucket;
            DateTimeOffset since = until - span;
            return Results.Json(ActivityReports.Series(activity.Between(since, until), since, until, bucket), ApiJson);
        });

        api.MapGet("/activity/export", (RoutingActivityLog activity, string? format, bool? prompts, DateTimeOffset? since, DateTimeOffset? until) =>
        {
            DateTimeOffset end = until ?? DateTimeOffset.UtcNow.AddMinutes(1);
            IReadOnlyList<RoutingActivityEntry> entries = activity.Between(since ?? end.AddDays(-7), end);
            bool includePrompts = prompts ?? false;
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(ActivityReports.ToJsonLines(entries, includePrompts)), "application/jsonl", $"semantic-router-activity-{stamp}.jsonl")
                : Results.File(System.Text.Encoding.UTF8.GetBytes(ActivityReports.ToCsv(entries, includePrompts)), "text/csv", $"semantic-router-activity-{stamp}.csv");
        });

        api.MapGet("/usage", (RouterRuntimeProvider provider, IUsageLedger ledger, BudgetGate budgets, string? window, string? by) =>
        {
            UsageOptions usage = provider.Current.Configuration.Usage;
            TimeZoneInfo zone = BudgetGate.ZoneOf(usage);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            (DateTimeOffset since, DateTimeOffset until) = window switch
            {
                "day" => BudgetGate.PeriodOf(BudgetPeriod.Day, now, zone),
                "week" => BudgetGate.PeriodOf(BudgetPeriod.Week, now, zone),
                "month" => BudgetGate.PeriodOf(BudgetPeriod.Month, now, zone),
                "30d" => (now.AddDays(-30), now.AddMinutes(1)),
                _ => (now.AddDays(-7), now.AddMinutes(1)),
            };
            UsageGrouping grouping = Enum.TryParse(by, ignoreCase: true, out UsageGrouping parsed) ? parsed : UsageGrouping.Model;
            return Results.Json(new UsageResponse(
                since, until, grouping, usage.ReferenceModel, ledger.Total(since, until), ledger.Summarize(since, until, grouping, zone), budgets.Status(usage)), ApiJson);
        });

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
        }).WithMetadata(DashboardWriteMetadata.Instance);

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
            if (!provider.Current.Resolved.Connectors.TryGetValue(name, out ConnectorOptions? connector))
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

            ConnectorProbeResult probe = await ConnectorProbe.ListModelsAsync(
                name, connector, http.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + name), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return Results.Json(new DiscoverResponse(probe.Models, probe.Ok ? null : provider.Current.Redactor.Redact(probe.Message)), ApiJson);
        });

        return group;
    }

    private static DateTimeOffset Align(DateTimeOffset time, TimeSpan bucket) =>
        new(time.UtcTicks - (time.UtcTicks % bucket.Ticks), TimeSpan.Zero);

    /// <summary>Who is making a change, for the configuration history. Never contains a token.</summary>
    private static string Actor(HttpContext context)
    {
        string role = DashboardAccess.RoleOf(context) == RouterDashboardRole.ReadOnly ? "read-only" : "admin";
        return DashboardAccess.IsLocal(context)
            ? "this machine"
            : $"{role} from {context.Connection.RemoteIpAddress}";
    }

    private static RouterDashboardOptions CopyOf(RouterDashboardOptions? source)
    {
        var copy = new RouterDashboardOptions();
        if (source is not null)
        {
            copy.RequireLocalClient = source.RequireLocalClient;
            copy.AdminToken = source.AdminToken;
            copy.ReadOnlyToken = source.ReadOnlyToken;
            copy.AllowUnauthenticatedRemoteClients = source.AllowUnauthenticatedRemoteClients;
            copy.RequireTokenForLocalClients = source.RequireTokenForLocalClients;
            copy.DefaultActivityLimit = source.DefaultActivityLimit;
            foreach (string origin in source.PublicOrigins)
            {
                copy.PublicOrigins.Add(origin);
            }
        }

        return copy;
    }

    private static DashboardAccess CreateAccess(IServiceProvider services, RouterDashboardOptions options, string prefix)
    {
        IRouterSecretStore? secrets = services.GetService<IRouterSecretStore>();
        var problems = new List<string>();
        string? admin = SecretReference.Resolve(options.AdminToken, secrets, "Dashboard:AdminToken", problems);
        string? readOnly = SecretReference.Resolve(options.ReadOnlyToken, secrets, "Dashboard:ReadOnlyToken", problems);
        foreach ((string setting, string? token) in new[] { ("AdminToken", admin), ("ReadOnlyToken", readOnly) })
        {
            if (!string.IsNullOrEmpty(token) && token.Length < RouterDashboardOptions.MinimumTokenLength)
            {
                throw new InvalidOperationException(
                    $"SemanticRouter:Dashboard:{setting} must be at least {RouterDashboardOptions.MinimumTokenLength} characters (try `openssl rand -hex 24`).");
            }
        }

        if (!string.IsNullOrEmpty(admin) && admin == readOnly)
        {
            throw new InvalidOperationException("SemanticRouter:Dashboard:AdminToken and ReadOnlyToken must differ.");
        }

        var access = new DashboardAccess(options, admin, readOnly);
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SemanticRouter.Dashboard");
        foreach (string problem in problems)
        {
            DashboardLog.ConfigurationProblem(logger, problem);
        }

        if (!options.RequireLocalClient && !access.TokensConfigured)
        {
            if (options.AllowUnauthenticatedRemoteClients)
            {
                DashboardLog.OpenToRemoteClients(logger, prefix);
            }
            else
            {
                DashboardLog.RemoteRefused(logger, prefix);
            }
        }

        return access;
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
            ["secretStore"] = provider.Secrets?.Location,
            ["warning"] = warning,
            ["config"] = JsonNode.Parse(masked.ToJson()),
        };
        return Results.Content(envelope.ToJsonString(), "application/json");
    }

    private static StatusResponse BuildStatus(RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity, BudgetGate budgets)
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
            provider.Secrets?.Location,
            runtime.Warnings,
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
                !string.IsNullOrEmpty(runtime.Resolved.Connectors.TryGetValue(kv.Key, out ConnectorOptions? resolved) ? resolved.ApiKey : null),
                config.Models.Count(m => string.Equals(m.Connector, kv.Key, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            config.Routes.Select(r => new RouteStatus(r.Name, r.Tier, r.Utterances.Count, r.LocalOnly, r.RequiredCapabilities.ToArray(), r.SimilarityThreshold ?? config.DefaultSimilarityThreshold)).ToArray(),
            new GatewayStatus(
                config.Middleware.Mode.ToString(),
                config.Middleware.PathPrefixes.ToArray(),
                config.Middleware.EffectiveAnthropicPathPrefixes.ToArray(),
                config.Middleware.EffectiveResponsesPathPrefixes.ToArray(),
                config.Middleware.ModelAliases.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                config.Selection.Strategy.ToString()),
            new ActivitySummary(
                recent.Count,
                recent.Count(e => e.Outcome == "served"),
                recent.Count(e => e.Outcome == "failed"),
                recent.Count(e => e.Degraded),
                recent.Where(e => e.ServedBy is not null).GroupBy(e => e.ServedBy!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                recent.GroupBy(e => e.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
                recent.GroupBy(e => e.Reason.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
            budgets.Status(config.Usage));
    }

    private static string[] CliModelHints(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => ["sonnet", "opus", "haiku"],
        CliPreset.Codex => ["gpt-6.1-sol", "gpt-6-luna"],
        CliPreset.GitHubCopilot => ["claude-sonnet-5.5", "gpt-6.1-sol"],
        _ => [],
    };

    /// <summary>Who the caller is signed in as.</summary>
    /// <param name="Role"><c>Admin</c>, <c>ReadOnly</c>, or <see langword="null"/> when not signed in.</param>
    /// <param name="TokenRequired">Whether this caller needs a token (it is remote, or tokens are required locally).</param>
    /// <param name="TokensConfigured">Whether the router has a token to sign in with.</param>
    public sealed record SessionResponse(string? Role, bool TokenRequired, bool TokensConfigured);

    /// <summary>Sign-in request.</summary>
    public sealed record SignInRequest(string? Token);

    /// <summary>Validation outcome.</summary>
    /// <param name="Errors">Problems; empty when valid.</param>
    public sealed record ValidationResult(IReadOnlyList<string> Errors)
    {
        /// <summary>Whether the document can be applied.</summary>
        public bool Valid => Errors.Count == 0;
    }

    /// <summary>Usage over a window.</summary>
    public sealed record UsageResponse(
        DateTimeOffset Since, DateTimeOffset Until, UsageGrouping By, string? ReferenceModel, UsageTotals Totals,
        IReadOnlyList<UsageGroup> Groups, IReadOnlyList<BudgetStatus> Budgets);

    /// <summary>Applied configuration versions, newest first.</summary>
    public sealed record HistoryResponse(IReadOnlyList<RouterConfigurationVersion> Versions);

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
        string? SecretStore,
        IReadOnlyList<string> Warnings,
        string LibraryVersion,
        EmbeddingsStatus Embeddings,
        IReadOnlyList<TierStatus> Tiers,
        IReadOnlyList<ModelStatus> Models,
        IReadOnlyList<ConnectorStatus> Connectors,
        IReadOnlyList<RouteStatus> Routes,
        GatewayStatus Gateway,
        ActivitySummary Activity,
        IReadOnlyList<BudgetStatus> Budgets);

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
    public sealed record GatewayStatus(string Mode, IReadOnlyList<string> PathPrefixes, IReadOnlyList<string> AnthropicPathPrefixes, IReadOnlyList<string> ResponsesPathPrefixes, IReadOnlyDictionary<string, string> ModelAliases, string Strategy);

    /// <summary>Counts over the activity log.</summary>
    public sealed record ActivitySummary(
        int Total, int Served, int Failed, int Degraded,
        IReadOnlyDictionary<string, int> ByModel, IReadOnlyDictionary<string, int> ByTier, IReadOnlyDictionary<string, int> ByReason);
}

internal static partial class DashboardLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Router dashboard: {Problem}")]
    public static partial void ConfigurationProblem(ILogger logger, string problem);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The router dashboard at {Prefix} is open to remote clients without authentication (AllowUnauthenticatedRemoteClients). Make sure a reverse proxy authenticates every request.")]
    public static partial void OpenToRemoteClients(ILogger logger, string prefix);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RequireLocalClient is off but no dashboard token is configured, so remote requests to {Prefix} will be refused. Set SemanticRouter:Dashboard:AdminToken (for example \"${{env:ROUTER_ADMIN_TOKEN}}\"), or AllowUnauthenticatedRemoteClients behind an authenticating reverse proxy.")]
    public static partial void RemoteRefused(ILogger logger, string prefix);
}
