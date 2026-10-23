using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>The wire format a chat request or response uses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatProtocol>))]
public enum ChatProtocol
{
    /// <summary>OpenAI <c>POST /v1/chat/completions</c>: used by most tools, local servers and APIs.</summary>
    OpenAI = 0,

    /// <summary>Anthropic <c>POST /v1/messages</c>: used by Claude Code and the Anthropic SDKs.</summary>
    Anthropic = 1,

    /// <summary>OpenAI <c>POST /v1/responses</c>: used by Codex CLI and the OpenAI SDKs' newer agent paths.</summary>
    Responses = 2,
}

/// <summary>One part of a chat message.</summary>
public abstract record ContentBlock
{
    /// <summary>
    /// The Anthropic <c>cache_control</c> marker the client put on this block, as JSON (e.g.
    /// <c>{"type":"ephemeral"}</c>). Written again when the request is rebuilt for an Anthropic backend.
    /// </summary>
    public string? CacheControl { get; init; }
}

/// <summary>Plain text.</summary>
public sealed record TextBlock : ContentBlock
{
    /// <summary>The text.</summary>
    public required string Text { get; init; }
}

/// <summary>An image, either inline (base64) or by URL.</summary>
public sealed record ImageBlock : ContentBlock
{
    /// <summary>MIME type for inline data, e.g. <c>image/png</c>.</summary>
    public string? MediaType { get; init; }

    /// <summary>Base64 data when inline.</summary>
    public string? Data { get; init; }

    /// <summary>The URL (or data URL) when referenced.</summary>
    public string? Url { get; init; }
}

/// <summary>A tool call issued by the assistant.</summary>
public sealed record ToolUseBlock : ContentBlock
{
    /// <summary>Opaque call id, echoed back by the matching <see cref="ToolResultBlock"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The tool name.</summary>
    public required string Name { get; init; }

    /// <summary>The call arguments as a JSON object.</summary>
    public required string InputJson { get; init; }
}

/// <summary>
/// The model's visible reasoning ("extended thinking"). Carried back by clients on tool-loop continuations; only an
/// Anthropic backend can use it again, and only with the signature it issued.
/// </summary>
public sealed record ThinkingBlock : ContentBlock
{
    /// <summary>The signature the router puts on reasoning it relays from a non-Anthropic backend.</summary>
    public const string RouterSignature = "semantic-router-relayed";

    /// <summary>The reasoning text (empty for redacted thinking).</summary>
    public required string Text { get; init; }

    /// <summary>The backend's signature, or the router's marker for reasoning it relayed from elsewhere.</summary>
    public string? Signature { get; init; }

    /// <summary>Encrypted content of a <c>redacted_thinking</c> block.</summary>
    public string? RedactedData { get; init; }
}

/// <summary>The result of a tool call, sent by the client.</summary>
public sealed record ToolResultBlock : ContentBlock
{
    /// <summary>The <see cref="ToolUseBlock.Id"/> this answers.</summary>
    public required string ToolUseId { get; init; }

    /// <summary>The result, flattened to text.</summary>
    public required string Content { get; init; }

    /// <summary>Whether the tool failed.</summary>
    public required bool IsError { get; init; }
}

/// <summary>One message in the conversation, with its structured content.</summary>
public sealed record ChatTurn
{
    /// <summary><c>system</c>, <c>user</c> or <c>assistant</c>. Tool results are <c>user</c> turns.</summary>
    public required string Role { get; init; }

    /// <summary>The content parts, in order.</summary>
    public required IReadOnlyList<ContentBlock> Blocks { get; init; }

    /// <summary>The text parts joined with newlines (tool calls and results excluded).</summary>
    public string Text => string.Join('\n', Blocks.OfType<TextBlock>().Select(b => b.Text).Where(t => t.Length > 0));

    /// <summary>Whether the turn consists only of tool results (a tool-loop continuation, not a new ask).</summary>
    public bool IsToolResultOnly => Blocks.Count > 0 && Blocks.All(b => b is ToolResultBlock);

