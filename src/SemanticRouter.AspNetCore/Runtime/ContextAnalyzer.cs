using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What a request's input is made of, in estimated tokens, and the signs that it carries more context than it needs
/// (<see cref="ContextCostOptions"/>). Kept with the request's activity entry.
/// </summary>
public sealed record ContextBreakdown
{
    /// <summary>The whole input: the sum of the system prompt, tool definitions, injected blocks, history, tool results, images and the new ask.</summary>
    public int TotalTokens { get; init; }

    /// <summary>System and developer messages.</summary>
    public int SystemTokens { get; init; }

    /// <summary>The tools offered, together.</summary>
    public int ToolDefinitionTokens { get; init; }

    /// <summary>How many tools were offered.</summary>
    public int ToolCount { get; init; }

    /// <summary>
    /// Of <see cref="ToolDefinitionTokens"/>, the tools never called in the conversation so far; 0 until it has a few
    /// assistant turns to tell from.
    /// </summary>
    public int UnusedToolDefinitionTokens { get; init; }

    /// <summary>Blocks the client injects into user messages (<c>&lt;system-reminder&gt;</c> and the other tags ignored when classifying).</summary>
    public int InjectedTokens { get; init; }

    /// <summary>Earlier turns: their text, tool calls and thinking (tool results and injected blocks are counted on their own).</summary>
    public int HistoryTokens { get; init; }

    /// <summary>Tool results, in every turn.</summary>
    public int ToolResultTokens { get; init; }

    /// <summary>Images, in every turn.</summary>
    public int ImageTokens { get; init; }

    /// <summary>The latest user message's own text: what was asked now.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Content sent more than once in the request (also counted in the parts above).</summary>
    public int RepeatedTokens { get; init; }

    /// <summary>How many turns the conversation has, system turns included.</summary>
    public int Turns { get; init; }

    /// <summary>What the input cost at the answering model's prices, once the backend reported usage.</summary>
    public decimal? InputCostUsd { get; init; }

    /// <summary>For a subscription model, what the input would have cost at its API prices, once the backend reported usage.</summary>
    public decimal? InputApiValueUsd { get; init; }

    /// <summary>
    /// The signs of too much context, by code: <c>large-system-prompt</c>, <c>large-tool-definitions</c>,
    /// <c>large-tool-result</c>, <c>injected-context</c>, <c>repeated-content</c>, <c>long-history</c>,
    /// <c>fixed-overhead</c>, <c>near-context-window</c> and <c>uncached</c>.
    /// </summary>
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>A kind of finding: its code, how serious it is, and what to do about it.</summary>
internal sealed record ContextFindingKind(string Code, string Severity, string Title, string Advice);

/// <summary>One finding about one request, with its numbers.</summary>
internal sealed record ContextFinding
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Title { get; init; }

    /// <summary>What was found, with this request's numbers.</summary>
    public required string Detail { get; init; }

    public required string Advice { get; init; }

    /// <summary>The tokens the finding is about.</summary>
    public required int Tokens { get; init; }
}

/// <summary>One tool definition or tool result, and its size.</summary>
internal sealed record ContextItem(string Name, int Tokens);

/// <summary>What one catalog model would charge for this request's input.</summary>
internal sealed record ContextModelCost
{
    public required string Model { get; init; }

    public required bool IsLocal { get; init; }

    /// <summary>The input at the full input price, on every request that sends this context.</summary>
    public required decimal InputCostUsd { get; init; }

    /// <summary>The same with everything before the latest turn read from the prompt cache, when the model prices cache reads lower.</summary>
    public decimal? CachedInputCostUsd { get; init; }

    /// <summary>For a subscription model, the input at its API prices.</summary>
    public decimal? ApiValueUsd { get; init; }

    /// <summary>The model's context window, when known.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>The share of <see cref="ContextWindow"/> the request fills; above 1 it does not fit.</summary>
    public double? WindowShare { get; init; }
}

/// <summary>Everything the analysis found about one request.</summary>
internal sealed record ContextReport
{
    public required ContextBreakdown Breakdown { get; init; }

