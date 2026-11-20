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
                    if (UpstreamReplyException.TryFind(root, ChatProtocol.OpenAI, out UpstreamReplyException? failed))
                    {
                        throw failed;
                    }

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
                        if ((delta.GetString("reasoning_content") ?? delta.GetString("reasoning")) is { Length: > 0 } reasoning)
                        {
                            await writer.WriteThinkingAsync(reasoning, cancellationToken).ConfigureAwait(false);
                        }

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
        if (UpstreamReplyException.TryFind(root, ChatProtocol.OpenAI, out UpstreamReplyException? failed))
        {
            throw failed;
        }

        ChatStopReason stop = ChatStopReason.EndTurn;
        if (root.TryGetArray("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            JsonElement choice = choices[0];
            if (choice.TryGetObject("message", out JsonElement message))
            {
                if ((message.GetString("reasoning_content") ?? message.GetString("reasoning")) is { Length: > 0 } reasoning)
                {
                    await writer.WriteThinkingAsync(reasoning, cancellationToken).ConfigureAwait(false);
                }

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
        "content_filter" => ChatStopReason.Refusal,
        _ => ChatStopReason.EndTurn,
    };

    private static ChatUsage ReadUsage(JsonElement usage)
    {
        int? cached = usage.TryGetObject("prompt_tokens_details", out JsonElement details) ? details.GetInt("cached_tokens") : null;
        int? prompt = usage.GetInt("prompt_tokens");
        return new ChatUsage
        {
            InputTokens = prompt is { } p && cached is { } c ? p - c : prompt,
            OutputTokens = usage.GetInt("completion_tokens"),
            CacheReadTokens = cached,
        };
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
                                case "thinking_delta" when delta.GetString("thinking") is { Length: > 0 } thinking:
                                    await writer.WriteThinkingAsync(thinking, cancellationToken).ConfigureAwait(false);
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
                                cacheRead = deltaUsage.GetInt("cache_read_input_tokens") ?? cacheRead;
                                cacheCreate = deltaUsage.GetInt("cache_creation_input_tokens") ?? cacheCreate;
                            }

                            break;

                        case "error" when UpstreamReplyException.TryFind(root, ChatProtocol.Anthropic, out UpstreamReplyException? failed):
                            // An upstream error (overloaded, rate limited) is not a finished reply, however much came before it.
                            throw failed;
                    }
                }
            }

            if (toolOpen)
            {
                await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
            }

            var usage = new ChatUsage { InputTokens = inputTokens, OutputTokens = outputTokens, CacheReadTokens = cacheRead, CacheCreationTokens = cacheCreate };
            await writer.CompleteAsync(stop, usage, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RelayJsonAsync(JsonElement root, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        if (UpstreamReplyException.TryFind(root, ChatProtocol.Anthropic, out UpstreamReplyException? failed))
        {
            throw failed;
        }

        if (root.TryGetArray("content", out JsonElement content))
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                switch (block.GetString("type"))
                {
                    case "text" when block.GetString("text") is { Length: > 0 } text:
                        await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                        break;
                    case "thinking" when block.GetString("thinking") is { Length: > 0 } thinking:
                        await writer.WriteThinkingAsync(thinking, cancellationToken).ConfigureAwait(false);
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
            usage = new ChatUsage
            {
                InputTokens = usageElement.GetInt("input_tokens"),
                OutputTokens = usageElement.GetInt("output_tokens"),
                CacheReadTokens = usageElement.GetInt("cache_read_input_tokens"),
                CacheCreationTokens = usageElement.GetInt("cache_creation_input_tokens"),
            };
        }

        await writer.CompleteAsync(MapStopReason(root.GetString("stop_reason") ?? "end_turn"), usage, cancellationToken).ConfigureAwait(false);
    }

    private static ChatStopReason MapStopReason(string reason) => reason switch
    {
        "tool_use" => ChatStopReason.ToolUse,
        "max_tokens" => ChatStopReason.MaxTokens,
        "stop_sequence" => ChatStopReason.StopSequence,
        "refusal" => ChatStopReason.Refusal,
        "pause_turn" => ChatStopReason.PauseTurn,
        _ => ChatStopReason.EndTurn,
    };
}

