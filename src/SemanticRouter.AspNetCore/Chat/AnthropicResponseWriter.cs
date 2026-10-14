using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Writes Anthropic Messages API responses: a single <c>message</c> object, or the documented event stream
/// (<c>message_start</c>, <c>content_block_start</c>/<c>_delta</c>/<c>_stop</c>, <c>message_delta</c>, <c>message_stop</c>).
/// </summary>
internal sealed class AnthropicResponseWriter : IChatResponseWriter
{
    private readonly HttpContext _context;
    private readonly string _model;
    private readonly bool _stream;
    private readonly Action<string>? _onToolCall;
    private readonly string _id = "msg_" + Guid.NewGuid().ToString("N")[..24];
    private readonly List<Block> _blocks = [];
    private bool _started;
    private int _openIndex = -1;

    public AnthropicResponseWriter(HttpContext context, string model, bool stream, Action<string>? onToolCall = null)
    {
        _context = context;
        _model = model;
        _stream = stream;
        _onToolCall = onToolCall;
    }

    public bool HasStarted => _started;

    public async Task WriteTextAsync(string delta, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (delta.Length == 0)
        {
            return;
        }

        if (_openIndex < 0 || !_blocks[_openIndex].IsText)
        {
            await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            _blocks.Add(new Block(null, null));
            _openIndex = _blocks.Count - 1;
            if (_stream)
            {
                await WriteEventAsync("content_block_start", w =>
                {
                    w.WriteNumber("index", _openIndex);
                    w.WriteStartObject("content_block");
                    w.WriteString("type", "text");
                    w.WriteString("text", string.Empty);
                    w.WriteEndObject();
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        _blocks[_openIndex].Content.Append(delta);
        if (_stream)
        {
            int index = _openIndex;
            await WriteEventAsync("content_block_delta", w =>
            {
                w.WriteNumber("index", index);
                w.WriteStartObject("delta");
                w.WriteString("type", "text_delta");
                w.WriteString("text", delta);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a <c>thinking</c> block. Its signature is the router's marker, not Anthropic's: if the client sends the
    /// block back and the turn reaches an Anthropic backend, the router removes it rather than let the API reject it.
    /// </summary>
    public async Task WriteThinkingAsync(string delta, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (delta.Length == 0)
        {
            return;
        }

        if (_openIndex < 0 || !_blocks[_openIndex].IsThinking)
        {
            await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            _blocks.Add(new Block(null, null, thinking: true));
            _openIndex = _blocks.Count - 1;
            if (_stream)
            {
                int start = _openIndex;
                await WriteEventAsync("content_block_start", w =>
                {
                    w.WriteNumber("index", start);
                    w.WriteStartObject("content_block");
                    w.WriteString("type", "thinking");
                    w.WriteString("thinking", string.Empty);
                    w.WriteEndObject();
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        _blocks[_openIndex].Content.Append(delta);
        if (_stream)
        {
            int index = _openIndex;
            await WriteEventAsync("content_block_delta", w =>
            {
                w.WriteNumber("index", index);
                w.WriteStartObject("delta");
                w.WriteString("type", "thinking_delta");
                w.WriteString("thinking", delta);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
        _blocks.Add(new Block(id, name));
        _openIndex = _blocks.Count - 1;
        _onToolCall?.Invoke(id);
        if (_stream)
        {
            int index = _openIndex;
            await WriteEventAsync("content_block_start", w =>
            {
                w.WriteNumber("index", index);
                w.WriteStartObject("content_block");
                w.WriteString("type", "tool_use");
                w.WriteString("id", id);
                w.WriteString("name", name);
                w.WriteStartObject("input");
                w.WriteEndObject();
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken)
    {
        if (_openIndex < 0 || !_blocks[_openIndex].IsTool || delta.Length == 0)
        {
            return;
        }

        _blocks[_openIndex].Content.Append(delta);
        if (_stream)
        {
            int index = _openIndex;
            await WriteEventAsync("content_block_delta", w =>
            {
                w.WriteNumber("index", index);
                w.WriteStartObject("delta");
                w.WriteString("type", "input_json_delta");
                w.WriteString("partial_json", delta);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task EndToolCallAsync(CancellationToken cancellationToken) =>
        _openIndex >= 0 && _blocks[_openIndex].IsTool ? CloseBlockAsync(cancellationToken) : Task.CompletedTask;

    public async Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        bool anyTool = _blocks.Any(b => b.IsTool);
        string stop = anyTool || stopReason == ChatStopReason.ToolUse
            ? "tool_use"
            : stopReason switch
            {
                ChatStopReason.MaxTokens => "max_tokens",
                ChatStopReason.StopSequence => "stop_sequence",
                ChatStopReason.Refusal => "refusal",
                ChatStopReason.PauseTurn => "pause_turn",
                _ => "end_turn",
            };

        if (_stream)
        {
            if (_blocks.Count == 0)
            {
                // A reply with no content still needs one block for strict clients.
                await WriteTextAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                _blocks.Add(new Block(null, null));
                _openIndex = _blocks.Count - 1;
                int index = _openIndex;
                await WriteEventAsync("content_block_start", w =>
                {
                    w.WriteNumber("index", index);
                    w.WriteStartObject("content_block");
                    w.WriteString("type", "text");
                    w.WriteString("text", string.Empty);
                    w.WriteEndObject();
                }, cancellationToken).ConfigureAwait(false);
            }

            await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            await WriteEventAsync("message_delta", w =>
            {
                w.WriteStartObject("delta");
                w.WriteString("stop_reason", stop);
                w.WriteNull("stop_sequence");
                w.WriteEndObject();
                w.WriteStartObject("usage");
                w.WriteNumber("output_tokens", usage.OutputTokens ?? 0);
                if (usage.InputTokens is { } input)
                {
                    w.WriteNumber("input_tokens", input);
                }

                // message_start went out before the backend reported usage; the cache counts arrive here, as
                // Anthropic's own message_delta carries them.
                if (usage.CacheReadTokens is { } read)
                {
                    w.WriteNumber("cache_read_input_tokens", read);
                }

                if (usage.CacheCreationTokens is { } created)
                {
                    w.WriteNumber("cache_creation_input_tokens", created);
                }

                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            await WriteEventAsync("message_stop", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", _id);
            writer.WriteString("type", "message");
            writer.WriteString("role", "assistant");
            writer.WriteString("model", _model);
            writer.WriteStartArray("content");
            if (_blocks.Count == 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", string.Empty);
                writer.WriteEndObject();
            }

            foreach (Block block in _blocks)
            {
                writer.WriteStartObject();
                if (block.IsThinking)
                {
                    writer.WriteString("type", "thinking");
                    writer.WriteString("thinking", block.Content.ToString());
                    writer.WriteString("signature", ThinkingBlock.RouterSignature);
                }
                else if (block.IsTool)
                {
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", block.Id);
                    writer.WriteString("name", block.Name);
                    writer.WritePropertyName("input");
                    OpenAIRequestBuilder.WriteRaw(writer, JsonHelpers.EnsureJsonObject(block.Content.ToString()));
                }
                else
                {
                    writer.WriteString("type", "text");
                    writer.WriteString("text", block.Content.ToString());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("stop_reason", stop);
            writer.WriteNull("stop_sequence");
            WriteUsage(writer, usage);
            writer.WriteEndObject();
        }

        _context.Response.StatusCode = StatusCodes.Status200OK;
        _context.Response.ContentType = "application/json";
        await _context.Response.Body.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteUsage(Utf8JsonWriter writer, ChatUsage usage)
    {
        writer.WriteStartObject("usage");
        writer.WriteNumber("input_tokens", usage.InputTokens ?? 0);
        writer.WriteNumber("output_tokens", usage.OutputTokens ?? 0);
        writer.WriteNumber("cache_read_input_tokens", usage.CacheReadTokens ?? 0);
        writer.WriteNumber("cache_creation_input_tokens", usage.CacheCreationTokens ?? 0);
        writer.WriteEndObject();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (_stream)
        {
            _context.Response.StatusCode = StatusCodes.Status200OK;
            _context.Response.ContentType = "text/event-stream";
            _context.Response.Headers.CacheControl = "no-cache";
            _context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await WriteEventAsync("message_start", w =>
            {
                w.WriteStartObject("message");
                w.WriteString("id", _id);
                w.WriteString("type", "message");
                w.WriteString("role", "assistant");
                w.WriteString("model", _model);
                w.WriteStartArray("content");
                w.WriteEndArray();
                w.WriteNull("stop_reason");
                w.WriteNull("stop_sequence");
                WriteUsage(w, default);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CloseBlockAsync(CancellationToken cancellationToken)
    {
        if (_openIndex < 0)
        {
            return;
        }

        int index = _openIndex;
        _openIndex = -1;
        if (_stream)
        {
            if (_blocks[index].IsThinking)
            {
                await WriteEventAsync("content_block_delta", w =>
                {
                    w.WriteNumber("index", index);
                    w.WriteStartObject("delta");
                    w.WriteString("type", "signature_delta");
                    w.WriteString("signature", ThinkingBlock.RouterSignature);
                    w.WriteEndObject();
                }, cancellationToken).ConfigureAwait(false);
            }

            await WriteEventAsync("content_block_stop", w => w.WriteNumber("index", index), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteEventAsync(string type, Action<Utf8JsonWriter>? body, CancellationToken cancellationToken)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            body?.Invoke(writer);
            writer.WriteEndObject();
        }

        Stream stream = _context.Response.Body;
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: "), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Block(string? id, string? name, bool thinking = false)
    {
        public string? Id { get; } = id;

        public string? Name { get; } = name;

        public StringBuilder Content { get; } = new();

        public bool IsTool => Id is not null;

        public bool IsThinking => thinking;

        public bool IsText => !IsTool && !IsThinking;
    }
}
