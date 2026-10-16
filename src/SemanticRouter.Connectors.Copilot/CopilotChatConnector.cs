using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>
/// Serves chat requests through GitHub's official Copilot SDK, on the Copilot sign-in of the person running the router.
/// Text and reasoning stream as the model writes them; the client's tools are declared natively and every call the model
/// makes goes back to the client, which runs it. Each request runs in a fresh session that is deleted afterwards.
/// </summary>
/// <remarks>
/// Like the subscription CLI connectors it only answers requests from this machine unless
/// <see cref="ConnectorOptions.AllowRemoteClients"/> is set (see docs/TERMS.md), limits concurrency, and reports quota and
/// rate limits as <see cref="ModelFailureKind.QuotaExceeded"/> so the router fails over until the cooldown ends.
/// </remarks>
internal sealed partial class CopilotChatConnector : IChatConnector, IModelDiscovery, IDisposable
{
    // Account-wide limits: every model on the connector cools down. Other quota and rate limits are per model
    // (Copilot's included models keep working when premium requests run out).
    private static readonly string[] AccountWideCodes = ["user_weekly_rate_limited", "user_global_rate_limited", "integration_rate_limited"];

    private readonly ConnectorOptions _options;
    private readonly ICopilotBackend _backend;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate;
    private int _waiting;

    public CopilotChatConnector(string name, ConnectorOptions options, ICopilotBackend backend, ILogger logger, TimeProvider? time = null)
    {
        Name = name;
        _options = options;
        _backend = backend;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    public string Name { get; }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) => _backend.ListModelsAsync(cancellationToken);

    public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.AllowRemoteClients && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
        {
            return ConnectorResult.Skipped("the Copilot connector only serves requests from this machine (AllowRemoteClients is false)");
        }

        if (request.Chat.HasImages)
        {
            return ConnectorResult.Skipped("the Copilot connector does not carry images");
        }