/// <summary>
/// Watches a response that is being passed through untouched and records the tool-call ids it contains, so a
/// follow-up request carrying their results can be pinned to the same model. A reply that reports an error instead of
/// an answer throws <see cref="UpstreamReplyException"/>: before anything is sent when the error comes first (a JSON
/// reply, or a stream's first event), so the next model can answer; otherwise once the stream, the backend's own error
/// event included, has reached the client.
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
            if (ScanJson(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), protocol, onToolCall, onUsage) is { } error)
            {
                throw error;
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Line-oriented copy: forward every line as soon as it arrives, inspecting the data lines on the way past. What
        // comes before the first data line is held back until that line shows the reply is not an error.
        using var reader = new StreamReader(upstream);
        List<string>? held = [];
        UpstreamReplyException? failed = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            bool data = line.StartsWith("data:", StringComparison.Ordinal);
            UpstreamReplyException? error = data
                && (line.Contains("\"id\"", StringComparison.Ordinal) || line.Contains("\"call_id\"", StringComparison.Ordinal)
                    || line.Contains("\"error\"", StringComparison.Ordinal)
                    || (onUsage is not null && line.Contains("\"usage\"", StringComparison.Ordinal)))
                ? ScanJson(System.Text.Encoding.UTF8.GetBytes(line.AsSpan(5).TrimStart().ToString()), protocol, onToolCall, onUsage)
                : null;
            if (held is not null)
            {
                if (error is not null)
                {
                    throw error; // nothing has reached the client yet
                }

                held.Add(line);
                if (data)
                {
                    await WriteLinesAsync(downstream, held, cancellationToken).ConfigureAwait(false);
                    held = null;
                }

                continue;
            }

            failed ??= error;
            await downstream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteLinesAsync(downstream, held ?? [], cancellationToken).ConfigureAwait(false);
        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (failed is not null)
        {
            // The client has the backend's own error event; the reply still did not finish.
            throw new UpstreamReplyException(failed.Failure, clientNotified: true);
        }
    }

    private static async Task WriteLinesAsync(Stream downstream, List<string> lines, CancellationToken cancellationToken)
    {
        foreach (string line in lines)
        {
            await downstream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reports the tool calls and usage in one reply or event, and returns the error it reports instead of an answer, if any.</summary>
    private static UpstreamReplyException? ScanJson(ReadOnlyMemory<byte> json, ChatProtocol protocol, Action<string> onToolCall, Action<ChatUsage>? onUsage)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (UpstreamReplyException.TryFind(root, protocol, out UpstreamReplyException? error))
            {
                return error;
            }

            if (onUsage is not null && root.ValueKind == JsonValueKind.Object && UsageCapture.Find(root, protocol) is { } usage)
            {
                onUsage(usage);
            }

            if (protocol == ChatProtocol.Responses)
            {
                // Stream events carry the item; the JSON response lists them under "output".
                IEnumerable<JsonElement> items = root.TryGetObject("item", out JsonElement single) ? [single]
                    : root.TryGetArray("output", out JsonElement output) ? output.EnumerateArray()
                    : [];
                foreach (JsonElement item in items)
                {
                    if (item.GetString("type") is "function_call" or "custom_tool_call" && item.GetString("call_id") is { Length: > 0 } callId)
                    {
                        onToolCall(callId);
                    }
                }
            }
            else if (protocol == ChatProtocol.Anthropic)
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

        return null;
    }
}

