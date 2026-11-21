using System.Buffers;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Reads <c>data:</c> lines from a server-sent events stream.</summary>
internal static class ServerSentEvents
{
    /// <summary>
    /// Yields each event's data payload (multi-line data joined with newlines). A line or an event longer than
    /// <see cref="UpstreamLimits.MaxLineLength"/> ends the stream with an <see cref="UpstreamLimitException"/>.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        await foreach (string line in ReadLinesAsync(reader, UpstreamLimits.MaxLineLength, cancellationToken).ConfigureAwait(false))
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
                if (data.Length + line.Length > UpstreamLimits.MaxLineLength)
                {
                    throw TooLong("an event");
                }

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

    /// <summary>
    /// Lines as <see cref="TextReader.ReadLineAsync(CancellationToken)"/> reads them (ended by <c>\n</c>, <c>\r</c> or
    /// <c>\r\n</c>), but none longer than <paramref name="maxLength"/>: a longer one throws
    /// <see cref="UpstreamLimitException"/> instead of being buffered without end.
    /// </summary>
    internal static async IAsyncEnumerable<string> ReadLinesAsync(
        TextReader reader, int maxLength, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var line = new System.Text.StringBuilder();
        bool afterCarriageReturn = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c == '\n' && afterCarriageReturn)
                {
                    // The second half of a "\r\n".
                    afterCarriageReturn = false;
                    start = i + 1;
                    continue;
                }