    public required IReadOnlyList<ContextFinding> Findings { get; init; }

    /// <summary>The largest tool definitions, largest first.</summary>
    public required IReadOnlyList<ContextItem> LargestTools { get; init; }

    /// <summary>Tools offered and never called in the conversation (empty until it has a few assistant turns), largest first.</summary>
    public required IReadOnlyList<ContextItem> UnusedTools { get; init; }

    /// <summary>The largest tool results, by the tool that produced them, largest first.</summary>
    public required IReadOnlyList<ContextItem> LargestToolResults { get; init; }

    /// <summary>Tokens of the latest turn (the new ask, or the tool results it carries): what a prompt cache cannot already hold.</summary>
    public required int LatestTurnTokens { get; init; }

    /// <summary>The catalog's enabled models and what each would charge, most expensive first.</summary>
    public IReadOnlyList<ContextModelCost> Models { get; init; } = [];
}

/// <summary>
/// Breaks a request's input down into its parts and flags the parts that are large enough to cost real money on every
/// turn: a long system prompt, many tool definitions, large tool results, injected context, repeated content, a long
/// history, a full context window and a prompt cache that is not used.
/// </summary>
internal static class ContextAnalyzer
{
    public const string LargeSystemPrompt = "large-system-prompt";
    public const string LargeToolDefinitions = "large-tool-definitions";
    public const string LargeToolResult = "large-tool-result";
    public const string InjectedContext = "injected-context";
    public const string RepeatedContent = "repeated-content";
    public const string LongHistory = "long-history";
    public const string FixedOverhead = "fixed-overhead";
    public const string NearContextWindow = "near-context-window";
    public const string Uncached = "uncached";

    /// <summary>Content smaller than this is not checked for repeats: short replies ("ok", "done") repeat for their own sake.</summary>
    internal const int RepeatMinimumTokens = 200;

    /// <summary>Assistant turns a conversation needs before tools it never called count as unused.</summary>
    internal const int UnusedToolMinimumTurns = 3;

    // Tokens an image counts for, as ChatCompletionRequest estimates it.
    private const int ImageTokenEstimate = 1000;

    /// <summary>Every kind of finding, in the order the dashboard lists them.</summary>
    public static readonly IReadOnlyList<ContextFindingKind> Kinds =
    [
        new(FixedOverhead, "warning", "Large fixed overhead",
            "Trim the largest of the system prompt, the tools and the injected blocks (see the breakdown). On a paid API, prompt caching makes a stable prefix much cheaper to send again."),
        new(LargeSystemPrompt, "info", "Large system prompt",
            "Trim instructions the model does not need for this work: long CLAUDE.md or AGENTS.md files, output styles, examples. Move guidance that is rarely needed into files the model reads when it needs them."),
        new(LargeToolDefinitions, "info", "Many tool definitions",
            "Turn off MCP servers and tools you do not use in this project, or enable them per project. Every tool definition is sent again on every turn."),
        new(InjectedContext, "info", "Injected context",
            "These are attached files, memory, todo lists or editor state the client adds by itself. Check what your client attaches, and keep memory files short."),
        new(LargeToolResult, "info", "Large tool result",
            "Read files in ranges, filter command output (head, grep, a narrower query), or ask for a summary instead of the raw output."),
        new(RepeatedContent, "info", "Repeated content",
            "Compact the conversation, or ask the client to refer back to what it already read instead of reading it again."),
        new(LongHistory, "info", "Long conversation history",
            "Compact the conversation (/compact in Claude Code) or start a new one when you move on to a new task."),
        new(NearContextWindow, "warning", "Close to the context window",
            "Compact or start a new conversation before the client has to: a nearly full context is also the most expensive one to send again."),
        new(Uncached, "warning", "Nothing read from the prompt cache",
            "Keep the start of the conversation stable (the same system prompt and tools, in the same order), and on Anthropic mark it with cache_control. The router keeps a conversation on the model whose cache is warm (Middleware:Conversation:StickToModel)."),
    ];

