using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Reads <c>data:</c> lines from a server-sent events stream.</summary>
internal static class ServerSentEvents
{
    /// <summary>Yields each event's data payload (multi-line data joined with newlines).</summary>
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (data.Length > 0)
        {
            yield return data.ToString();
        }
    }
}

/// <summary>Relays an OpenAI chat-completions response (JSON or SSE) through a protocol writer.</summary>
internal static class OpenAIResponseReader
{
    public static async Task RelayAsync(HttpResponseMessage response, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        bool sse = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (!sse)
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                await RelayJsonAsync(document.RootElement, writer, cancellationToken).ConfigureAwait(false);
                return;
            }

            ChatStopReason stop = ChatStopReason.EndTurn;
            ChatUsage usage = default;
            int openToolIndex = -1;
            await foreach (string data in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (data == "[DONE]")
                {
                    break;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (root.TryGetObject("usage", out JsonElement usageElement))
                    {
                        usage = ReadUsage(usageElement);
                    }

                    if (!root.TryGetArray("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    JsonElement choice = choices[0];
                    if (choice.TryGetObject("delta", out JsonElement delta))
                    {
                        if (delta.GetString("content") is { Length: > 0 } text)
                        {
                            await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                        }

                        if (delta.TryGetArray("tool_calls", out JsonElement calls))
                        {
                            foreach (JsonElement call in calls.EnumerateArray())
                            {
                                int index = call.GetInt("index") ?? openToolIndex + 1;
                                call.TryGetObject("function", out JsonElement function);
                                if (index != openToolIndex)
                                {
                                    if (openToolIndex >= 0)
                                    {
                                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                                    }

                                    openToolIndex = index;
                                    await writer.StartToolCallAsync(
                                        call.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                                        function.GetString("name") ?? string.Empty,
                                        cancellationToken).ConfigureAwait(false);
                                }

                                if (function.GetString("arguments") is { Length: > 0 } arguments)
                                {
                                    await writer.WriteToolCallArgumentsAsync(arguments, cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    if (choice.GetString("finish_reason") is { } finish)
                    {
                        stop = MapFinishReason(finish);
                    }
                }
            }

            if (openToolIndex >= 0)
            {
                await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(stop, usage, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RelayJsonAsync(JsonElement root, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        ChatStopReason stop = ChatStopReason.EndTurn;
        if (root.TryGetArray("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            JsonElement choice = choices[0];
            if (choice.TryGetObject("message", out JsonElement message))
            {
                if (message.TryGetProperty("content", out JsonElement content))
                {
                    string text = content.ValueKind == JsonValueKind.String
                        ? content.GetString()!
                        : content.ValueKind == JsonValueKind.Array
                            ? string.Concat(content.EnumerateArray().Select(p => p.GetString("text")))
                            : string.Empty;
                    if (text.Length > 0)
                    {
                        await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (message.TryGetArray("tool_calls", out JsonElement calls))
                {
                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        call.TryGetObject("function", out JsonElement function);
                        await writer.StartToolCallAsync(
                            call.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                            function.GetString("name") ?? string.Empty,
                            cancellationToken).ConfigureAwait(false);
                        await writer.WriteToolCallArgumentsAsync(function.GetString("arguments") ?? "{}", cancellationToken).ConfigureAwait(false);
                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (choice.GetString("finish_reason") is { } finish)
            {
                stop = MapFinishReason(finish);
            }
        }

        ChatUsage usage = root.TryGetObject("usage", out JsonElement usageElement) ? ReadUsage(usageElement) : default;
        await writer.CompleteAsync(stop, usage, cancellationToken).ConfigureAwait(false);
    }

    private static ChatStopReason MapFinishReason(string finish) => finish switch
    {
        "tool_calls" or "function_call" => ChatStopReason.ToolUse,
        "length" => ChatStopReason.MaxTokens,
        _ => ChatStopReason.EndTurn,
    };

    private static ChatUsage ReadUsage(JsonElement usage)
    {
        int? cached = usage.TryGetObject("prompt_tokens_details", out JsonElement details) ? details.GetInt("cached_tokens") : null;
        int? prompt = usage.GetInt("prompt_tokens");
        return new ChatUsage(prompt is { } p && cached is { } c ? p - c : prompt, usage.GetInt("completion_tokens"), cached);
    }
}

/// <summary>Relays an Anthropic Messages response (JSON or SSE) through a protocol writer.</summary>
internal static class AnthropicResponseReader
{
    public static async Task RelayAsync(HttpResponseMessage response, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        bool sse = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (!sse)
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                await RelayJsonAsync(document.RootElement, writer, cancellationToken).ConfigureAwait(false);
                return;
            }

            ChatStopReason stop = ChatStopReason.EndTurn;
            int? inputTokens = null, outputTokens = null, cacheRead = null, cacheCreate = null;
            bool toolOpen = false;
            await foreach (string data in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    switch (root.GetString("type"))
                    {
                        case "message_start" when root.TryGetObject("message", out JsonElement message) && message.TryGetObject("usage", out JsonElement startUsage):
                            inputTokens = startUsage.GetInt("input_tokens");
                            cacheRead = startUsage.GetInt("cache_read_input_tokens");
                            cacheCreate = startUsage.GetInt("cache_creation_input_tokens");
                            break;

                        case "content_block_start" when root.TryGetObject("content_block", out JsonElement block):
                            if (block.GetString("type") == "tool_use")
                            {
                                toolOpen = true;
                                await writer.StartToolCallAsync(
                                    block.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                                    block.GetString("name") ?? string.Empty,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else if (block.GetString("type") == "text" && block.GetString("text") is { Length: > 0 } initial)
                            {
                                await writer.WriteTextAsync(initial, cancellationToken).ConfigureAwait(false);
                            }

                            break;

                        case "content_block_delta" when root.TryGetObject("delta", out JsonElement delta):
                            switch (delta.GetString("type"))
                            {
                                case "text_delta" when delta.GetString("text") is { Length: > 0 } text:
                                    await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                                    break;
                                case "input_json_delta" when delta.GetString("partial_json") is { Length: > 0 } json:
                                    await writer.WriteToolCallArgumentsAsync(json, cancellationToken).ConfigureAwait(false);
                                    break;
                            }

                            break;

                        case "content_block_stop":
                            if (toolOpen)
                            {
                                toolOpen = false;
                                await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                            }

                            break;

                        case "message_delta":
                            if (root.TryGetObject("delta", out JsonElement messageDelta) && messageDelta.GetString("stop_reason") is { } reason)
                            {
                                stop = MapStopReason(reason);
                            }

                            if (root.TryGetObject("usage", out JsonElement deltaUsage))
                            {
                                outputTokens = deltaUsage.GetInt("output_tokens") ?? outputTokens;
                                inputTokens = deltaUsage.GetInt("input_tokens") ?? inputTokens;
                            }

                            break;

                        case "error":
                            // Mid-stream upstream error: end the reply cleanly with what was received.
                            break;
                    }
                }
            }

            if (toolOpen)
            {
                await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(stop, new ChatUsage(inputTokens, outputTokens, cacheRead, cacheCreate), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RelayJsonAsync(JsonElement root, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        if (root.TryGetArray("content", out JsonElement content))
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                switch (block.GetString("type"))
                {
                    case "text" when block.GetString("text") is { Length: > 0 } text:
                        await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                        break;
                    case "tool_use":
                        await writer.StartToolCallAsync(
                            block.GetString("id") is { Length: > 0 } id ? id : JsonHelpers.NewToolUseId(),
                            block.GetString("name") ?? string.Empty,
                            cancellationToken).ConfigureAwait(false);
                        await writer.WriteToolCallArgumentsAsync(
                            block.TryGetObject("input", out JsonElement input) ? input.GetRawText() : "{}", cancellationToken).ConfigureAwait(false);
                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        ChatUsage usage = default;
        if (root.TryGetObject("usage", out JsonElement usageElement))
        {
            usage = new ChatUsage(
                usageElement.GetInt("input_tokens"),
                usageElement.GetInt("output_tokens"),
                usageElement.GetInt("cache_read_input_tokens"),
                usageElement.GetInt("cache_creation_input_tokens"));
        }

        await writer.CompleteAsync(MapStopReason(root.GetString("stop_reason") ?? "end_turn"), usage, cancellationToken).ConfigureAwait(false);
    }

    private static ChatStopReason MapStopReason(string reason) => reason switch
    {
        "tool_use" => ChatStopReason.ToolUse,
        "max_tokens" => ChatStopReason.MaxTokens,
        "stop_sequence" => ChatStopReason.StopSequence,
        _ => ChatStopReason.EndTurn,
    };
}

/// <summary>
/// Watches a response that is being passed through untouched and records the tool-call ids it contains, so a
/// follow-up request carrying their results can be pinned to the same model.
/// </summary>
internal static class PassThroughToolCallScanner
{
    public static async Task CopyAsync(
        Stream upstream, Stream downstream, bool sse, ChatProtocol protocol, Action<string> onToolCall, CancellationToken cancellationToken, Action<ChatUsage>? onUsage = null)
    {
        if (!sse)
        {
            using var buffer = new MemoryStream();
            await upstream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
            ScanJson(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), protocol, onToolCall, onUsage);
            return;
        }

        // Line-oriented copy: forward every line as soon as it arrives, inspecting the data lines on the way past.
        using var reader = new StreamReader(upstream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await downstream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal)
                && (line.Contains("\"id\"", StringComparison.Ordinal) || (onUsage is not null && line.Contains("\"usage\"", StringComparison.Ordinal))))
            {
                ScanJson(System.Text.Encoding.UTF8.GetBytes(line.AsSpan(5).TrimStart().ToString()), protocol, onToolCall, onUsage);
            }
        }

        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ScanJson(ReadOnlyMemory<byte> json, ChatProtocol protocol, Action<string> onToolCall, Action<ChatUsage>? onUsage)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (onUsage is not null && root.ValueKind == JsonValueKind.Object && UsageCapture.Find(root, protocol) is { } usage)
            {
                onUsage(usage);
            }

            if (protocol == ChatProtocol.Anthropic)
            {
                if (root.TryGetObject("content_block", out JsonElement block) && block.GetString("type") == "tool_use" && block.GetString("id") is { } id)
                {
                    onToolCall(id);
                }

                if (root.TryGetArray("content", out JsonElement content))
                {
                    foreach (JsonElement item in content.EnumerateArray())
                    {
                        if (item.GetString("type") == "tool_use" && item.GetString("id") is { } blockId)
                        {
                            onToolCall(blockId);
                        }
                    }
                }
            }
            else if (root.TryGetArray("choices", out JsonElement choices))
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    JsonElement holder = choice.TryGetObject("delta", out JsonElement delta) ? delta : choice.TryGetObject("message", out JsonElement message) ? message : default;
                    if (holder.TryGetArray("tool_calls", out JsonElement calls))
                    {
                        foreach (JsonElement call in calls.EnumerateArray())
                        {
                            if (call.GetString("id") is { Length: > 0 } callId)
                            {
                                onToolCall(callId);
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. "[DONE]"): nothing to record.
        }
    }
}
