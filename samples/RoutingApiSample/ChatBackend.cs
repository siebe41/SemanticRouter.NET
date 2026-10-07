using System.Text.Json.Serialization;
using SemanticRouter;

namespace RoutingApiSample;

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatCompletionRequest(string? Model, IReadOnlyList<ChatMessage> Messages, bool Stream = false);

public sealed record ChatCompletionResponse(
    string Id,
    string Model,
    IReadOnlyList<ChatChoice> Choices,
    [property: JsonPropertyName("x_routing")] RoutingSummary? Routing)
{
    [JsonPropertyName("object")]
    public string ObjectType => "chat.completion";
}

public sealed record ChatChoice(int Index, ChatMessage Message, [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record RoutingSummary(RouteTarget Target, RoutingReason Reason, string? Route, float? Similarity, double Complexity);

/// <summary>A chat model client. Implement this over Ollama, OpenAI, Anthropic, Azure, …</summary>
public interface IChatBackend
{
    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        PromptClassificationResult? routing,
        CancellationToken cancellationToken);
}

/// <summary>Echo backend so the sample runs without any model installed.</summary>
public sealed class SimulatedChatBackend(string modelName) : IChatBackend
{
    public Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        PromptClassificationResult? routing,
        CancellationToken cancellationToken)
    {
        string lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        string preview = lastUser.Length > 60 ? lastUser[..60] + "…" : lastUser;

        var reply = new ChatMessage("assistant", $"[{modelName}] would answer: \"{preview}\"");
        RoutingSummary? summary = routing is null
            ? null
            : new RoutingSummary(routing.Target, routing.Reason, routing.RouteName, routing.Similarity, Math.Round(routing.Complexity.Score, 3));

        return Task.FromResult(new ChatCompletionResponse(
            $"chatcmpl-{Guid.NewGuid():N}",
            modelName,
            [new ChatChoice(0, reply, "stop")],
            summary));
    }
}
