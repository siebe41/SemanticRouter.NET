using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>What a connector is asked to do: serve <see cref="Chat"/> with <see cref="Model"/>.</summary>
public sealed record ConnectorRequest
{
    /// <summary>The parsed request.</summary>
    public required ChatCompletionRequest Chat { get; init; }

    /// <summary>The catalog model chosen for this attempt.</summary>
    public required ModelDefinition Model { get; init; }

    /// <summary>The original request body, for pass-through connectors.</summary>
    public required ReadOnlyMemory<byte> RawBody { get; init; }
}

/// <summary>Outcome of a connector attempt.</summary>
public enum ConnectorOutcome
{
    /// <summary>The connector wrote a response; the request is finished.</summary>
    Completed = 0,

    /// <summary>The model failed before anything was written; the router may try the next model.</summary>
    Failed = 1,

    /// <summary>The connector declined this request (e.g. remote client, unsupported feature); no health penalty.</summary>
    Skipped = 2,
}

/// <summary>The result of <see cref="IChatConnector.SendAsync"/>.</summary>
public readonly record struct ConnectorResult
{
    /// <summary>What happened.</summary>
    public required ConnectorOutcome Outcome { get; init; }

    /// <summary>For <see cref="ConnectorOutcome.Failed"/>: how long the model should cool down.</summary>
    public ModelFailureKind FailureKind { get; init; }

    /// <summary>Human-readable explanation for logs and the 503 response.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The failure applies to the whole account behind the connector (e.g. a subscription's session limit), so every
    /// catalog model served by this connector should cool down, not just the one that was tried.
    /// </summary>
    public bool AffectsAllConnectorModels { get; init; }

    /// <summary>
    /// When the limit ends, if the provider said so (<c>Retry-After</c>, "resets 3:45pm"). The cooldown then lasts until
    /// this time instead of the configured <see cref="ModelSelectionOptions.QuotaCooldown"/>.
    /// </summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>The response was written.</summary>
    public static ConnectorResult Completed { get; } = new ConnectorResult { Outcome = ConnectorOutcome.Completed };

    /// <summary>The attempt failed before writing anything.</summary>
    public static ConnectorResult Failed(ModelFailureKind kind, string detail, bool affectsAllConnectorModels = false, DateTimeOffset? retryAt = null) =>
        new ConnectorResult
        {
            Outcome = ConnectorOutcome.Failed,
            FailureKind = kind,
            Detail = detail,
            AffectsAllConnectorModels = affectsAllConnectorModels,
            RetryAt = retryAt,
        };

    /// <summary>The connector declined the request.</summary>
    public static ConnectorResult Skipped(string detail) => new ConnectorResult { Outcome = ConnectorOutcome.Skipped, Detail = detail };
}

/// <summary>
/// Reaches one provider. Register custom implementations as singletons; <see cref="Name"/> is what
/// <see cref="ModelDefinition.Connector"/> refers to.
/// </summary>
/// <remarks>
/// Contract: either write a complete response and return <see cref="ConnectorResult.Completed"/>, or return
/// <see cref="ConnectorResult.Failed"/>/<see cref="ConnectorResult.Skipped"/> <em>without</em> having started the
/// response, so the router can fail over to the next model. Once output has started, the connector owns the
/// response and must finish it (even on a mid-stream error).
/// </remarks>
public interface IChatConnector
{
    /// <summary>Connector name (case-insensitive).</summary>
    string Name { get; }

    /// <summary>Serves the request.</summary>
    Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken);
}
