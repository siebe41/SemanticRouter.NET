namespace SemanticRouter.AspNetCore;

/// <summary>
/// Turns the forced tool call that carries a structured-output reply on the Messages API back into the JSON text the
/// client asked for with <c>response_format</c> / <c>text.format</c>.
/// </summary>
internal sealed class StructuredOutputWriter(IChatResponseWriter inner, string toolName) : IChatResponseWriter
{
    private System.Text.StringBuilder? _arguments;

    public bool HasStarted => inner.HasStarted;

    public Task WriteTextAsync(string delta, CancellationToken cancellationToken) => inner.WriteTextAsync(delta, cancellationToken);

    public Task WriteThinkingAsync(string delta, CancellationToken cancellationToken) => inner.WriteThinkingAsync(delta, cancellationToken);

    public Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken)
    {
        if (name == toolName)
        {
            _arguments = new System.Text.StringBuilder();
            return Task.CompletedTask;
        }

        return inner.StartToolCallAsync(id, name, cancellationToken);
    }

    public Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken)
    {
        if (_arguments is not null)
        {
            _arguments.Append(delta);
            return Task.CompletedTask;
        }

        return inner.WriteToolCallArgumentsAsync(delta, cancellationToken);
    }

    public Task EndToolCallAsync(CancellationToken cancellationToken)
    {
        if (_arguments is { } arguments)
        {
            _arguments = null;
            return inner.WriteTextAsync(arguments.Length == 0 ? "{}" : arguments.ToString(), cancellationToken);
        }

        return inner.EndToolCallAsync(cancellationToken);
    }

    public Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken) =>
        inner.CompleteAsync(stopReason == ChatStopReason.ToolUse ? ChatStopReason.EndTurn : stopReason, usage, cancellationToken);
}
