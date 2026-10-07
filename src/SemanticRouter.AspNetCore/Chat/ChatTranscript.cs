using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>Turns a chat conversation into a single prompt for text-in/text-out backends such as CLIs.</summary>
internal static class ChatTranscript
{
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
                if (system.Length > 0)
                {
                    system.Append("\n\n");
                }

                system.Append(message.Content);
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

        var prompt = new StringBuilder("Continue the following conversation. Reply only with the assistant's next message.\n");
        foreach (ChatMessage turn in turns)
        {
            prompt.Append("\n[").Append(turn.Role).Append("]\n").Append(turn.Content).Append('\n');
        }

        return (systemPrompt, prompt.ToString());
    }

    /// <summary>Renders the system prompt inline, for CLIs that have no separate system prompt option.</summary>
    public static string RenderInline(IReadOnlyList<ChatMessage> messages)
    {
        (string? system, string prompt) = Render(messages);
        return system is null ? prompt : $"[system]\n{system}\n\n{prompt}";
    }
}