    /// <summary>The kind with <paramref name="code"/>.</summary>
    public static ContextFindingKind? Kind(string code) => Kinds.FirstOrDefault(k => k.Code == code);

    /// <summary>Breaks <paramref name="chat"/> down and flags what crosses a threshold in <paramref name="options"/>.</summary>
    /// <param name="chat">The parsed request.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="injectedTags">Tags injected besides the built-in ones (<see cref="PromptRouterMiddlewareOptions.InjectedTags"/>).</param>
    public static ContextReport Analyze(ChatCompletionRequest chat, ContextCostOptions options, IEnumerable<string>? injectedTags = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(options);
        string[] extra = injectedTags?.ToArray() ?? [];

        int system = 0, injected = 0, injectedBlocks = 0, history = 0, toolResults = 0, images = 0, prompt = 0, latestTurn = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int repeated = 0;
        void Count(string content, int tokens)
        {
            if (tokens >= RepeatMinimumTokens && !seen.Add(content))
            {
                repeated += tokens;
            }
        }

        // Which tool each result came from, and which tools were called at all.
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var called = new HashSet<string>(StringComparer.Ordinal);
        int assistantTurns = 0;
        foreach (ChatTurn turn in chat.Turns)
        {
            if (turn.Role == "assistant")
            {
                assistantTurns++;
            }

            foreach (ToolUseBlock use in turn.Blocks.OfType<ToolUseBlock>())
            {
                toolNames[use.Id] = use.Name;
                called.Add(use.Name);
            }
        }

        var results = new List<ContextItem>();
        int last = -1;
        for (int i = chat.Turns.Count - 1; i >= 0; i--)
        {
            if (chat.Turns[i].Role == "user")
            {
                last = i;
                break;
            }
        }

        for (int i = 0; i < chat.Turns.Count; i++)
        {
            ChatTurn turn = chat.Turns[i];
            bool latest = i == last;
            int before = system + injected + history + toolResults + images + prompt;
            if (turn.Role == "system")
            {
                system += 4;
            }
            else if (latest)
            {
                prompt += 4;
            }
            else
            {
                history += 4;
            }

            foreach (ContentBlock block in turn.Blocks)
            {
                switch (block)
                {
                    case TextBlock text when turn.Role == "system":
                        int systemTokens = TokenEstimator.Estimate(text.Text);
                        system += systemTokens;
                        Count(text.Text, systemTokens);
                        break;

                    case TextBlock text when turn.Role == "user":
                        // Injected blocks on their own, so the same reminder in several turns is seen as a repeat.
                        string own = text.Text;
                        IReadOnlyList<Range> found = InjectedBlocks.Find(text.Text, extra);
                        if (found.Count > 0)
                        {
                            var rest = new StringBuilder(text.Text.Length);
                            int position = 0;
                            foreach (Range range in found)
                            {
                                (int offset, int length) = range.GetOffsetAndLength(text.Text.Length);
                                int blockTokens = TokenEstimator.Estimate(text.Text.AsSpan(offset, length));
                                injected += blockTokens;
                                injectedBlocks++;
                                if (blockTokens >= RepeatMinimumTokens)
                                {
                                    Count(text.Text.Substring(offset, length), blockTokens);
                                }

                                rest.Append(text.Text, position, offset - position);
                                position = offset + length;
                            }

                            own = rest.Append(text.Text, position, text.Text.Length - position).ToString();
                        }

                        int ownTokens = TokenEstimator.Estimate(own);
                        if (latest)
                        {
                            prompt += ownTokens;
                        }
                        else
                        {
                            history += ownTokens;
                        }

                        Count(own.Trim(), ownTokens);
                        break;

                    case TextBlock text:
                        int textTokens = TokenEstimator.Estimate(text.Text);
                        history += textTokens;
                        Count(text.Text, textTokens);
                        break;

                    case ToolUseBlock use:
                        history += TokenEstimator.Estimate(use.Name) + TokenEstimator.Estimate(use.InputJson) + 8;
                        break;

                    case ToolResultBlock result:
                        int resultTokens = TokenEstimator.Estimate(result.Content);
                        toolResults += resultTokens + 8;
                        Count(result.Content, resultTokens);
                        results.Add(new ContextItem(toolNames.TryGetValue(result.ToolUseId, out string? name) && name.Length > 0 ? name : "tool", resultTokens + 8));
                        break;

                    case ImageBlock:
                        images += ImageTokenEstimate;
                        break;

                    case ThinkingBlock thinking:
                        history += TokenEstimator.Estimate(thinking.Text);
                        break;
                }
            }

            if (latest)
            {
                latestTurn = system + injected + history + toolResults + images + prompt - before;
            }
        }

        ContextItem[] tools = [.. chat.Tools.Select(t => new ContextItem(t.Name, ChatCompletionRequest.EstimateTokens(t))).OrderByDescending(t => t.Tokens)];
        int toolTokens = tools.Sum(t => t.Tokens);
        ContextItem[] unused = assistantTurns >= UnusedToolMinimumTurns ? [.. tools.Where(t => !called.Contains(t.Name))] : [];
        int total = system + toolTokens + injected + history + toolResults + images + prompt;

        var breakdown = new ContextBreakdown
        {
            TotalTokens = total,
            SystemTokens = system,
            ToolDefinitionTokens = toolTokens,
            ToolCount = tools.Length,
            UnusedToolDefinitionTokens = unused.Sum(t => t.Tokens),
            InjectedTokens = injected,
            HistoryTokens = history,
            ToolResultTokens = toolResults,
            ImageTokens = images,
            PromptTokens = prompt,
            RepeatedTokens = repeated,
            Turns = chat.Turns.Count,
        };

        ContextItem[] largestResults = [.. results.OrderByDescending(r => r.Tokens)];
        var findings = new List<ContextFinding>();
        void Flag(string code, int tokens, string detail)
        {
            ContextFindingKind kind = Kind(code)!;
            findings.Add(new ContextFinding { Code = code, Severity = kind.Severity, Title = kind.Title, Detail = detail, Advice = kind.Advice, Tokens = tokens });
        }

        int overhead = system + toolTokens + injected;
        if (overhead >= options.FixedOverheadTokens && overhead > 0)
        {
            Flag(FixedOverhead, overhead,
                $"The system prompt, tool definitions and injected blocks are {Tokens(overhead)} tokens ({Share(overhead, total)} of the request) before anything is asked. "
                + "That part is sent with every request, tool-call follow-ups included.");
        }

        if (system >= options.LargeSystemPromptTokens && system > 0)
        {
            Flag(LargeSystemPrompt, system, $"The system prompt is {Tokens(system)} tokens ({Share(system, total)} of the request) and is sent again with every request.");
        }

        if (toolTokens >= options.LargeToolDefinitionTokens && toolTokens > 0)
        {
            string largest = string.Join(", ", tools.Take(3).Select(t => $"{t.Name} ({Tokens(t.Tokens)})"));
            string never = unused.Length > 0
                ? $" {unused.Length} of them ({Tokens(breakdown.UnusedToolDefinitionTokens)} tokens) have not been called in this conversation."
                : string.Empty;
            Flag(LargeToolDefinitions, toolTokens,
                $"{tools.Length} tools take {Tokens(toolTokens)} tokens ({Share(toolTokens, total)} of the request) on every request; the largest are {largest}.{never}");
        }

        if (injected >= options.InjectedContextTokens && injected > 0)
        {
            Flag(InjectedContext, injected,
                $"{Tokens(injected)} tokens ({Share(injected, total)} of the request) are in {injectedBlocks} {(injectedBlocks == 1 ? "block" : "blocks")} the client injected into messages (<system-reminder> and similar).");
        }

        ContextItem[] large = [.. largestResults.Where(r => r.Tokens >= options.LargeToolResultTokens)];
        if (large.Length > 0 && large[0].Tokens > 0)
        {
            string more = large.Length > 1 ? $"; {large.Length} results of {Tokens(options.LargeToolResultTokens)} tokens or more are in this request" : string.Empty;
            Flag(LargeToolResult, large.Sum(r => r.Tokens),
                $"The largest tool result is {Tokens(large[0].Tokens)} tokens ({large[0].Name}){more}. Tool results stay in the conversation and are sent again on every later turn.");
        }

        if (repeated >= options.RepeatedContentTokens && repeated > 0)
        {
            Flag(RepeatedContent, repeated,
                $"{Tokens(repeated)} tokens are content sent more than once in this request, such as a file read twice or the same reminder in several turns.");
        }

        int past = history + toolResults;
        if (past >= options.LongHistoryTokens && past > 0)
        {
            Flag(LongHistory, past,
                $"Earlier turns and tool results are {Tokens(past)} tokens ({Share(past, total)} of the request) over {chat.Turns.Count} turns, all sent again on every turn.");
        }

        return new ContextReport
        {
            Breakdown = breakdown with { Findings = [.. findings.Select(f => f.Code)] },
            Findings = findings,
            LargestTools = [.. tools.Take(10)],
            UnusedTools = [.. unused.Take(20)],
            LargestToolResults = [.. largestResults.Take(10)],
            LatestTurnTokens = latestTurn,
        };
    }