    /// <summary>Creates a turn with a single text block.</summary>
    public static ChatTurn FromText(string role, string text) => new ChatTurn { Role = role, Blocks = [new TextBlock { Text = text }] };
}

/// <summary>What kind of tool a client offers.</summary>
public enum ToolKind
{
    /// <summary>A function with JSON arguments.</summary>
    Function = 0,

    /// <summary>
    /// A Responses API custom tool whose input is free text (optionally constrained by a grammar), such as Codex's
    /// <c>exec</c>. Other protocols see it as a function with one string argument, <c>input</c>.
    /// </summary>
    Custom = 1,
}

/// <summary>A tool the client offers the model.</summary>
public sealed record ToolDefinition
{
    /// <summary>The name used with backends: unique and safe for every protocol (namespaces are folded in).</summary>
    public required string Name { get; init; }

    /// <summary>What it does.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema of its arguments (an object schema).</summary>
    public required string InputSchemaJson { get; init; }

    /// <summary>Function or custom (free-text input).</summary>
    public ToolKind Kind { get; init; } = ToolKind.Function;

    /// <summary>The Responses API namespace the client declared it in, when not the default.</summary>
    public string? Namespace { get; init; }

    /// <summary>The name the client uses, when it differs from <see cref="Name"/>.</summary>
    public string? ClientName { get; init; }

    /// <summary>The schema a custom tool is given when a backend only knows functions.</summary>
    public const string CustomInputSchema = """{"type":"object","properties":{"input":{"type":"string","description":"The tool's raw input."}},"required":["input"]}""";

    /// <summary>The name the client knows the tool by.</summary>
    public string OriginalName => ClientName ?? Name;

    /// <summary>The Anthropic <c>cache_control</c> marker the client put on this tool, as JSON.</summary>
    public string? CacheControl { get; init; }
}

/// <summary>A request for the model to reason before answering.</summary>
public sealed record ReasoningRequest
{
    /// <summary><c>minimal</c>, <c>low</c>, <c>medium</c> or <c>high</c>, when the client gave one.</summary>
    public string? Effort { get; init; }

    /// <summary>The thinking budget, when the client gave one (Anthropic).</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>The effort, derived from the budget when the client only gave a budget.</summary>
    public string EffectiveEffort => Effort ?? BudgetTokens switch
    {
        null => "medium",
        <= 2048 => "low",
        <= 12_000 => "medium",
        _ => "high",
    };

    /// <summary>The budget, derived from the effort when the client only gave an effort.</summary>
    public int EffectiveBudget => BudgetTokens ?? (Effort switch
    {
        "minimal" or "low" => 2048,
        "high" => 16_384,
        "xhigh" or "max" => 32_768,
        _ => 8192,
    });
}

/// <summary>What shape the reply must have.</summary>
/// <remarks>
/// There is deliberately no zero member. A plain-text reply is no <see cref="ResponseFormat"/> at all
/// (<see cref="ChatCompletionRequest.ResponseFormat"/> is <see langword="null"/>), and every request builder treats a
/// format that is present as a request for JSON. <see cref="ResponseFormat.Kind"/> is required, so it is never left
/// at <c>default</c>.
/// </remarks>
public enum ResponseFormatKind
{
    /// <summary>Any JSON object.</summary>
    JsonObject = 1,

    /// <summary>JSON matching a schema.</summary>
    JsonSchema = 2,
}

/// <summary>Structured output: OpenAI <c>response_format</c> or Responses <c>text.format</c>.</summary>
public sealed record ResponseFormat
{
    /// <summary>JSON object or JSON schema.</summary>
    public required ResponseFormatKind Kind { get; init; }

    /// <summary>The schema's name.</summary>
    public string? Name { get; init; }

    /// <summary>The JSON schema.</summary>
    public string? SchemaJson { get; init; }

    /// <summary>Whether the schema must be followed exactly.</summary>
    public bool Strict { get; init; }
}

/// <summary>How the model may use the offered tools.</summary>
public enum ToolChoiceMode
{
    /// <summary>The model decides (default).</summary>
    Auto = 0,

    /// <summary>The model must not call a tool.</summary>
    None = 1,

