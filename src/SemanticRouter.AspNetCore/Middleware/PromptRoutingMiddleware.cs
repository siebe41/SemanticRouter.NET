using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Routes JSON chat requests: classifies the prompt, ranks the model catalog, and either annotates the request for
/// downstream endpoints or serves it through the chosen connectors. Accepts OpenAI chat-completions bodies on
/// <see cref="PromptRouterMiddlewareOptions.PathPrefixes"/> and Anthropic Messages bodies on
/// <see cref="PromptRouterMiddlewareOptions.AnthropicPathPrefixes"/>.
/// </summary>
internal sealed partial class PromptRoutingMiddleware
{
    private const int ReadChunkSize = 16 * 1024;

    private readonly RequestDelegate _next;
    private readonly RouterRuntimeProvider _runtime;
    private readonly PromptDispatcher _dispatcher;
    private readonly PromptRouterMetrics _metrics;
    private readonly RoutingActivityLog _activity;
    private readonly MemoryIndex _memory;
    private readonly MemoryExposureLog _exposure;
    private readonly IToolCallAffinity? _affinity;
    private readonly RetryTracker? _retries;
    private readonly ConversationTracker? _conversations;
    private readonly RoutingFeedbackLog? _feedback;
    private readonly RouterTuner? _tuner;
    private readonly AnswerJudge? _judge;
    private readonly ClientTurnPatterns? _clientTurns;
    private readonly ClientRateLimiter? _rateLimiter;
    private readonly CorrectionDetector? _corrections;
    private readonly ModelTrials? _trials;
    private readonly IWarmCacheStore? _warmCaches;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PromptRoutingMiddleware(
        RequestDelegate next,
        RouterRuntimeProvider runtime,
        PromptDispatcher dispatcher,
        PromptRouterMetrics metrics,
        RoutingActivityLog activity,
        MemoryIndex memory,
        MemoryExposureLog exposure,
        ILogger<PromptRoutingMiddleware> logger,
        IToolCallAffinity? affinity = null,
        RetryTracker? retries = null,
        ConversationTracker? conversations = null,
        RoutingFeedbackLog? feedback = null,
        RouterTuner? tuner = null,
        AnswerJudge? judge = null,
        ClientTurnPatterns? clientTurns = null,
        ClientRateLimiter? rateLimiter = null,
        CorrectionDetector? corrections = null,
        ModelTrials? trials = null,
        IWarmCacheStore? warmCaches = null,
        TimeProvider? time = null)
    {
        _next = next;
        _runtime = runtime;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _activity = activity;
        _memory = memory;
        _exposure = exposure;
        _affinity = affinity;
        _retries = retries;
        _conversations = conversations;
        _feedback = feedback;
        _tuner = tuner;
        _judge = judge;
        _clientTurns = clientTurns;
        _rateLimiter = rateLimiter;
        _corrections = corrections;
        _trials = trials;
        _warmCaches = warmCaches;
        _time = time ?? TimeProvider.System;
        _logger = logger;

        // Building the runtime validates the configuration, so a bad setup fails at start-up rather than on the first request.
        _ = runtime.Current;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        RouterRuntime runtime = _runtime.Current;
        PromptRouterMiddlewareOptions options = runtime.Configuration.Middleware;
        if (!ShouldIntercept(context.Request, runtime, out ChatProtocol protocol))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        GatewayAdmission gateway = GatewayAuthentication.Admit(context, runtime);
        if (gateway.Refused)
        {
            if (gateway.KeyMissing)
            {
                LogGatewayKeyMissing(_logger, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path);
            }
            else
            {
                LogGatewayRequestRefused(_logger, context.Request.Path, gateway.Detail!);
            }

            await ProtocolErrors.WriteAsync(context, protocol, gateway.Status, gateway.Title!, gateway.Detail!, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        string? client = gateway.Client;

        using Activity? routeSpan = RouterTelemetry.Source.StartActivity("semantic_router.route");
        routeSpan?.SetTag("semantic_router.protocol", protocol.ToString());
        routeSpan?.SetTag("semantic_router.client", client);
        ReadOnlyMemory<byte>? body = await ReadBodyAsync(context.Request, options.MaxRequestBodyBytes, context.RequestAborted).ConfigureAwait(false);
        if (body is not { } bodyBytes)
        {
            await ProtocolErrors.WriteAsync(
                context, protocol,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large",
                $"Prompt routing buffers at most {options.MaxRequestBodyBytes} bytes.",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // Give downstream handlers a fresh, seekable copy of the body we consumed.
        context.Request.Body = new MemoryStream(bodyBytes.ToArray(), writable: false);

        bool parsed;
        string? prompt;
        ChatCompletionRequest chat;
        IReadOnlyList<MemoryReference> memory;
        using (RouterTelemetry.Source.StartActivity("semantic_router.parse"))
        {
            parsed = TryParse(context, runtime, protocol, bodyBytes, out prompt, out chat, out memory);
        }

        if (!parsed)
        {
            LogNoPrompt(_logger, context.Request.Path);
            if (options.Mode == PromptRouterMode.Dispatch)
            {
                await ProtocolErrors.WriteAsync(
                    context, protocol,
                    StatusCodes.Status400BadRequest,
                    "No prompt found",
                    protocol switch
                    {
                        ChatProtocol.Anthropic => "Expected an Anthropic Messages JSON body with 'messages'.",
                        ChatProtocol.Responses => "Expected an OpenAI Responses JSON body with 'input'.",
                        _ => "Expected an OpenAI chat-completions JSON body with 'messages'.",
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        // An inline callout ("@opus …") routes as the person asked. The tag is taken out of every user message before
        // the request goes on, so the model never sees it; the body is only rewritten when there is one.
        Callout? callout = Callouts.Find(prompt, runtime);
        if (options.Callouts.Enabled && Callouts.Strip(bodyBytes, protocol, runtime) is { } stripped
            && TryParse(context, runtime, protocol, stripped, out string strippedPrompt, out ChatCompletionRequest strippedChat, out IReadOnlyList<MemoryReference> strippedMemory))
        {
            bodyBytes = stripped;
            context.Request.Body = new MemoryStream(stripped, writable: false);
            (prompt, chat, memory) = (strippedPrompt, strippedChat, strippedMemory);
        }

        long started = _time.GetTimestamp();
        DateTimeOffset routedAt = _time.GetUtcNow();
        ProfileSelection.ModelChoice modelChoice = ProfileSelection.ResolveModel(runtime, chat.Model);
        SelectionStrategy? strategy = modelChoice.Strategy;
        string? requestedModel = modelChoice.RequestedModel;
        ProfileSelection.Choice profile = ProfileSelection.Choose(context.Request, runtime, chat, client, modelChoice.Profile);
        if (profile.Error is { } profileError)
        {
            await ProtocolErrors.WriteAsync(context, protocol, StatusCodes.Status400BadRequest, "Unknown routing profile", profileError, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        // A client over its rate limit is turned away before anything is spent on it. A client without a gateway key is
        // counted by its address.
        string rateClient = client ?? context.Connection.RemoteIpAddress?.ToString() ?? "local";
        bool remoteClient = !DashboardAccess.IsLocal(context);
        (string Name, RateLimitOptions Limit)? rateLimit = _rateLimiter is null ? null : ClientRateLimiter.LimitFor(options, client, remoteClient);
        if (rateLimit is { } limit)
        {
            RateLimitDecision admission = _rateLimiter!.TryAdmit(rateClient, limit.Limit, chat.EstimatedInputTokens, remoteClient);
            if (!admission.Admitted)
            {
                await RejectRateLimitedAsync(context, protocol, client, rateClient, chat, prompt, profile.Profile, admission).ConfigureAwait(false);
                return;
            }
        }

        if (requestedModel is not null
            && !runtime.Router.Models.Any(m => m.Enabled && string.Equals(m.Name, requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            // A vendor model name the catalog does not know (e.g. Claude Code's defaults): route as "auto".
            requestedModel = null;
        }

        // Only a request that carries an earlier answer continues a conversation; a first turn starts it afresh (two
        // separate conversations can open with the same message).
        // An assistant turn may hold only tool calls (no text), so look at the turns, not the flattened messages.
        bool firstTurn = !chat.Turns.Any(t => t.Role == "assistant") && !chat.Messages.Any(m => m.Role == "assistant");

        // A request the client makes for itself (a title, a compaction summary, a warm-up) by its fixed wording, or a
        // session it resumed from a summary.
        ClientTurnKind? clientTurn = options.Housekeeping.Enabled && requestedModel is null && !chat.IsToolResultContinuation
            ? ClientTurns.Recognise(chat, prompt, firstTurn, options.Housekeeping, _clientTurns?.Current)
            : null;
        bool housekeeping = clientTurn is { } turn && turn != ClientTurnKind.Resumed;

        // The conversation this request continues: for its floor and model (conversation routing) and for a callout
        // made earlier in it. Not for a client's own side tasks (auto:cheapest, e.g. Claude Code's Haiku calls, or a
        // recognised title or compaction request).
        ConversationRoutingOptions conversation = options.Conversation;
        bool feedback = options.Feedback.Enabled && _feedback is not null;
        string retryClient = client ?? context.Connection.RemoteIpAddress?.ToString() ?? "local";
        string? conversationKey = (conversation.Enabled || options.Callouts.Enabled || feedback) && _conversations is not null
            && modelChoice.Strategy != SelectionStrategy.CheapestCapable && !housekeeping
            ? ConversationTracker.Identify(retryClient, chat, SessionId(context.Request, conversation))
            : null;

        if (conversationKey is not null && firstTurn)
        {
            _conversations!.Forget(conversationKey);
            _warmCaches?.Forget(conversationKey);
        }

        // A conversation that was kept local stays local on every later turn: follow-ups that match no local-only route,
        // tool loops, and the client's own compaction and title requests about it. A first turn starts afresh, but not a
        // title request sent alongside it.
        string? localKey = _conversations is not null ? ConversationTracker.LocalKey(retryClient, chat) : null;
        if (localKey is not null && firstTurn && clientTurn is null)
        {
            _conversations!.ForgetLocalOnly(localKey);
        }

        bool keptLocal = localKey is not null && !(firstTurn && clientTurn is null) && _conversations!.IsLocalOnly(localKey);

        // The decision this turn may judge (feedback): the conversation's previous turn.
        DecisionRecord? previousTurn = feedback && conversationKey is not null && !firstTurn ? _conversations!.Last(conversationKey, conversation) : null;

        // Explicit beats automatic: this turn's callout, or one made earlier in the conversation, until "@auto".
        Callout? choice = callout;
        bool remembered = false;
        if (conversationKey is not null && options.Callouts.Enabled)
        {
            if (callout is not null)
            {
                _conversations!.Choose(conversationKey, callout.Clear ? null : callout);
            }
            else if (_conversations!.Choice(conversationKey, conversation) is { } earlier)
            {
                (choice, remembered) = (earlier, true);
            }
        }

        bool explicitTurn = choice is { Clear: false };
        if (choice is not null)
        {
            (requestedModel, strategy) = choice.Choose(requestedModel, strategy);
        }

        string? affinityModel = options.PinToolCallingTurns && chat.IsToolResultContinuation && requestedModel is null
            ? _affinity?.Lookup(chat.AnsweredToolUseIds)
            : null;

        // A prompt the same client repeats soon after it was answered: feedback on that answer, and one tier up when
        // retry escalation is on.
        RetryEscalationOptions retry = options.RetryEscalation;
        int highestTier = runtime.Router.Models.Where(m => m.Enabled).Select(m => m.Tier).DefaultIfEmpty(0).Max();
        bool retryable = (retry.Enabled || feedback) && _retries is not null && requestedModel is null && !chat.IsToolResultContinuation && clientTurn is null;
        DecisionRecord? retried = retryable ? _retries!.Previous(retryClient, prompt, retry.Window) : null;
        int? minimumTier = retry.Enabled && retried is not null && retried.Tier < highestTier ? retried.Tier + 1 : null;

        // Route by the conversation: keep the tier recent turns needed and the model that answered them. Not for
        // tool-result turns (pinned above), explicit choices, or a conversation's first turn.
        bool classifiedTurn = requestedModel is null && !chat.IsToolResultContinuation;
        bool continued = conversation.Enabled && conversationKey is not null && classifiedTurn && !explicitTurn && !firstTurn;
        ConversationContext conversationContext = continued ? _conversations!.Lookup(conversationKey!, prompt, conversation) : default;

        // The prompt caches earlier turns left on models: staying on one can cost less than moving.
        IReadOnlyList<WarmCache> warmCaches = continued && conversation.StickToModel && _warmCaches is not null ? _warmCaches.Lookup(conversationKey!) : [];

        bool localOnly = profile.LocalOnly || choice is { LocalOnly: true } || keptLocal
            || (profile.Profile is { } named && runtime.Router.Profiles.TryGetValue(named, out RoutingProfile? rules) && rules.LocalOnly);
        var routingRequest = new PromptRoutingRequest
        {
            ConversationFloor = conversationContext.Floor,
            PreferredModel = conversationContext.PreferredModel,
            WarmCaches = warmCaches,
            MinimumTier = minimumTier,
            ExplicitTier = choice?.ExplicitTier,
            Profile = profile.Profile,
            LocalOnly = localOnly,
            EmbeddingsAllowed = !localOnly || runtime.Configuration.Embeddings.KeepsPromptsLocal,
            Prompt = prompt,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            AdditionalCapabilities = ImpliedCapabilities(chat),
            RequestedModel = requestedModel,
            Strategy = strategy,
            AffinityModel = affinityModel,
            Classification = clientTurn is { } kind && !explicitTurn ? ClientTurns.Decide(kind, options.Housekeeping, chat.EstimatedInputTokens) : null,
        };

        RoutingPlan plan = await RouteAsync(runtime, routingRequest, options.FailOpen, context.RequestAborted).ConfigureAwait(false);
        if (localKey is not null && plan.Requirements.LocalOnly)
        {
            _conversations!.MarkLocalOnly(localKey);
        }

        (plan, string? calloutNote) = Callouts.Settle(choice, plan, remembered);

        // A cheaper model on trial for this route tries a share of its plain matches: the first turn of a conversation
        // the router decided itself (not escalated, explicit, held by the conversation, a tool result or a client's
        // own request). The plan keeps the usual models behind it, and leaves it out when it cannot serve this request.
        long? trial = null;
        if (_trials is not null && classifiedTurn && !explicitTurn && clientTurn is null && firstTurn && !plan.Sticky
            && plan.Classification is { Reason: RoutingReason.SemanticMatch, RouteName: { } trialRoute }
            && _trials.Pick(trialRoute) is { } pick)
        {
            RoutingPlan tried = runtime.Router.Plan(plan.Classification, routingRequest with { TrialModel = pick.Model });
            if (tried.Trial is not null)
            {
                (plan, trial) = (tried, pick.Id);
                _trials.Counted(pick.Id);
            }
        }

        PromptClassificationResult result = plan.Classification;

        context.Features.Set<IPromptRoutingFeature>(new PromptRoutingFeature(prompt, plan));
        if (options.EmitResponseHeaders)
        {
            WriteHeaders(context.Response.Headers, options.HeaderPrefix, plan);
            if (calloutNote is not null && calloutNote.All(char.IsAscii))
            {
                context.Response.Headers[options.HeaderPrefix + "-Callout"] = calloutNote;
            }
        }

        _metrics.RecordDecision(result);
        LogDecision(
            _logger, result.RequiredTier, result.Reason, result.RouteName ?? "none",
            result.Similarity ?? float.NaN, result.Complexity.Score,
            plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : "none", result.Elapsed.TotalMilliseconds);

        TuningChange? learned = plan.RouteModel is not null && result.RouteName is { } learnedRoute ? _tuner?.ExplainModel(learnedRoute) : _tuner?.Explain(result);
        RoutingActivityEntry entry = _activity.Add(new RoutingActivityEntry
        {
            Id = _activity.Reserve(),
            TraceId = Activity.Current?.TraceId.ToHexString(),
            Timestamp = DateTimeOffset.UtcNow,
            Protocol = protocol,
            Path = context.Request.Path.Value ?? string.Empty,
            Client = client,
            RequestedModel = chat.Model,
            PromptPreview = PromptPreviews.Of(prompt, runtime.Configuration.State.StorePrompts),
            Tier = result.RequiredTier,
            Reason = result.Reason,
            Route = result.RouteName,
            Similarity = result.Similarity,
            Complexity = Math.Round(result.Complexity.Score, 3),
            Cached = result.Cached,
            Profile = profile.Profile,
            Project = profile.Project is { Length: > 0 } project ? project[..Math.Min(project.Length, 120)] : null,
            Signals = profile.Signals.Count > 0 ? string.Join(", ", profile.Signals) : null,
            HasTools = chat.HasTools,
            ToolContinuation = chat.IsToolResultContinuation,
            LocalOnly = plan.Requirements.LocalOnly,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            PlannedModel = plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : null,
            Conversation = conversationKey?[..12],
            Callout = calloutNote,
            Sticky = plan.Sticky,
            Unreliable = plan.Unreliable.Count > 0 ? string.Join(", ", plan.Unreliable) : null,
            Learned = learned?.Id,
            LearnedNote = learned is null ? null : $"learned {learned.Timestamp.ToString("d MMM", CultureInfo.InvariantCulture)}: {learned.Summary}",
            ClassificationMilliseconds = Math.Round(result.Elapsed.TotalMilliseconds, 2),
            Memory = memory,
            Trial = trial,
            Selection = plan.SelectionNote,
        });

        if (feedback)
        {
            RecordFeedback(runtime, entry, client, chat, prompt, conversationKey, previousTurn, retried, callout, classifiedTurn, highestTier);
        }

        routeSpan?.SetTag("semantic_router.request_id", entry.Id);
        routeSpan?.SetTag("semantic_router.tier", result.RequiredTier);
        routeSpan?.SetTag("semantic_router.reason", result.Reason.ToString());
        routeSpan?.SetTag("semantic_router.route", result.RouteName);
        routeSpan?.SetTag("semantic_router.planned_model", entry.PlannedModel);
        if (options.EmitResponseHeaders)
        {
            // Matches the activity entry (dashboard, export), so a client can quote it when something went wrong.
            context.Response.Headers[options.HeaderPrefix + "-Id"] = entry.Id.ToString(CultureInfo.InvariantCulture);
        }

        // A sample of the answers cheap models give to decisions the router made itself is graded (the judge): keep a copy.
        bool automatic = requestedModel is null && !explicitTurn;
        Stream responseBody = context.Response.Body;
        AnswerCapture? answer = options.Mode == PromptRouterMode.Dispatch && _judge is not null && classifiedTurn && clientTurn is null
            && _judge.Sample(runtime, plan, automatic, remoteClient)
            ? new AnswerCapture(responseBody, AnswerJudge.CaptureLimit)
            : null;

        using (_logger.BeginScope(new Dictionary<string, object> { ["RouteRequestId"] = entry.Id }))
        {
            if (answer is not null)
            {
                context.Response.Body = answer;
                try
                {
                    await _dispatcher.DispatchAsync(context, runtime, plan, chat, bodyBytes, entry).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = responseBody;
                }
            }
            else if (options.Mode == PromptRouterMode.Dispatch)
            {
                await _dispatcher.DispatchAsync(context, runtime, plan, chat, bodyBytes, entry).ConfigureAwait(false);
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }

        entry.TotalMilliseconds = Math.Round(_time.GetElapsedTime(started).TotalMilliseconds, 1);
        routeSpan?.SetTag("semantic_router.outcome", entry.Outcome);
        routeSpan?.SetTag("semantic_router.served_by", entry.ServedBy);
        if (entry.Outcome == "failed")
        {
            routeSpan?.SetStatus(ActivityStatusCode.Error, "no model available");
        }

        string? answeredBy = entry.ServedBy ?? (options.Mode == PromptRouterMode.Annotate ? entry.PlannedModel : null);
        if (retryable && answeredBy is not null
            && runtime.Router.Models.FirstOrDefault(m => string.Equals(m.Name, answeredBy, StringComparison.OrdinalIgnoreCase)) is { } answering)
        {
            _retries!.Answered(retryClient, prompt, new DecisionRecord(entry.Id, answering.Tier, result.RouteName, answeredBy, automatic), retry.Window);
        }

        if (conversationKey is not null && answeredBy is not null && !chat.IsToolResultContinuation)
        {
            _conversations!.Decided(conversationKey, new DecisionRecord(entry.Id, result.RequiredTier, result.RouteName, answeredBy, automatic));
        }

        if (feedback && automatic && entry.Outcome == "served" && entry.OutputTokens == 0)
        {
            AddFeedback(entry, FeedbackKind.EmptyReply, new DecisionRecord(entry.Id, result.RequiredTier, result.RouteName, answeredBy, automatic),
                client, Math.Min(result.RequiredTier + 1, Math.Max(highestTier, result.RequiredTier)), "no output tokens");
        }

        if (feedback && automatic && entry.Notes is { } notes && notes.Contains("malformed tool call", StringComparison.OrdinalIgnoreCase))
        {
            AddFeedback(entry, FeedbackKind.ToolErrors, new DecisionRecord(entry.Id, result.RequiredTier, result.RouteName, answeredBy, automatic),
                client, Math.Min(result.RequiredTier + 1, Math.Max(highestTier, result.RequiredTier)), notes.Length <= 160 ? notes : notes[..160] + "…");
        }

        if (conversationKey is not null && answeredBy is not null)
        {
            if (classifiedTurn)
            {
                _conversations!.Record(
                    conversationKey, result.RequiredTier, result.Reason == RoutingReason.ConversationFloor, conversationContext.FollowUp, answeredBy, conversation);
            }
            else
            {
                _conversations!.Touch(conversationKey, answeredBy, conversation);
            }
        }

        if (conversationKey is not null && entry.ServedBy is { } servedBy && _warmCaches is not null && conversation.Enabled && conversation.StickToModel)
        {
            RememberCache(conversationKey, servedBy, entry, routedAt, CacheLifetime(runtime, servedBy, chat) ?? conversation.CacheLifetime);
        }

        if (rateLimit is not null && (entry.InputTokens is not null || entry.OutputTokens is not null))
        {
            // Admission charged the estimate; charge the rest now that the real usage is known.
            _rateLimiter!.Charge(rateClient, (long)(entry.InputTokens ?? chat.EstimatedInputTokens) + (entry.OutputTokens ?? 0) - chat.EstimatedInputTokens);
        }

        RecordMemoryExposure(runtime, entry);
        _activity.Complete(entry);
        if (answer is not null)
        {
            using (answer)
            {
                _judge!.Enqueue(runtime, entry, prompt, protocol, answer, highestTier, remoteClient);
            }
        }
    }

    /// <summary>
    /// How long the cache entries the turn wrote on <paramref name="model"/> live, when its connector tells
    /// (<see cref="PromptCacheLifetime"/>); otherwise <see cref="ConversationRoutingOptions.CacheLifetime"/> applies.
    /// </summary>
    private static TimeSpan? CacheLifetime(RouterRuntime runtime, string model, ChatCompletionRequest chat) =>
        runtime.Router.Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)) is { } served
        && runtime.Configuration.Connectors.TryGetValue(served.Connector, out ConnectorOptions? connector)
            ? PromptCacheLifetime.Of(chat, connector)
            : null;

    /// <summary>
    /// Records the prompt cache the served turn left on its model: what it read and wrote, warm for the cache's lifetime
    /// from the turn's start (a read refreshes it). A turn that reported usage without touching the cache means the
    /// model's cache is cold, or that it does not cache.
    /// </summary>
    private void RememberCache(string conversation, string model, RoutingActivityEntry entry, DateTimeOffset started, TimeSpan lifetime)
    {
        int cached = (entry.CacheReadTokens ?? 0) + (entry.CacheCreationTokens ?? 0);
        if (cached > 0)
        {
            _warmCaches!.Remember(conversation, new WarmCache(model, cached, started + lifetime));
        }
        else if (entry.InputTokens is not null)
        {
            _warmCaches!.Forget(conversation, model);
        }
    }

    /// <summary>
    /// Feedback this request gives on earlier decisions: a retry of an answered prompt, a correction of the previous
    /// turn, a callout right after the router chose, or failing tool calls on consecutive tool turns. Only decisions the
    /// router made itself are judged; an explicit choice is not the router's to learn from.
    /// </summary>
    private void RecordFeedback(
        RouterRuntime runtime, RoutingActivityEntry entry, string? client, ChatCompletionRequest chat, string prompt, string? conversationKey,
        DecisionRecord? previousTurn, DecisionRecord? retried, Callout? callout, bool classifiedTurn, int highestTier)
    {
        int Above(int tier) => Math.Min(tier + 1, Math.Max(highestTier, tier));

        if (retried is { Automatic: true })
        {
            AddFeedback(entry, FeedbackKind.Retried, retried, client, Above(retried.Tier), $"sent again as request {entry.Id}");
        }

        if (previousTurn is not { Automatic: true })
        {
            return;
        }

        if (callout is { Clear: false })
        {
            int? asked = callout switch
            {
                { Model: { } model } => runtime.Router.Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase))?.Tier,
                { Tier: { } tier } => tier,
                { Strategy: SelectionStrategy.MostCapable } => highestTier,
                _ => null,
            };
            AddFeedback(entry, FeedbackKind.Overridden, previousTurn, client, asked > previousTurn.Tier ? asked : null, $"asked for {callout.Tag} in request {entry.Id}");
        }
        else if (classifiedTurn && callout is null && Corrections.Matches(prompt))
        {
            AddFeedback(entry, FeedbackKind.Corrected, previousTurn, client, Above(previousTurn.Tier), $"corrected in request {entry.Id}");
        }
        else if (classifiedTurn && callout is null && (!entry.LocalOnly || runtime.Configuration.Embeddings.KeepsPromptsLocal))
        {
            // Other wording or another language: compared by meaning, after the request has gone on.
            _corrections?.CheckInBackground(prompt, () =>
                AddFeedback(entry, FeedbackKind.Corrected, previousTurn, client, Above(previousTurn.Tier), $"corrected in request {entry.Id} (by meaning)"));
        }

        if (chat.IsToolResultContinuation && conversationKey is not null)
        {
            bool failed = chat.Turns.LastOrDefault(t => t.Role == "user")?.Blocks.Any(b => b is ToolResultBlock { IsError: true }) == true;
            if (_conversations!.ToolTurn(conversationKey, failed) == 2)
            {
                AddFeedback(entry, FeedbackKind.ToolErrors, previousTurn, client, Above(previousTurn.Tier), "tool calls failed on 2 turns in a row");
            }
        }
    }

    private void AddFeedback(RoutingActivityEntry entry, FeedbackKind kind, DecisionRecord about, string? client, int? neededTier, string detail) =>
        _feedback!.Add(new RoutingFeedback
        {
            Timestamp = entry.Timestamp,
            EntryId = about.EntryId,
            Kind = kind,
            Conversation = entry.Conversation,
            Client = client,
            Route = about.Route,
            Model = about.Model,
            Tier = about.Tier,
            NeededTier = neededTier,
            Detail = detail,
        });

    /// <summary>Counts the request's memory notes against every model it was sent to (Dispatch mode only: in Annotate mode the router does not know).</summary>
    private void RecordMemoryExposure(RouterRuntime runtime, RoutingActivityEntry entry)
    {
        if (entry.Memory.Count == 0)
        {
            return;
        }

        foreach (string name in entry.SentTo.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ModelDefinition? model = runtime.Router.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            _exposure.Record(entry.Memory, name, model?.IsLocal ?? false, entry.Timestamp);
        }
    }

    /// <summary>The client's session id from <see cref="ConversationRoutingOptions.SessionHeader"/>, when it sends one.</summary>
    private static string? SessionId(HttpRequest request, ConversationRoutingOptions conversation) =>
        conversation.SessionHeader is { Length: > 0 } header && request.Headers[header].FirstOrDefault() is { Length: > 0 } session ? session : null;

    /// <summary>
    /// Applies the configured model aliases to the client's model name: a key without <c>*</c> that names it exactly,
    /// else the first glob that matches. Routing aliases (<c>auto…</c>, <c>semantic-router</c> and its Claude-style
    /// names) and catalog model names are returned as they are, so an explicit choice always beats a pattern.
    /// </summary>
    internal static string? MapModel(RouterRuntime runtime, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        string trimmed = model.Trim();
        if (trimmed.StartsWith(ModelAliases.Auto, StringComparison.OrdinalIgnoreCase)
            || ModelAliases.IsRouterName(trimmed)
            || runtime.Router.Models.Any(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return model;
        }

        foreach ((string pattern, string target) in runtime.ModelAliases)
        {
            if (!pattern.Contains('*', StringComparison.Ordinal) && string.Equals(pattern, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        foreach ((string pattern, string target) in runtime.ModelAliases)
        {
            if (GlobMatches(pattern, model))
            {
                return target;
            }
        }

        return model;
    }

    internal static bool GlobMatches(string pattern, string value)
    {
        // Simple '*' glob, case-insensitive.
        string[] parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        int position = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            int index = i == 0
                ? (value.StartsWith(part, StringComparison.OrdinalIgnoreCase) ? 0 : -1)
                : value.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (i == parts.Length - 1 && !value.EndsWith(part, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            position = index + part.Length;
        }

        return true;
    }

    /// <summary>
    /// The name of the gateway key the client presented, or <see langword="null"/>. Keys are compared in constant time
    /// and are only read for this comparison: they are never logged or forwarded.
    /// </summary>
    internal static string? IdentifyClient(HttpRequest request, IReadOnlyDictionary<string, string> keys)
    {
        string? bearer = request.Headers.Authorization;
        bearer = bearer is not null && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..].Trim() : null;
        string? apiKey = request.Headers["x-api-key"];
        string? match = null;
        foreach ((string key, string name) in keys)
        {
            byte[] expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            foreach (string? presented in new[] { bearer, apiKey })
            {
                if (!string.IsNullOrEmpty(presented)
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        expected, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented))))
                {
                    match ??= name;
                }
            }
        }

        return match;
    }

    private static string[] ImpliedCapabilities(ChatCompletionRequest chat) =>
        (chat.HasTools, chat.HasImages) switch
        {
            (true, true) => ["tools", "vision"],
            (true, false) => ["tools"],
            (false, true) => ["vision"],
            _ => [],
        };

    private static bool ShouldIntercept(HttpRequest request, RouterRuntime runtime, out ChatProtocol protocol)
    {
        protocol = ChatProtocol.OpenAI;
        if (!HttpMethods.IsPost(request.Method) || !request.HasJsonContentType())
        {
            return false;
        }

        foreach (PathString prefix in runtime.AnthropicPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out PathString remaining))
            {
                if (remaining.HasValue && remaining.Value!.EndsWith("/count_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    // Token counting is served by its own endpoint and must not be routed.
                    return false;
                }

                protocol = ChatProtocol.Anthropic;
                return true;
            }
        }

        foreach (PathString prefix in runtime.ResponsesPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out PathString rest) && (!rest.HasValue || rest.Value == "/"))
            {
                protocol = ChatProtocol.Responses;
                return true;
            }
        }

        if (runtime.OpenAIPrefixes.Length == 0)
        {
            return runtime.Configuration.Middleware.Mode != PromptRouterMode.Dispatch;
        }

        foreach (PathString prefix in runtime.OpenAIPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        int initialCapacity = (int)Math.Min(request.ContentLength ?? ReadChunkSize, limit);
        var buffer = new ArrayBufferWriter<byte>(Math.Max(initialCapacity, 256));
        while (true)
        {
            Memory<byte> chunk = buffer.GetMemory(ReadChunkSize);
            int read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenMemory;
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                return null;
            }
        }
    }

    private bool TryParse(
        HttpContext context, RouterRuntime runtime, ChatProtocol protocol, ReadOnlyMemory<byte> body,
        out string prompt, out ChatCompletionRequest chat, out IReadOnlyList<MemoryReference> memory)
    {
        prompt = string.Empty;
        chat = new ChatCompletionRequest();
        memory = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string? extracted = runtime.Extractor.ExtractPrompt(context, document.RootElement);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return false;
            }

            prompt = extracted;
            chat = ChatCompletionRequest.Parse(document.RootElement, protocol);
            if (runtime.Configuration.Memory.TrackSharing)
            {
                _memory.EnsureFresh();
                memory = _memory.Match(document.RootElement);
            }

            return true;
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            return false;
        }
    }

    private async ValueTask<RoutingPlan> RouteAsync(RouterRuntime runtime, PromptRoutingRequest request, bool failOpen, CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.Router.RouteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (failOpen && !cancellationToken.IsCancellationRequested)
        {
            int fallbackTier = runtime.Configuration.FallbackTier;
            if (ex is HttpRequestException or InvalidOperationException)
            {
                // An embedding model that could not be downloaded or a server that does not answer: the message says
                // what to do, on every request until it is fixed, so without a stack trace each time.
                LogClassificationProblem(_logger, fallbackTier, ex.Message);
            }
            else
            {
                LogClassificationFailed(_logger, fallbackTier, ex);
            }

            // Unclassified, the prompt may be one a local-only route would have kept on this machine, so while any
            // route is local-only the fallback is too: it goes to a local model or fails, never to the cloud.
            bool localOnly = runtime.Configuration.Routes.Any(r => r.LocalOnly);
            return runtime.Router.Plan(
                PromptClassificationResult.Bypass(fallbackTier, RoutingReason.ClassifierFailure) with { LocalOnly = localOnly }, request);
        }
    }

    private static void WriteHeaders(IHeaderDictionary headers, string prefix, RoutingPlan plan)
    {
        PromptClassificationResult result = plan.Classification;
        headers[prefix] = result.RouteName ?? "none";
        headers[prefix + "-Tier"] = result.RequiredTier.ToString(CultureInfo.InvariantCulture);
        headers[prefix + "-Reason"] = result.Reason.ToString();
        headers[prefix + "-Complexity"] = result.Complexity.Score.ToString("0.000", CultureInfo.InvariantCulture);
        headers[prefix + "-Model"] = plan.Candidates.Count > 0 ? plan.Candidates[0].Model.Name : "none";
        if (plan.SelectionNote is { } selection && selection.All(char.IsAscii))
        {
            headers[prefix + "-Selection"] = selection;
        }

        if (result.Similarity is { } similarity)
        {
            headers[prefix + "-Similarity"] = similarity.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        if (plan.Requirements.Profile is { } profile)
        {
            headers[prefix + "-Profile"] = profile;
        }

        if (plan.Requirements.LocalOnly)
        {
            headers[prefix + "-Local-Only"] = "true";
        }

        if (plan.Sticky)
        {
            headers[prefix + "-Sticky"] = "true";
        }

        if (plan.Unreliable.Count > 0 && string.Join(", ", plan.Unreliable) is { } unreliable && unreliable.All(char.IsAscii))
        {
            headers[prefix + "-Unreliable"] = unreliable;
        }
    }

    internal static Task WriteProblemAsync(HttpContext context, ChatProtocol protocol, int statusCode, string title, string detail) =>
        ProtocolErrors.WriteAsync(context, protocol, statusCode, title, detail, context.RequestAborted);

    /// <summary>Answers a client over its rate limit with a 429 and <c>Retry-After</c> in its protocol, and records it in the activity log.</summary>
    private async Task RejectRateLimitedAsync(
        HttpContext context, ChatProtocol protocol, string? client, string rateClient, ChatCompletionRequest chat, string prompt, string? profile, RateLimitDecision admission)
    {
        int seconds = (int)Math.Ceiling(admission.RetryAfter.TotalSeconds);
        string who = client is not null ? $"Client '{client}'" : $"This client ({rateClient})";
        string detail = $"{who} is over {admission.Detail}; retry in {seconds.ToString(CultureInfo.InvariantCulture)} s.";
        LogRateLimited(_logger, rateClient, admission.Detail!, seconds);
        RoutingActivityEntry entry = _activity.Add(new RoutingActivityEntry
        {
            Id = _activity.Reserve(),
            TraceId = Activity.Current?.TraceId.ToHexString(),
            Timestamp = DateTimeOffset.UtcNow,
            Protocol = protocol,
            Path = context.Request.Path.Value ?? string.Empty,
            Client = client,
            RequestedModel = chat.Model,
            PromptPreview = PromptPreviews.Of(prompt, _runtime.Current.Configuration.State.StorePrompts),
            Reason = RoutingReason.RateLimited,
            Profile = profile,
            HasTools = chat.HasTools,
            ToolContinuation = chat.IsToolResultContinuation,
            EstimatedInputTokens = chat.EstimatedInputTokens,
            Outcome = "rate-limited",
            Detail = detail,
        });
        _activity.Complete(entry);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await ProtocolErrors.WriteAsync(context, protocol, StatusCodes.Status429TooManyRequests, "Rate limit reached", detail, context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit: {Client} is over {Limit}; asked to retry in {Seconds} s.")]
    private static partial void LogRateLimited(ILogger logger, string client, string limit, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a gateway request from {Remote} to {Path}: no valid gateway key.")]
    private static partial void LogGatewayKeyMissing(ILogger logger, string remote, PathString path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a gateway request to {Path}: {Reason}")]
    private static partial void LogGatewayRequestRefused(ILogger logger, PathString path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No prompt found in JSON request to {Path}; passing through.")]
    private static partial void LogNoPrompt(ILogger logger, PathString path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request body is not valid JSON; prompt routing skipped.")]
    private static partial void LogInvalidJson(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prompt classification failed; failing open to tier {Tier}.")]
    private static partial void LogClassificationFailed(ILogger logger, int tier, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prompt classification failed; failing open to tier {Tier}: {Problem}")]
    private static partial void LogClassificationProblem(ILogger logger, int tier, string problem);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Prompt needs tier {Tier} ({Reason}); route={Route} similarity={Similarity:0.000} complexity={Complexity:0.000} first choice={Model} in {ElapsedMs:0.0} ms")]
    private static partial void LogDecision(
        ILogger logger, int tier, RoutingReason reason, string route, float similarity, double complexity, string model, double elapsedMs);
}
