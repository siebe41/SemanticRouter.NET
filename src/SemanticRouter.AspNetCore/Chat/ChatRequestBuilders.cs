using System.Buffers;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Re-expresses a parsed request as an OpenAI <c>/v1/chat/completions</c> body.</summary>
internal static class OpenAIRequestBuilder
{
    public static ReadOnlyMemory<byte> Build(ChatCompletionRequest request, string? model, bool? stream = null)
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

    public static ReadOnlyMemory<byte> Build(ChatCompletionRequest request, string? model, bool? stream = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model ?? request.Model ?? "auto");
            writer.WriteNumber("max_tokens", request.MaxTokens ?? DefaultMaxTokens);

            string system = string.Join("\n\n", request.Turns.Where(t => t.Role == "system").Select(t => t.Text).Where(t => t.Length > 0));
            if (system.Length > 0)
            {
                writer.WriteString("system", system);
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

            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (ToolDefinition tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    if (tool.Description is not null)
                    {
                        writer.WriteString("description", tool.Description);
                    }

                    writer.WritePropertyName("input_schema");
                    OpenAIRequestBuilder.WriteRaw(writer, tool.InputSchemaJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                switch (request.ToolChoice.Mode)
                {
                    case ToolChoiceMode.None:
                        WriteToolChoice(writer, "none", null);
                        break;
                    case ToolChoiceMode.Required:
                        WriteToolChoice(writer, "any", null);
                        break;
                    case ToolChoiceMode.Named when request.ToolChoice.Name is { } name:
                        WriteToolChoice(writer, "tool", name);
                        break;
                }
            }

            if (stream ?? request.Stream)
            {
                writer.WriteBoolean("stream", true);
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

            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static void WriteToolChoice(Utf8JsonWriter writer, string type, string? name)
    {
        writer.WriteStartObject("tool_choice");
        writer.WriteString("type", type);
        if (name is not null)
        {
            writer.WriteString("name", name);
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
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
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
                    writer.WriteEndObject();
                    break;

                case ToolUseBlock call:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    OpenAIRequestBuilder.WriteRaw(writer, JsonHelpers.EnsureJsonObject(call.InputJson));
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

                    writer.WriteEndObject();
                    break;
            }
        }
    }
}
