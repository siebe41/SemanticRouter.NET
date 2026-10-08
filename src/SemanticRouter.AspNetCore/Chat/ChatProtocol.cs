using System.Text.Json;
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
public abstract record ContentBlock;

/// <summary>Plain text.</summary>
/// <param name="Text">The text.</param>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>An image, either inline (base64) or by URL.</summary>
/// <param name="MediaType">MIME type for inline data, e.g. <c>image/png</c>.</param>
/// <param name="Data">Base64 data when inline.</param>
/// <param name="Url">The URL (or data URL) when referenced.</param>
public sealed record ImageBlock(string? MediaType, string? Data, string? Url) : ContentBlock;

/// <summary>A tool call issued by the assistant.</summary>
/// <param name="Id">Opaque call id, echoed back by the matching <see cref="ToolResultBlock"/>.</param>
/// <param name="Name">The tool name.</param>
/// <param name="InputJson">The call arguments as a JSON object.</param>
public sealed record ToolUseBlock(string Id, string Name, string InputJson) : ContentBlock;

/// <summary>
/// The model's visible reasoning ("extended thinking"). Carried back by clients on tool-loop continuations; only an
/// Anthropic backend can use it again, and only with the signature it issued.
/// </summary>
/// <param name="Text">The reasoning text (empty for redacted thinking).</param>
/// <param name="Signature">The backend's signature, or the router's marker for reasoning it relayed from elsewhere.</param>
/// <param name="RedactedData">Encrypted content of a <c>redacted_thinking</c> block.</param>
public sealed record ThinkingBlock(string Text, string? Signature, string? RedactedData = null) : ContentBlock
{
    /// <summary>The signature the router puts on reasoning it relays from a non-Anthropic backend.</summary>
    public const string RouterSignature = "semantic-router-relayed";
}

/// <summary>The result of a tool call, sent by the client.</summary>
/// <param name="ToolUseId">The <see cref="ToolUseBlock.Id"/> this answers.</param>
/// <param name="Content">The result, flattened to text.</param>
/// <param name="IsError">Whether the tool failed.</param>
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;

/// <summary>One message in the conversation, with its structured content.</summary>
/// <param name="Role"><c>system</c>, <c>user</c> or <c>assistant</c>. Tool results are <c>user</c> turns.</param>
/// <param name="Blocks">The content parts, in order.</param>
public sealed record ChatTurn(string Role, IReadOnlyList<ContentBlock> Blocks)
{
    /// <summary>The text parts joined with newlines (tool calls and results excluded).</summary>
    public string Text => string.Join('\n', Blocks.OfType<TextBlock>().Select(b => b.Text).Where(t => t.Length > 0));

    /// <summary>Whether the turn consists only of tool results (a tool-loop continuation, not a new ask).</summary>
    public bool IsToolResultOnly => Blocks.Count > 0 && Blocks.All(b => b is ToolResultBlock);

    /// <summary>Creates a turn with a single text block.</summary>
    public static ChatTurn FromText(string role, string text) => new(role, [new TextBlock(text)]);
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
/// <param name="Name">The name used with backends: unique and safe for every protocol (namespaces are folded in).</param>
/// <param name="Description">What it does.</param>
/// <param name="InputSchemaJson">JSON Schema of its arguments (an object schema).</param>
/// <param name="Kind">Function or custom (free-text input).</param>
/// <param name="Namespace">The Responses API namespace the client declared it in, when not the default.</param>
/// <param name="ClientName">The name the client uses, when it differs from <paramref name="Name"/>.</param>
public sealed record ToolDefinition(
    string Name, string? Description, string InputSchemaJson, ToolKind Kind = ToolKind.Function, string? Namespace = null, string? ClientName = null)
{
    /// <summary>The schema a custom tool is given when a backend only knows functions.</summary>
    public const string CustomInputSchema = """{"type":"object","properties":{"input":{"type":"string","description":"The tool's raw input."}},"required":["input"]}""";

    /// <summary>The name the client knows the tool by.</summary>
    public string OriginalName => ClientName ?? Name;
}

/// <summary>A request for the model to reason before answering.</summary>
/// <param name="Effort"><c>minimal</c>, <c>low</c>, <c>medium</c> or <c>high</c>, when the client gave one.</param>
/// <param name="BudgetTokens">The thinking budget, when the client gave one (Anthropic).</param>
public sealed record ReasoningRequest(string? Effort, int? BudgetTokens)
{
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
public enum ResponseFormatKind
{
    /// <summary>Any JSON object.</summary>
    JsonObject = 1,

    /// <summary>JSON matching a schema.</summary>
    JsonSchema = 2,
}

/// <summary>Structured output: OpenAI <c>response_format</c> or Responses <c>text.format</c>.</summary>
/// <param name="Kind">JSON object or JSON schema.</param>
/// <param name="Name">The schema's name.</param>
/// <param name="SchemaJson">The JSON schema.</param>
/// <param name="Strict">Whether the schema must be followed exactly.</param>
public sealed record ResponseFormat(ResponseFormatKind Kind, string? Name = null, string? SchemaJson = null, bool Strict = false);

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
/// <param name="Mode">The mode.</param>
/// <param name="Name">The required tool for <see cref="ToolChoiceMode.Named"/>.</param>
public readonly record struct ToolChoice(ToolChoiceMode Mode, string? Name = null);

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
/// <param name="InputTokens">Prompt tokens.</param>
/// <param name="OutputTokens">Completion tokens.</param>
/// <param name="CacheReadTokens">Prompt tokens served from a prompt cache, when reported.</param>
/// <param name="CacheCreationTokens">Prompt tokens written to a prompt cache, when reported.</param>
public readonly record struct ChatUsage(int? InputTokens, int? OutputTokens, int? CacheReadTokens = null, int? CacheCreationTokens = null)
{
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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
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
