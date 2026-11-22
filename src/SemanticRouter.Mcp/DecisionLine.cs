using System.Globalization;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Mcp;

/// <summary>
/// What the router decided, a field per line: the "decision line" the dashboard's Report form fills in
/// (<c>decisionLine</c> in <c>wwwroot/js/feedback.js</c>, kept the same). Never the prompt, the project, memory note
/// titles or error details, which can carry private text.
/// </summary>
internal static class DecisionLine
{
    /// <summary>The decision line of a routed request, with the feedback it drew.</summary>
    public static string Of(RoutingActivityEntry e, IReadOnlyList<FeedbackKind> feedback)
    {
        List<string> lines =
        [
            $"Reason: {e.Reason}{(e.Cached ? " (reused a recent decision)" : string.Empty)}",
            $"Tier: {e.Tier}",
            $"Route: {e.Route ?? "(none)"}{(e.Similarity is { } similarity ? $" · similarity {Number(similarity)}" : string.Empty)}",
            $"Complexity: {Number(e.Complexity)}",
        ];
        Add(lines, "Callout", e.Callout);
        Add(lines, "Profile", e.Profile);
        Add(lines, "Selection", e.Selection);
        if (e.Sticky)
        {
            lines.Add("Sticky: stayed on the conversation's model");
        }

        if (e.Learned is { } learned)
        {
            lines.Add($"Learned change: #{learned}");
        }

        Add(lines, "Left out as unreliable", e.Unreliable);
        lines.Add($"Planned model: {e.PlannedModel ?? "(none)"}");
        lines.Add($"Served by: {e.ServedBy ?? "(none)"}{(e.Attempt > 1 ? $" · attempt {e.Attempt}" : string.Empty)}{(e.Degraded ? " · degraded" : string.Empty)} · {e.Outcome}");
        string?[] request =
        [
            e.Protocol.ToString(),
            e.Client is { Length: > 0 } client ? $"client {client}" : null,
            e.RequestedModel is { Length: > 0 } model ? $"model {model}" : null,
            e.HasTools ? "tools" : null,
            e.ToolContinuation ? "tool result" : null,
            e.LocalOnly ? "local only" : null,
            $"~{e.EstimatedInputTokens} tokens",
        ];
        lines.Add("Request: " + string.Join(" · ", request.OfType<string>()));
        if (feedback.Count > 0)
        {
            lines.Add("Feedback: " + string.Join(", ", feedback));
        }

        if (e.Grade is { } grade)
        {
            lines.Add($"Judge grade: {grade}/5");
        }

        lines.Add($"Activity entry: #{e.Id} at {e.Timestamp.ToString("O", CultureInfo.InvariantCulture)}");
        return string.Join('\n', lines);
    }

    /// <summary>The decision line of a classification (<c>POST api/classify</c>): what a request with the prompt would get.</summary>
    public static string Of(PromptClassificationResponse c)
    {
        List<string> lines =
        [
            $"Reason: {c.Reason}",
            $"Tier: {c.RequiredTier}",
            $"Route: {c.Route ?? "(none)"}{(c.Similarity is { } similarity ? $" · similarity {Number(similarity)}" : string.Empty)}",
            $"Complexity: {Number(c.Complexity.Score)}",
        ];
        Add(lines, "Callout", c.Callout);
        Add(lines, "Profile", c.Profile);
        if (c.LocalOnly)
        {
            lines.Add("Local only: yes");
        }

        lines.Add($"Planned model: {(c.Models.Count > 0 ? c.Models[0].Model : "(none)")}");
        if (c.Models.Count > 1)
        {
            lines.Add("Then: " + string.Join(", ", c.Models.Skip(1).Take(4).Select(m => m.Model)));
        }

        return string.Join('\n', lines);
    }

    private static void Add(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            lines.Add($"{label}: {value}");
        }
    }

    /// <summary>As the dashboard shows a score: at most three decimals, no trailing zeros.</summary>
    private static string Number(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
