namespace SemanticRouter.AspNetCore;

/// <summary>
/// Context cost analysis: what each request's input is made of (system prompt, tool definitions, injected blocks,
/// history, tool results, images and the new ask) and the signs that it carries more context than it needs. Every
/// threshold is in estimated tokens; a request at or above one is flagged. Bound from <c>SemanticRouter:Usage:Context</c>.
/// </summary>
public sealed class ContextCostOptions
{
    /// <summary>Analyze every routed request and keep the breakdown with its activity entry. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A system prompt (system and developer messages) at least this large is flagged. Default 8,000.</summary>
    public int LargeSystemPromptTokens { get; set; } = 8_000;

    /// <summary>Tool definitions at least this large, together, are flagged. Default 10,000.</summary>
    public int LargeToolDefinitionTokens { get; set; } = 10_000;

    /// <summary>A single tool result at least this large is flagged. Default 10,000.</summary>
    public int LargeToolResultTokens { get; set; } = 10_000;

    /// <summary>
    /// Injected blocks (<c>&lt;system-reminder&gt;</c> and the other tags ignored when classifying) at least this large,
    /// together, are flagged. Default 4,000.
    /// </summary>
    public int InjectedContextTokens { get; set; } = 4_000;

    /// <summary>Content sent more than once in the same request, at least this much of it, is flagged. Default 2,000.</summary>
    public int RepeatedContentTokens { get; set; } = 2_000;

    /// <summary>Conversation history (earlier turns and every tool result) at least this large is flagged. Default 60,000.</summary>
    public int LongHistoryTokens { get; set; } = 60_000;

    /// <summary>
    /// The part re-sent on every turn whatever was asked (system prompt, tool definitions and injected blocks), at least
    /// this large, is flagged. Default 20,000.
    /// </summary>
    public int FixedOverheadTokens { get; set; } = 20_000;

    /// <summary>
    /// A served request that the backend billed at least this many input tokens for at the full input price, with
    /// nothing read from or written to its prompt cache, is flagged when the model's cache reads are cheaper. Default 20,000.
    /// </summary>
    public int UncachedTokens { get; set; } = 20_000;

    /// <summary>A request that fills at least this share (0–1) of the answering model's context window is flagged. Default 0.8.</summary>
    public double ContextWindowShare { get; set; } = 0.8;
}
