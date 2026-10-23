using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>One chat message, flattened to text.</summary>
public sealed record ChatMessage
{
    /// <summary><c>system</c>, <c>developer</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</summary>
    public required string Role { get; init; }

    /// <summary>The message's text parts joined with newlines.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// A chat request in a protocol-neutral shape: what the router needs to classify it, and what a connector needs to
/// re-express it for a backend that speaks another protocol. The original bytes are kept separately and forwarded
/// untouched when the backend speaks the client's protocol.
/// </summary>
public sealed partial class ChatCompletionRequest
{
    /// <summary>The wire format the client used.</summary>
    public ChatProtocol Protocol { get; init; }

    /// <summary>The requested model (<c>auto</c>, an <c>auto:</c> strategy alias, a catalog model name, or a vendor name to map).</summary>
    public string? Model { get; init; }

    /// <summary>Whether the client asked for server-sent events.</summary>
    public bool Stream { get; init; }

    /// <summary>The conversation, flattened to text (system and developer messages included, tool traffic excluded).</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>The conversation with structured content: text, images, tool calls and tool results.</summary>
    public IReadOnlyList<ChatTurn> Turns { get; init; } = [];

    /// <summary>The tools the client offers the model.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>How the model may use <see cref="Tools"/>.</summary>
    public ToolChoice ToolChoice { get; init; }

    /// <summary>Whether the request defines tools (function calling).</summary>
    public bool HasTools => Tools.Count > 0;

    /// <summary>Whether any message carries an image.</summary>
    public bool HasImages { get; init; }

    /// <summary>Whether the latest user turn only carries tool results, i.e. it continues a tool-calling turn.</summary>
    public bool IsToolResultContinuation { get; init; }

    /// <summary>The ids of the tool calls the latest user turn answers.</summary>
    public IReadOnlyList<string> AnsweredToolUseIds { get; init; } = [];

    /// <summary>Estimated input tokens for the whole request (system prompt, messages and tool definitions).</summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>Output limit requested by the client, when given.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Sampling temperature, when given.</summary>
    public double? Temperature { get; init; }

    /// <summary>Nucleus sampling, when given.</summary>
    public double? TopP { get; init; }

    /// <summary>Stop sequences, when given.</summary>
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>The <c>metadata.user_id</c> (Anthropic) or <c>user</c> (OpenAI) field, when given.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// OpenAI's <c>prompt_cache_key</c> (chat completions and Responses): groups requests that share a prompt prefix
    /// so they reach the same cache. Carried between those two protocols. Other backends do not need it (Anthropic
    /// caches by prefix), so it is not reported as dropped there.
    /// </summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>
    /// Anthropic's top-level <c>cache_control</c> (automatic caching), as JSON, when the client set it. Markers on
    /// single blocks and tools are on <see cref="ContentBlock.CacheControl"/> and <see cref="ToolDefinition.CacheControl"/>.
    /// </summary>
    public string? CacheControl { get; init; }

    /// <summary>Whether the client placed any Anthropic <c>cache_control</c> marker (top-level, on a tool or on a block).</summary>
    public bool HasCacheMarkers => CacheMarkerCount > 0;

    /// <summary>How many cache breakpoints the client's own markers use (the top-level marker counts as one).</summary>
    public int CacheMarkerCount =>
        (CacheControl is null ? 0 : 1)
        + Tools.Count(t => t.CacheControl is not null)
        + Turns.Sum(t => t.Blocks.Count(b => b.CacheControl is not null));

    /// <summary>Extended thinking / reasoning effort the client asked for.</summary>
    public ReasoningRequest? Reasoning { get; init; }

    /// <summary>Structured output the client asked for.</summary>
    public ResponseFormat? ResponseFormat { get; init; }

    /// <summary><see langword="false"/> when the client allows at most one tool call per reply.</summary>
    public bool? ParallelToolCalls { get; init; }

    /// <summary>
    /// Provider-hosted tools the request offers (Responses <c>web_search</c>, <c>file_search</c>, …). Only a backend
    /// that speaks the client's protocol can run them; elsewhere they are left out and reported as dropped.
    /// </summary>
    public IReadOnlyList<string> HostedTools { get; init; } = [];

    /// <summary>
    /// Request features that no other protocol can carry (documents, files, <c>logprobs</c>, …). They survive
    /// pass-through and are reported as dropped when the request is translated.
    /// </summary>
    public IReadOnlyList<string> ProtocolOnlyFeatures { get; init; } = [];

