using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The assistant's text in a reply as the client received it, in any of the three protocols, as one JSON document or
/// as server-sent events. Used to grade answers (<see cref="AnswerJudge"/>), so it is lenient: what it cannot read it
/// leaves out.
/// </summary>
internal static class AnswerText
{
    /// <summary>The text, and whether the reply made tool calls.</summary>
    public static (string Text, bool ToolCalls) Extract(ChatProtocol protocol, ReadOnlySpan<byte> reply)
    {
        string body = Encoding.UTF8.GetString(reply).Trim();
        var text = new StringBuilder();
        bool tools = false;
        if (body.StartsWith('{'))
        {
            Read(protocol, body, streamed: false, text, ref tools);
        }
        else
        {
            foreach (string line in body.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("data:", StringComparison.Ordinal) && trimmed[5..].Trim() is { Length: > 0 } data && data != "[DONE]")
                {
                    Read(protocol, data, streamed: true, text, ref tools);
                }
            }
        }

        return (text.ToString(), tools);
    }

    private static void Read(ChatProtocol protocol, string json, bool streamed, StringBuilder text, ref bool tools)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        switch (protocol)
        {
            case ChatProtocol.Anthropic when streamed:
                if (root.TryGetProperty("delta", out JsonElement delta) && Str(delta, "type") == "text_delta")
                {
                    text.Append(Str(delta, "text"));
                }

                tools |= root.TryGetProperty("content_block", out JsonElement block) && Str(block, "type") == "tool_use";
                break;

            case ChatProtocol.Anthropic:
                foreach (JsonElement part in Array(root, "content"))
                {
                    if (Str(part, "type") == "text")
                    {
                        text.Append(Str(part, "text"));
                    }

                    tools |= Str(part, "type") == "tool_use";
                }

                break;

            case ChatProtocol.Responses when streamed:
                if (Str(root, "type") == "response.output_text.delta")
                {
                    text.Append(Str(root, "delta"));
                }

                tools |= root.TryGetProperty("item", out JsonElement item) && Str(item, "type") is "function_call" or "custom_tool_call";
                break;

            case ChatProtocol.Responses:
                foreach (JsonElement output in Array(root, "output"))
                {
                    tools |= Str(output, "type") is "function_call" or "custom_tool_call";
                    foreach (JsonElement content in Array(output, "content"))
                    {
                        if (Str(content, "type") == "output_text")
                        {
                            text.Append(Str(content, "text"));
                        }
                    }
                }

                break;

            default:
                foreach (JsonElement choice in Array(root, "choices"))
                {
                    if (choice.TryGetProperty(streamed ? "delta" : "message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
                    {
                        text.Append(Str(message, "content"));
                        tools |= message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0;
                    }
                }

                break;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                yield return item;
            }
        }
    }
}
