using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Writes a chat reply to the client in the client's protocol, as a single JSON document or as server-sent events.
/// Connectors whose backend speaks another protocol (or no protocol, like a CLI) drive one of these.
/// </summary>
internal interface IChatResponseWriter
{
    /// <summary>Whether anything has been sent to the client (the point of no return for failover).</summary>
    bool HasStarted { get; }

    /// <summary>Appends assistant text.</summary>
    Task WriteTextAsync(string delta, CancellationToken cancellationToken);

    /// <summary>Starts a tool call; arguments follow through <see cref="WriteToolCallArgumentsAsync"/>.</summary>
    Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken);

    /// <summary>Appends a fragment of the current tool call's JSON arguments.</summary>
    Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken);

    /// <summary>Ends the current tool call.</summary>
    Task EndToolCallAsync(CancellationToken cancellationToken);

    /// <summary>Finishes the reply. Safe to call when nothing was written (an empty reply).</summary>
    Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken);
}

/// <summary>Creates the right writer for a request.</summary>
internal static class ChatResponseWriterFactory
{
    public static IChatResponseWriter Create(HttpContext context, ChatCompletionRequest request, ModelDefinition model, IToolCallAffinity? affinity)
    {
        Action<string>? onToolCall = affinity is null ? null : id => affinity.Remember(id, model.Name);
        IChatResponseWriter writer = request.Protocol == ChatProtocol.Anthropic
            ? new AnthropicResponseWriter(context, model.Name, request.Stream, onToolCall)
            : new OpenAIResponseWriter(context, model.Name, request.Stream, onToolCall);
        return UsageCapture.Of(context) is { } capture ? new UsageRecordingWriter(writer, capture) : writer;
    }
}
