using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>Turns a chat conversation into a single prompt for text-in/text-out backends such as CLIs.</summary>
internal static class ChatTranscript
{
    private const string ContinueInstruction = "Continue the following conversation. Reply only with the assistant's next message.\n";

    /// <summary>
    /// Splits system/developer messages out (for CLIs that accept a separate system prompt) and renders the rest. A
    /// lone user message is passed through verbatim; a longer conversation is rendered as a labelled transcript.
    /// </summary>
    public static (string? System, string Prompt) Render(IReadOnlyList<ChatMessage> messages)
    {
        var system = new StringBuilder();
        var turns = new List<ChatMessage>();
        foreach (ChatMessage message in messages)
        {
            if (message.Role is "system" or "developer")
            {
                AppendSystem(system, message.Content);
            }
            else if (!string.IsNullOrWhiteSpace(message.Content))
            {
                turns.Add(message);
            }
        }

        string? systemPrompt = system.Length > 0 ? system.ToString() : null;
        if (turns.Count == 1 && turns[0].Role == "user")
        {
            return (systemPrompt, turns[0].Content);
        }

        var prompt = new StringBuilder(ContinueInstruction);
        foreach (ChatMessage turn in turns)
        {
            prompt.Append("\n[").Append(turn.Role).Append("]\n").Append(turn.Content).Append('\n');
        }

        return (systemPrompt, prompt.ToString());
    }

    /// <summary>
    /// Renders structured turns, including tool calls and results in the <see cref="PromptedToolCalls"/> format, and
    /// appends the tool instructions to the system prompt when the request offers tools.
    /// </summary>
    /// <param name="request">The parsed request.</param>
    /// <param name="describeTools">Append the prompted tool-calling instructions (off for backends with native tools).</param>
    /// <param name="format">The fence label tool calls use; the plain <c>tool_call</c> by default.</param>
    public static (string? System, string Prompt) Render(ChatCompletionRequest request, bool describeTools = true, PromptedToolFormat? format = null)
    {
        if (request.Tools.Count == 0 && !request.Turns.Any(t => t.Blocks.Any(b => b is ToolUseBlock or ToolResultBlock)))
        {
            return Render(request.Messages);
        }

        var system = new StringBuilder();
        var turns = new List<(string Role, string Content)>();
        foreach (ChatTurn turn in request.Turns)
        {
            if (turn.Role == "system")
            {
                AppendSystem(system, turn.Text);
                continue;
            }

            var content = new StringBuilder();
            foreach (ContentBlock block in turn.Blocks)
            {
                string part = block switch
                {
                    TextBlock t => t.Text,
                    ToolUseBlock u => PromptedToolCalls.RenderToolUse(u, format),
                    ToolResultBlock r => PromptedToolCalls.RenderToolResult(r),
                    ImageBlock => "[image omitted]",
                    _ => string.Empty,
                };
                if (part.Length == 0)
                {
                    continue;
                }

                if (content.Length > 0)
                {
                    content.Append("\n\n");
                }

                content.Append(part);
            }

            if (content.Length > 0)
            {
                turns.Add((turn.Role, content.ToString()));
            }
        }

        if (describeTools && request.Tools.Count > 0)
        {
            AppendSystem(system, PromptedToolCalls.RenderInstructions(request.Tools, request.ToolChoice, format));
        }

        string? systemPrompt = system.Length > 0 ? system.ToString() : null;
        if (turns.Count == 1 && turns[0].Role == "user")
        {
            return (systemPrompt, turns[0].Content);
        }

        var prompt = new StringBuilder(ContinueInstruction);
        foreach ((string role, string content) in turns)
        {
            prompt.Append("\n[").Append(role).Append("]\n").Append(content).Append('\n');
        }

        return (systemPrompt, prompt.ToString());
    }

    /// <summary>Renders the system prompt inline, for CLIs that have no separate system prompt option.</summary>
    public static string RenderInline(IReadOnlyList<ChatMessage> messages)
    {
        (string? system, string prompt) = Render(messages);
        return Inline(system, prompt);
    }

    /// <summary>Renders the system prompt inline, for CLIs that have no separate system prompt option.</summary>
    public static string RenderInline(ChatCompletionRequest request, PromptedToolFormat? format = null)
    {
        (string? system, string prompt) = Render(request, format: format);
        return Inline(system, prompt);
    }

    private static string Inline(string? system, string prompt) => system is null ? prompt : $"[system]\n{system}\n\n{prompt}";

    private static void AppendSystem(StringBuilder system, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (system.Length > 0)
        {
            system.Append("\n\n");
        }

        system.Append(text);
    }
}
