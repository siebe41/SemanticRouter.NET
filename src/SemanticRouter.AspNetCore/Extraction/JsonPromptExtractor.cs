using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Extracts prompts from the common LLM request shapes:
/// <list type="bullet">
///   <item>Chat: <c>{"messages":[{"role":"user","content":"…"}]}</c>, where <c>content</c> may be a string or an
///         array of parts (<c>{"type":"text","text":"…"}</c>), as used by OpenAI, Ollama and Anthropic.</item>
///   <item>Completion: <c>{"prompt":"…"}</c> (Ollama <c>/api/generate</c>, legacy completions).</item>
///   <item>Input: <c>{"input":"…"}</c> or <c>{"input":["…","…"]}</c>.</item>
/// </list>
/// </summary>
public sealed class JsonPromptExtractor : IPromptExtractor
{
    private readonly PromptMessageSelection _selection;

    /// <summary>Creates an extractor.</summary>
    public JsonPromptExtractor(PromptMessageSelection selection = PromptMessageSelection.LastUserMessage)
    {
        _selection = selection;
    }

    /// <inheritdoc />
    public string? ExtractPrompt(HttpContext context, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (body.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
        {
            string? fromMessages = ExtractFromMessages(messages);
            if (!string.IsNullOrWhiteSpace(fromMessages))
            {
                return fromMessages;
            }
        }

        foreach (string property in (ReadOnlySpan<string>)["prompt", "input"])
        {
            if (body.TryGetProperty(property, out JsonElement value) && ReadText(value) is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private string? ExtractFromMessages(JsonElement messages)
    {
        if (_selection == PromptMessageSelection.LastUserMessage)
        {
            string? last = null;
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (IsRole(message, "user") && ReadContent(message) is { Length: > 0 } text)
                {
                    last = text;
                }
            }

            return last;
        }

        var builder = new StringBuilder();
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (_selection == PromptMessageSelection.AllUserMessages && !IsRole(message, "user"))
            {
                continue;
            }

            if (ReadContent(message) is { Length: > 0 } text)
            {
                if (builder.Length > 0)
                {
                    builder.Append("\n\n");
                }

                builder.Append(text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsRole(JsonElement message, string role) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("role", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.ValueEquals(role);

    private static string? ReadContent(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement content)
            ? ReadText(content)
            : null;

    private static string? ReadText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();

            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (JsonElement part in value.EnumerateArray())
                {
                    string? text = part.ValueKind switch
                    {
                        JsonValueKind.String => part.GetString(),
                        JsonValueKind.Object when part.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String
                            => t.GetString(),
                        _ => null,
                    };

                    if (!string.IsNullOrEmpty(text))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(text);
                    }
                }

                return builder.Length == 0 ? null : builder.ToString();

            default:
                return null;
        }
    }
}
