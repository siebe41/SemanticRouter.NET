using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Mcp;

/// <summary>
/// The router's MCP tools. Each reuses the admin API's code for the same answer (<c>POST api/classify</c>,
/// <c>GET api/activity</c>, <c>GET api/usage</c>, <c>GET api/status</c>, <c>POST api/replay</c>) and needs the role that
/// endpoint needs: <see cref="Roles"/>. A caller is shown only the tools its role may call (<see cref="ForCallerAsync"/>).
/// </summary>
[McpServerToolType]
internal sealed class RouterMcpTools(
    IHttpContextAccessor http,
    RouterRuntimeProvider provider,
    IServiceProvider services,
    IOptions<RouterMcpOptions> options,
    TimeProvider time)
{
    public const string Classify = "classify";
    public const string ExplainRequest = "explain_request";
    public const string Usage = "usage";
    public const string Health = "health";
    public const string ProposeTuning = "propose_tuning";

    /// <summary>The most replay moves a proposal lists; the dashboard's What-if page shows them all.</summary>
    private const int MaxMovesListed = 20;

    /// <summary>As the admin API writes its answers (camelCase).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    /// <summary>The role each tool needs, as its admin API endpoint does.</summary>
    public static IReadOnlyDictionary<string, RouterDashboardRole> Roles { get; } = new Dictionary<string, RouterDashboardRole>(StringComparer.Ordinal)
    {
        [Classify] = RouterDashboardRole.Admin, // embeds the prompt, which can cost an embedding call (POST api/classify)
        [ExplainRequest] = RouterDashboardRole.ReadOnly, // what the Activity page shows (GET api/activity)
        [Usage] = RouterDashboardRole.Metrics, // figures (GET api/usage)
        [Health] = RouterDashboardRole.Metrics, // figures (GET api/status, without connectors' last errors for metrics)
        [ProposeTuning] = RouterDashboardRole.Admin, // replays prompts and records a suggestion (POST api/replay)
    };

    public const string Instructions =
        "Semantic Router routes each prompt to a local model, a subscription CLI or a paid API by meaning and difficulty. " +
        "Use classify to see where a prompt would go without sending it, explain_request with a request id (the X-Prompt-Route-Id " +
        "response header, or the Activity page) to see why a request went where it did, usage for tokens and cost, and health for " +
        "connectors that are failing or cooling down. propose_tuning, when offered, records a suggestion that a person applies " +
        "on the dashboard; it never changes the running configuration.";

    /// <summary>
    /// Keeps the router's tools a caller may not use out of this request's server: those its role does not reach, and
    /// <c>propose_tuning</c> unless <see cref="RouterMcpOptions.AllowProposals"/> is set. Stateless, so this runs on every
    /// request and a call to a tool left out is an unknown tool.
    /// </summary>
    public static Task ForCallerAsync(HttpContext context, McpServerOptions server, CancellationToken cancellationToken)
    {
        RouterDashboardRole? role = DashboardAccess.RoleOf(context);
        bool proposals = context.RequestServices.GetService<IOptions<RouterMcpOptions>>()?.Value.AllowProposals == true;
        if (server.ToolCollection is { } tools)
        {
            foreach (McpServerTool tool in tools.ToArray())
            {
                string name = tool.ProtocolTool.Name;
                if (Roles.TryGetValue(name, out RouterDashboardRole needed) && (role is not { } has || has < needed || (name == ProposeTuning && !proposals)))
                {
                    tools.Remove(tool);
                }
            }
        }

        return Task.CompletedTask;
    }

    [McpServerTool(Name = Classify, Title = "Where would this prompt go?", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Classifies a prompt as the router would route a first turn, without sending it to any model: the tier, the matched route " +
        "and its similarity, the route scores, the models in the order they would be tried with their estimated cost, and a one-line-per-field " +
        "decision. A leading callout such as @opus or @local applies as it would in a request. Needs the admin role, since it embeds the prompt.")]
    public async Task<ClassifyResult> ClassifyAsync(
        [Description("The prompt to route.")] string prompt,
        [Description("Optional model or alias, as a chat client would send it (auto, auto@profile, a virtual model).")] string? model = null,
        [Description("Optional routing profile to route under.")] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        HttpContext context = Require(Classify);
        (PromptClassificationResponse? response, string? problem) = await RouterDashboardEndpoints.ClassifyAsync(
            new ClassifyPromptRequest(prompt, model, profile), provider, services, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            throw new McpException(problem ?? "The prompt could not be classified.");
        }

        Audit(context, "MCP: classified a prompt");
        return new ClassifyResult { Classification = response, Decision = DecisionLine.Of(response) };
    }

    [McpServerTool(Name = ExplainRequest, Title = "Why did this request go there?", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Explains one routed request by its id (the X-Prompt-Route-Id response header, or the id on the dashboard's Activity page): " +
        "its activity entry as the Activity page shows it, the decision line, why the model was chosen and the feedback it drew. Only the " +
        "recent requests the Activity page holds can be explained.")]
    public ExplainResult Explain([Description("The request's activity id.")] long id)
    {
        Require(ExplainRequest);

        // The Activity page's own projection (GET api/activity): the in-memory recent entries, as that endpoint serializes
        // them, with the feedback kinds it lists. Stored history beyond them is not read here.
        RoutingActivityEntry entry = services.GetRequiredService<RoutingActivityLog>().Recent(int.MaxValue).FirstOrDefault(e => e.Id == id)
            ?? throw new McpException($"Request {id} is not among the recent requests the Activity page shows. Older requests are in the activity export.");
        IReadOnlyList<FeedbackKind> feedback = [.. services.GetRequiredService<RoutingFeedbackLog>().Recent(5000)
            .Where(f => f.EntryId == id).Select(f => f.Kind).Distinct()];
        return new ExplainResult
        {
            Entry = entry,
            Decision = DecisionLine.Of(entry, feedback),
            Selection = entry.Selection,
            Learned = entry.LearnedNote,
            Feedback = feedback,
        };
    }

    [McpServerTool(Name = Usage, Title = "Usage and cost", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Tokens, requests and cost at the current prices for a window, in total and by model, connector, route and client, with the " +
        "budgets and how much of each is spent.")]
    public UsageResult GetUsage(
        [Description("day, week or month (the budget periods, in the budget time zone), 30d, or 7d (the default: the last 7 days).")] string? window = null)
    {
        Require(Usage);
        RouterConfiguration configuration = provider.Current.Configuration;
        UsageOptions usage = configuration.Usage;
        TimeZoneInfo zone = BudgetGate.ZoneOf(usage);
        (DateTimeOffset since, DateTimeOffset until) = RouterDashboardEndpoints.UsageWindow(window, zone, time.GetUtcNow());
        IUsageLedger ledger = services.GetRequiredService<IUsageLedger>();
        IReadOnlyList<UsageGroup> By(UsageGrouping grouping) => ledger.SummarizeAtCurrentPrices(configuration, since, until, grouping, zone);
        return new UsageResult
        {
            Since = since,
            Until = until,
            ReferenceModel = usage.ReferenceModel,
            Totals = ledger.TotalAtCurrentPrices(configuration, since, until),
            ByModel = By(UsageGrouping.Model),
            ByConnector = By(UsageGrouping.Connector),
            ByRoute = By(UsageGrouping.Route),
            ByClient = By(UsageGrouping.Client),
            Budgets = services.GetRequiredService<BudgetGate>().Status(usage),
            Forecast = QuotaForecast(configuration, since, until), // #374
        };
    }

    [McpServerTool(Name = Health, Title = "Connector health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Each connector's state (ok, degraded, failing, down, idle, unused), why, its recent failures and latency, and the models cooling " +
        "down with when each becomes available again (a provider's usage-limit reset, when it said).")]
    public HealthResult GetHealth()
    {
        HttpContext context = Require(Health);
        RouterRuntime runtime = provider.Current;
        RouterConfiguration configuration = runtime.Configuration;
        IReadOnlyDictionary<string, DateTimeOffset> cooldowns = services.GetRequiredService<IModelHealth>().GetCooldowns();
        IReadOnlyList<EndpointStatus> endpoints = EndpointHealth.Build(
            configuration, cooldowns, services.GetRequiredService<RoutingActivityLog>().Recent(500), services.GetService<ConnectorIncidents>(), services.GetService<EndpointChecks>());

        // A connector's last error comes from the activity log's details, which can quote a reply: not for the metrics role.
        if (DashboardAccess.RoleOf(context) == RouterDashboardRole.Metrics)
        {
            endpoints = [.. endpoints.Select(e => e with { LastError = null })];
        }

        return new HealthResult
        {
            Version = runtime.Version,
            WarmedUp = runtime.WarmedUp,
            Endpoints = endpoints,
            CoolingDown = [.. configuration.Models
                .Where(m => cooldowns.ContainsKey(m.Name))
                .Select(m => new ModelCooldown { Model = m.Name, Connector = m.Connector, Until = cooldowns[m.Name] })
                .OrderBy(c => c.Until)],
        };
    }

    [McpServerTool(Name = ProposeTuning, Title = "Propose a tuning change", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Proposes one tuning change: the default similarity threshold, a route's threshold, an utterance added to a route, or the model a " +
        "route prefers. Replays the last days of requests (and the evaluation set) through the configuration with the change, and records the " +
        "change as a suggestion on the dashboard's Router learned page, where a person applies or dismisses it. It never changes the running " +
        "configuration.")]
    public async Task<ProposalResult> ProposeTuningAsync(
        [Description("What to change: DefaultThreshold, RouteThreshold, Utterance or RouteModel.")] TuningChangeKind kind,
        [Description("Why: shown with the suggestion.")] string reason,
        [Description("The route, for RouteThreshold, Utterance and RouteModel.")] string? route = null,
        [Description("The new similarity threshold (0 to 1), for DefaultThreshold and RouteThreshold; left out for RouteThreshold, the route uses the default.")] float? threshold = null,
        [Description("The utterance to add, for Utterance.")] string? utterance = null,
        [Description("The catalog model the route should prefer, for RouteModel.")] string? model = null,
        [Description("How many days of recent requests to replay, 1 to 90. Default 7.")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        HttpContext context = Require(ProposeTuning);
        if (!options.Value.AllowProposals)
        {
            throw new McpException("Proposals are turned off (SemanticRouter:Mcp:AllowProposals).");
        }

        if (days is < 1 or > ReplayApi.MaxDays)
        {
            throw new McpException($"'days' is between 1 and {ReplayApi.MaxDays}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new McpException("'reason' is required: it is shown with the suggestion.");
        }

        RouterConfiguration running = provider.Current.Configuration;
        (TuningChange? change, string? problem) = RouterTuner.Complete(running, new TuningChange
        {
            Kind = kind,
            Route = route,
            NewValue = threshold,
            Utterance = utterance,
            NewModel = model,
        });
        if (change is null)
        {
            throw new McpException(problem ?? "That change does not fit the running configuration.");
        }

        RouterConfiguration candidate = RouterTuner.WithChange(running, change);
        ReplayOutcome outcome = await ReplayApi.RunAsync(
            context.RequestServices, provider, services.GetRequiredService<RoutingActivityLog>(), services.GetRequiredService<IEvaluationSetStore>(),
            candidate, DashboardAccess.IsLocal(context), days, evaluation: true, cancellationToken).ConfigureAwait(false);
        if (outcome.Errors.Count > 0)
        {
            throw new McpException("The change would not be applied: " + string.Join(" ", outcome.Errors));
        }

        if (outcome.Report is not { } report)
        {
            throw new McpException(outcome.Problem ?? "The replay failed.");
        }

        string actor = DashboardAccess.ActorOf(context) ?? "unknown";
        string why = $"Proposed over MCP by {actor}: {Sentence(reason)} Replay of the last {Plural(days, "day")}: {Plural(report.Moved, "request")} of " +
            $"{report.Requests.Replayed} would move" +
            (report.Evaluation is { } labelled ? $"; {labelled.CorrectBefore} → {labelled.CorrectAfter} of {Plural(labelled.Cases, "labelled prompt")} routed to their tier." : ".");
        TuningChange proposal;
        try
        {
            proposal = services.GetRequiredService<RouterTuner>().Propose(change with
            {
                Why = why,
                Fixed = report.Evaluation is { } fixedSet ? Math.Max(0, fixedSet.CorrectAfter - fixedSet.CorrectBefore) : 0,
                Broken = report.Evaluation is { } brokenSet ? Math.Max(0, brokenSet.CorrectBefore - brokenSet.CorrectAfter) : 0,
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        Audit(context, $"MCP: proposed a tuning change: change {proposal.Id}, {proposal.PlainSummary}");
        return new ProposalResult
        {
            Proposal = new ProposedChange
            {
                Id = proposal.Id,
                Kind = proposal.Kind,
                Route = proposal.Route,
                Summary = proposal.Summary,
                Why = proposal.Why,
                State = proposal.State,
            },
            Replay = ReplaySummary.From(report, MaxMovesListed),
            Note = "Recorded as a suggestion on the dashboard's Router learned page; nothing was applied. A person applies or dismisses it there.",
        };
    }

    /// <summary>
    /// The quota forecast for the usage window: when each subscription or budget runs out at the current pace.
    /// <see langword="null"/> until the forecast exists. #374: fill this from the quota forecast when it lands.
    /// </summary>
    private static object? QuotaForecast(RouterConfiguration configuration, DateTimeOffset since, DateTimeOffset until)
    {
        _ = (configuration, since, until);
        return null;
    }

    /// <summary>The request, after checking the caller's role reaches <paramref name="tool"/> (the tool list already says so).</summary>
    private HttpContext Require(string tool)
    {
        HttpContext context = http.HttpContext ?? throw new McpException("The router's MCP tools answer over HTTP only.");
        return DashboardAccess.RoleOf(context) is { } role && role >= Roles[tool]
            ? context
            : throw new McpException($"The {tool} tool needs the {Roles[tool]} role.");
    }

    private void Audit(HttpContext context, string summary) =>
        services.GetService<RouterMcpServerMarker>()?.Gate?.Audit.Action(context, StatusCodes.Status200OK, summary);

    private static string Sentence(string text)
    {
        text = text.Trim();
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
