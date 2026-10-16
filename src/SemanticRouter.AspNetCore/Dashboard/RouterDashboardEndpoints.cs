using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    /// <summary>Token for view-only access: status, activity and the masked configuration, but nothing that spends embedding or API calls.</summary>
    public string? ReadOnlyToken { get; set; }

    /// <summary>
    /// Token for figures only (<see cref="RouterDashboardRole.Metrics"/>): status and health, usage and cost totals,
    /// activity counts and the digest's counts, never prompt or reply text, utterances, the configuration or memory.
    /// </summary>
    public string? MetricsToken { get; set; }

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
    /// Let the admin API change what a CLI or Copilot connector runs (its program, arguments, environment variables and
    /// home directory, beyond a preset started from PATH) and which folders and files memory sources read. Off by
    /// default, because loopback clients are admin without a token, and with this any program on the machine (or any
    /// user, on a shared one) could run whatever it liked as you, or have the router read any file you can. Those
    /// settings are then changed in the configuration file. Default <see langword="false"/>.
    /// </summary>
    public bool AllowProcessSettings { get; set; }

    /// <summary>
    /// Addresses the dashboard is reached at besides <c>localhost</c>, e.g. <c>https://router.home.lan</c>. Changes
    /// are accepted from these origins, and loopback requests may name them in the Host header.
    /// </summary>
    public IList<string> PublicOrigins { get; } = new List<string>();

    /// <summary>
    /// How long a dashboard sign-in lasts: the session cookie then lapses and the token must be entered again. Changing
    /// a token ends every session signed in with it, and signing out ends that session on the server. Default 12 hours.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Header an authenticating reverse proxy sets to the signed-in user, e.g. <c>X-Forwarded-User</c> or
    /// <c>X-Auth-Request-Email</c> (oauth2-proxy), <c>Cf-Access-Authenticated-User-Email</c> (Cloudflare Access). Believed
    /// only on requests that come straight from a proxy named in <c>SemanticRouter:ForwardedHeaders:KnownProxies</c> or
    /// <c>KnownNetworks</c> (<see cref="RouterForwardedHeadersExtensions.UseRouterForwardedHeaders"/>); from anyone else
    /// it is ignored. The user gets the role <see cref="TrustedIdentityRoles"/> gives them (none: <c>403</c>), tokens are
    /// not consulted, and the user is named in the configuration history and the audit log. The proxy must replace the
    /// header, not add to one the client sent.
    /// </summary>
    public string? TrustedIdentityHeader { get; set; }

    /// <summary>
    /// Header with the signed-in user's groups, comma separated (e.g. <c>X-Forwarded-Groups</c>), for
    /// <see cref="TrustedIdentityRoles"/> entries that name a group. Optional; believed only with <see cref="TrustedIdentityHeader"/>.
    /// </summary>
    public string? TrustedGroupsHeader { get; set; }

    /// <summary>
    /// The role of each user or group a trusted proxy names, e.g. <c>{ "alice@corp.example": "Admin", "router-admins":
    /// "Admin", "finance": "Metrics", "*": "ReadOnly" }</c>. The user's own entry wins, then the highest role among their
    /// groups (<c>Admin</c>, then <c>ReadOnly</c>, then <c>Metrics</c>), then <c>*</c>. Names are compared ignoring case.
    /// </summary>
    public IDictionary<string, RouterDashboardRole> TrustedIdentityRoles { get; } = new Dictionary<string, RouterDashboardRole>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Entries shown by the activity endpoint when the client gives no limit. Default 100.</summary>
    public int DefaultActivityLimit { get; set; } = 100;

    /// <summary>
    /// Directory of drop-in themes: one <c>{id}.css</c> file per theme declaring <c>:root[data-theme="{id}"] { … }</c>,
    /// with an optional <c>{id}.json</c> manifest (<c>{ "name": "Ocean" }</c>). Default: a <c>themes</c> directory next to
    /// the configuration file (<c>UseConfigurationFile</c>), or none. See docs/dashboard.md#themes.
    /// </summary>
    public string? ThemesDirectory { get; set; }
}