    /// <summary>
    /// Adds what is known once the request was answered: its input cost, whether it nearly filled
    /// <paramref name="model"/>'s context window, and whether the backend's prompt cache was used.
    /// </summary>
    /// <param name="breakdown">The request's breakdown.</param>
    /// <param name="entry">Its activity entry, with the usage the backend reported.</param>
    /// <param name="model">The model that answered (or the planned one in Annotate mode), if it is in the catalog.</param>
    /// <param name="options">The thresholds.</param>
    public static ContextBreakdown Complete(ContextBreakdown breakdown, RoutingActivityEntry entry, ModelDefinition? model, ContextCostOptions options)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        if (model is null)
        {
            return breakdown;
        }

        var findings = breakdown.Findings.ToList();
        bool reported = entry.InputTokens is not null || entry.CacheReadTokens is not null || entry.CacheCreationTokens is not null;
        long input = entry.InputTokens ?? 0, cacheRead = entry.CacheReadTokens ?? 0, cacheWrite = entry.CacheCreationTokens ?? 0;

        // What the backend counted is better than the estimate, when it said.
        long size = reported ? input + cacheRead + cacheWrite : breakdown.TotalTokens;
        if (model.ContextWindow > 0 && size >= options.ContextWindowShare * model.ContextWindow && !findings.Contains(NearContextWindow))
        {
            findings.Add(NearContextWindow);
        }

