using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Tool calling for text-in/text-out backends (CLIs): the client's tools are described in the prompt, the model is
/// asked to answer with a fenced <c>tool_call</c> block when it wants to use one, and the reply is parsed back into
/// structured tool calls for the client. Prior tool calls and results are rendered the same way in the transcript.
/// </summary>
internal static class PromptedToolCalls
{
    public const string Fence = "tool_call";

    /// <summary>Instructions and the tool catalog, appended to the system prompt.</summary>
    public static string RenderInstructions(IReadOnlyList<ToolDefinition> tools, ToolChoice choice)
    {
        var builder = new StringBuilder();
        builder.Append("# Tools\n\n");
        builder.Append("You can call the tools listed below. You cannot run them yourself: the caller runs each call and sends you the result.\n");
        builder.Append("To call a tool, end your reply with one fenced block per call, exactly in this form, and write nothing after the last block:\n\n");
        builder.Append("```").Append(Fence).Append("\n{\"name\": \"<tool name>\", \"input\": { <arguments matching the tool's input schema> }}\n```\n\n");
        builder.Append("Rules: use only the tools listed; give every required argument; put explanations before the block, never inside it; ");
        builder.Append("when no tool is needed, answer directly without a block.\n");
        switch (choice.Mode)
        {
            case ToolChoiceMode.Required:
                builder.Append("You must call at least one tool in this reply.\n");
                break;
            case ToolChoiceMode.Named when choice.Name is { } name:
                builder.Append("You must call the tool '").Append(name).Append("' in this reply.\n");
                break;
            case ToolChoiceMode.None:
                builder.Append("Do not call any tool in this reply.\n");
                break;
        }

        builder.Append("\n## Available tools\n");
        foreach (ToolDefinition tool in tools)
        {
            builder.Append("\n### ").Append(tool.Name).Append('\n');
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                builder.Append(tool.Description.Trim()).Append('\n');
            }

            builder.Append("Input schema: ").Append(tool.InputSchemaJson).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Renders an assistant tool call the way the model is asked to produce it.</summary>
    public static string RenderToolUse(ToolUseBlock call)
    {
        string name = JsonSerializer.Serialize(call.Name, ChatJsonContext.Default.String);
        return $"```{Fence}\n{{\"name\": {name}, \"input\": {JsonHelpers.EnsureJsonObject(call.InputJson)}}}\n```";
    }

    /// <summary>Renders a tool result for the transcript.</summary>
    public static string RenderToolResult(ToolResultBlock result) =>
        $"[tool_result{(result.IsError ? " error" : string.Empty)}]\n{result.Content}";

    /// <summary>Splits a reply into the leading text and the tool calls it ends with.</summary>
    public static (string Text, IReadOnlyList<ToolUseBlock> Calls) Parse(string reply)
    {
        var calls = new List<ToolUseBlock>();
        string remaining = reply;
        string text = reply;
        int searchFrom = 0;
        var textParts = new StringBuilder();

        while (true)
        {
            int start = remaining.IndexOf("```" + Fence, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                textParts.Append(remaining.AsSpan(searchFrom));
                break;
            }

            int bodyStart = remaining.IndexOf('\n', start);
            if (bodyStart < 0)
            {
                textParts.Append(remaining.AsSpan(searchFrom));
                break;
            }

            int end = remaining.IndexOf("```", bodyStart + 1, StringComparison.Ordinal);
            string body = end < 0 ? remaining[(bodyStart + 1)..] : remaining[(bodyStart + 1)..end];
            textParts.Append(remaining.AsSpan(searchFrom, start - searchFrom));
            if (TryParseCall(body.Trim(), out ToolUseBlock? call) && call is not null)
            {
                calls.Add(call);
            }
            else
            {
                // Not a well-formed call: keep it as text so nothing the model said is lost.
                textParts.Append(remaining.AsSpan(start, (end < 0 ? remaining.Length : end + 3) - start));
            }

            if (end < 0)
            {
                break;
            }

            searchFrom = end + 3;
        }

        text = textParts.ToString().Trim();
        return (text, calls);
    }

    private static bool TryParseCall(string json, out ToolUseBlock? call)
    {
        call = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? name = root.GetString("name") ?? root.GetString("tool") ?? root.GetString("tool_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string input = root.TryGetObject("input", out JsonElement i) ? i.GetRawText()
                : root.TryGetObject("arguments", out JsonElement a) ? a.GetRawText()
                : root.TryGetObject("parameters", out JsonElement p) ? p.GetRawText()
                : "{}";
            call = new ToolUseBlock(JsonHelpers.NewToolUseId(), name, input);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
