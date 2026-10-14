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
    private readonly bool _stripSystemReminders;
    private readonly string[] _injectedTags;

    /// <summary>Creates an extractor.</summary>
    /// <param name="selection">Which messages contribute.</param>
    /// <param name="stripSystemReminders">
    /// Remove the context blocks agentic clients inject into user messages (Claude Code's
    /// <c>&lt;system-reminder&gt;</c>, the GitHub Copilot app's <c>&lt;system_reminder&gt;</c>,
    /// <c>&lt;current_datetime&gt;</c> and <c>&lt;copilot_tauri_workspace&gt;</c>), so the prompt reflects what the
    /// person asked.
    /// </param>
    /// <param name="injectedTags">More tags whose blocks are removed, besides the built-in ones.</param>
    public JsonPromptExtractor(
        PromptMessageSelection selection = PromptMessageSelection.LastUserMessage, bool stripSystemReminders = true, IEnumerable<string>? injectedTags = null)
    {
        _selection = selection;
        _stripSystemReminders = stripSystemReminders;
        _injectedTags = injectedTags?.ToArray() ?? [];
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
            string? fromMessages = Clean(ExtractFromMessages(messages));
            if (!string.IsNullOrWhiteSpace(fromMessages))
            {
                return fromMessages;
            }
        }

        // Responses API: "input" is a list of items; messages among them have roles like chat messages.
        if (body.TryGetProperty("input", out JsonElement items) && items.ValueKind == JsonValueKind.Array
            && Clean(ExtractFromMessages(items)) is { Length: > 0 } fromItems)
        {
            return fromItems;
        }

        foreach (string property in (ReadOnlySpan<string>)["prompt", "input"])
        {
            if (body.TryGetProperty(property, out JsonElement value) && Clean(ReadText(value)) is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Removes injected context blocks (<c>&lt;system-reminder&gt;</c> and the like) and trims the result.</summary>
    public static string? StripSystemReminders(string? text) => StripInjectedBlocks(text, null);

    /// <summary>Removes injected context blocks, the built-in tags and <paramref name="extraTags"/>, and trims the result.</summary>
    public static string? StripInjectedBlocks(string? text, IEnumerable<string>? extraTags)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string stripped = InjectedBlocks.Remove(text, extraTags);
        return ReferenceEquals(stripped, text) ? text : stripped.Trim();
    }

    private string? Clean(string? text) => _stripSystemReminders ? StripInjectedBlocks(text, _injectedTags) : text;

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
