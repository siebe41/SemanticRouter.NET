using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>One chat message, flattened to text.</summary>
/// <param name="Role"><c>system</c>, <c>developer</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">The message's text parts joined with newlines.</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// A chat request in a protocol-neutral shape: what the router needs to classify it, and what a connector needs to
/// re-express it for a backend that speaks another protocol. The original bytes are kept separately and forwarded
/// untouched when the backend speaks the client's protocol.
/// </summary>
public sealed class ChatCompletionRequest
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

    /// <summary>Parses a body in <paramref name="protocol"/>.</summary>
    public static ChatCompletionRequest Parse(JsonElement root, ChatProtocol protocol) =>
        protocol == ChatProtocol.Anthropic ? ParseAnthropic(root) : Parse(root);

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
                    blocks.Add(new ToolResultBlock(
                        message.GetString("tool_call_id") ?? string.Empty,
                        message.TryGetProperty("content", out JsonElement toolContent) ? ReadOpenAIContent(toolContent, null, ref hasImages) : string.Empty,
                        IsError: false));
                    turns.Add(new ChatTurn("user", blocks));
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
                        blocks.Add(new ToolUseBlock(
                            call.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                            function.GetString("name") ?? string.Empty,
                            JsonHelpers.EnsureJsonObject(function.GetString("arguments"))));
                    }
                }

                turns.Add(new ChatTurn(role == "developer" ? "system" : role, blocks));
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
                    tools.Add(new ToolDefinition(
                        name,
                        function.GetString("description"),
                        function.TryGetObject("parameters", out JsonElement parameters) ? parameters.GetRawText() : """{"type":"object","properties":{}}"""));
                }
            }
        }
        else if (root.TryGetArray("functions", out JsonElement functions))
        {
            foreach (JsonElement function in functions.EnumerateArray())
            {
                if (function.GetString("name") is { Length: > 0 } name)
                {
                    tools.Add(new ToolDefinition(
                        name,
                        function.GetString("description"),
                        function.TryGetObject("parameters", out JsonElement parameters) ? parameters.GetRawText() : """{"type":"object","properties":{}}"""));
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
                    "none" => new ToolChoice(ToolChoiceMode.None),
                    "required" => new ToolChoice(ToolChoiceMode.Required),
                    _ => default,
                },
                JsonValueKind.Object when choice.TryGetObject("function", out JsonElement fn) && fn.GetString("name") is { } name
                    => new ToolChoice(ToolChoiceMode.Named, name),
                _ => default,
            };
        }

        var stops = new List<string>();
        if (root.TryGetProperty("stop", out JsonElement stop))
        {
            ReadStrings(stop, stops);
        }

        return Finish(new ChatCompletionRequest
        {
            Protocol = ChatProtocol.OpenAI,
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
            string text = ReadAnthropicText(system);
            if (text.Length > 0)
            {
                turns.Add(ChatTurn.FromText("system", text));
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

                turns.Add(new ChatTurn(role, blocks));
            }
        }

        var tools = new List<ToolDefinition>();
        if (root.TryGetArray("tools", out JsonElement toolArray))
        {
            foreach (JsonElement tool in toolArray.EnumerateArray())
            {
                if (tool.GetString("name") is { Length: > 0 } name)
                {
                    tools.Add(new ToolDefinition(
                        name,
                        tool.GetString("description"),
                        tool.TryGetObject("input_schema", out JsonElement schema) ? schema.GetRawText() : """{"type":"object","properties":{}}"""));
                }
            }
        }

        ToolChoice toolChoice = default;
        if (root.TryGetObject("tool_choice", out JsonElement choice))
        {
            toolChoice = choice.GetString("type") switch
            {
                "none" => new ToolChoice(ToolChoiceMode.None),
                "any" => new ToolChoice(ToolChoiceMode.Required),
                "tool" => new ToolChoice(ToolChoiceMode.Named, choice.GetString("name")),
                _ => default,
            };
        }

        var stops = new List<string>();
        if (root.TryGetProperty("stop_sequences", out JsonElement stop))
        {
            ReadStrings(stop, stops);
        }

        root.TryGetObject("metadata", out JsonElement metadata);

        return Finish(new ChatCompletionRequest
        {
            Protocol = ChatProtocol.Anthropic,
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
                    _ => 0,
                };
            }

            string text = turn.Text;
            if (text.Length > 0 || turn.Blocks.Count == 0)
            {
                messages.Add(new ChatMessage(turn.Role, text));
            }
        }

        foreach (ToolDefinition tool in request.Tools)
        {
            tokens += TokenEstimator.Estimate(tool.Name) + TokenEstimator.Estimate(tool.Description ?? string.Empty) + TokenEstimator.Estimate(tool.InputSchemaJson) + 8;
        }

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
        };
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
                blocks?.Add(new TextBlock(text));
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
                blocks?.Add(new TextBlock(part.GetString()!));
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
                blocks?.Add(new TextBlock(text));
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
                return new ImageBlock(url[5..semicolon], url[(comma + 1)..], null);
            }
        }

        return new ImageBlock(null, null, url);
    }

    private static string ReadAnthropicText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement part in value.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                Append(builder, part.GetString());
            }
            else if (part.GetString("type") == "text" && part.GetString("text") is { } text)
            {
                Append(builder, text);
            }
        }

        return builder.ToString();
    }

    private static void ReadAnthropicContent(JsonElement content, List<ContentBlock> blocks, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } text)
            {
                blocks.Add(new TextBlock(text));
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
                        blocks.Add(new TextBlock(text));
                    }

                    break;

                case "image":
                    hasImages = true;
                    part.TryGetObject("source", out JsonElement source);
                    blocks.Add(source.GetString("type") == "url"
                        ? new ImageBlock(null, null, source.GetString("url"))
                        : new ImageBlock(source.GetString("media_type"), source.GetString("data"), null));
                    break;

                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        part.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                        part.GetString("name") ?? string.Empty,
                        part.TryGetObject("input", out JsonElement input) ? input.GetRawText() : "{}"));
                    break;

                case "tool_result":
                    bool nestedImages = false;
                    string result = part.TryGetProperty("content", out JsonElement resultContent)
                        ? ReadToolResultContent(resultContent, ref nestedImages)
                        : string.Empty;
                    hasImages |= nestedImages;
                    blocks.Add(new ToolResultBlock(part.GetString("tool_use_id") ?? string.Empty, result, part.GetBool("is_error")));
                    break;

                default:
                    // thinking, redacted_thinking, document, …: not needed for routing or for other backends.
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
