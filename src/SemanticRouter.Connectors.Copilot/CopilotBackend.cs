using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>What the connector needs from GitHub's Copilot SDK; the seam that lets tests fake the SDK client.</summary>
internal interface ICopilotBackend
{
    /// <summary>The model ids the signed-in account can use.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one turn in a fresh session and streams what happens. The sequence ends when the turn is over: the model
    /// answered, asked for tools (their calls are the last updates, and the session is abandoned, not waited on), or an
    /// <see cref="CopilotUpdate.Error"/> was reported.
    /// </summary>
    IAsyncEnumerable<CopilotUpdate> RunAsync(CopilotTurn turn, CancellationToken cancellationToken);
}

/// <summary>One request, as the Copilot runtime gets it.</summary>
/// <param name="Model">The Copilot model id, or <see langword="null"/> for the account's default.</param>
/// <param name="SystemPrompt">Replaces Copilot's own system prompt, when given.</param>
/// <param name="Prompt">The conversation so far, rendered as a transcript.</param>
/// <param name="Tools">The client's tools, declared to the model; Copilot never runs them.</param>
/// <param name="Reasoning">Whether the client wants the model's reasoning streamed.</param>
internal sealed record CopilotTurn(string? Model, string? SystemPrompt, string Prompt, IReadOnlyList<ToolDefinition> Tools, bool Reasoning = false);

/// <summary>Something that happened during a turn.</summary>
internal abstract record CopilotUpdate
{
    /// <summary>Answer text.</summary>
    public sealed record Text(string Delta) : CopilotUpdate;

    /// <summary>Reasoning text (only when the turn asked for it).</summary>
    public sealed record Thinking(string Delta) : CopilotUpdate;

    /// <summary>A call to one of the client's tools.</summary>
    public sealed record ToolCall(string Id, string Name, string ArgumentsJson) : CopilotUpdate;

    /// <summary>Tokens of one model call.</summary>
    public sealed record Usage(int? InputTokens, int? OutputTokens, int? CacheReadTokens) : CopilotUpdate;

    /// <summary>The runtime reported a failure (<c>session.error</c>).</summary>
    /// <param name="Type"><c>quota</c>, <c>rate_limit</c>, <c>authentication</c>, <c>authorization</c>, <c>context_limit</c>, <c>query</c>, …</param>
    /// <param name="Code">The specific code, such as <c>user_weekly_rate_limited</c> or <c>quota_exceeded</c>.</param>
    /// <param name="StatusCode">The upstream HTTP status, when there was one.</param>
    /// <param name="Message">The runtime's message.</param>
    /// <param name="RetryAfter">How long the runtime says to wait, when it says.</param>
    public sealed record Error(string Type, string? Code, int? StatusCode, string Message, TimeSpan? RetryAfter = null) : CopilotUpdate;
}
