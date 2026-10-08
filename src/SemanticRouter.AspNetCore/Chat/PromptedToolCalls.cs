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

    /// <summary>Splits a whole reply into its text and the tool calls it ends with (see <see cref="PromptedReplyStream"/>).</summary>
    /// <param name="reply">The model's reply.</param>
    /// <param name="toolNames">The request's tools; when given, near-miss formats that name one of them count as calls too.</param>
    public static (string Text, IReadOnlyList<ToolUseBlock> Calls) Parse(string reply, IReadOnlyCollection<string>? toolNames = null)
    {
        var stream = new PromptedReplyStream(toolNames);
        string text = stream.Feed(reply);
        PromptedReply end = stream.Finish();
        return ((text + end.Text).Trim(), end.Calls);
    }
}
