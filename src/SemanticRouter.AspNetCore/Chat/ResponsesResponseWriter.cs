using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Writes OpenAI Responses API replies: a single <c>response</c> object, or the documented event stream
/// (<c>response.created</c>, <c>response.output_item.added</c>, <c>response.output_text.delta</c>,
/// <c>response.function_call_arguments.delta</c>, <c>response.custom_tool_call_input.delta</c>,
/// <c>response.reasoning_summary_text.delta</c>, <c>response.output_item.done</c>, <c>response.completed</c>, …).
/// Tool calls come back under the name and namespace the client declared, and custom tools (Codex's <c>exec</c>) as
/// <c>custom_tool_call</c> items with their raw input.
/// </summary>
internal sealed class ResponsesResponseWriter : IChatResponseWriter
{
    private readonly HttpContext _context;
    private readonly string _model;
    private readonly bool _stream;
    private readonly Action<string>? _onToolCall;
    private readonly Dictionary<string, ToolDefinition> _tools;
    private readonly string _id = "resp_" + Guid.NewGuid().ToString("N");
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly List<Item> _items = [];
    private Item? _open;
    private int _sequence;
    private bool _started;

    public ResponsesResponseWriter(HttpContext context, string model, bool stream, IReadOnlyList<ToolDefinition> tools, Action<string>? onToolCall = null)
    {
        _context = context;
        _model = model;
        _stream = stream;
        _onToolCall = onToolCall;
        _tools = tools.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public bool HasStarted => _started;

    public async Task WriteTextAsync(string delta, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (delta.Length == 0)
        {
            return;
        }

        if (_open is not { Kind: ItemKind.Message })
        {
            await OpenAsync(new Item(ItemKind.Message, "msg_" + NewId()), cancellationToken).ConfigureAwait(false);
            await EventAsync("response.content_part.added", w =>
            {
                ItemRef(w, _open!);
                w.WriteNumber("content_index", 0);
                w.WritePropertyName("part");
                TextPart(w, string.Empty);
            }, cancellationToken).ConfigureAwait(false);
        }

        _open!.Content.Append(delta);
        await EventAsync("response.output_text.delta", w =>
        {
            ItemRef(w, _open);
            w.WriteNumber("content_index", 0);
            w.WriteString("delta", delta);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteThinkingAsync(string delta, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (delta.Length == 0)
        {
            return;
        }

        if (_open is not { Kind: ItemKind.Reasoning })
        {
            await OpenAsync(new Item(ItemKind.Reasoning, "rs_" + NewId()), cancellationToken).ConfigureAwait(false);
            await EventAsync("response.reasoning_summary_part.added", w =>
            {
                ItemRef(w, _open!);
                w.WriteNumber("summary_index", 0);
                w.WriteStartObject("part");
                w.WriteString("type", "summary_text");
                w.WriteString("text", string.Empty);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }

        _open!.Content.Append(delta);
        await EventAsync("response.reasoning_summary_text.delta", w =>
        {
            ItemRef(w, _open);
            w.WriteNumber("summary_index", 0);
            w.WriteString("delta", delta);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        _tools.TryGetValue(name, out ToolDefinition? tool);
        bool custom = tool?.Kind == ToolKind.Custom;
        var item = new Item(custom ? ItemKind.CustomToolCall : ItemKind.FunctionCall, (custom ? "ctc_" : "fc_") + NewId())
        {
            CallId = id,
            Name = tool?.OriginalName ?? name,
            Namespace = tool?.Namespace,
        };
        _onToolCall?.Invoke(id);
        await OpenAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken)
    {
        if (_open is not { Kind: ItemKind.FunctionCall or ItemKind.CustomToolCall } call || delta.Length == 0)
        {
            return;
        }

        call.Content.Append(delta);
        if (call.Kind == ItemKind.FunctionCall)
        {
            await EventAsync("response.function_call_arguments.delta", w =>
            {
                ItemRef(w, call);
                w.WriteString("delta", delta);
            }, cancellationToken).ConfigureAwait(false);
        }

        // Custom tool input is the "input" string inside the arguments object: sent whole when the call ends.
    }

    public Task EndToolCallAsync(CancellationToken cancellationToken) =>
        _open is { Kind: ItemKind.FunctionCall or ItemKind.CustomToolCall } ? CloseAsync(cancellationToken) : Task.CompletedTask;

    public async Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await CloseAsync(cancellationToken).ConfigureAwait(false);
        string? incomplete = stopReason switch
        {
            ChatStopReason.MaxTokens => "max_output_tokens",
            ChatStopReason.Refusal => "content_filter",
            _ => null,
        };

        if (_stream)
        {
            await EventAsync(incomplete is null ? "response.completed" : "response.incomplete", w =>
            {
                w.WritePropertyName("response");
                WriteResponse(w, incomplete is null ? "completed" : "incomplete", incomplete, usage);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            WriteResponse(writer, incomplete is null ? "completed" : "incomplete", incomplete, usage);
        }

        _context.Response.StatusCode = StatusCodes.Status200OK;
        _context.Response.ContentType = "application/json";
        await _context.Response.Body.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenAsync(Item item, CancellationToken cancellationToken)
    {
        await CloseAsync(cancellationToken).ConfigureAwait(false);
        item.Index = _items.Count;
        _items.Add(item);
        _open = item;
        await EventAsync("response.output_item.added", w =>
        {
            w.WriteNumber("output_index", item.Index);
            w.WritePropertyName("item");
            WriteItem(w, item, done: false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_open is not { } item)
        {
            return;
        }

        _open = null;
        string text = item.Content.ToString();
        switch (item.Kind)
        {
            case ItemKind.Message:
                await EventAsync("response.output_text.done", w =>
                {
                    ItemRef(w, item);
                    w.WriteNumber("content_index", 0);
                    w.WriteString("text", text);
                }, cancellationToken).ConfigureAwait(false);
                await EventAsync("response.content_part.done", w =>
                {
                    ItemRef(w, item);
                    w.WriteNumber("content_index", 0);
                    w.WritePropertyName("part");
                    TextPart(w, text);
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ItemKind.Reasoning:
                await EventAsync("response.reasoning_summary_text.done", w =>
                {
                    ItemRef(w, item);
                    w.WriteNumber("summary_index", 0);
                    w.WriteString("text", text);
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ItemKind.FunctionCall:
                await EventAsync("response.function_call_arguments.done", w =>
                {
                    ItemRef(w, item);
                    w.WriteString("arguments", item.Arguments);
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ItemKind.CustomToolCall:
                string input = item.CustomInput;
                await EventAsync("response.custom_tool_call_input.delta", w =>
                {
                    ItemRef(w, item);
                    w.WriteString("delta", input);
                }, cancellationToken).ConfigureAwait(false);
                await EventAsync("response.custom_tool_call_input.done", w =>
                {
                    ItemRef(w, item);
                    w.WriteString("input", input);
                }, cancellationToken).ConfigureAwait(false);
                break;
        }

        await EventAsync("response.output_item.done", w =>
        {
            w.WriteNumber("output_index", item.Index);
            w.WritePropertyName("item");
            WriteItem(w, item, done: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void WriteResponse(Utf8JsonWriter writer, string status, string? incompleteReason, ChatUsage usage)
    {
        writer.WriteStartObject();
        writer.WriteString("id", _id);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", _created);
        writer.WriteString("status", status);
        writer.WriteString("model", _model);
        if (incompleteReason is not null)
        {
            writer.WriteStartObject("incomplete_details");
            writer.WriteString("reason", incompleteReason);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("incomplete_details");
        }

        writer.WriteStartArray("output");
        foreach (Item item in _items)
        {
            WriteItem(writer, item, done: status != "in_progress");
        }

        writer.WriteEndArray();
        if (usage.IsEmpty && status == "in_progress")
        {
            writer.WriteNull("usage");
        }
        else
        {
            int input = (usage.InputTokens ?? 0) + (usage.CacheReadTokens ?? 0) + (usage.CacheCreationTokens ?? 0);
            writer.WriteStartObject("usage");
            writer.WriteNumber("input_tokens", input);
            writer.WriteStartObject("input_tokens_details");
            writer.WriteNumber("cached_tokens", usage.CacheReadTokens ?? 0);
            writer.WriteEndObject();
            writer.WriteNumber("output_tokens", usage.OutputTokens ?? 0);
            writer.WriteStartObject("output_tokens_details");
            writer.WriteNumber("reasoning_tokens", 0);
            writer.WriteEndObject();
            writer.WriteNumber("total_tokens", input + (usage.OutputTokens ?? 0));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter writer, Item item, bool done)
    {
        writer.WriteStartObject();
        writer.WriteString("id", item.Id);
        switch (item.Kind)
        {
            case ItemKind.Message:
                writer.WriteString("type", "message");
                writer.WriteString("status", done ? "completed" : "in_progress");
                writer.WriteString("role", "assistant");
                writer.WriteStartArray("content");
                if (done)
                {
                    TextPart(writer, item.Content.ToString());
                }

                writer.WriteEndArray();
                break;

            case ItemKind.Reasoning:
                writer.WriteString("type", "reasoning");
                writer.WriteStartArray("summary");
                if (done)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "summary_text");
                    writer.WriteString("text", item.Content.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;

            case ItemKind.FunctionCall:
                writer.WriteString("type", "function_call");
                writer.WriteString("status", done ? "completed" : "in_progress");
                writer.WriteString("call_id", item.CallId);
                writer.WriteString("name", item.Name);
                if (item.Namespace is not null)
                {
                    writer.WriteString("namespace", item.Namespace);
                }

                writer.WriteString("arguments", done ? item.Arguments : string.Empty);
                break;

            case ItemKind.CustomToolCall:
                writer.WriteString("type", "custom_tool_call");
                writer.WriteString("status", done ? "completed" : "in_progress");
                writer.WriteString("call_id", item.CallId);
                writer.WriteString("name", item.Name);
                if (item.Namespace is not null)
                {
                    writer.WriteString("namespace", item.Namespace);
                }

                writer.WriteString("input", done ? item.CustomInput : string.Empty);
                break;
        }

        writer.WriteEndObject();
    }

    private static void TextPart(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WriteString("text", text);
        writer.WriteStartArray("annotations");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void ItemRef(Utf8JsonWriter writer, Item item)
    {
        writer.WriteString("item_id", item.Id);
        writer.WriteNumber("output_index", item.Index);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (!_stream)
        {
            return;
        }

        _context.Response.StatusCode = StatusCodes.Status200OK;
        _context.Response.ContentType = "text/event-stream";
        _context.Response.Headers.CacheControl = "no-cache";
        _context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await EventAsync("response.created", w =>
        {
            w.WritePropertyName("response");
            WriteResponse(w, "in_progress", null, default);
        }, cancellationToken).ConfigureAwait(false);
        await EventAsync("response.in_progress", w =>
        {
            w.WritePropertyName("response");
            WriteResponse(w, "in_progress", null, default);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EventAsync(string type, Action<Utf8JsonWriter> body, CancellationToken cancellationToken)
    {
        if (!_stream)
        {
            return;
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("sequence_number", _sequence++);
            body(writer);
            writer.WriteEndObject();
        }

        Stream stream = _context.Response.Body;
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: "), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private enum ItemKind
    {
        Message,
        Reasoning,
        FunctionCall,
        CustomToolCall,
    }

    private sealed class Item(ItemKind kind, string id)
    {
        public ItemKind Kind { get; } = kind;

        public string Id { get; } = id;

        public int Index { get; set; }

        public string? CallId { get; init; }

        public string? Name { get; init; }

        public string? Namespace { get; init; }

        public StringBuilder Content { get; } = new();

        public string Arguments => Content.Length == 0 ? "{}" : Content.ToString();

        /// <summary>A custom tool's raw input: the <c>input</c> string of the arguments a backend produced, or their text.</summary>
        public string CustomInput
        {
            get
            {
                string arguments = Content.ToString();
                try
                {
                    using JsonDocument document = JsonDocument.Parse(arguments);
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.GetString("input") is { } input)
                    {
                        return input;
                    }
                }
                catch (JsonException)
                {
                }

                return arguments;
            }
        }
    }
}
