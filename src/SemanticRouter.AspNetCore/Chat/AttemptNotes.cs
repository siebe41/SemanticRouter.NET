using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Things a connector wants on the record about the attempt it served (a repaired tool call, a malformed one). The
/// dispatcher puts one in the request's features before each attempt and copies it to the activity entry.
/// </summary>
internal sealed class AttemptNotes
{
    private readonly List<string> _notes = [];

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>How many tool calls the attempt read out of the model's text (prompted tool calling), if any.</summary>
    public int? PromptedToolCalls { get; private set; }

    /// <summary>Records that the attempt being served returned <paramref name="count"/> tool calls read out of the model's text.</summary>
    public static void ReportPromptedToolCalls(HttpContext context, int count)
    {
        if (context.Features.Get<AttemptNotes>() is { } notes)
        {
            notes.PromptedToolCalls = count;
        }
    }

    /// <summary>Records <paramref name="note"/> on the attempt being served, if the dispatcher is tracking one.</summary>
    public static void Report(HttpContext context, string note)
    {
        if (context.Features.Get<AttemptNotes>() is { } notes && !notes._notes.Contains(note, StringComparer.Ordinal))
        {
            notes._notes.Add(note);
        }
    }
}
