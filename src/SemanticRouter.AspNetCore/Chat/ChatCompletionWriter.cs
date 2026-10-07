using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Writes OpenAI chat-completions responses (a single JSON object, or <c>chat.completion.chunk</c> server-sent events)
/// for connectors whose backend does not speak that protocol itself.
/// </summary>
internal sealed class ChatCompletionWriter
{
    private readonly HttpContext _context;
    private readonly string _model;
    private readonly bool _stream;
    private readonly string _id = "chatcmpl-" + Guid.NewGuid().ToString("N");
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly StringBuilder _buffer = new();
    private bool _started;

    public ChatCompletionWriter(HttpContext context, string model, bool stream)
    {
        _context = context;
        _model = model;
        _stream = stream;
    }

    /// <summary>Whether anything has been sent to the client (the point of no return for failover).</summary>
    public bool HasStarted => _started;

    public async Task WriteDeltaAsync(string text, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _started = true;
            if (_stream)
            {
                _context.Response.StatusCode = StatusCodes.Status200OK;
                _context.Response.ContentType = "text/event-stream";
                _context.Response.Headers.CacheControl = "no-cache";
                _context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await WriteChunkAsync(role: "assistant", content: string.Empty, finishReason: null, cancellationToken).ConfigureAwait(false);
            }
        }

        if (text.Length == 0)
        {
            return;
        }

        if (_stream)
        {
            await WriteChunkAsync(role: null, content: text, finishReason: null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _buffer.Append(text);
        }
    }

    public async Task CompleteAsync(int? inputTokens, int? outputTokens, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            await WriteDeltaAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        if (_stream)
        {
            await WriteChunkAsync(role: null, content: null, finishReason: "stop", cancellationToken).ConfigureAwait(false);
            await _context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "chat.completion");
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            writer.WriteString("content", _buffer.ToString());
            writer.WriteEndObject();
            writer.WriteString("finish_reason", "stop");
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (inputTokens is not null || outputTokens is not null)
            {
                writer.WriteStartObject("usage");
                writer.WriteNumber("prompt_tokens", inputTokens ?? 0);
                writer.WriteNumber("completion_tokens", outputTokens ?? 0);
                writer.WriteNumber("total_tokens", (inputTokens ?? 0) + (outputTokens ?? 0));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        _context.Response.StatusCode = StatusCodes.Status200OK;
        _context.Response.ContentType = "application/json";
        await _context.Response.Body.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteChunkAsync(string? role, string? content, string? finishReason, CancellationToken cancellationToken)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "chat.completion.chunk");
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("delta");
            if (role is not null)
            {
                writer.WriteString("role", role);
            }

            if (content is not null)
            {
                writer.WriteString("content", content);
            }

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
