using System.Text.Json.Serialization;
using SemanticRouter;

namespace RoutingApiSample;

public sealed record ChatMessageBody(string Role, string Content);

public sealed record ChatCompletionBody(string? Model, IReadOnlyList<ChatMessageBody> Messages, bool Stream = false);

public sealed record ChatChoice(int Index, ChatMessageBody Message, [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record RoutingSummary(int RequiredTier, RoutingReason Reason, string? Route, float? Similarity, double Complexity, IReadOnlyList<string> Models);

public sealed record ChatCompletionResponse(
    string Id,
    string Model,
    IReadOnlyList<ChatChoice> Choices,
    [property: JsonPropertyName("x_routing")] RoutingSummary? Routing)
{
    [JsonPropertyName("object")]
    public string ObjectType => "chat.completion";
}

/// <summary>Annotate-mode stand-in for a real model call, so the sample runs with nothing installed.</summary>
public static class SimulatedCompletion
{
    public static ChatCompletionResponse Create(ChatCompletionBody request, RoutingPlan? plan)
    {
        string model = plan is { Candidates.Count: > 0 } ? plan.Candidates[0].Model.Name : "none";
        string lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        string preview = lastUser.Length > 60 ? lastUser[..60] + "…" : lastUser;

        RoutingSummary? summary = plan is null
            ? null
            : new RoutingSummary(
                plan.Classification.RequiredTier,
                plan.Classification.Reason,
                plan.Classification.RouteName,
                plan.Classification.Similarity,
                Math.Round(plan.Classification.Complexity.Score, 3),
                plan.Candidates.Select(c => c.Model.Name).ToArray());

        return new ChatCompletionResponse(
            $"chatcmpl-{Guid.NewGuid():N}",
            model,
            [new ChatChoice(0, new ChatMessageBody("assistant", $"[{model}] would answer: \"{preview}\""), "stop")],
            summary);
    }
}