/// <summary>
/// Relays an OpenAI Responses API reply (JSON or the event stream) through a protocol writer: output text, reasoning
/// summaries, function calls and custom tool calls (whose raw input becomes an <c>{"input": …}</c> argument).
/// </summary>
internal static class ResponsesResponseReader
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

            var customInputs = new Dictionary<string, System.Text.StringBuilder>(StringComparer.Ordinal);
            string? openCall = null;
            bool sawToolCall = false;
            JsonElement? final = null;
            JsonDocument? finalDocument = null;
            try
            {
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

                    JsonElement root = document.RootElement;
                    if (UpstreamReplyException.TryFind(root, ChatProtocol.Responses, out UpstreamReplyException? failed))
                    {
                        document.Dispose();
                        throw failed;
                    }

                    bool keep = false;
                    switch (root.GetString("type"))
                    {
                        case "response.output_text.delta" when root.GetString("delta") is { Length: > 0 } text:
                            await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                            break;

                        case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta" when root.GetString("delta") is { Length: > 0 } thinking:
                            await writer.WriteThinkingAsync(thinking, cancellationToken).ConfigureAwait(false);
                            break;

                        case "response.output_item.added" when root.TryGetObject("item", out JsonElement item)
                                                              && item.GetString("type") is "function_call" or "custom_tool_call":
                            openCall = item.GetString("id") ?? item.GetString("call_id");
                            sawToolCall = true;
                            await writer.StartToolCallAsync(
                                item.GetString("call_id") ?? JsonHelpers.NewToolUseId(), WireName(item), cancellationToken).ConfigureAwait(false);
                            if (item.GetString("type") == "custom_tool_call" && openCall is not null)
                            {
                                customInputs[openCall] = new System.Text.StringBuilder();
                            }

                            break;

                        case "response.function_call_arguments.delta" when root.GetString("delta") is { Length: > 0 } arguments:
                            await writer.WriteToolCallArgumentsAsync(arguments, cancellationToken).ConfigureAwait(false);
                            break;

                        case "response.custom_tool_call_input.delta" when root.GetString("delta") is { } input:
                            if (root.GetString("item_id") is { } inputItem && customInputs.TryGetValue(inputItem, out System.Text.StringBuilder? buffer))
                            {
                                buffer.Append(input);
                            }

                            break;

                        case "response.output_item.done" when root.TryGetObject("item", out JsonElement done)
                                                             && done.GetString("type") is "function_call" or "custom_tool_call":
                            string? key = done.GetString("id") ?? done.GetString("call_id");
                            if (done.GetString("type") == "custom_tool_call")
                            {
                                string raw = key is not null && customInputs.TryGetValue(key, out System.Text.StringBuilder? collected) && collected.Length > 0
                                    ? collected.ToString()
                                    : done.GetString("input") ?? string.Empty;
                                await writer.WriteToolCallArgumentsAsync(CustomArguments(raw), cancellationToken).ConfigureAwait(false);
                            }

                            await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                            openCall = null;
                            break;

                        case "response.completed" or "response.incomplete" when root.TryGetObject("response", out JsonElement finished):
                            finalDocument?.Dispose();
                            finalDocument = document;
                            final = finished;
                            keep = true;
                            break;
                    }

                    if (!keep)
                    {
                        document.Dispose();
                    }
                }

                // response.completed may leave out the output it already streamed.
                ChatStopReason stop = final is { } f ? StopReasonOf(f) : ChatStopReason.EndTurn;
                await writer.CompleteAsync(
                    sawToolCall && stop == ChatStopReason.EndTurn ? ChatStopReason.ToolUse : stop,
                    final is { } u && u.TryGetObject("usage", out JsonElement usage) ? Usage(usage) : default,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                finalDocument?.Dispose();
            }
        }
    }

    /// <summary>The arguments a non-Responses client sees for a custom tool call.</summary>
    public static string CustomArguments(string input) => JsonHelpers.InputObject(input);

    public static ChatUsage Usage(JsonElement usage)
    {
        int? cached = usage.TryGetObject("input_tokens_details", out JsonElement details) ? details.GetInt("cached_tokens") : null;
        int? input = usage.GetInt("input_tokens");
        return new ChatUsage
        {
            InputTokens = input is { } i && cached is { } c ? i - c : input,
            OutputTokens = usage.GetInt("output_tokens"),
            CacheReadTokens = cached is > 0 ? cached : null,
        };
    }

    private static async Task RelayJsonAsync(JsonElement root, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        if (UpstreamReplyException.TryFind(root, ChatProtocol.Responses, out UpstreamReplyException? failed))
        {
            throw failed;
        }

        if (root.TryGetArray("output", out JsonElement output))
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                switch (item.GetString("type"))
                {
                    case "reasoning" when item.TryGetArray("summary", out JsonElement summary):
                        foreach (JsonElement part in summary.EnumerateArray())
                        {
                            if (part.GetString("text") is { Length: > 0 } thinking)
                            {
                                await writer.WriteThinkingAsync(thinking, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        break;

                    case "message" when item.TryGetArray("content", out JsonElement content):
                        foreach (JsonElement part in content.EnumerateArray())
                        {
                            if ((part.GetString("text") ?? part.GetString("refusal")) is { Length: > 0 } text)
                            {
                                await writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        break;

                    case "function_call" or "custom_tool_call":
                        await writer.StartToolCallAsync(item.GetString("call_id") ?? JsonHelpers.NewToolUseId(), WireName(item), cancellationToken).ConfigureAwait(false);
                        await writer.WriteToolCallArgumentsAsync(
                            item.GetString("type") == "custom_tool_call" ? CustomArguments(item.GetString("input") ?? string.Empty) : item.GetString("arguments") ?? "{}",
                            cancellationToken).ConfigureAwait(false);
                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        await writer.CompleteAsync(StopReasonOf(root), root.TryGetObject("usage", out JsonElement usage) ? Usage(usage) : default, cancellationToken).ConfigureAwait(false);
    }

    private static string WireName(JsonElement item) => ChatCompletionRequest.WireToolName(item.GetString("namespace"), item.GetString("name") ?? string.Empty);

    private static ChatStopReason StopReasonOf(JsonElement response)
    {
        if (response.TryGetArray("output", out JsonElement output)
            && output.EnumerateArray().Any(i => i.GetString("type") is "function_call" or "custom_tool_call"))
        {
            return ChatStopReason.ToolUse;
        }

        return response.TryGetObject("incomplete_details", out JsonElement incomplete)
            ? incomplete.GetString("reason") switch
            {
                "max_output_tokens" => ChatStopReason.MaxTokens,
                "content_filter" => ChatStopReason.Refusal,
                _ => ChatStopReason.EndTurn,
            }
            : ChatStopReason.EndTurn;
    }
}