        int waiting = Interlocked.Increment(ref _waiting);
        if (_options.EffectiveMaxQueue is var maxQueue && _gate.CurrentCount == 0 && waiting > maxQueue)
        {
            Interlocked.Decrement(ref _waiting);
            return ConnectorResult.Skipped($"{waiting - 1} request(s) are already waiting for Copilot (MaxQueue {maxQueue})");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        try
        {
            return await RunAsync(request, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ConnectorResult> RunAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        ChatCompletionRequest chat = request.Chat;
        bool reasoning = chat.Reasoning is not null;
        DroppedFeatures.Report(context, DroppedFeatures.ForCli(chat).Where(f => !(reasoning && f == "reasoning")));
        (string? system, string prompt) = ChatTranscript.Render(chat, describeTools: false);
        var turn = new CopilotTurn(request.Model.Model, system, prompt, chat.Tools, reasoning);
        IChatResponseWriter writer = ChatResponseWriterFactory.Create(context, chat, request.Model, context.RequestServices?.GetService<IToolCallAffinity>());

        using var firstOutput = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstOutput.CancelAfter(_options.EffectiveTimeout);
        int calls = 0;
        int? input = null, output = null, cached = null, written = null;
        try
        {
            await foreach (CopilotUpdate update in _backend.RunAsync(turn, firstOutput.Token).ConfigureAwait(false))
            {
                switch (update)
                {
                    case CopilotUpdate.Text text when text.Delta.Length > 0:
                        firstOutput.CancelAfter(Timeout.InfiniteTimeSpan); // the client owns the stream now
                        await writer.WriteTextAsync(text.Delta, cancellationToken).ConfigureAwait(false);
                        break;
                    case CopilotUpdate.Thinking thinking when thinking.Delta.Length > 0:
                        firstOutput.CancelAfter(Timeout.InfiniteTimeSpan);
                        await writer.WriteThinkingAsync(thinking.Delta, cancellationToken).ConfigureAwait(false);
                        break;
                    case CopilotUpdate.ToolCall call:
                        firstOutput.CancelAfter(Timeout.InfiniteTimeSpan);
                        await writer.StartToolCallAsync(call.Id, call.Name, cancellationToken).ConfigureAwait(false);
                        await writer.WriteToolCallArgumentsAsync(call.ArgumentsJson, cancellationToken).ConfigureAwait(false);
                        await writer.EndToolCallAsync(cancellationToken).ConfigureAwait(false);
                        calls++;
                        break;
                    case CopilotUpdate.Usage usage:
                        input = Sum(input, usage.InputTokens);
                        output = Sum(output, usage.OutputTokens);
                        cached = Sum(cached, usage.CacheReadTokens);
                        written = Sum(written, usage.CacheWriteTokens);
                        break;
                    case CopilotUpdate.Error error when !writer.HasStarted:
                        return Failure(error);
                    case CopilotUpdate.Error error:
                        LogMidStreamFailure(_logger, Name, error.Type, error.Message);
                        await writer.CompleteAsync(ChatStopReason.EndTurn, default, cancellationToken).ConfigureAwait(false);
                        return ConnectorResult.Completed;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !writer.HasStarted)
        {
            LogTimedOut(_logger, Name, _options.EffectiveTimeout);
            return ConnectorResult.Failed(ModelFailureKind.Unavailable, "timed out");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or FileNotFoundException && !cancellationToken.IsCancellationRequested)
        {
            if (writer.HasStarted)
            {
                LogMidStreamFailure(_logger, Name, ex.GetType().Name, ex.Message);
                await writer.CompleteAsync(ChatStopReason.EndTurn, default, cancellationToken).ConfigureAwait(false);
                return ConnectorResult.Completed;
            }

            LogRuntimeFailed(_logger, Name, ex.Message);
            return ConnectorResult.Failed(ModelFailureKind.Unavailable, "Copilot runtime: " + ex.Message);
        }

        if (!writer.HasStarted && calls == 0)
        {
            // An empty "successful" reply is treated as a failure so the router can try another model.
            return ConnectorResult.Failed(ModelFailureKind.Unavailable, "empty reply");
        }

        await writer.CompleteAsync(calls > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn, new ChatUsage(input, output, cached, written), cancellationToken)
            .ConfigureAwait(false);
        return ConnectorResult.Completed;
    }

    private ConnectorResult Failure(CopilotUpdate.Error error)
    {
        bool quota = error.Type is "quota" or "rate_limit" || error.StatusCode is 402 or 429;
        string detail = $"{error.Type}{(error.Code is null ? string.Empty : " (" + error.Code + ")")}: {error.Message}";
        LogFailed(_logger, Name, detail);
        if (quota)
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset? retryAt = error.RetryAfter is { } wait ? now + wait
                : QuotaResetParser.TryParse(error.Message, now, null, out DateTimeOffset reset) ? reset : null;
            return ConnectorResult.Failed(
                ModelFailureKind.QuotaExceeded, detail, affectsAllConnectorModels: AccountWideCodes.Contains(error.Code), retryAt: retryAt);
        }

        // Without any sign-in the runtime reports a "query" error that names the missing token.
        bool signedOut = error.Message.Contains("Not authenticated", StringComparison.OrdinalIgnoreCase)
                         || error.Message.Contains("OAuth token", StringComparison.OrdinalIgnoreCase);
        if (error.Type is "authentication" or "authorization" || error.StatusCode is 401 or 403 || signedOut)
        {
            return ConnectorResult.Failed(
                ModelFailureKind.Unavailable,
                detail + " — sign in to Copilot (run `copilot` and use /login, or `gh auth login`)",
                affectsAllConnectorModels: error.Type == "authentication" || error.StatusCode == 401 || signedOut);
        }

        return ConnectorResult.Failed(ModelFailureKind.Unavailable, detail);
    }

    private static int? Sum(int? total, int? add) => add is null ? total : (total ?? 0) + add;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: Copilot reported {Detail}")]
    private static partial void LogFailed(ILogger logger, string connector, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: the Copilot runtime could not be used: {Error}")]
    private static partial void LogRuntimeFailed(ILogger logger, string connector, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: no output within {Timeout}; trying the next model.")]
    private static partial void LogTimedOut(ILogger logger, string connector, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: failed mid-stream ({Kind}): {Error}")]
    private static partial void LogMidStreamFailure(ILogger logger, string connector, string kind, string error);
}