    /// <summary>The model must call some tool.</summary>
    Required = 2,

    /// <summary>The model must call <see cref="ToolChoice.Name"/>.</summary>
    Named = 3,
}

/// <summary>The request's tool choice.</summary>
public readonly record struct ToolChoice
{
    /// <summary>The mode.</summary>
    public required ToolChoiceMode Mode { get; init; }

    /// <summary>The required tool for <see cref="ToolChoiceMode.Named"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>Why a reply ended.</summary>
public enum ChatStopReason
{
    /// <summary>The model finished its turn.</summary>
    EndTurn = 0,

    /// <summary>The model is waiting for tool results.</summary>
    ToolUse = 1,

    /// <summary>The output limit was reached.</summary>
    MaxTokens = 2,

    /// <summary>A stop sequence was produced.</summary>
    StopSequence = 3,

    /// <summary>The model declined (Anthropic <c>refusal</c>, OpenAI <c>content_filter</c>).</summary>
    Refusal = 4,

    /// <summary>A long-running turn paused and can be continued (Anthropic <c>pause_turn</c>).</summary>
    PauseTurn = 5,
}

/// <summary>Token usage reported by a backend.</summary>
public readonly record struct ChatUsage
{
    /// <summary>Prompt tokens.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Completion tokens.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Prompt tokens served from a prompt cache, when reported.</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Prompt tokens written to a prompt cache, when reported.</summary>
    public int? CacheCreationTokens { get; init; }

    /// <summary>Whether any count is known.</summary>
    public bool IsEmpty => InputTokens is null && OutputTokens is null && CacheReadTokens is null && CacheCreationTokens is null;
}

/// <summary>Shared writer settings: plain escaping, as the vendors' own responses use.</summary>
internal static class ChatJsonOptions
{
    public static JsonWriterOptions Writer { get; } = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>JSON helpers shared by the parsers and builders.</summary>
internal static class JsonHelpers
{
    public static string? GetString(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetInt(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : null;

    public static double? GetDouble(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static bool GetBool(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public static bool TryGetObject(this JsonElement element, string property, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object
        || Unset(out value);

    public static bool TryGetArray(this JsonElement element, string property, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Array
        || Unset(out value);

    /// <summary>Returns <paramref name="json"/> when it is a JSON object, otherwise wraps it so it always is one.</summary>
    public static string EnsureJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return json;
            }
        }
        catch (JsonException)
        {
        }

        return InputObject(json);
    }

    /// <summary><c>{"input": text}</c>: how free-text tool input (custom tools, non-object arguments) travels as JSON.</summary>
    public static string InputObject(string text)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("input", text);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string NewToolUseId() => "toolu_" + Guid.NewGuid().ToString("N")[..24];

    /// <summary>
    /// A client's body as a tree to edit. A property named twice keeps its last value, as
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> reads it everywhere else; a plain
    /// <see cref="JsonNode.Parse(ReadOnlySpan{byte}, JsonNodeOptions?, JsonDocumentOptions)"/> tree would throw
    /// <see cref="ArgumentException"/> on first access instead. Throws <see cref="JsonException"/> when it isn't JSON.
    /// </summary>
    public static JsonNode? ParseEditable(ReadOnlyMemory<byte> json, JsonDocumentOptions options = default)
    {
        try
        {
            return JsonNode.Parse(json.Span, documentOptions: options with { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            // A repeated property name, or not JSON at all: the tolerant parse below tells them apart.
        }

        using JsonDocument document = JsonDocument.Parse(json, options);
        return LastValueWins(document.RootElement);
    }

    /// <inheritdoc cref="ParseEditable(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>
    public static JsonNode? ParseEditable(string json, JsonDocumentOptions options = default) =>
        ParseEditable(System.Text.Encoding.UTF8.GetBytes(json), options);

    private static JsonNode? LastValueWins(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    obj[property.Name] = LastValueWins(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                return new JsonArray([.. element.EnumerateArray().Select(LastValueWins)]);
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    private static bool Unset(out JsonElement value)
    {
        value = default;
        return false;
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ChatJsonContext : JsonSerializerContext;