        if (reported && input >= options.UncachedTokens && cacheRead == 0 && cacheWrite == 0 && CachesCheaper(model) && !findings.Contains(Uncached))
        {
            findings.Add(Uncached);
        }

        return breakdown with
        {
            Findings = findings,
            InputCostUsd = reported ? model.Cost(input, 0, cacheRead, cacheWrite) : null,
            InputApiValueUsd = reported && model.ApiValue(input, 0, cacheRead, cacheWrite) is > 0 and var value ? value : null,
        };
    }

    /// <summary>What each enabled model in <paramref name="models"/> would charge for the request's input, and how much of its window it fills.</summary>
    public static ContextReport Price(ContextReport report, IEnumerable<ModelDefinition> models, ContextCostOptions options)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(options);
        int total = report.Breakdown.TotalTokens;
        int prefix = Math.Max(0, total - report.LatestTurnTokens);
        var costs = new List<ContextModelCost>();
        foreach (ModelDefinition model in models.Where(m => m.Enabled))
        {
            decimal? cached = model.CacheReadCostPerMillionTokens is { } read && read < model.InputCostPerMillionTokens
                ? ((prefix * read) + ((total - prefix) * model.InputCostPerMillionTokens)) / 1_000_000m
                : null;
            decimal api = model.ApiValue(total, 0);
            costs.Add(new ContextModelCost
            {
                Model = model.Name,
                IsLocal = model.IsLocal,
                InputCostUsd = model.Cost(total, 0),
                CachedInputCostUsd = cached,
                ApiValueUsd = api > 0 ? api : null,
                ContextWindow = model.ContextWindow > 0 ? model.ContextWindow : null,
                WindowShare = model.ContextWindow > 0 ? Math.Round((double)total / model.ContextWindow, 4) : null,
            });
        }

        // A request that nearly fills the largest window any model has is one the conversation will soon outgrow.
        var findings = report.Findings.ToList();
        var codes = report.Breakdown.Findings.ToList();
        if (costs.Where(c => c.ContextWindow is not null).MaxBy(c => c.ContextWindow) is { WindowShare: { } share } roomiest && share >= options.ContextWindowShare)
        {
            ContextFindingKind kind = Kind(NearContextWindow)!;
            findings.Add(new ContextFinding
            {
                Code = kind.Code,
                Severity = kind.Severity,
                Title = kind.Title,
                Detail = $"The request fills {share.ToString("0%", CultureInfo.InvariantCulture)} of {roomiest.Model}'s {Tokens(roomiest.ContextWindow!.Value)}-token context window, the largest in the catalog.",
                Advice = kind.Advice,
                Tokens = total,
            });
            codes.Add(kind.Code);
        }

        return report with
        {
            Findings = findings,
            Breakdown = report.Breakdown with { Findings = codes },
            Models = [.. costs.OrderByDescending(c => c.InputCostUsd).ThenByDescending(c => c.ApiValueUsd ?? 0).ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>Whether <paramref name="model"/> reads from its prompt cache for less than its input price (or, on a subscription, its API input price).</summary>
    private static bool CachesCheaper(ModelDefinition model) =>
        model.CacheReadCostPerMillionTokens is { } read && read < model.InputCostPerMillionTokens
        || (model.InputCostPerMillionTokens == 0 && model.ApiCacheReadCostPerMillionTokens is { } apiRead && model.ApiInputCostPerMillionTokens is { } apiInput && apiRead < apiInput);

    private static string Tokens(int tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    private static string Share(int part, int total) => total > 0 ? ((double)part / total).ToString("0%", CultureInfo.InvariantCulture) : "0%";
}

/// <summary>Which protocol a pasted request body is in, for analyzing it without knowing which endpoint it was sent to.</summary>
internal static class ContextProtocol
{
    /// <summary>Reads <c>openai</c> (or <c>chat</c>), <c>anthropic</c> (or <c>messages</c>) and <c>responses</c>.</summary>
    public static bool TryParse(string value, out ChatProtocol protocol)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "openai" or "chat" or "chat-completions":
                protocol = ChatProtocol.OpenAI;
                return true;
            case "anthropic" or "messages":
                protocol = ChatProtocol.Anthropic;
                return true;
            case "responses":
                protocol = ChatProtocol.Responses;
                return true;
            default:
                protocol = default;
                return false;
        }
    }

    /// <summary>
    /// Responses has <c>input</c> and no <c>messages</c>; Anthropic has a top-level <c>system</c>, tools with an
    /// <c>input_schema</c> or <c>tool_use</c> and <c>tool_result</c> blocks; anything else is read as chat completions.
    /// </summary>
    public static ChatProtocol Detect(JsonElement root)
    {
        if (root.TryGetProperty("input", out _) && !root.TryGetProperty("messages", out _))
        {
            return ChatProtocol.Responses;
        }

        if (root.TryGetProperty("system", out _) || root.TryGetProperty("anthropic_version", out _))
        {
            return ChatProtocol.Anthropic;
        }

        if (root.TryGetArray("tools", out JsonElement tools) && tools.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("input_schema", out _)))
        {
            return ChatProtocol.Anthropic;
        }

        if (root.TryGetArray("messages", out JsonElement messages))
        {
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Object && message.TryGetArray("content", out JsonElement content)
                    && content.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.Object && b.GetString("type") is "tool_use" or "tool_result" or "thinking"))
                {
                    return ChatProtocol.Anthropic;
                }
            }
        }

        return ChatProtocol.OpenAI;
    }
}
