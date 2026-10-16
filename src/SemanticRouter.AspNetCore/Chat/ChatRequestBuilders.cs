using System.Buffers;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Re-expresses a parsed request as an OpenAI <c>/v1/chat/completions</c> body.</summary>
internal static class OpenAIRequestBuilder
{
    /// <param name="request">The parsed request.</param>
    /// <param name="model">The backend model id.</param>
    /// <param name="stream">Overrides the request's streaming flag.</param>
    /// <param name="reasoning">The target model declares <c>reasoning</c>: send <c>reasoning_effort</c>.</param>
    public static ReadOnlyMemory<byte> Build(ChatCompletionRequest request, string? model, bool? stream = null, bool reasoning = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model ?? request.Model ?? "auto");
            writer.WriteStartArray("messages");
            foreach (ChatTurn turn in request.Turns)
            {
                WriteTurn(writer, turn);
            }

            writer.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (ToolDefinition tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    if (tool.Description is not null)
                    {
                        writer.WriteString("description", tool.Description);
                    }

                    writer.WritePropertyName("parameters");
                    WriteRaw(writer, tool.InputSchemaJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                switch (request.ToolChoice.Mode)
                {
                    case ToolChoiceMode.None:
                        writer.WriteString("tool_choice", "none");
                        break;
                    case ToolChoiceMode.Required:
                        writer.WriteString("tool_choice", "required");
                        break;
                    case ToolChoiceMode.Named when request.ToolChoice.Name is { } name:
                        writer.WriteStartObject("tool_choice");
                        writer.WriteString("type", "function");
                        writer.WriteStartObject("function");
                        writer.WriteString("name", name);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                        break;
                }
            }

            bool streaming = stream ?? request.Stream;
            if (streaming)
            {
                writer.WriteBoolean("stream", true);
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            if (request.MaxTokens is { } maxTokens)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            if (request.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (request.StopSequences.Count > 0)
            {
                writer.WriteStartArray("stop");
                foreach (string stop in request.StopSequences)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            if (request.UserId is { } user)
            {
                writer.WriteString("user", user);
            }

            if (request.PromptCacheKey is { } cacheKey)
            {
                writer.WriteString("prompt_cache_key", cacheKey);
            }

            if (reasoning && request.Reasoning is { } effort)
            {
                writer.WriteString("reasoning_effort", effort.EffectiveEffort is "xhigh" or "max" ? "high" : effort.EffectiveEffort);
            }

            if (request.Tools.Count > 0 && request.ParallelToolCalls == false)
            {
                writer.WriteBoolean("parallel_tool_calls", false);
            }

            if (request.ResponseFormat is { } format)
            {
                writer.WriteStartObject("response_format");
                if (format.Kind == ResponseFormatKind.JsonObject)
                {
                    writer.WriteString("type", "json_object");
                }
                else
                {
                    writer.WriteString("type", "json_schema");
                    writer.WriteStartObject("json_schema");
                    writer.WriteString("name", format.Name ?? "response");
                    writer.WritePropertyName("schema");
                    WriteRaw(writer, format.SchemaJson ?? """{"type":"object"}""");
                    if (format.Strict)
                    {
                        writer.WriteBoolean("strict", true);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static void WriteTurn(Utf8JsonWriter writer, ChatTurn turn)
    {
        // Tool results are separate "tool" messages in the OpenAI protocol.
        foreach (ToolResultBlock result in turn.Blocks.OfType<ToolResultBlock>())
        {
            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            writer.WriteString("tool_call_id", result.ToolUseId);
            writer.WriteString("content", result.IsError && result.Content.Length == 0 ? "The tool call failed." : result.Content);
            writer.WriteEndObject();
        }

        var text = turn.Blocks.Where(b => b is TextBlock or ImageBlock).ToArray();
        var calls = turn.Blocks.OfType<ToolUseBlock>().ToArray();
        if (text.Length == 0 && calls.Length == 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", turn.Role);
        if (text.All(b => b is TextBlock))
        {
            string joined = string.Join('\n', text.Cast<TextBlock>().Select(t => t.Text));
            if (joined.Length > 0 || calls.Length == 0)
            {
                writer.WriteString("content", joined);
            }
            else
            {
                writer.WriteNull("content");
            }
        }
        else
        {
            writer.WriteStartArray("content");
            foreach (ContentBlock block in text)
            {
                writer.WriteStartObject();
                if (block is TextBlock t)
                {
                    writer.WriteString("type", "text");
                    writer.WriteString("text", t.Text);
                }
                else if (block is ImageBlock image)
                {
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", image.Url ?? $"data:{image.MediaType ?? "image/png"};base64,{image.Data}");
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (calls.Length > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (ToolUseBlock call in calls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.InputJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    internal static void WriteRaw(Utf8JsonWriter writer, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteEndObject();
        }
    }
}

/// <summary>Re-expresses a parsed request as an Anthropic <c>/v1/messages</c> body.</summary>
internal static class AnthropicRequestBuilder
{
    /// <summary>Used when an OpenAI client did not set a limit; the Messages API requires one.</summary>
    public const int DefaultMaxTokens = 8192;

    /// <summary>The tool that carries a JSON-schema reply (structured output) on the Messages API.</summary>
    public const string StructuredOutputTool = "structured_output";

    /// <summary>Whether <paramref name="request"/> asks for a schema reply that the builder turns into a forced tool call.</summary>
    public static bool UsesStructuredOutputTool(ChatCompletionRequest request) =>
        request.ResponseFormat is { Kind: ResponseFormatKind.JsonSchema } && request.Tools.Count == 0;

    /// <summary>
    /// Whether a reasoning request can become <c>thinking</c>: it needs a budget of at least 1,024 below
    /// <c>max_tokens</c>, and cannot be combined with the forced structured-output tool.
    /// </summary>
    public static bool CarriesThinking(ChatCompletionRequest request) =>
        request.Reasoning is not null && !UsesStructuredOutputTool(request) && (request.MaxTokens ?? DefaultMaxTokens) > 1024;

    /// <summary>
    /// The shortest prefix, in estimated tokens, that <paramref name="model"/> caches: 4,096 on Opus 4.5 and 4.6 and
    /// Haiku 4.5, 512 on the others. Below it a marker caches nothing.
    /// </summary>
    public static int CacheableMinimum(string model)
    {
        string id = model.Replace('.', '-');
        foreach (string family in (ReadOnlySpan<string>)["claude-opus-4-5", "claude-opus-4-6", "claude-haiku-4-5"])
        {
            int at = id.IndexOf(family, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (at + family.Length == id.Length || !char.IsAsciiDigit(id[at + family.Length])))
            {
                return 4096;
            }
        }

        return 512;
    }

    /// <param name="request">The parsed request.</param>
    /// <param name="model">The backend model id.</param>
    /// <param name="stream">Overrides the request's streaming flag.</param>
    /// <param name="reasoning">The target model declares <c>reasoning</c>: send <c>thinking</c> with a budget.</param>
    /// <param name="caching">
    /// <see cref="PromptCaching.Auto"/>: when the prompt is long enough to cache, mark the end of the system prompt and
    /// turn on automatic caching for the conversation. Never added to a request that carries the client's own markers.
    /// </param>
    /// <param name="cacheTtl"><c>1h</c> for one-hour cache entries; otherwise the five-minute default.</param>
    public static ReadOnlyMemory<byte> Build(
        ChatCompletionRequest request, string? model, bool? stream = null, bool reasoning = false, PromptCaching caching = PromptCaching.Off, string? cacheTtl = null)
    {
        bool structured = UsesStructuredOutputTool(request);
        int maxTokens = request.MaxTokens ?? DefaultMaxTokens;
        int? thinkingBudget = reasoning && CarriesThinking(request) && request.Reasoning is { } wanted
            ? Math.Clamp(wanted.EffectiveBudget, 1024, maxTokens - 1)
            : null;
        string modelId = model ?? request.Model ?? "auto";

        string system = string.Join("\n\n", request.Turns.Where(t => t.Role == "system").Select(t => t.Text).Where(t => t.Length > 0));
        string? jsonInstruction = null;
        if (request.ResponseFormat is { Kind: ResponseFormatKind.JsonObject } || (request.ResponseFormat is not null && !structured))
        {
            // The Messages API has no JSON mode: ask for it in the system prompt (an approximation).
            jsonInstruction = "Reply with a single JSON object and nothing else."
                              + (request.ResponseFormat!.SchemaJson is { } schema ? " It must match this JSON schema: " + schema : string.Empty);
        }

        string fullSystem = jsonInstruction is null ? system : (system.Length > 0 ? system + "\n\n" : string.Empty) + jsonInstruction;

        // The router's own markers: tools and system render first and stay the same every turn, so one breakpoint at
        // their end is read by every later turn; automatic caching moves a second one along the growing conversation.
        string marker = cacheTtl == "1h" ? """{"type":"ephemeral","ttl":"1h"}""" : """{"type":"ephemeral"}""";
        bool markPrefix = false;
        bool markConversation = false;
        if (caching == PromptCaching.Auto && !request.HasCacheMarkers)
        {
            int minimum = CacheableMinimum(modelId);
            int prefix = TokenEstimator.Estimate(fullSystem) + request.Tools.Sum(ChatCompletionRequest.EstimateTokens);
            markPrefix = prefix >= minimum && (fullSystem.Length > 0 || request.Tools.Count > 0);
            markConversation = markPrefix || request.EstimatedInputTokens >= minimum;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteNumber("max_tokens", maxTokens);

            var systemBlocks = request.Turns.Where(t => t.Role == "system").SelectMany(t => t.Blocks).OfType<TextBlock>().Where(b => b.Text.Length > 0).ToArray();
            if (systemBlocks.Any(b => b.CacheControl is not null))
            {
                // The client's own markers keep their place: one system block per part.
                writer.WriteStartArray("system");
                foreach (TextBlock block in systemBlocks)
                {
                    WriteTextBlock(writer, block.Text, block.CacheControl);
                }

                if (jsonInstruction is not null)
                {
                    WriteTextBlock(writer, jsonInstruction, null);
                }

                writer.WriteEndArray();
            }
            else if (fullSystem.Length > 0 && markPrefix)
            {
                // The same text as the plain string, as one block that can carry the marker.
                writer.WriteStartArray("system");
                WriteTextBlock(writer, fullSystem, marker);
                writer.WriteEndArray();
            }
            else if (fullSystem.Length > 0)
            {
                writer.WriteString("system", fullSystem);
            }

            if (thinkingBudget is { } budget)
            {
                writer.WriteStartObject("thinking");
                writer.WriteString("type", "enabled");
                writer.WriteNumber("budget_tokens", budget);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("messages");
            string? openRole = null;
            foreach (ChatTurn turn in request.Turns.Where(t => t.Role != "system"))
            {
                // The Messages API wants strictly alternating roles: merge consecutive same-role turns.
                if (openRole == turn.Role)
                {
                    WriteBlocks(writer, turn);
                    continue;
                }

                if (openRole is not null)
                {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                openRole = turn.Role;
                writer.WriteStartObject();
                writer.WriteString("role", turn.Role);
                writer.WriteStartArray("content");
                WriteBlocks(writer, turn);
            }

            if (openRole is not null)
            {
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            bool serial = request.ParallelToolCalls == false;
            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                for (int i = 0; i < request.Tools.Count; i++)
                {
                    ToolDefinition tool = request.Tools[i];
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    if (tool.Description is not null)
                    {
                        writer.WriteString("description", tool.Description);
                    }

                    writer.WritePropertyName("input_schema");
                    OpenAIRequestBuilder.WriteRaw(writer, tool.InputSchemaJson);

                    // Without a system prompt, the prefix breakpoint goes on the last tool.
                    string? toolMarker = tool.CacheControl ?? (markPrefix && fullSystem.Length == 0 && i == request.Tools.Count - 1 ? marker : null);
                    WriteCacheControl(writer, toolMarker);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                switch (request.ToolChoice.Mode)
                {
                    case ToolChoiceMode.None:
                        WriteToolChoice(writer, "none", null, serial);
                        break;
                    case ToolChoiceMode.Required:
                        WriteToolChoice(writer, "any", null, serial);
                        break;
                    case ToolChoiceMode.Named when request.ToolChoice.Name is { } name:
                        WriteToolChoice(writer, "tool", name, serial);
                        break;
                    default:
                        if (serial)
                        {
                            WriteToolChoice(writer, "auto", null, serial);
                        }

                        break;
                }
            }
            else if (structured)
            {
                // Structured output as a forced call to one tool whose input schema is the requested schema.
                writer.WriteStartArray("tools");
                writer.WriteStartObject();
                writer.WriteString("name", StructuredOutputTool);
                writer.WriteString("description", "Reply by calling this tool with the answer as its input.");
                writer.WritePropertyName("input_schema");
                OpenAIRequestBuilder.WriteRaw(writer, request.ResponseFormat!.SchemaJson ?? """{"type":"object"}""");
                writer.WriteEndObject();
                writer.WriteEndArray();
                WriteToolChoice(writer, "tool", StructuredOutputTool, false);
            }

            if (stream ?? request.Stream)
            {
                writer.WriteBoolean("stream", true);
            }

            // With thinking on, the Messages API only accepts the default temperature.
            if (request.Temperature is { } temperature && thinkingBudget is null)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP && thinkingBudget is null)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (request.StopSequences.Count > 0)
            {
                writer.WriteStartArray("stop_sequences");
                foreach (string stop in request.StopSequences)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            if (request.UserId is { } user)
            {
                writer.WriteStartObject("metadata");
                writer.WriteString("user_id", user);
                writer.WriteEndObject();
            }

            // Automatic caching: Anthropic puts this breakpoint on the last cacheable block and moves it forward as
            // the conversation grows.
            WriteCacheControl(writer, request.CacheControl ?? (markConversation ? marker : null));
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static void WriteTextBlock(Utf8JsonWriter writer, string text, string? cacheControl)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        WriteCacheControl(writer, cacheControl);
        writer.WriteEndObject();
    }

    private static void WriteCacheControl(Utf8JsonWriter writer, string? cacheControl)
    {
        if (cacheControl is not null)
        {
            writer.WritePropertyName("cache_control");
            OpenAIRequestBuilder.WriteRaw(writer, cacheControl);
        }
    }

    private static void WriteToolChoice(Utf8JsonWriter writer, string type, string? name, bool disableParallel)
    {
        writer.WriteStartObject("tool_choice");
        writer.WriteString("type", type);
        if (name is not null)
        {
            writer.WriteString("name", name);
        }

        if (disableParallel && type != "none")
        {
            writer.WriteBoolean("disable_parallel_tool_use", true);
        }

        writer.WriteEndObject();
    }

    private static void WriteBlocks(Utf8JsonWriter writer, ChatTurn turn)
    {
        foreach (ContentBlock block in turn.Blocks)
        {
            switch (block)
            {
                case TextBlock { Text.Length: > 0 } text:
                    WriteTextBlock(writer, text.Text, text.CacheControl);
                    break;

                case ImageBlock image:
                    writer.WriteStartObject();
                    writer.WriteString("type", "image");
                    writer.WriteStartObject("source");
                    if (image.Data is not null)
                    {
                        writer.WriteString("type", "base64");
                        writer.WriteString("media_type", image.MediaType ?? "image/png");
                        writer.WriteString("data", image.Data);
                    }
                    else
                    {
                        writer.WriteString("type", "url");
                        writer.WriteString("url", image.Url ?? string.Empty);
                    }

                    writer.WriteEndObject();
                    WriteCacheControl(writer, image.CacheControl);
                    writer.WriteEndObject();
                    break;

                case ToolUseBlock call:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    OpenAIRequestBuilder.WriteRaw(writer, JsonHelpers.EnsureJsonObject(call.InputJson));
                    WriteCacheControl(writer, call.CacheControl);
                    writer.WriteEndObject();
                    break;

                case ToolResultBlock result:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", result.ToolUseId);
                    writer.WriteString("content", result.Content);
                    if (result.IsError)
                    {
                        writer.WriteBoolean("is_error", true);
                    }

                    WriteCacheControl(writer, result.CacheControl);
                    writer.WriteEndObject();
                    break;
            }
        }
    }
}

/// <summary>Re-expresses a parsed request as an OpenAI Responses API (<c>/v1/responses</c>) body.</summary>
internal static class ResponsesRequestBuilder
{
    /// <param name="request">The parsed request.</param>
    /// <param name="model">The backend model id.</param>
    /// <param name="stream">Overrides the request's streaming flag.</param>
    /// <param name="reasoning">The target model declares <c>reasoning</c>: send <c>reasoning.effort</c>.</param>
    public static ReadOnlyMemory<byte> Build(ChatCompletionRequest request, string? model, bool? stream = null, bool reasoning = false)
    {
        var customTools = request.Tools.Where(t => t.Kind == ToolKind.Custom).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var customCalls = request.Turns.SelectMany(t => t.Blocks).OfType<ToolUseBlock>()
            .Where(u => customTools.Contains(u.Name)).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model ?? request.Model ?? "auto");
            writer.WriteBoolean("store", false);
            string instructions = string.Join("\n\n", request.Turns.Where(t => t.Role == "system").Select(t => t.Text).Where(t => t.Length > 0));
            if (instructions.Length > 0)
            {
                writer.WriteString("instructions", instructions);
            }

            writer.WriteStartArray("input");
            foreach (ChatTurn turn in request.Turns.Where(t => t.Role != "system"))
            {
                WriteTurn(writer, turn, customTools, customCalls);
            }

            writer.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (ToolDefinition tool in request.Tools)
                {
                    writer.WriteStartObject();
                    if (tool.Kind == ToolKind.Custom)
                    {
                        writer.WriteString("type", "custom");
                        writer.WriteString("name", tool.Name);
                        if (tool.Description is not null)
                        {
                            writer.WriteString("description", tool.Description);
                        }
                    }
                    else
                    {
                        writer.WriteString("type", "function");
                        writer.WriteString("name", tool.Name);
                        if (tool.Description is not null)
                        {
                            writer.WriteString("description", tool.Description);
                        }

                        writer.WritePropertyName("parameters");
                        OpenAIRequestBuilder.WriteRaw(writer, tool.InputSchemaJson);
                        writer.WriteBoolean("strict", false);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                switch (request.ToolChoice.Mode)
                {
                    case ToolChoiceMode.None:
                        writer.WriteString("tool_choice", "none");
                        break;
                    case ToolChoiceMode.Required:
                        writer.WriteString("tool_choice", "required");
                        break;
                    case ToolChoiceMode.Named when request.ToolChoice.Name is { } name:
                        writer.WriteStartObject("tool_choice");
                        writer.WriteString("type", customTools.Contains(name) ? "custom" : "function");
                        writer.WriteString("name", name);
                        writer.WriteEndObject();
                        break;
                }

                if (request.ParallelToolCalls == false)
                {
                    writer.WriteBoolean("parallel_tool_calls", false);
                }
            }

            if (stream ?? request.Stream)
            {
                writer.WriteBoolean("stream", true);
            }

            if (request.MaxTokens is { } maxTokens)
            {
                writer.WriteNumber("max_output_tokens", maxTokens);
            }

            if (request.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (reasoning && request.Reasoning is { } effort)
            {
                writer.WriteStartObject("reasoning");
                writer.WriteString("effort", effort.EffectiveEffort);
                writer.WriteString("summary", "auto");
                writer.WriteEndObject();
            }

            if (request.ResponseFormat is { } format)
            {
                writer.WriteStartObject("text");
                writer.WriteStartObject("format");
                if (format.Kind == ResponseFormatKind.JsonObject)
                {
                    writer.WriteString("type", "json_object");
                }
                else
                {
                    writer.WriteString("type", "json_schema");
                    writer.WriteString("name", format.Name ?? "response");
                    writer.WritePropertyName("schema");
                    OpenAIRequestBuilder.WriteRaw(writer, format.SchemaJson ?? """{"type":"object"}""");
                    writer.WriteBoolean("strict", format.Strict);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            if (request.UserId is { } user)
            {
                writer.WriteString("safety_identifier", user);
            }

            if (request.PromptCacheKey is { } cacheKey)
            {
                writer.WriteString("prompt_cache_key", cacheKey);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static void WriteTurn(Utf8JsonWriter writer, ChatTurn turn, HashSet<string> customTools, HashSet<string> customCalls)
    {
        var content = turn.Blocks.Where(b => b is TextBlock or ImageBlock).ToArray();
        if (content.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteString("role", turn.Role);
            writer.WriteStartArray("content");
            foreach (ContentBlock block in content)
            {
                writer.WriteStartObject();
                if (block is TextBlock text)
                {
                    writer.WriteString("type", turn.Role == "assistant" ? "output_text" : "input_text");
                    writer.WriteString("text", text.Text);
                }
                else if (block is ImageBlock image)
                {
                    writer.WriteString("type", "input_image");
                    writer.WriteString("image_url", image.Url ?? $"data:{image.MediaType ?? "image/png"};base64,{image.Data}");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        foreach (ContentBlock block in turn.Blocks)
        {
            switch (block)
            {
                case ToolUseBlock call when customTools.Contains(call.Name):
                    writer.WriteStartObject();
                    writer.WriteString("type", "custom_tool_call");
                    writer.WriteString("call_id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("input", CustomInput(call.InputJson));
                    writer.WriteEndObject();
                    break;

                case ToolUseBlock call:
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call");
                    writer.WriteString("call_id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.InputJson);
                    writer.WriteEndObject();
                    break;

                case ToolResultBlock result:
                    writer.WriteStartObject();
                    writer.WriteString("type", customCalls.Contains(result.ToolUseId) ? "custom_tool_call_output" : "function_call_output");
                    writer.WriteString("call_id", result.ToolUseId);
                    writer.WriteString("output", result.IsError && result.Content.Length == 0 ? "The tool call failed." : result.Content);
                    writer.WriteEndObject();
                    break;
            }
        }
    }

    private static string CustomInput(string argumentsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.GetString("input") ?? argumentsJson;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }
}