                afterCarriageReturn = c == '\r';
                if (c is '\r' or '\n')
                {
                    Append(line, buffer, start, i - start, maxLength);
                    yield return line.ToString();
                    line = line.Capacity > 64 * 1024 ? new System.Text.StringBuilder() : line.Clear();
                    start = i + 1;
                }
            }

            Append(line, buffer, start, read - start, maxLength);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static void Append(System.Text.StringBuilder line, char[] buffer, int start, int count, int maxLength)
    {
        if (line.Length + count > maxLength)
        {
            throw TooLong("a line");
        }

        line.Append(buffer, start, count);
    }

    private static UpstreamLimitException TooLong(string what) =>
        new($"{what} of the reply is longer than {UpstreamLimits.MaxLineLength / (1024 * 1024)} Mi characters");
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
                using JsonDocument document = await JsonDocument.ParseAsync(UpstreamLimits.Limit(stream, UpstreamLimits.MaxReplyBytes), cancellationToken: cancellationToken).ConfigureAwait(false);
                await RelayJsonAsync(document.RootElement, writer, cancellationToken).ConfigureAwait(false);
                return;
            }

            ChatStopReason stop = ChatStopReason.EndTurn;
            ChatUsage usage = default;
            int openToolIndex = -1;
            string? openServerId = null; // the id the server gave the open call, if it gave one yet
            string? stopSequence = null;
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
                                string? callId = call.GetString("id") is { Length: > 0 } given ? given : null;

                                // Some servers send parallel calls all at index 0: an id other than the one the server
                                // sent for the open call starts a new call too. An id that only arrives in a later delta
                                // of a call that started without one belongs to that call.
                                if (index != openToolIndex || (callId is not null && openServerId is not null && callId != openServerId))
                                {
                                    if (openToolIndex >= 0)
                                    {
                                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                                    }

                                    openToolIndex = index;
                                    openServerId = callId;
                                    await writer.StartToolCallAsync(callId ?? JsonHelpers.NewToolUseId(), function.GetString("name") ?? string.Empty, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    openServerId ??= callId;
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
                        (stop, stopSequence) = MapFinishReason(finish, choice);
                    }
                }
            }

            if (openToolIndex >= 0)
            {
                await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(stop, usage, stopSequence, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RelayJsonAsync(JsonElement root, IChatResponseWriter writer, CancellationToken cancellationToken)
    {
        if (UpstreamReplyException.TryFind(root, ChatProtocol.OpenAI, out UpstreamReplyException? failed))
        {
            throw failed;
        }

        ChatStopReason stop = ChatStopReason.EndTurn;
        string? stopSequence = null;
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
                (stop, stopSequence) = MapFinishReason(finish, choice);
            }
        }

        ChatUsage usage = root.TryGetObject("usage", out JsonElement usageElement) ? ReadUsage(usageElement) : default;
        await writer.CompleteAsync(stop, usage, stopSequence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The stop reason, and the stop sequence that ended the reply when the server says which: vLLM puts it in the
    /// choice's <c>stop_reason</c>, SGLang in <c>matched_stop</c> (a token id there is not a sequence the client gave).
    /// OpenAI does not say, so its replies end with <see cref="ChatStopReason.EndTurn"/>.
    /// </summary>
    private static (ChatStopReason Stop, string? Sequence) MapFinishReason(string finish, JsonElement choice) => finish switch
    {
        "tool_calls" or "function_call" => (ChatStopReason.ToolUse, null),
        "length" => (ChatStopReason.MaxTokens, null),
        "content_filter" => (ChatStopReason.Refusal, null),
        "stop" when (choice.GetString("stop_reason") ?? choice.GetString("matched_stop")) is { Length: > 0 } sequence => (ChatStopReason.StopSequence, sequence),
        _ => (ChatStopReason.EndTurn, null),
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
                using JsonDocument document = await JsonDocument.ParseAsync(UpstreamLimits.Limit(stream, UpstreamLimits.MaxReplyBytes), cancellationToken: cancellationToken).ConfigureAwait(false);
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
/// <remarks>
/// The bytes reach the client unchanged, and what is held or looked at is bounded. A JSON reply is held until it is
/// complete, so that an error in place of the answer can still go to the next model, unless it is longer than
/// <see cref="UpstreamLimits.MaxReplyBytes"/>: then it streams on as it arrives and is not scanned. An event stream is
/// relayed line by line, each line with the line break it came with; a line longer than
/// <see cref="UpstreamLimits.MaxLineLength"/> is relayed as it arrives and not scanned. A reply too large to scan still
/// reaches the client whole; only its tool-call ids and usage are not recorded (usage is then estimated).
/// </remarks>
internal static class PassThroughToolCallScanner
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>The most of an event stream that is held back before its first data line: 64 KiB.</summary>
    internal const int HoldLimit = 64 * 1024;

    /// <summary>How long the start of an event stream is held back at most: 5 seconds, longer than an error takes to come.</summary>
    internal static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copies the reply, reporting tool-call ids and usage. With <c>stripUsageChunk</c> (the router asked for usage the
    /// client did not, with <c>stream_options.include_usage</c>), the usage-only final chunk is read but not relayed.
    /// </summary>
    public static Task CopyAsync(
        Stream upstream, Stream downstream, bool sse, ChatProtocol protocol, Action<string> onToolCall, CancellationToken cancellationToken,
        Action<ChatUsage>? onUsage = null, bool stripUsageChunk = false) =>
        CopyAsync(upstream, downstream, sse, protocol, onToolCall, onUsage, stripUsageChunk, TimeProvider.System, cancellationToken);

    /// <summary>As above, with the clock that times how long the start of an event stream is held back.</summary>
    internal static async Task CopyAsync(
        Stream upstream, Stream downstream, bool sse, ChatProtocol protocol, Action<string> onToolCall, Action<ChatUsage>? onUsage,
        bool stripUsageChunk, TimeProvider time, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[ChunkSize];
        int read;
        if (!sse)
        {
            using var body = new MemoryStream();
            while ((read = await upstream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > UpstreamLimits.MaxReplyBytes)
                {
                    // Too large to hold, and far larger than an error reply: what is held goes first, the rest as it arrives.
                    await downstream.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length), cancellationToken).ConfigureAwait(false);
                    await downstream.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    await upstream.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
                    await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                body.Write(chunk, 0, read);
            }

            if (ScanJson(body.GetBuffer().AsMemory(0, (int)body.Length), protocol, onToolCall, onUsage) is { } error)
            {
                throw error; // nothing has reached the client yet
            }

            await downstream.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length), cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var relay = new EventStreamRelay(downstream, protocol, onToolCall, onUsage, stripUsageChunk, time);
        while ((read = await upstream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await relay.FeedAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await relay.EndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits an event stream into lines (ended by <c>\n</c>, <c>\r</c> or <c>\r\n</c>), relays each with the bytes it
    /// came with and scans its data lines. What comes before the first data line (an <c>event:</c> line, comments) is held
    /// back until that line shows the reply is not an error, but no more than <see cref="HoldLimit"/> bytes of it and no
    /// longer than <see cref="HoldTime"/>: keep-alive comments a backend sends while a model thinks still reach the client,
    /// so a client that waits for a first byte does not give up.
    /// </summary>
    private sealed class EventStreamRelay(
        Stream downstream, ChatProtocol protocol, Action<string> onToolCall, Action<ChatUsage>? onUsage, bool stripUsageChunk, TimeProvider time)
    {
        // The line being read, its line break included once that has arrived.
        private ArrayBufferWriter<byte> _line = new();

        // The line ended in "\r" at the end of a chunk, so the "\n" of a "\r\n" may start the next one.
        private bool _afterCarriageReturn;

        // The line is longer than UpstreamLimits.MaxLineLength: it is relayed as it arrives and not scanned.
        private bool _tooLong;

        private List<byte[]>? _held = [];
        private long _heldBytes;
        private long _heldSince;
        private UpstreamReplyException? _failed;
        private bool _skipBlank;

        public async Task FeedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (_afterCarriageReturn)
            {
                _afterCarriageReturn = false;
                if (data.Span[0] == (byte)'\n')
                {
                    await AddAsync(data[..1], cancellationToken).ConfigureAwait(false);
                    data = data[1..];
                }

                await EndLineAsync(cancellationToken).ConfigureAwait(false);
            }

            while (!data.IsEmpty)
            {
                int end = data.Span.IndexOfAny((byte)'\r', (byte)'\n');
                if (end < 0)
                {
                    await AddAsync(data, cancellationToken).ConfigureAwait(false);
                    return;
                }

                int length = end + 1;
                if (data.Span[end] == (byte)'\r')
                {
                    if (length == data.Length)
                    {
                        await AddAsync(data, cancellationToken).ConfigureAwait(false);
                        _afterCarriageReturn = true;
                        return;
                    }

                    if (data.Span[length] == (byte)'\n')
                    {
                        length++;
                    }
                }

                await AddAsync(data[..length], cancellationToken).ConfigureAwait(false);
                await EndLineAsync(cancellationToken).ConfigureAwait(false);
                data = data[length..];
            }
        }

        /// <summary>Relays the last line and what is still held, and reports an error the stream carried after it started.</summary>
        public async Task EndAsync(CancellationToken cancellationToken)
        {
            _afterCarriageReturn = false;
            if (_line.WrittenCount > 0 || _tooLong)
            {
                await EndLineAsync(cancellationToken).ConfigureAwait(false);
            }

            await WriteHeldAsync(cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_failed is not null)
            {
                // The client has the backend's own error event; the reply still did not finish.
                throw new UpstreamReplyException(_failed.Failure, clientNotified: true);
            }
        }

        private async Task AddAsync(ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
        {
            if (!_tooLong && _line.WrittenCount + part.Length > UpstreamLimits.MaxLineLength)
            {
                // Too long to hold: what was held, and the line so far, go to the client now.
                _tooLong = true;
                _skipBlank = false;
                await WriteHeldAsync(cancellationToken).ConfigureAwait(false);
                await downstream.WriteAsync(_line.WrittenMemory, cancellationToken).ConfigureAwait(false);
                Reset();
            }

            if (_tooLong)
            {
                await downstream.WriteAsync(part, cancellationToken).ConfigureAwait(false);
                return;
            }

            _line.Write(part.Span);
        }

        private async Task EndLineAsync(CancellationToken cancellationToken)
        {
            if (_tooLong)
            {
                _tooLong = false;
                await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            ReadOnlyMemory<byte> raw = _line.WrittenMemory;
            ReadOnlyMemory<byte> text = raw.TrimEnd("\r\n"u8);
            try
            {
                if (_skipBlank && text.IsEmpty)
                {
                    _skipBlank = false; // the end of the event that was not relayed
                    return;
                }

                _skipBlank = false;
                if (stripUsageChunk && IsUsageOnlyChunk(System.Text.Encoding.UTF8.GetString(text.Span)))
                {
                    _ = ScanJson(text[5..], protocol, onToolCall, onUsage);
                    _skipBlank = true;
                    return;
                }

                ReadOnlySpan<byte> line = text.Span;
                bool data = line.StartsWith("data:"u8);
                UpstreamReplyException? error = data
                    && (line.IndexOf("\"id\""u8) >= 0 || line.IndexOf("\"call_id\""u8) >= 0 || line.IndexOf("\"error\""u8) >= 0
                        || (onUsage is not null && line.IndexOf("\"usage\""u8) >= 0))
                    ? ScanJson(text[5..], protocol, onToolCall, onUsage) // JSON allows the whitespace that may follow "data:"
                    : null;
                if (_held is not null)
                {
                    if (error is not null)
                    {
                        throw error; // nothing has reached the client yet
                    }

                    if (_held.Count == 0)
                    {
                        _heldSince = time.GetTimestamp();
                    }

                    _held.Add(raw.ToArray());
                    _heldBytes += raw.Length;
                    if (data || _heldBytes > HoldLimit || time.GetElapsedTime(_heldSince) >= HoldTime)
                    {
                        await WriteHeldAsync(cancellationToken).ConfigureAwait(false);
                        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }

                _failed ??= error;
                await downstream.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
                if (text.IsEmpty)
                {
                    await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Reset();
            }
        }

        private async Task WriteHeldAsync(CancellationToken cancellationToken)
        {
            if (_held is null)
            {
                return;
            }

            foreach (byte[] line in _held)
            {
                await downstream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }

            _held = null;
        }

        private void Reset()
        {
            if (_line.Capacity > 64 * 1024)
            {
                _line = new ArrayBufferWriter<byte>();
            }
            else
            {
                _line.ResetWrittenCount();
            }
        }
    }

    /// <summary>
    /// Whether an SSE line is the chunk <c>stream_options.include_usage</c> adds at the end of a chat-completions stream:
    /// no choices, and the usage.
    /// </summary>
    internal static bool IsUsageOnlyChunk(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)
            || !(line.Contains("\"choices\":[]", StringComparison.Ordinal) || line.Contains("\"choices\": []", StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line.AsSpan(5).TrimStart().ToString());
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetArray("choices", out JsonElement choices) && choices.GetArrayLength() == 0
                   && root.TryGetObject("usage", out _);
        }
        catch (JsonException)
        {
            return false;
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
                using JsonDocument document = await JsonDocument.ParseAsync(UpstreamLimits.Limit(stream, UpstreamLimits.MaxReplyBytes), cancellationToken: cancellationToken).ConfigureAwait(false);
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
                        // A refusal is the reply's text, as in a JSON reply.
                        case "response.output_text.delta" or "response.refusal.delta" when root.GetString("delta") is { Length: > 0 } text:
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
        // A reply cut off at the limit says so even with a tool call: its arguments may be incomplete.
        ChatStopReason incomplete = response.TryGetObject("incomplete_details", out JsonElement details)
            ? details.GetString("reason") switch
            {
                "max_output_tokens" => ChatStopReason.MaxTokens,
                "content_filter" => ChatStopReason.Refusal,
                _ => ChatStopReason.EndTurn,
            }
            : ChatStopReason.EndTurn;
        if (incomplete == ChatStopReason.MaxTokens)
        {
            return incomplete;
        }

        return response.TryGetArray("output", out JsonElement output)
               && output.EnumerateArray().Any(i => i.GetString("type") is "function_call" or "custom_tool_call")
            ? ChatStopReason.ToolUse
            : incomplete;
    }
}
