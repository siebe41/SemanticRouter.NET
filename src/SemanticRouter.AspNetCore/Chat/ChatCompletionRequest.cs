using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>One chat message, flattened to text.</summary>
/// <param name="Role"><c>system</c>, <c>developer</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">The message's text parts joined with newlines.</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// The parts of an OpenAI-style <c>POST /v1/chat/completions</c> body the router needs. The original bytes are kept
/// separately and forwarded untouched to HTTP connectors.
/// </summary>
public sealed class ChatCompletionRequest
{
    /// <summary>The requested model (<c>auto</c>, an <c>auto:</c> strategy alias, or a catalog model name).</summary>
    public string? Model { get; init; }

    /// <summary>Whether the client asked for server-sent events.</summary>
    public bool Stream { get; init; }

    /// <summary>The conversation, flattened to text.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>Whether the request defines tools (function calling).</summary>
    public bool HasTools { get; init; }

    /// <summary>Whether any message carries an image.</summary>
    public bool HasImages { get; init; }

    /// <summary>Estimated input tokens for the whole request (messages and tool definitions).</summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>Parses a chat-completions body. Also accepts <c>{"prompt": "..."}</c> as a single user message.</summary>
    public static ChatCompletionRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ChatCompletionRequest();
        }

        var messages = new List<ChatMessage>();
        bool hasImages = false;
        int tokens = 0;

        if (root.TryGetProperty("messages", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement message in array.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string role = message.TryGetProperty("role", out JsonElement r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()!
                    : "user";
                string content = message.TryGetProperty("content", out JsonElement c) ? ReadContent(c, ref hasImages) : string.Empty;
                messages.Add(new ChatMessage(role, content));
                tokens += TokenEstimator.Estimate(content) + 4;
            }
        }
        else if (root.TryGetProperty("prompt", out JsonElement prompt) && prompt.ValueKind == JsonValueKind.String)
        {
            string content = prompt.GetString()!;
            messages.Add(new ChatMessage("user", content));
            tokens += TokenEstimator.Estimate(content);
        }

        bool hasTools = HasNonEmptyArray(root, "tools") || HasNonEmptyArray(root, "functions");
        if (hasTools)
        {
            tokens += TokenEstimator.Estimate((HasNonEmptyArray(root, "tools") ? root.GetProperty("tools") : root.GetProperty("functions")).GetRawText());
        }

        return new ChatCompletionRequest
        {
            Model = root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            Stream = root.TryGetProperty("stream", out JsonElement s) && s.ValueKind == JsonValueKind.True,
            Messages = messages,
            HasTools = hasTools,
            HasImages = hasImages,
            EstimatedInputTokens = tokens,
        };
    }

    private static bool HasNonEmptyArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static string ReadContent(JsonElement content, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                Append(builder, part.GetString());
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = part.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is "image_url" or "image" or "input_image")
            {
                hasImages = true;
            }
            else if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                Append(builder, text.GetString());
            }
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(text);
    }
}