    /// <summary>Parses a body in <paramref name="protocol"/>.</summary>
    public static ChatCompletionRequest Parse(JsonElement root, ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.Anthropic => ParseAnthropic(root),
        ChatProtocol.Responses => ParseResponses(root),
        _ => Parse(root),
    };

    /// <summary>Parses an OpenAI chat-completions body. Also accepts <c>{"prompt": "..."}</c> as a single user message.</summary>
    public static ChatCompletionRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ChatCompletionRequest();
        }

        var turns = new List<ChatTurn>();
        bool hasImages = false;

        if (root.TryGetArray("messages", out JsonElement array))
        {
            foreach (JsonElement message in array.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string role = message.GetString("role") ?? "user";
                var blocks = new List<ContentBlock>();
                if (role == "tool")
                {
                    blocks.Add(new ToolResultBlock
                    {
                        ToolUseId = message.GetString("tool_call_id") ?? string.Empty,
                        Content = message.TryGetProperty("content", out JsonElement toolContent) ? ReadOpenAIContent(toolContent, null, ref hasImages) : string.Empty,
                        IsError = false,
                    });
                    turns.Add(new ChatTurn { Role = "user", Blocks = blocks });
                    continue;
                }

                if (message.TryGetProperty("content", out JsonElement content))
                {
                    ReadOpenAIContent(content, blocks, ref hasImages);
                }

                if (role == "assistant" && message.TryGetArray("tool_calls", out JsonElement calls))
                {
                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        call.TryGetObject("function", out JsonElement function);
                        blocks.Add(new ToolUseBlock
                        {
                            Id = call.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                            Name = function.GetString("name") ?? string.Empty,
                            InputJson = JsonHelpers.EnsureJsonObject(function.GetString("arguments")),
                        });
                    }
                }

                turns.Add(new ChatTurn { Role = role == "developer" ? "system" : role, Blocks = blocks });
            }
        }
        else if (root.GetString("prompt") is { } prompt)
        {
            turns.Add(ChatTurn.FromText("user", prompt));
        }

        var tools = new List<ToolDefinition>();
        if (root.TryGetArray("tools", out JsonElement toolArray))
        {
            foreach (JsonElement tool in toolArray.EnumerateArray())
            {
                JsonElement function = tool.TryGetObject("function", out JsonElement f) ? f : tool;
                if (function.GetString("name") is { Length: > 0 } name)
                {
                    tools.Add(new ToolDefinition
                    {
                        Name = name,
                        Description = function.GetString("description"),
                        InputSchemaJson = function.TryGetObject("parameters", out JsonElement parameters) ? parameters.GetRawText() : """{"type":"object","properties":{}}""",
                    });
                }
            }
        }
        else if (root.TryGetArray("functions", out JsonElement functions))
        {
            foreach (JsonElement function in functions.EnumerateArray())
            {
                if (function.GetString("name") is { Length: > 0 } name)
                {
                    tools.Add(new ToolDefinition
                    {
                        Name = name,
                        Description = function.GetString("description"),
                        InputSchemaJson = function.TryGetObject("parameters", out JsonElement parameters) ? parameters.GetRawText() : """{"type":"object","properties":{}}""",
                    });
                }
            }
        }

        ToolChoice toolChoice = default;
        if (root.TryGetProperty("tool_choice", out JsonElement choice))
        {
            toolChoice = choice.ValueKind switch
            {
                JsonValueKind.String => choice.GetString() switch
                {
                    "none" => new ToolChoice { Mode = ToolChoiceMode.None },
                    "required" => new ToolChoice { Mode = ToolChoiceMode.Required },
                    _ => default,
                },
                JsonValueKind.Object when choice.TryGetObject("function", out JsonElement fn) && fn.GetString("name") is { } name
                    => new ToolChoice { Mode = ToolChoiceMode.Named, Name = name },
                _ => default,
            };
        }

        var stops = new List<string>();
        if (root.TryGetProperty("stop", out JsonElement stop))
        {
            ReadStrings(stop, stops);
        }

        var protocolOnly = new List<string>();
        if (root.GetBool("logprobs"))
        {
            protocolOnly.Add("logprobs");
        }

        if (root.GetInt("n") is > 1)
        {
            protocolOnly.Add("n");
        }

        if (root.TryGetObject("audio", out _))
        {
            protocolOnly.Add("audio");
        }

        return Finish(new ChatCompletionRequest
        {
            Protocol = ChatProtocol.OpenAI,
            Reasoning = root.GetString("reasoning_effort") is { } effort ? new ReasoningRequest { Effort = effort } : null,
            ResponseFormat = root.TryGetObject("response_format", out JsonElement format) ? ReadResponseFormat(format, nested: true) : null,
            ParallelToolCalls = root.TryGetProperty("parallel_tool_calls", out JsonElement parallel) && parallel.ValueKind == JsonValueKind.False ? false : null,
            ProtocolOnlyFeatures = protocolOnly,
            Model = root.GetString("model"),
            Stream = root.GetBool("stream"),
            Turns = turns,
            Tools = tools,
            ToolChoice = toolChoice,
            HasImages = hasImages,
            MaxTokens = root.GetInt("max_completion_tokens") ?? root.GetInt("max_tokens"),
            Temperature = root.GetDouble("temperature"),
            TopP = root.GetDouble("top_p"),
            StopSequences = stops,
            UserId = root.GetString("user"),
            PromptCacheKey = root.GetString("prompt_cache_key"),
        });
    }

    /// <summary>Parses an Anthropic Messages API body.</summary>
    public static ChatCompletionRequest ParseAnthropic(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ChatCompletionRequest { Protocol = ChatProtocol.Anthropic };
        }

        var turns = new List<ChatTurn>();
        bool hasImages = false;

        if (root.TryGetProperty("system", out JsonElement system))
        {
            List<ContentBlock> systemBlocks = ReadAnthropicSystem(system);
            if (systemBlocks.Count > 0)
            {
                turns.Add(new ChatTurn { Role = "system", Blocks = systemBlocks });
            }
        }

        if (root.TryGetArray("messages", out JsonElement messages))
        {
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string role = message.GetString("role") ?? "user";
                var blocks = new List<ContentBlock>();
                if (message.TryGetProperty("content", out JsonElement content))
                {
                    ReadAnthropicContent(content, blocks, ref hasImages);
                }

                turns.Add(new ChatTurn { Role = role, Blocks = blocks });
            }
        }

        var tools = new List<ToolDefinition>();
        if (root.TryGetArray("tools", out JsonElement toolArray))
        {
            foreach (JsonElement tool in toolArray.EnumerateArray())
            {
                if (tool.GetString("name") is { Length: > 0 } name)
                {
                    tools.Add(new ToolDefinition
                    {
                        Name = name,
                        Description = tool.GetString("description"),
                        InputSchemaJson = tool.TryGetObject("input_schema", out JsonElement schema) ? schema.GetRawText() : """{"type":"object","properties":{}}""",
                        CacheControl = ReadCacheControl(tool),
                    });
                }
            }
        }

        ToolChoice toolChoice = default;
        bool? parallelToolCalls = null;
        if (root.TryGetObject("tool_choice", out JsonElement choice))
        {
            parallelToolCalls = choice.GetBool("disable_parallel_tool_use") ? false : null;
            toolChoice = choice.GetString("type") switch
            {
                "none" => new ToolChoice { Mode = ToolChoiceMode.None },
                "any" => new ToolChoice { Mode = ToolChoiceMode.Required },
                "tool" => new ToolChoice { Mode = ToolChoiceMode.Named, Name = choice.GetString("name") },
                _ => default,
            };
        }

        var stops = new List<string>();
        if (root.TryGetProperty("stop_sequences", out JsonElement stop))
        {
            ReadStrings(stop, stops);
        }

        root.TryGetObject("metadata", out JsonElement metadata);
        var protocolOnly = new List<string>();
        string raw = root.GetRawText();
        if (raw.Contains("\"type\":\"document\"", StringComparison.Ordinal) || raw.Contains("\"type\": \"document\"", StringComparison.Ordinal))
        {
            protocolOnly.Add("documents");
        }

        if (root.TryGetArray("mcp_servers", out _))
        {
            protocolOnly.Add("mcp_servers");
        }

        ReasoningRequest? reasoning = root.TryGetObject("thinking", out JsonElement thinking) && thinking.GetString("type") is "enabled" or "adaptive"
            ? new ReasoningRequest { BudgetTokens = thinking.GetInt("budget_tokens") }
            : null;

        return Finish(new ChatCompletionRequest
        {
            Protocol = ChatProtocol.Anthropic,
            Reasoning = reasoning,
            ParallelToolCalls = parallelToolCalls,
            ProtocolOnlyFeatures = protocolOnly,
            Model = root.GetString("model"),
            Stream = root.GetBool("stream"),
            Turns = turns,
            Tools = tools,
            ToolChoice = toolChoice,
            HasImages = hasImages,
            MaxTokens = root.GetInt("max_tokens"),
            Temperature = root.GetDouble("temperature"),
            TopP = root.GetDouble("top_p"),
            StopSequences = stops,
            UserId = metadata.GetString("user_id"),
            CacheControl = ReadCacheControl(root),
        });
    }

    /// <summary>Derives the flattened messages, token estimate and tool-continuation facts from <see cref="Turns"/>.</summary>
    internal static ChatCompletionRequest Finish(ChatCompletionRequest request)
    {
        var messages = new List<ChatMessage>(request.Turns.Count);
        int tokens = 0;
        foreach (ChatTurn turn in request.Turns)
        {
            tokens += 4;
            foreach (ContentBlock block in turn.Blocks)
            {
                tokens += block switch
                {
                    TextBlock t => TokenEstimator.Estimate(t.Text),
                    ToolUseBlock u => TokenEstimator.Estimate(u.Name) + TokenEstimator.Estimate(u.InputJson) + 8,
                    ToolResultBlock r => TokenEstimator.Estimate(r.Content) + 8,
                    ImageBlock => 1000,
                    ThinkingBlock k => TokenEstimator.Estimate(k.Text),
                    _ => 0,
                };
            }

            string text = turn.Text;
            if (text.Length > 0 || turn.Blocks.Count == 0)
            {
                messages.Add(new ChatMessage { Role = turn.Role, Content = text });
            }
        }

        tokens += request.Tools.Sum(EstimateTokens);

        ChatTurn? lastUser = request.Turns.LastOrDefault(t => t.Role == "user");
        bool continuation = lastUser?.IsToolResultOnly == true;

        return new ChatCompletionRequest
        {
            Protocol = request.Protocol,
            Model = request.Model,
            Stream = request.Stream,
            Messages = messages,
            Turns = request.Turns,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            HasImages = request.HasImages,
            IsToolResultContinuation = continuation,
            AnsweredToolUseIds = lastUser is null ? [] : lastUser.Blocks.OfType<ToolResultBlock>().Select(r => r.ToolUseId).Where(id => id.Length > 0).ToArray(),
            EstimatedInputTokens = tokens,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            StopSequences = request.StopSequences,
            UserId = request.UserId,
            PromptCacheKey = request.PromptCacheKey,
            CacheControl = request.CacheControl,
            Reasoning = request.Reasoning,
            ResponseFormat = request.ResponseFormat,
            ParallelToolCalls = request.ParallelToolCalls,
            HostedTools = request.HostedTools,
            ProtocolOnlyFeatures = request.ProtocolOnlyFeatures,
        };
    }

    /// <summary>Estimated tokens of one tool definition.</summary>
    internal static int EstimateTokens(ToolDefinition tool) =>
        TokenEstimator.Estimate(tool.Name) + TokenEstimator.Estimate(tool.Description ?? string.Empty) + TokenEstimator.Estimate(tool.InputSchemaJson) + 8;

    /// <summary>Reads OpenAI <c>response_format</c> (<paramref name="nested"/>: schema under <c>json_schema</c>) or Responses <c>text.format</c>.</summary>
    private static ResponseFormat? ReadResponseFormat(JsonElement format, bool nested)
    {
        switch (format.GetString("type"))
        {
            case "json_object":
                return new ResponseFormat { Kind = ResponseFormatKind.JsonObject };
            case "json_schema":
                JsonElement holder = nested && format.TryGetObject("json_schema", out JsonElement inner) ? inner : format;
                return new ResponseFormat
                {
                    Kind = ResponseFormatKind.JsonSchema,
                    Name = holder.GetString("name"),
                    SchemaJson = holder.TryGetObject("schema", out JsonElement schema) ? schema.GetRawText() : """{"type":"object"}""",
                    Strict = holder.GetBool("strict"),
                };
            default:
                return null;
        }
    }

    private static void ReadStrings(JsonElement value, List<string> into)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            into.Add(value.GetString()!);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    into.Add(item.GetString()!);
                }
            }
        }
    }

    /// <summary>Reads OpenAI content (string or parts). Adds blocks to <paramref name="blocks"/> when given; returns the flattened text.</summary>
    private static string ReadOpenAIContent(JsonElement content, List<ContentBlock>? blocks, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            string text = content.GetString()!;
            if (text.Length > 0)
            {
                blocks?.Add(new TextBlock { Text = text });
            }

            return text;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                Append(builder, part.GetString());
                blocks?.Add(new TextBlock { Text = part.GetString()! });
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = part.GetString("type");
            if (type is "image_url" or "image" or "input_image")
            {
                hasImages = true;
                string? url = part.TryGetObject("image_url", out JsonElement imageUrl) ? imageUrl.GetString("url") : part.GetString("image_url") ?? part.GetString("url");
                blocks?.Add(ImageFromUrl(url));
            }
            else if (part.GetString("text") is { } text)
            {
                Append(builder, text);
                blocks?.Add(new TextBlock { Text = text });
            }
        }

        return builder.ToString();
    }

    private static ImageBlock ImageFromUrl(string? url)
    {
        if (url is not null && url.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = url.IndexOf(',', StringComparison.Ordinal);
            int semicolon = url.IndexOf(';', StringComparison.Ordinal);
            if (comma > 0 && semicolon > 5 && semicolon < comma)
            {
                return new ImageBlock { MediaType = url[5..semicolon], Data = url[(comma + 1)..] };
            }
        }

        return new ImageBlock { Url = url };
    }

    /// <summary>Reads <c>system</c> (a string or text blocks), one block per part so markers keep their place.</summary>
    private static List<ContentBlock> ReadAnthropicSystem(JsonElement value)
    {
        var blocks = new List<ContentBlock>();
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } text)
            {
                blocks.Add(new TextBlock { Text = text });
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in value.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String && part.GetString() is { Length: > 0 } plain)
                {
                    blocks.Add(new TextBlock { Text = plain });
                }
                else if (part.GetString("type") == "text" && part.GetString("text") is { Length: > 0 } text)
                {
                    blocks.Add(new TextBlock { Text = text, CacheControl = ReadCacheControl(part) });
                }
            }
        }

        return blocks;
    }

    /// <summary>The <c>cache_control</c> object on <paramref name="element"/>, as JSON, or <see langword="null"/>.</summary>
    private static string? ReadCacheControl(JsonElement element) =>
        element.TryGetObject("cache_control", out JsonElement marker) ? marker.GetRawText() : null;

    private static void ReadAnthropicContent(JsonElement content, List<ContentBlock> blocks, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } text)
            {
                blocks.Add(new TextBlock { Text = text });
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (part.GetString("type"))
            {
                case "text":
                    if (part.GetString("text") is { } text)
                    {
                        blocks.Add(new TextBlock { Text = text, CacheControl = ReadCacheControl(part) });
                    }

                    break;

                case "image":
                    hasImages = true;
                    part.TryGetObject("source", out JsonElement source);
                    ImageBlock image = source.GetString("type") == "url"
                        ? new ImageBlock { Url = source.GetString("url") }
                        : new ImageBlock { MediaType = source.GetString("media_type"), Data = source.GetString("data") };
                    blocks.Add(image with { CacheControl = ReadCacheControl(part) });
                    break;

                case "tool_use":
                    blocks.Add(new ToolUseBlock
                    {
                        Id = part.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                        Name = part.GetString("name") ?? string.Empty,
                        InputJson = part.TryGetObject("input", out JsonElement input) ? input.GetRawText() : "{}",
                        CacheControl = ReadCacheControl(part),
                    });
                    break;

                case "tool_result":
                    bool nestedImages = false;
                    string result = part.TryGetProperty("content", out JsonElement resultContent)
                        ? ReadToolResultContent(resultContent, ref nestedImages)
                        : string.Empty;
                    hasImages |= nestedImages;
                    blocks.Add(new ToolResultBlock
                    {
                        ToolUseId = part.GetString("tool_use_id") ?? string.Empty,
                        Content = result,
                        IsError = part.GetBool("is_error"),
                        CacheControl = ReadCacheControl(part),
                    });
                    break;

                case "thinking":
                    blocks.Add(new ThinkingBlock { Text = part.GetString("thinking") ?? string.Empty, Signature = part.GetString("signature") });
                    break;

                case "redacted_thinking":
                    blocks.Add(new ThinkingBlock { Text = string.Empty, RedactedData = part.GetString("data") });
                    break;

                default:
                    // document, search_result, server tool results, …: kept on pass-through only (see ProtocolOnlyFeatures).
                    break;
            }
        }
    }

    private static string ReadToolResultContent(JsonElement content, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!;
        }

        var builder = new StringBuilder();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.GetString("type") == "image")
                {
                    hasImages = true;
                }
                else if (part.GetString("text") is { } text)
                {
                    Append(builder, text);
                }
            }
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(text);
    }
}
