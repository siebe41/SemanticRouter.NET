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

/// <summary>A tool the client offers the model.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">What it does.</param>
/// <param name="InputSchemaJson">JSON Schema of its arguments (an object schema).</param>
public sealed record ToolDefinition(string Name, string? Description, string InputSchemaJson);

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

        return JsonSerializer.Serialize(new Dictionary<string, string> { ["input"] = json }, ChatJsonContext.Default.DictionaryStringString);
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
