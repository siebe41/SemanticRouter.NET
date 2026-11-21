using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The backend failed inside a reply it had begun as a success: an error event in a <c>200</c> stream (Anthropic
/// <c>error</c>, an OpenAI <c>{"error": …}</c> chunk, Responses <c>error</c> or <c>response.failed</c>), a CLI that
/// exited with an error after it began to answer, or a Copilot session error. It is not a finished answer. Before
/// anything reached the client the connector returns <see cref="Failure"/>, so the next model answers; after, the
/// dispatcher ends the reply with an error in the client's protocol and cools the model down.
/// </summary>
/// <remarks>
/// Internal: a contract between the packages of one version (Connectors.Copilot throws it), not a public API.
/// </remarks>
internal sealed class UpstreamReplyException : Exception
{
    private const int MaxMessage = 300;

    public UpstreamReplyException(ConnectorResult failure, bool clientNotified = false, Exception? innerException = null)
        : base(failure.Detail ?? "the backend failed mid-reply", innerException)
    {
        Failure = failure;
        ClientNotified = clientNotified;
    }

    /// <summary>The failure to report: what failed over or cooled the model down, and for how long.</summary>
    public ConnectorResult Failure { get; }

    /// <summary>
    /// The client already has the backend's own error event (a reply passed through untouched), so the dispatcher records
    /// the failure without writing another.
    /// </summary>
    public bool ClientNotified { get; }

    /// <summary>
    /// Whether <paramref name="root"/>, an event of a stream or a whole reply in <paramref name="protocol"/>, reports an
    /// error instead of an answer.
    /// </summary>
    public static bool TryFind(JsonElement root, ChatProtocol protocol, [NotNullWhen(true)] out UpstreamReplyException? error)
    {
        error = root.ValueKind != JsonValueKind.Object ? null : protocol switch
        {
            // event: error / data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}
            ChatProtocol.Anthropic when root.GetString("type") == "error" =>
                root.TryGetObject("error", out JsonElement anthropic)
                    ? Of(anthropic.GetString("type"), null, anthropic.GetString("message"))
                    : Of(null, null, null),

            // {"type":"error","code":"…","message":"…"}, {"type":"response.failed","response":{"error":{…}}}, or a whole
            // response with "status":"failed".
            ChatProtocol.Responses when root.GetString("type") == "error" =>
                root.TryGetObject("error", out JsonElement nested)
                    ? Of(nested.GetString("type"), nested.GetString("code"), nested.GetString("message"))
                    : Of(null, root.GetString("code"), root.GetString("message")),
            ChatProtocol.Responses when root.GetString("type") == "response.failed" =>
                FailedResponse(root.TryGetObject("response", out JsonElement response) ? response : default),
            ChatProtocol.Responses when root.GetString("object") == "response" && root.GetString("status") == "failed" =>
                FailedResponse(root),

            // data: {"error":{"message":"…","type":"…","code":"…"}}, or {"error":"…"}: an error in place of the choices.
            ChatProtocol.OpenAI when !root.TryGetProperty("choices", out _) && root.TryGetProperty("error", out JsonElement openAI) =>
                openAI.ValueKind switch
                {
                    JsonValueKind.Object => Of(openAI.GetString("type"), Code(openAI), openAI.GetString("message")),
                    JsonValueKind.String => Of(null, null, openAI.GetString()),
                    _ => null,
                },
            _ => null,
        };
        return error is not null;
    }

    private static UpstreamReplyException FailedResponse(JsonElement response) =>
        response.TryGetObject("error", out JsonElement error)
            ? Of(error.GetString("type"), error.GetString("code"), error.GetString("message"))
            : Of(null, null, "response failed");

    /// <summary>OpenAI sends <c>code</c> as a string, some compatible servers as a number.</summary>
    private static string? Code(JsonElement error) =>
        error.TryGetProperty("code", out JsonElement code) && code.ValueKind is JsonValueKind.String or JsonValueKind.Number ? code.ToString() : null;

    /// <summary>
    /// A rate or quota limit (<c>rate_limit_error</c>, <c>rate_limit_exceeded</c>, <c>insufficient_quota</c>) cools the
    /// model down for longer, until the reset the message names when it names one; anything else (overloaded, a server
    /// error) is treated as the model being unavailable for a while.
    /// </summary>
    private static UpstreamReplyException Of(string? type, string? code, string? message)
    {
        bool quota = new[] { type, code }.Any(s => s is not null
            && (s.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) || s.Contains("quota", StringComparison.OrdinalIgnoreCase)));
        string kind = type ?? code ?? "error";
        string text = string.IsNullOrWhiteSpace(message) ? kind
            : $"{kind}: {(message.Length <= MaxMessage ? message : message[..MaxMessage] + "…")}";
        DateTimeOffset? retryAt = quota && QuotaResetParser.TryParse(message, DateTimeOffset.UtcNow, null, out DateTimeOffset reset) ? reset : null;
        return new UpstreamReplyException(ConnectorResult.Failed(
            quota ? ModelFailureKind.QuotaExceeded : ModelFailureKind.Unavailable, "error event (" + text + ")", retryAt: retryAt));
    }
}
