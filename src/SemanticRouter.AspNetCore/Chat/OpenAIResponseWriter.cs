using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Writes OpenAI chat-completions responses: a single <c>chat.completion</c> object, or <c>chat.completion.chunk</c>
/// server-sent events, including tool calls.
/// </summary>
internal sealed class OpenAIResponseWriter : IChatResponseWriter
{
    private readonly HttpContext _context;
    private readonly string _model;
    private readonly bool _stream;
    private readonly Action<string>? _onToolCall;
    private readonly string _id = "chatcmpl-" + Guid.NewGuid().ToString("N");
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly StringBuilder _text = new();
    private readonly List<(string Id, string Name, StringBuilder Arguments)> _toolCalls = [];
    private bool _started;
    private bool _toolCallOpen;

    public OpenAIResponseWriter(HttpContext context, string model, bool stream, Action<string>? onToolCall = null)
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

        if (_stream)
        {
            await WriteChunkAsync(w => w.WriteString("content", delta), finishReason: null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _text.Append(delta);
        }
    }

    public async Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        _toolCalls.Add((id, name, new StringBuilder()));
        _toolCallOpen = true;
        _onToolCall?.Invoke(id);
        if (_stream)
        {
            int index = _toolCalls.Count - 1;
            await WriteChunkAsync(w => WriteToolCallDelta(w, index, id, name, string.Empty), finishReason: null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken)
    {
        if (!_toolCallOpen || delta.Length == 0)
        {
            return;
        }

        _toolCalls[^1].Arguments.Append(delta);
        if (_stream)
        {
            int index = _toolCalls.Count - 1;
            await WriteChunkAsync(w => WriteToolCallDelta(w, index, null, null, delta), finishReason: null, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task EndToolCallAsync(CancellationToken cancellationToken)
    {
        _toolCallOpen = false;
        return Task.CompletedTask;
    }

    public async Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        string finishReason = _toolCalls.Count > 0 || stopReason == ChatStopReason.ToolUse
            ? "tool_calls"
            : stopReason == ChatStopReason.MaxTokens ? "length" : "stop";

        if (_stream)
        {
            await WriteChunkAsync(null, finishReason, cancellationToken, usage).ConfigureAwait(false);
            await _context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
            await _context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "chat.completion");
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            if (_text.Length > 0 || _toolCalls.Count == 0)
            {
                writer.WriteString("content", _text.ToString());
            }
            else
            {
                writer.WriteNull("content");
            }

            if (_toolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach ((string id, string name, StringBuilder arguments) in _toolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", name);
                    writer.WriteString("arguments", arguments.Length == 0 ? "{}" : arguments.ToString());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteString("finish_reason", finishReason);
            writer.WriteEndObject();
            writer.WriteEndArray();
            WriteUsage(writer, usage);
            writer.WriteEndObject();
        }

        _context.Response.StatusCode = StatusCodes.Status200OK;
        _context.Response.ContentType = "application/json";
        await _context.Response.Body.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteToolCallDelta(Utf8JsonWriter writer, int index, string? id, string? name, string arguments)
    {
        writer.WriteStartArray("tool_calls");
        writer.WriteStartObject();
        writer.WriteNumber("index", index);
        if (id is not null)
        {
            writer.WriteString("id", id);
            writer.WriteString("type", "function");
        }

        writer.WriteStartObject("function");
        if (name is not null)
        {
            writer.WriteString("name", name);
        }

        writer.WriteString("arguments", arguments);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WriteUsage(Utf8JsonWriter writer, ChatUsage usage)
    {
        if (usage.IsEmpty)
        {
            return;
        }

        int input = (usage.InputTokens ?? 0) + (usage.CacheReadTokens ?? 0) + (usage.CacheCreationTokens ?? 0);
        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", input);
        writer.WriteNumber("completion_tokens", usage.OutputTokens ?? 0);
        writer.WriteNumber("total_tokens", input + (usage.OutputTokens ?? 0));
        if (usage.CacheReadTokens is { } cached)
        {
            writer.WriteStartObject("prompt_tokens_details");
            writer.WriteNumber("cached_tokens", cached);
            writer.WriteEndObject();
        }

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
            await WriteChunkAsync(w =>
            {
                w.WriteString("role", "assistant");
                w.WriteString("content", string.Empty);
            }, finishReason: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteChunkAsync(Action<Utf8JsonWriter>? delta, string? finishReason, CancellationToken cancellationToken, ChatUsage usage = default)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "chat.completion.chunk");
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("delta");
            delta?.Invoke(writer);
            writer.WriteEndObject();
            if (finishReason is null)
            {
                writer.WriteNull("finish_reason");
            }
            else
            {
                writer.WriteString("finish_reason", finishReason);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            WriteUsage(writer, usage);
            writer.WriteEndObject();
        }

        Stream body = _context.Response.Body;
        await body.WriteAsync("data: "u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await body.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await body.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteEnvelope(Utf8JsonWriter writer, string objectType)
    {
        writer.WriteString("id", _id);
        writer.WriteString("object", objectType);
        writer.WriteNumber("created", _created);
        writer.WriteString("model", _model);
    }
}