/// <summary>Maps the configuration dashboard and its admin API.</summary>
public static partial class RouterDashboardEndpoints
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
        if (endpoints.ServiceProvider.GetService<RouterDashboardLocation>() is { } location)
        {
            // Gateway errors link to the page that fixes them.
            location.Prefix = prefix;
        }

        DashboardAccess access = CreateAccess(endpoints.ServiceProvider, options, prefix);
        IAuditLog auditLog = endpoints.ServiceProvider.GetService<IAuditLog>() ?? new InMemoryAuditLog();
        var audit = new DashboardAudit(
            auditLog,
            endpoints.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System,
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SemanticRouter.Audit"));
        RouteGroupBuilder group = endpoints.MapGroup(prefix);
        var themes = new ThemeCatalog(options.ThemesDirectory ?? (endpoints.ServiceProvider.GetService<IRouterConfigurationStore>() is JsonFileRouterConfigurationStore store
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Location))!, "themes")
            : null));
        // No other site may frame the dashboard: inside a frame, clicks a page tricks the user into are same-origin writes.
        // The page loads nothing from other hosts and runs no inline script, so text an attacker gets into the page (a
        // prompt preview, a model name) cannot run as script or send what it reads anywhere else.
        group.AddEndpointFilter((invocation, next) =>
        {
            IHeaderDictionary headers = invocation.HttpContext.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            return next(invocation);
        });
        // Outside the access check, so the audit log also records what the access check refused a signed-in caller.
        group.AddEndpointFilter(audit.FilterAsync);
        group.AddEndpointFilter(access.FilterAsync);

        group.MapGet("/", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/index.html", (HttpContext context) => Asset(context, "index.html", "text/html; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/app.css", (HttpContext context) => Asset(context, "app.css", "text/css; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/themes/{file}", (HttpContext context, string file) =>
            {
                string id = file.EndsWith(".css", StringComparison.Ordinal) ? file[..^4] : string.Empty;
                if (themes.ReadCss(id) is not { } css)
                {
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = "no-cache";
                return Results.Text(css, "text/css; charset=utf-8");
            })
            .WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/js/{file}", (HttpContext context, string file) => ModuleName().IsMatch(file)
                ? Asset(context, "js/" + file, "text/javascript; charset=utf-8", prefix)
                : Results.NotFound())
            .WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/fonts/{file}", (HttpContext context, string file) => FontName().IsMatch(file)
                ? Asset(context, "fonts/" + file, file.EndsWith(".woff2", StringComparison.Ordinal) ? "font/woff2" : "text/plain; charset=utf-8", prefix)
                : Results.NotFound())
            .WithMetadata(DashboardAnonymousMetadata.Instance);
        group.MapGet("/graph.js", (HttpContext context) => Asset(context, "graph.js", "text/javascript; charset=utf-8", prefix)).WithMetadata(DashboardAnonymousMetadata.Instance);

        RouteGroupBuilder api = group.MapGroup("/api");
        api.MapGet("/themes", () => Results.Json(new ThemesResponse(themes.Directory, ThemeCatalog.RequiredTokens, themes.List()), ApiJson))
            .WithMetadata(DashboardMetricsMetadata.Instance);
        api.MapGet("/session", (HttpContext context) =>
                Results.Json(new SessionResponse(
                    DashboardAccess.RoleOf(context)?.ToString(), access.TokenRequired(context), access.TokensConfigured,
                    DashboardAccess.RoleOf(context) is null ? null : DashboardAccess.ActorOf(context)), ApiJson))
            .WithMetadata(DashboardAnonymousMetadata.Instance);

        api.MapPost("/session", (HttpContext context, SignInRequest request) =>
            {
                if (access.Throttle.RetryAfter(context) is { } wait)
                {
                    return DashboardAccess.TooManyAttempts(context, wait);
                }

                if (access.RoleForToken(request.Token) is not { } role)
                {
                    access.WrongToken(context);
                    DashboardAudit.Summary(context, "sign-in failed: wrong token");
                    return Results.Problem("That token is not valid for this router.", statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed");
                }

                access.SignIn(context, role, prefix);
                DashboardAccess.SetActor(context, DashboardAccess.TokenActor(role));
                return Results.Json(new SessionResponse(role.ToString(), access.TokenRequired(context), access.TokensConfigured, DashboardAccess.TokenActor(role)), ApiJson);
            })
            .WithMetadata(DashboardAnonymousMetadata.Instance)
            .Audited("signed in");

        // Ends the caller's session on the server too: the cookie is refused from now on, even if someone kept a copy.
        api.MapDelete("/session", (HttpContext context) =>
            {
                if (access.SignOut(context, prefix))
                {
                    DashboardAudit.Detail(context, "session revoked");
                }

                return Results.NoContent();
            })
            .WithMetadata(DashboardAnonymousMetadata.Instance)
            .Audited("signed out");

        // Ends every session signed in until now, everyone's (tokens and loopback access keep working).
        api.MapPost("/session/revoke-all", (HttpContext context) =>
            {
                access.Sessions.RevokeAll();
                access.SignOut(context, prefix);
                return Results.NoContent();
            })
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("signed out every session");

        api.MapGet("/status", (HttpContext context, RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity, BudgetGate budgets, IEnumerable<IEmbeddingProviderFactory> factories, ClientRateLimiter rateLimits, IServiceProvider services) =>
            {
                StatusResponse status = BuildStatus(
                    provider, health, activity, budgets, factories, rateLimits, services.GetService<ConnectorIncidents>(), services.GetService<EndpointChecks>(),
                    services.GetService<IModelCacheShare>(), Listening(services), options.PublicOrigins);
                status = status with { Unsaved = provider.Unsaved };

                // A connector's last error comes from the activity log's details, which can quote a reply.
                return Results.Json(
                    DashboardAccess.RoleOf(context) == RouterDashboardRole.Metrics ? status with { Endpoints = [.. status.Endpoints.Select(e => e with { LastError = null })] } : status,
                    ApiJson);
            })
            .WithMetadata(DashboardMetricsMetadata.Instance);

        api.MapGet("/config", (RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.Current;
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), null);
        });

        // The machine policy an administrator set, which the configuration cannot override (empty when there is none).
        api.MapGet("/policy", (RouterRuntimeProvider provider) => Results.Text(
            $$"""{"source":{{System.Text.Json.JsonSerializer.Serialize(provider.Policy.Source)}},"policy":{{provider.Policy.ToJson()}}}""",
            "application/json"));

        api.MapGet("/config/baseline", (RouterRuntimeProvider provider) =>
            ConfigEnvelope(provider.Current, provider, provider.Baseline.Masked(), null));

        api.MapPost("/config/validate", async (HttpContext context, RouterRuntimeProvider provider) =>
        {
            (RouterConfiguration? candidate, IResult? unreadable) = await ReadConfigurationAsync(context, provider).ConfigureAwait(false);
            if (candidate is null)
            {
                return unreadable!;
            }

            return Results.Json(Validation([.. provider.ValidateChange(candidate), .. ProcessSettingErrors(candidate, provider, options), .. candidate.ReferenceErrors(provider.Current.Configuration)], candidate), ApiJson);
        }).Audited("validated a configuration");

        // baseVersion: the version the caller's draft started from. When another version runs by now, nothing is applied
        // and the answer (409) says who changed it when, so the caller can reload or compare instead of undoing that change.
        api.MapPut("/config", async (HttpContext context, RouterRuntimeProvider provider, bool? persist, string? note, int? baseVersion) =>
        {
            if (baseVersion is { } expected && provider.Current is { } running && running.Version != expected)
            {
                return Conflict(running);
            }

            (RouterConfiguration? candidate, IResult? unreadable) = await ReadConfigurationAsync(context, provider).ConfigureAwait(false);
            if (candidate is null)
            {
                return unreadable!;
            }

            IReadOnlyList<string> errors = [.. provider.ValidateChange(candidate), .. ProcessSettingErrors(candidate, provider, options), .. candidate.ReferenceErrors(provider.Current.Configuration)];
            if (errors.Count > 0)
            {
                return Results.Json(Validation(errors, candidate), ApiJson, statusCode: StatusCodes.Status400BadRequest);
            }

            bool shouldPersist = persist ?? true;
            RouterRuntime runtime;
            try
            {
                runtime = provider.Apply(candidate, shouldPersist, "admin API", Actor(context), note, baseVersion);
            }
            catch (RouterConfigurationConflictException ex)
            {
                return Conflict(ex.Current);
            }

            DashboardAudit.Detail(context, $"version {runtime.Version}{(shouldPersist ? ", saved" : ", not saved")}");
            var warnings = new List<string>(runtime.ApplyNotes);
            if (shouldPersist && provider.Store is null)
            {
                warnings.Insert(0, "Applied in memory only: no configuration store is registered (see SemanticRouterBuilder.UseConfigurationFile).");
            }

            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), warnings.Count > 0 ? string.Join(" ", warnings) : null);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("applied the configuration");

        api.MapPost("/config/reset", (HttpContext context, RouterRuntimeProvider provider) =>
        {
            RouterRuntime runtime = provider.ResetToBaseline(Actor(context));
            DashboardAudit.Detail(context, $"version {runtime.Version}");
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), "Reverted to the application's configuration (the stored document was left in place).");
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("reset the configuration to the application's");

        // The running configuration was applied without saving: keep it (save) or go back to what is saved (revert).
        api.MapPost("/config/save", (HttpContext context, RouterRuntimeProvider provider) =>
        {
            if (provider.Store is null)
            {
                return Results.Problem(
                    "There is nowhere to save the configuration: this router has no configuration file (see SemanticRouterBuilder.UseConfigurationFile).",
                    statusCode: StatusCodes.Status409Conflict, title: "Not saved");
            }

            RouterRuntime runtime = provider.SaveRunning(Actor(context));
            DashboardAudit.Detail(context, $"version {runtime.Version}");
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), runtime.ApplyNotes.Count > 0 ? string.Join(" ", runtime.ApplyNotes) : null);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("saved the running configuration");

        api.MapPost("/config/revert", (HttpContext context, RouterRuntimeProvider provider) =>
        {
            if (provider.Store is null)
            {
                return Results.Problem(
                    "There is no saved configuration to go back to: this router has no configuration file (see SemanticRouterBuilder.UseConfigurationFile).",
                    statusCode: StatusCodes.Status409Conflict, title: "Nothing saved");
            }

            RouterRuntime runtime = provider.RevertToSaved(Actor(context));
            DashboardAudit.Detail(context, $"version {runtime.Version}");
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), runtime.Source == RouterConfigurationSource.Store
                ? null
                : "Nothing usable is saved, so the application's own configuration is running again.");
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("went back to the saved configuration");

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

            // The history keeps no secrets: they come from the running configuration.
            document.UnmaskFrom(provider.Current.Configuration);
            IReadOnlyList<string> errors = provider.ValidateChange(document);
            if (errors.Count > 0)
            {
                return Results.Json(Validation(errors, document), ApiJson, statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Apply(document, persist: true, "rollback", Actor(context), $"to version {id}");
            DashboardAudit.Detail(context, $"to version {id}, now version {runtime.Version}");
            return ConfigEnvelope(runtime, provider, runtime.Configuration.Masked(), $"Rolled back to version {id}.");
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("rolled back the configuration");

        api.MapGet("/activity", (RoutingActivityLog activity, RoutingFeedbackLog feedback, int? limit, long? after) =>
        {
            IReadOnlyList<RoutingActivityEntry> entries = activity.Recent(limit ?? options.DefaultActivityLimit, after);
            var ids = entries.Select(e => e.Id).ToHashSet();
            Dictionary<long, IReadOnlyList<FeedbackKind>> kinds = feedback.Recent(5000)
                .Where(f => ids.Contains(f.EntryId))
                .GroupBy(f => f.EntryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<FeedbackKind>)[.. g.Select(f => f.Kind).Distinct()]);
            return Results.Json(new ActivityResponse(entries, activity.NextId - 1, kinds), ApiJson);
        });

        // Deletes activity (and its prompt previews) for good: everything, or since/until. The state database zeroes and
        // compacts what it deleted, so the rows cannot be recovered from its files.
        api.MapDelete("/activity", (HttpContext context, RoutingActivityLog activity, DateTimeOffset? since, DateTimeOffset? until) =>
            {
                int removed = activity.Purge(since ?? DateTimeOffset.MinValue, until ?? DateTimeOffset.MaxValue);
                DashboardAudit.Detail(context, $"{removed} {(removed == 1 ? "entry" : "entries")}" + (since is null && until is null ? ", everything" : $" from {since?.ToString("u") ?? "the start"} to {until?.ToString("u") ?? "now"}"));
                return Results.Json(new { removed }, ApiJson);
            })
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("deleted activity");

        api.MapGet("/feedback", (RoutingFeedbackLog feedback, int? limit) =>
            Results.Json(feedback.Recent(Math.Clamp(limit ?? 200, 1, 5000)), ApiJson));

        api.MapGet("/feedback/summary", (RoutingActivityLog activity, RoutingFeedbackLog feedback, int? days) =>
        {
            DateTimeOffset until = DateTimeOffset.UtcNow, since = until.AddDays(-Math.Clamp(days ?? 7, 1, 90));
            return Results.Json(FeedbackSummary.Build(activity.Between(since, until), feedback.Between(since, until), since, until), ApiJson);
        }).WithMetadata(DashboardMetricsMetadata.Instance);

        api.MapGet("/events", StreamEventsAsync);

        api.MapGet("/memory", async (RouterRuntimeProvider provider, MemoryIndex memory, MemoryExposureLog exposure, CancellationToken cancellationToken) =>
        {
            MemorySnapshot snapshot = memory.Current;
            if (snapshot.LoadedAt is null)
            {
                snapshot = await memory.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                memory.EnsureFresh();
            }

            return Results.Json(BuildMemoryMap(provider.Current, snapshot, exposure), ApiJson);
        });

        api.MapPost("/memory/refresh", async (RouterRuntimeProvider provider, MemoryIndex memory, MemoryExposureLog exposure, CancellationToken cancellationToken) =>
            Results.Json(BuildMemoryMap(provider.Current, await memory.RefreshAsync(cancellationToken).ConfigureAwait(false), exposure), ApiJson))
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("rescanned the memory sources");

        api.MapPost("/memory/exposure/reset", (MemoryExposureLog exposure) =>
        {
            exposure.Clear();
            return Results.Ok(new { cleared = true });
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("forgot where notes were sent");

        api.MapGet("/activity/series", (RoutingActivityLog activity, string? window) =>
        {
            (TimeSpan span, TimeSpan bucket) = ActivityReports.Window(window);
            DateTimeOffset until = Align(DateTimeOffset.UtcNow, bucket) + bucket;
            DateTimeOffset since = until - span;
            return Results.Json(ActivityReports.Series(activity.Between(since, until), since, until, bucket), ApiJson);
        }).WithMetadata(DashboardMetricsMetadata.Instance);

        api.MapGet("/activity/export", (HttpContext context, RoutingActivityLog activity, string? format, bool? prompts, DateTimeOffset? since, DateTimeOffset? until) =>
        {
            DateTimeOffset end = until ?? DateTimeOffset.UtcNow.AddMinutes(1);
            IReadOnlyList<RoutingActivityEntry> entries = activity.Between(since ?? end.AddDays(-7), end);
            bool includePrompts = prompts ?? false;
            DashboardAudit.Detail(context, $"{entries.Count} {(entries.Count == 1 ? "entry" : "entries")}, {(includePrompts ? "with" : "without")} prompt previews");
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(ActivityReports.ToJsonLines(entries, includePrompts)), "application/jsonl", $"semantic-router-activity-{stamp}.jsonl")
                : Results.File(System.Text.Encoding.UTF8.GetBytes(ActivityReports.ToCsv(entries, includePrompts)), "text/csv", $"semantic-router-activity-{stamp}.csv");
        }).Audited("exported activity");

        api.MapGet("/usage", (RouterRuntimeProvider provider, IUsageLedger ledger, BudgetGate budgets, string? window, string? by) =>
        {
            UsageOptions usage = provider.Current.Configuration.Usage;
            TimeZoneInfo zone = BudgetGate.ZoneOf(usage);
            (DateTimeOffset since, DateTimeOffset until) = UsageWindow(window, zone, DateTimeOffset.UtcNow);
            UsageGrouping grouping = Enum.TryParse(by, ignoreCase: true, out UsageGrouping parsed) ? parsed : UsageGrouping.Model;
            return Results.Json(new UsageResponse(
                since, until, grouping, usage.ReferenceModel, ledger.Total(since, until), ledger.Summarize(since, until, grouping, zone), budgets.Status(usage)), ApiJson);
        }).WithMetadata(DashboardMetricsMetadata.Instance);

        api.MapPost("/classify", async (ClassifyPromptRequest request, RouterRuntimeProvider provider, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.Problem("'prompt' is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            RouterRuntime runtime = provider.Current;
            ProfileSelection.ModelChoice choice = ProfileSelection.ResolveModel(runtime, request.Model);
            string? profile = request.Profile is { Length: > 0 } named ? named : choice.Profile;
            if (profile is not null && !runtime.Configuration.Profiles.ContainsKey(profile))
            {
                return Results.Problem($"Unknown routing profile '{profile}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            // As the gateway routes a first turn: a leading callout ("@opus …") applies and is not classified.
            (RoutingPlan plan, string? callout) = await Callouts.RouteAsync(
                runtime, runtime.Router,
                new PromptRoutingRequest { Prompt = request.Prompt, Strategy = choice.Strategy, RequestedModel = choice.RequestedModel, Profile = profile },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(PromptClassificationResponse.From(plan, services.GetService<IModelResidency>()) with { Callout = callout });
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("classified a prompt"); // classifying embeds the prompt, which can cost an embedding call

        api.MapPost("/health/reset", (IModelHealth health) =>
        {
            if (health is ModelHealthTracker tracker)
            {
                tracker.Clear();
                return Results.Ok(new { cleared = true });
            }

            return Results.Problem("The registered IModelHealth does not support clearing cooldowns.", statusCode: StatusCodes.Status501NotImplemented);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("cleared every cooldown");

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
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("tested the embeddings");

        api.MapPost("/connectors/{name}/discover", async (string name, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            ModelLineupReport report;
            try
            {
                report = await ModelLineup.DiscoverAsync(services, name, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(DiscoverResponse.From(report), ApiJson);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("discovered a connector's models");

        // The same for a connector in the dashboard's draft that is not applied yet: {"name", "settings"} and optionally the
        // draft's "models", to mark what is already in the catalog. Held to the checks applying it would meet; applies nothing.
        api.MapPost("/connectors/discover", async (HttpContext context, RouterRuntimeProvider provider, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            (DraftConnector? draft, IResult? unreadable) = await ReadDraftConnectorAsync(context).ConfigureAwait(false);
            if (draft is null)
            {
                return unreadable!;
            }

            RouterRuntime runtime = provider.Current;
            RouterConfiguration current = runtime.Configuration;
            string name = draft.Name;
            RouterConfiguration candidate = current.Clone();
            candidate.Connectors[name] = draft.Document.Connectors[name];

            // A masked secret is filled in from the running connector of the same name only if it points to the same place.
            candidate.UnmaskFrom(current);
            ConnectorOptions settings = candidate.Connectors[name];
            var errors = new List<string>();
            if (provider.Policy.Blocks(settings) is { } blocked)
            {
                errors.Add($"Connectors:{name}: not allowed: {blocked}.");
            }

            errors.AddRange(candidate.MaskedSecretErrors());
            errors.AddRange(ProcessSettingErrors(candidate, provider, options));
            errors.AddRange(candidate.ReferenceErrors(current));
            if (errors.Count > 0)
            {
                return Results.Json(Validation(errors, candidate), ApiJson, statusCode: StatusCodes.Status400BadRequest);
            }

            var alone = new RouterConfiguration();
            alone.Connectors[name] = settings;
            var unresolved = new List<string>();
            RouterConfiguration resolved = alone.ResolveSecrets(provider.Secrets, unresolved);
            ConnectorOptions connector = resolved.Connectors[name];

            // Copilot lists models through its runtime: the running connector when it is this one, else a new one for this call.
            IModelDiscovery? live = null;
            IDisposable? created = null;
            if (connector.Type == ConnectorType.Copilot)
            {
                if (current.Connectors.TryGetValue(name, out ConnectorOptions? existing) && RouterConfiguration.SameDestination(existing, settings)
                    && runtime.Connectors.TryGet(name, out IChatConnector? running))
                {
                    live = running as IModelDiscovery;
                }
                else if (services.GetServices<IChatConnectorFactory>().FirstOrDefault(f => f.Type == ConnectorType.Copilot) is { } factory)
                {
                    IChatConnector made = factory.Create(name, connector);
                    (live, created) = (made as IModelDiscovery, made as IDisposable);
                }
            }

            try
            {
                var redactor = new SecretRedactor([.. runtime.Resolved.SecretValues(), .. resolved.SecretValues()]);
                ModelLineupReport report = await ModelLineup.DiscoverAsync(
                    services, name, connector, live, draft.Models ?? [.. runtime.Resolved.Models], redactor, cancellationToken).ConfigureAwait(false);
                if (unresolved.Count > 0)
                {
                    // E.g. ${env:OPENAI_API_KEY} with the variable not set: the provider then refuses, and this says why.
                    report = report with { Note = string.Join(" ", [.. unresolved, report.Note ?? string.Empty]).Trim() };
                }

                DashboardAudit.Detail(context, $"{name} ({connector.Type}), not applied");
                return Results.Json(DiscoverResponse.From(report), ApiJson);
            }
            finally
            {
                created?.Dispose();
            }
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("discovered a draft connector's models");

        api.MapPost("/connectors/{name}/check", async (string name, IServiceProvider services, RouterRuntimeProvider provider) =>
        {
            EndpointChecks? checks = services.GetService<EndpointChecks>();
            if (checks is null || !provider.Current.Configuration.Connectors.ContainsKey(name))
            {
                return Results.Problem($"Connector '{name}' is not configured.", statusCode: StatusCodes.Status404NotFound);
            }

            // Kept with where the connector pointed, so the result is forgotten once it points somewhere else.
            string target = EndpointHealth.TargetKey(provider.Current.Configuration, name);
            EndpointCheck result = await checks.RunAsync(name, target, () => RouterDoctor.CheckConnectorAsync(services, name)).ConfigureAwait(false);
            return Results.Json(result, ApiJson);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("checked a connector");

        // The audit log: admin only, since it names who did what from where.
        api.MapGet("/audit", (DateTimeOffset? since, DateTimeOffset? until, int? limit, string? format) =>
        {
            IReadOnlyList<AuditEntry> entries = auditLog.Query(since, until, Math.Clamp(limit ?? 200, 1, 10_000));
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(DashboardAudit.ToJsonLines(entries, ApiJson)), "application/jsonl", $"semantic-router-audit-{stamp}.jsonl")
                : Results.Json(new AuditResponse(entries), ApiJson);
        }).WithMetadata(DashboardAdminReadMetadata.Instance);

        SetupApi.Map(api);
        EvaluationApi.Map(api, ApiJson, Actor);
        TuningApi.Map(api, ApiJson, Actor);
        ReliabilityApi.Map(api, ApiJson);
        DigestApi.Map(api, ApiJson);
        return group;
    }

    /// <summary>
    /// Sends a browser that opens the site's root (exactly <c>/</c>, with GET or HEAD) to the dashboard at
    /// <paramref name="prefix"/>, for an application whose root serves nothing else, such as the <c>semantic-router</c>
    /// tool. It has the lowest precedence: an endpoint the application maps at <c>/</c> itself wins.
    /// </summary>
    public static IEndpointConventionBuilder MapRouterDashboardRedirect(
        this IEndpointRouteBuilder endpoints, [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string prefix = "/router")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        string target = "/" + prefix.Trim('/') + "/";
        return endpoints.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], (HttpContext context) => Results.Redirect(context.Request.PathBase + target))
            .WithOrder(int.MaxValue)
            .WithDisplayName("Redirect / to the router dashboard");
    }

    /// <summary>
    /// Server-sent events for live views: an <c>activity</c> event when a request is routed and again when it finishes
    /// (the entry as <c>GET api/activity</c> returns it), and a comment every 15 seconds to keep proxies from closing
    /// the stream. A slow reader loses the oldest events rather than holding up requests.
    /// </summary>
    private static async Task StreamEventsAsync(HttpContext context, RoutingActivityLog activity)
    {
        CancellationToken aborted = context.RequestAborted;
        Channel<RoutingActivityEntry> channel = Channel.CreateBounded<RoutingActivityEntry>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void OnUpdated(RoutingActivityEntry entry) => channel.Writer.TryWrite(entry);

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        activity.Updated += OnUpdated;
        try
        {
            await context.Response.WriteAsync(": connected\n\n", aborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
            while (!aborted.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                keepAlive.CancelAfter(TimeSpan.FromSeconds(15));
                bool ready;
                try
                {
                    ready = await channel.Reader.WaitToReadAsync(keepAlive.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", aborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
                    continue;
                }

                if (!ready)
                {
                    break;
                }

                while (channel.Reader.TryRead(out RoutingActivityEntry? entry))
                {
                    string json = JsonSerializer.Serialize(entry, ApiJson);
                    await context.Response.WriteAsync($"id: {entry.Id}\nevent: activity\ndata: {json}\n\n", aborted).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The browser went away.
        }
        finally
        {
            activity.Updated -= OnUpdated;
        }
    }

    private static MemoryMapResponse BuildMemoryMap(RouterRuntime runtime, MemorySnapshot snapshot, MemoryExposureLog exposure)
    {
        IReadOnlyDictionary<string, MemoryExposure> exposures = exposure.Snapshot();
        return new MemoryMapResponse(
            snapshot.LoadedAt,
            snapshot.LoadMilliseconds,
            runtime.Configuration.Memory.TrackSharing,
            runtime.Configuration.Middleware.Mode.ToString(),
            snapshot.Sources,
            snapshot.Notes.Select(n => new MemoryNodeResponse(
                n.Id, n.Title, n.Source, n.Path, n.Folder, n.Tags, n.Aliases, n.Size, n.Modified, n.Fingerprints, n.Missing)).ToArray(),
            snapshot.Links.Select(l => new[] { l.From, l.To }).ToArray(),
            exposures);
    }

    private static DateTimeOffset Align(DateTimeOffset time, TimeSpan bucket) =>
        new(time.UtcTicks - (time.UtcTicks % bucket.Ticks), TimeSpan.Zero);

    /// <summary>
    /// Who is making a change, for the configuration history, learned changes and the audit log: the user a trusted proxy
    /// named, <c>admin token</c>, <c>read-only token</c>, <c>metrics token</c> or <c>this machine</c>. Never contains a token.
    /// </summary>
    private static string Actor(HttpContext context) =>
        DashboardAccess.ActorOf(context) ?? (DashboardAccess.IsLocal(context) ? "this machine" : "unknown");

    /// <summary>
    /// Errors for CLI and Copilot connectors whose program, arguments or environment the request would change, and for
    /// memory sources it would point at another folder or file.
    /// </summary>
    private static IEnumerable<string> ProcessSettingErrors(RouterConfiguration candidate, RouterRuntimeProvider provider, RouterDashboardOptions options)
    {
        if (options.AllowProcessSettings)
        {
            return [];
        }

        string file = $"configuration file{(provider.Store is { } store ? $" ({store.Location})" : string.Empty)}";
        return candidate.ProcessSettingChanges(provider.Current.Configuration).Select(name =>
                $"Connectors:{name}: what a CLI runs (its program, arguments, environment variables and home directory) is changed in the " +
                $"{file}, not over the admin API, which any program on this machine can reach. Presets started from PATH can be added here. " +
                "To allow it, set SemanticRouter:Dashboard:AllowProcessSettings.")
            .Concat(candidate.MemoryPathChanges(provider.Current.Configuration).Select(name =>
                $"Memory:Sources:{name}:Path: which folders and files the router reads is changed in the {file}, not over the admin API, " +
                "which any program on this machine can reach. To allow it, set SemanticRouter:Dashboard:AllowProcessSettings."));
    }

    private static RouterDashboardOptions CopyOf(RouterDashboardOptions? source)
    {
        var copy = new RouterDashboardOptions();
        if (source is not null)
        {
            copy.RequireLocalClient = source.RequireLocalClient;
            copy.AdminToken = source.AdminToken;
            copy.ReadOnlyToken = source.ReadOnlyToken;
            copy.MetricsToken = source.MetricsToken;
            copy.AllowUnauthenticatedRemoteClients = source.AllowUnauthenticatedRemoteClients;
            copy.RequireTokenForLocalClients = source.RequireTokenForLocalClients;
            copy.AllowProcessSettings = source.AllowProcessSettings;
            copy.SessionLifetime = source.SessionLifetime;
            copy.TrustedIdentityHeader = source.TrustedIdentityHeader;
            copy.TrustedGroupsHeader = source.TrustedGroupsHeader;
            foreach ((string name, RouterDashboardRole role) in source.TrustedIdentityRoles)
            {
                copy.TrustedIdentityRoles[name] = role;
            }

            copy.DefaultActivityLimit = source.DefaultActivityLimit;
            copy.ThemesDirectory = source.ThemesDirectory;
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
        string? metrics = SecretReference.Resolve(options.MetricsToken, secrets, "Dashboard:MetricsToken", problems);
        (string Setting, string? Token)[] tokens = [("AdminToken", admin), ("ReadOnlyToken", readOnly), ("MetricsToken", metrics)];
        foreach ((string setting, string? token) in tokens)
        {
            if (!string.IsNullOrEmpty(token) && token.Length < RouterDashboardOptions.MinimumTokenLength)
            {
                throw new InvalidOperationException(
                    $"SemanticRouter:Dashboard:{setting} must be at least {RouterDashboardOptions.MinimumTokenLength} characters (try `openssl rand -hex 24`).");
            }
        }

        // One token, one role: a token shared by two roles would get the higher one.
        for (int i = 0; i < tokens.Length; i++)
        {
            for (int j = i + 1; j < tokens.Length; j++)
            {
                if (!string.IsNullOrEmpty(tokens[i].Token) && tokens[i].Token == tokens[j].Token)
                {
                    throw new InvalidOperationException($"SemanticRouter:Dashboard:{tokens[i].Setting} and {tokens[j].Setting} must differ.");
                }
            }
        }

        if (options.SessionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SemanticRouter:Dashboard:SessionLifetime must be positive, e.g. 12:00:00.");
        }

        if (!string.IsNullOrWhiteSpace(options.TrustedIdentityHeader) && options.TrustedIdentityRoles.Count == 0)
        {
            problems.Add($"TrustedIdentityHeader is {options.TrustedIdentityHeader} but TrustedIdentityRoles is empty, so every user the proxy names is refused.");
        }

        var access = new DashboardAccess(
            options, admin, readOnly, services.GetService<MachinePolicy>()?.Dashboard ?? DashboardPolicy.Default, services.GetService<TimeProvider>(),
            metrics, services.GetService<IStateValueStore>());
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

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9-]+\\.js$")]
    private static partial System.Text.RegularExpressions.Regex ModuleName();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+\\.(woff2|txt)$")]
    private static partial System.Text.RegularExpressions.Regex FontName();

    /// <summary>
    /// What dashboard pages may load: their own scripts, styles, fonts and API, and nothing from another host. Inline
    /// style attributes are allowed (the views set widths and colours with them); inline script is not.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// The period a usage window names (<c>day</c>, <c>week</c>, <c>month</c> in the budget time zone; <c>30d</c>; the last
    /// 7 days otherwise), shared by the Usage page and the digest so they add up the same requests.
    /// </summary>
    internal static (DateTimeOffset Since, DateTimeOffset Until) UsageWindow(string? window, TimeZoneInfo zone, DateTimeOffset now) => window switch
    {
        "day" => BudgetGate.PeriodOf(BudgetPeriod.Day, now, zone),
        "week" => BudgetGate.PeriodOf(BudgetPeriod.Week, now, zone),
        "month" => BudgetGate.PeriodOf(BudgetPeriod.Month, now, zone),
        "30d" => (now.AddDays(-30), now.AddMinutes(1)),
        _ => (now.AddDays(-7), now.AddMinutes(1)),
    };

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

    /// <summary>
    /// Reads the configuration document in the body (bare, or as the <c>config</c> of an envelope) with masked secrets
    /// filled in from the running configuration. When it cannot be read, the 400 to answer with instead: a field whose
    /// value does not fit (<c>"Timeout": "5m"</c>) is named, with the format it needs.
    /// </summary>
    private static async Task<(RouterConfiguration? Candidate, IResult? Problem)> ReadConfigurationAsync(HttpContext context, RouterRuntimeProvider provider)
    {
        using var reader = new StreamReader(context.Request.Body);
        string raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, NotADocument());
        }

        JsonNode? document = null;
        try
        {
            JsonNode? node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            document = node is JsonObject obj && obj.TryGetPropertyValue("config", out JsonNode? inner) ? inner : node;
            if (document is not JsonObject)
            {
                return (null, NotADocument());
            }

            RouterConfiguration candidate = RouterConfiguration.FromJson(document.ToJsonString());
            candidate.UnmaskFrom(provider.Current.Configuration);
            return (candidate, null);
        }
        catch (JsonException ex)
        {
            return (null, Unreadable(ex, document));
        }

        static IResult NotADocument() => Results.Json(
            new ValidationResult(["Expected a configuration document: a JSON object such as GET api/config returns."]) { Issues = [new ConfigurationIssue(null, "Expected a configuration document: a JSON object such as GET api/config returns.")] },
            ApiJson,
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>A connector from the dashboard's draft: its name, settings and the draft's catalog, if sent.</summary>
    private sealed record DraftConnector(string Name, RouterConfiguration Document, IReadOnlyList<ModelDefinition>? Models);

    /// <summary>Reads <c>{"name": …, "settings": {…}, "models": [ … ]}</c>; when it cannot be read, the 400 to answer with instead.</summary>
    private static async Task<(DraftConnector? Draft, IResult? Problem)> ReadDraftConnectorAsync(HttpContext context)
    {
        const string expected = "Expected {\"name\": \"…\", \"settings\": { …connector settings… }}, with the draft's \"models\" optionally.";
        using var reader = new StreamReader(context.Request.Body);
        string raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        JsonObject? wrapper = null;
        try
        {
            JsonObject? body = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            string? name = body?["name"] is JsonValue value && value.TryGetValue(out string? text) ? text?.Trim() : null;
            if (string.IsNullOrEmpty(name) || body!["settings"] is not JsonObject settings)
            {
                return (null, Results.Json(new ValidationResult([expected]) { Issues = [new ConfigurationIssue(null, expected)] }, ApiJson, statusCode: StatusCodes.Status400BadRequest));
            }

            // Parsed as part of a document, so it reads exactly as PUT api/config would read it.
            wrapper = new JsonObject { ["Connectors"] = new JsonObject { [name] = settings.DeepClone() } };
            bool withModels = body["models"] is JsonArray;
            if (withModels)
            {
                wrapper["Models"] = body["models"]!.DeepClone();
            }

            RouterConfiguration document = RouterConfiguration.FromJson(wrapper.ToJsonString());
            return (new DraftConnector(name, document, withModels ? [.. document.Models] : null), null);
        }
        catch (JsonException ex)
        {
            return (null, Unreadable(ex, wrapper));
        }
    }

    /// <summary>The 400 for JSON that does not parse, or a value that does not fit its field.</summary>
    private static IResult Unreadable(JsonException exception, JsonNode? document, string? pathPrefix = null)
    {
        ConfigurationIssue issue = ConfigurationIssues.FromJsonException(exception, document, pathPrefix);
        return Results.Json(
            new ValidationResult([issue.Path is null ? issue.Message : $"{issue.Path}: {issue.Message}"]) { Issues = [issue] },
            ApiJson,
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>The validation outcome, with each error tied to its field.</summary>
    private static ValidationResult Validation(IReadOnlyList<string> errors, RouterConfiguration candidate) =>
        new(errors) { Issues = ConfigurationIssues.Locate(errors, candidate) };

    /// <summary>The 409 for a change made against a version that is no longer running.</summary>
    private static IResult Conflict(RouterRuntime running) => Results.Json(
        new ConflictResponse(
            "The configuration changed since you loaded it.",
            running.Version,
            running.ChangedBy,
            running.CreatedAt,
            running.ChangeSource,
            $"Version {running.Version} is running now ({running.ChangeSource ?? "applied"}{(running.ChangedBy is null ? string.Empty : " by " + running.ChangedBy)}). " +
            "Nothing was applied: reload it, or apply yours again on top of it."),
        ApiJson,
        statusCode: StatusCodes.Status409Conflict);

    private static IResult ConfigEnvelope(RouterRuntime runtime, RouterRuntimeProvider provider, RouterConfiguration masked, string? warning)
    {
        var envelope = new JsonObject
        {
            ["version"] = runtime.Version,
            ["source"] = runtime.Source.ToString(),
            ["createdAt"] = runtime.CreatedAt,
            ["store"] = provider.Store?.Location,
            ["secretStore"] = provider.Secrets?.Location,
            ["unsaved"] = provider.IsUnsaved(runtime),
            ["warning"] = warning,
            ["config"] = JsonNode.Parse(masked.ToJson()),
        };
        return Results.Content(envelope.ToJsonString(), "application/json");
    }

    private static StatusResponse BuildStatus(
        RouterRuntimeProvider provider, IModelHealth health, RoutingActivityLog activity, BudgetGate budgets, IEnumerable<IEmbeddingProviderFactory> factories,
        ClientRateLimiter rateLimits, ConnectorIncidents? incidents, EndpointChecks? checks, IModelCacheShare? cache, IEnumerable<string> listening,
        IEnumerable<string> publicOrigins)
    {
        RouterRuntime runtime = provider.Current;
        RouterConfiguration config = runtime.Configuration;
        IReadOnlyDictionary<string, DateTimeOffset> cooldowns = health.GetCooldowns();
        IReadOnlyList<RoutingActivityEntry> recent = activity.Recent(500);
        IReadOnlyList<EndpointStatus> endpoints = EndpointHealth.Build(config, cooldowns, recent, incidents, checks);

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
            new EmbeddingsStatus(
                config.Embeddings.Provider.ToString(), config.Embeddings.Model, AddressDisplay.Show(config.Embeddings.Endpoint), config.Embeddings.Dimensions,
                [nameof(EmbeddingProviderKind.Hashing), nameof(EmbeddingProviderKind.OpenAICompatible), .. factories.Select(f => f.Kind.ToString()).Distinct()],
                factories.SelectMany(f => f.Models.Select(m => new EmbeddingModelStatus(f.Kind.ToString(), m.Name, m.Description, m.SuggestedThreshold, m.DownloadBytes))).ToArray(),
                (runtime.Classifier as SemanticPromptClassifier)?.IndexStatistics is { } index
                    ? new IndexStatus(index.Utterances, index.Dimensions, Math.Round(index.BuildTime.TotalMilliseconds, 1))
                    : null,
                (runtime.Classifier as SemanticPromptClassifier)?.CacheStatistics),
            config.EffectiveTiers.Select(t => new TierStatus(t.Level, t.Name, t.Description)).ToArray(),
            config.Models.Select(m => new ModelStatus(
                m.Name, m.Connector, m.Model, m.Tier, m.Capabilities.ToArray(), m.IsLocal, m.Enabled, m.Priority, m.ContextWindow,
                m.InputCostPerMillionTokens, m.OutputCostPerMillionTokens,
                cooldowns.TryGetValue(m.Name, out DateTimeOffset until) ? until : null,
                config.Connectors.TryGetValue(m.Connector, out ConnectorOptions? c) ? c.Type.ToString() : null)
            {
                CacheShare = cache?.Typical(m, route: null),
                EffectiveInputCostPerMillionTokens = cache?.Typical(m, route: null) is { } share ? m.EstimateCost(1_000_000, 0, share) : null,
            }).ToArray(),
            config.Connectors.Select(kv => new ConnectorStatus(
                kv.Key, kv.Value.Type.ToString(), kv.Value.Type == ConnectorType.Cli ? kv.Value.Preset.ToString() : null,
                kv.Value.Type is ConnectorType.Cli or ConnectorType.Copilot ? kv.Value.Executable : AddressDisplay.Show(kv.Value.BaseAddress),
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
                config.Selection.Strategy.ToString(),
                rateLimits.Status(config.Middleware)),
            new ActivitySummary(
                recent.Count,
                recent.Count(e => e.Outcome == "served"),
                recent.Count(e => e.Outcome == "failed"),
                recent.Count(e => e.Degraded),
                recent.Where(e => e.ServedBy is not null).GroupBy(e => e.ServedBy!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                recent.GroupBy(e => e.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
                recent.GroupBy(e => e.Reason.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                recent.Where(e => e.Outcome == "served" && e.Connector is not null && e.ServingTimes().Total is not null)
                    .GroupBy(e => e.Connector!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ConnectorLatency(
                        g.Key,
                        config.Connectors.TryGetValue(g.Key, out ConnectorOptions? kind) ? (kind.Type == ConnectorType.Cli ? $"Cli/{kind.Preset}" : kind.Type.ToString()) : null,
                        g.Count(),
                        g.Count(e => e.HasTools),
                        Percentile(g.Select(e => e.ServingTimes().FirstByte), 0.5),
                        Percentile(g.Select(e => e.ServingTimes().FirstByte), 0.95),
                        Percentile(g.Select(e => e.ServingTimes().Total), 0.5)))
                    .OrderBy(l => l.Connector, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            budgets.Status(config.Usage),
            endpoints,
            Environment.MachineName,
            ServiceHealth.Build(runtime, endpoints, recent, listening, publicOrigins));
    }

    /// <summary>The addresses the server listens on, when it is a real server (not the test host).</summary>
    private static IEnumerable<string> Listening(IServiceProvider services) =>
        services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? (IEnumerable<string>)[];

    private static double? Percentile(IEnumerable<double?> values, double quantile)
    {
        double[] sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)], 0);
    }

    /// <summary>Who the caller is signed in as.</summary>
    /// <param name="Role"><c>Admin</c>, <c>ReadOnly</c>, <c>Metrics</c>, or <see langword="null"/> when not signed in.</param>
    /// <param name="TokenRequired">Whether this caller needs a token (it is remote, or tokens are required locally).</param>
    /// <param name="TokensConfigured">Whether the router has a token to sign in with.</param>
    /// <param name="Identity">Who the caller acts as (the user a trusted proxy named, <c>admin token</c>, …), when signed in.</param>
    public sealed record SessionResponse(string? Role, bool TokenRequired, bool TokensConfigured, string? Identity = null);

    /// <summary>Sign-in request.</summary>
    public sealed record SignInRequest(string? Token);

    /// <summary>Validation outcome.</summary>
    /// <param name="Errors">Problems; empty when valid.</param>
    public sealed record ValidationResult(IReadOnlyList<string> Errors)
    {
        /// <summary>Whether the document can be applied.</summary>
        public bool Valid => Errors.Count == 0;

        /// <summary>The same problems, each tied to the field it is about (see <see cref="ConfigurationIssue.Path"/>).</summary>
        public IReadOnlyList<ConfigurationIssue> Issues { get; init; } = [];
    }

    /// <summary>
    /// <c>409</c> from <c>PUT api/config?baseVersion=N</c> when another version runs by now: nothing was applied.
    /// </summary>
    /// <param name="Title">What happened.</param>
    /// <param name="CurrentVersion">The version running now.</param>
    /// <param name="Actor">Who applied it, when known.</param>
    /// <param name="At">When it was applied.</param>
    /// <param name="Source">What applied it (<c>admin API</c>, <c>auto-tune</c>, <c>rollback</c>, <c>startup</c>, …).</param>
    /// <param name="Detail">The same in a sentence, with what to do.</param>
    public sealed record ConflictResponse(string Title, int CurrentVersion, string? Actor, DateTimeOffset At, string? Source, string Detail)
    {
        /// <summary>The HTTP status, as in a problem document.</summary>
        public int Status { get; } = StatusCodes.Status409Conflict;
    }

    /// <summary>Usage over a window.</summary>
    public sealed record UsageResponse(
        DateTimeOffset Since, DateTimeOffset Until, UsageGrouping By, string? ReferenceModel, UsageTotals Totals,
        IReadOnlyList<UsageGroup> Groups, IReadOnlyList<BudgetStatus> Budgets);

    /// <summary>Applied configuration versions, newest first.</summary>
    public sealed record HistoryResponse(IReadOnlyList<RouterConfigurationVersion> Versions);

    /// <summary>Recent routing activity.</summary>
    public sealed record ActivityResponse(IReadOnlyList<RoutingActivityEntry> Entries, long Latest, IReadOnlyDictionary<long, IReadOnlyList<FeedbackKind>> Feedback);

    /// <summary>The memory map: every note and link, and where notes have been sent.</summary>
    /// <param name="LoadedAt">When the sources were last scanned; <see langword="null"/> before the first scan.</param>
    /// <param name="LoadMilliseconds">How long the scan took.</param>
    /// <param name="TrackSharing">Whether requests are checked for notes.</param>
    /// <param name="Mode">The gateway mode; sharing is only recorded in Dispatch mode.</param>
    /// <param name="Sources">Each source with its note count or error.</param>
    /// <param name="Nodes">Notes, and link targets with no note (<c>missing</c>).</param>
    /// <param name="Links">Links as pairs of indexes into <paramref name="Nodes"/>.</param>
    /// <param name="Exposure">By note id: the requests that carried the note, local and cloud, per model.</param>
    public sealed record MemoryMapResponse(
        DateTimeOffset? LoadedAt,
        double LoadMilliseconds,
        bool TrackSharing,
        string Mode,
        IReadOnlyList<MemorySourceStatus> Sources,
        IReadOnlyList<MemoryNodeResponse> Nodes,
        IReadOnlyList<int[]> Links,
        IReadOnlyDictionary<string, MemoryExposure> Exposure);

    /// <summary>A note in the memory map. Its text is never sent.</summary>
    public sealed record MemoryNodeResponse(
        string Id, string Title, string Source, string Path, string Folder, IReadOnlyList<string> Tags, IReadOnlyList<string> Aliases,
        int Size, DateTimeOffset? Modified, int Fingerprints, bool Missing);

    /// <summary>Request for the embeddings probe.</summary>
    public sealed record EmbeddingTestRequest(string? Text);

    /// <summary>Result of the embeddings probe.</summary>
    public sealed record EmbeddingTestResponse(bool Ok, int Dimensions, double Milliseconds, string? Error);

    /// <summary>What a connector offers: <c>POST api/connectors/{name}/discover</c>.</summary>
    /// <param name="Models">The model ids the provider serves (for the model editor's suggestions).</param>
    /// <param name="Note">A failure, or how the list was obtained.</param>
    /// <param name="Server">The server software, when recognised (<c>LM Studio</c>, <c>Ollama</c>).</param>
    /// <param name="Loading">Whether the server loads models on demand (<c>OnDemand</c>), only serves loaded ones (<c>LoadedOnly</c>), or does not say.</param>
    /// <param name="LoadingNote">What that means for routing.</param>
    /// <param name="Lineup">Every model found, with the suggested catalog entry.</param>
    /// <param name="Families">The model-family table the suggestions used.</param>
    /// <param name="FamiliesWarning">Why the local model-family table was not (fully) used.</param>
    public sealed record DiscoverResponse(
        IReadOnlyList<string> Models, string? Note, string? Server, ModelLoading Loading, string? LoadingNote, IReadOnlyList<LineupModel> Lineup,
        string? Families = null, string? FamiliesWarning = null)
    {
        internal static DiscoverResponse From(ModelLineupReport report) => new(
            report.Entries.Where(e => e.Model.Listed).Select(e => e.Model.Id).ToArray(),
            report.Note ?? (report.Ok && report.Entries.Count == 0 ? "The provider listed no models." : null),
            report.Server,
            report.Loading,
            report.LoadingNote,
            report.Entries.Select(e => new LineupModel(
                e.Model.Id, e.Model.Kind, e.Model.Listed, e.Model.Loaded, e.Model.LoadedContextLength ?? e.Model.MaxContextLength, e.Model.ParametersBillions,
                e.Model.Capabilities, e.InCatalog, e.Recommended, e.Reason, e.Suggestion is null ? null : ModelLineup.ToConfigurationJson(e.Suggestion))
            {
                TierFrom = e.Suggestion is null ? null : e.TierFrom,
                Family = e.Family,
            }).ToArray(),
            report.Families,
            report.FamiliesWarning);
    }

    /// <summary>One model of a <see cref="DiscoverResponse"/>.</summary>
    /// <param name="Id">The provider's model id.</param>
    /// <param name="Kind">Chat, vision or embedding.</param>
    /// <param name="Listed">Whether the server serves it now.</param>
    /// <param name="Loaded">Whether it is in memory, when the server says.</param>
    /// <param name="ContextLength">The context it is loaded with, else the most it supports.</param>
    /// <param name="ParametersBillions">Its size, when known.</param>
    /// <param name="Capabilities">What the server says it can do.</param>
    /// <param name="InCatalog">The catalog model that already serves it from this connector.</param>
    /// <param name="Recommended">Whether adding it is a sensible default.</param>
    /// <param name="Reason">Why the suggestion looks the way it does.</param>
    /// <param name="Suggestion">The suggested catalog entry, in the configuration document's shape; <see langword="null"/> for embedding models.</param>
    public sealed record LineupModel(
        string Id, DiscoveredModelKind Kind, bool Listed, bool? Loaded, int? ContextLength, double? ParametersBillions, IReadOnlyList<string> Capabilities,
        string? InCatalog, bool Recommended, string Reason, JsonElement? Suggestion)
    {
        /// <summary>Where the suggested tier came from: <c>family</c> (the model-family table), <c>size</c> or <c>name</c>.</summary>
        public string? TierFrom { get; init; }

        /// <summary>The model family it was recognised as.</summary>
        public string? Family { get; init; }
    }

    /// <summary>The themes the picker offers.</summary>
    /// <param name="Directory">Where theme files are read from, if anywhere.</param>
    /// <param name="RequiredTokens">The tokens a theme file should declare.</param>
    /// <param name="Themes">Built-in themes first, then the files.</param>
    public sealed record ThemesResponse(string? Directory, IReadOnlyList<string> RequiredTokens, IReadOnlyList<DashboardTheme> Themes);

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
        IReadOnlyList<BudgetStatus> Budgets,
        IReadOnlyList<EndpointStatus> Endpoints,
        string Machine,
        ServiceStatus Service)
    {
        /// <summary>
        /// Whether the running configuration was applied without saving: a configuration file exists and holds something
        /// else, so a restart would bring that back. <c>POST api/config/save</c> keeps it; <c>POST api/config/revert</c> undoes it.
        /// </summary>
        public bool Unsaved { get; init; }
    }

    /// <summary>Embedding provider in use.</summary>
    public sealed record EmbeddingsStatus(
        string Provider, string? Model, string? Endpoint, int Dimensions, IReadOnlyList<string> Available, IReadOnlyList<EmbeddingModelStatus> Models,
        IndexStatus? Index, ClassificationCacheStatistics? Cache);

    /// <summary>The route index of the running configuration: utterances embedded and how long the warm-up took.</summary>
    public sealed record IndexStatus(int Utterances, int Dimensions, double BuildMilliseconds);

    /// <summary>A model a provider kind offers by name.</summary>
    public sealed record EmbeddingModelStatus(string Provider, string Name, string Description, float SuggestedThreshold, long DownloadBytes);

    /// <summary>A tier on the ladder.</summary>
    public sealed record TierStatus(int Level, string Name, string? Description);

    /// <summary>A catalog model and its health.</summary>
    public sealed record ModelStatus(
        string Name, string Connector, string? Model, int Tier, IReadOnlyList<string> Capabilities, bool IsLocal, bool Enabled, int Priority,
        int ContextWindow, decimal InputCostPerMillionTokens, decimal OutputCostPerMillionTokens, DateTimeOffset? CoolingDownUntil, string? ConnectorType)
    {
        /// <summary>The share of its input the model read from and wrote to its prompt cache lately, when there were enough turns.</summary>
        public PromptCacheShare? CacheShare { get; init; }

        /// <summary>The input price per million tokens with <see cref="CacheShare"/> at the cache prices: what cost estimates use.</summary>
        public decimal? EffectiveInputCostPerMillionTokens { get; init; }
    }

    /// <summary>A connector.</summary>
    public sealed record ConnectorStatus(string Name, string Type, string? Preset, string? Target, string ToolCalling, bool HasApiKey, int ModelCount);

    /// <summary>A route.</summary>
    public sealed record RouteStatus(string Name, int Tier, int Utterances, bool LocalOnly, IReadOnlyList<string> RequiredCapabilities, float Threshold);

    /// <summary>Gateway settings.</summary>
    public sealed record GatewayStatus(
        string Mode, IReadOnlyList<string> PathPrefixes, IReadOnlyList<string> AnthropicPathPrefixes, IReadOnlyList<string> ResponsesPathPrefixes,
        IReadOnlyDictionary<string, string> ModelAliases, string Strategy, IReadOnlyList<RateLimitStatus> RateLimits);

    /// <summary>Counts over the activity log.</summary>
    public sealed record ActivitySummary(
        int Total, int Served, int Failed, int Degraded,
        IReadOnlyDictionary<string, int> ByModel, IReadOnlyDictionary<string, int> ByTier, IReadOnlyDictionary<string, int> ByReason,
        IReadOnlyList<ConnectorLatency> Latency);

    /// <summary>How fast one connector answered the served requests in the activity log.</summary>
    /// <param name="Connector">The connector's name.</param>
    /// <param name="Kind">Its type (<c>OpenAICompatible</c>, <c>Cli/ClaudeCode</c>, …).</param>
    /// <param name="Requests">Served requests.</param>
    /// <param name="WithTools">How many of them offered tools.</param>
    /// <param name="FirstByteP50">Median time to the first byte of the reply, in milliseconds, from the start of the attempt that served it.</param>
    /// <param name="FirstByteP95">95th percentile time to the first byte.</param>
    /// <param name="TotalP50">Median time to the end of the reply.</param>
    public sealed record ConnectorLatency(string Connector, string? Kind, int Requests, int WithTools, double? FirstByteP50, double? FirstByteP95, double? TotalP50);
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
