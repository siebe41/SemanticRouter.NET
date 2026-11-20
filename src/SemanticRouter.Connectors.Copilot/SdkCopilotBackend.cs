using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>
/// <see cref="ICopilotBackend"/> on GitHub's Copilot SDK. One SDK client (one Copilot runtime process, started on first
/// use) serves every request; each request gets its own session, which is deleted when the turn ends.
/// </summary>
/// <remarks>
/// The client runs in the SDK's <c>Empty</c> mode: none of Copilot's built-in tools (shell, file edits, web) exist, only
/// the client's tools, declared without a handler so the runtime hands each call back instead of running it. The
/// runtime is the user's own <c>copilot</c> CLI, and sign-in is the runtime's own: the router never sees a token. It is
/// started with <c>environment</c> in place of the router's own (<see cref="CopilotConnectorFactory.RuntimeEnvironment"/>).
/// <para>
/// The SDK does not restart a runtime that exits: its client keeps the closed connection. So when a call fails, or a turn
/// cannot be cleaned up (the runtime died or hangs), the runtime is pinged, and if it does not answer the client is
/// stopped and dropped, and the next request starts a fresh runtime.
/// </para>
/// </remarks>
internal sealed partial class SdkCopilotBackend(string? executable, string? homeDirectory, IReadOnlyDictionary<string, string> environment, ILogger logger)
    : ICopilotBackend, IAsyncDisposable
{
    /// <summary>How long ending a turn (abort, dispose, delete the session) may take before the runtime is suspected.</summary>
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a suspected runtime has to answer a ping before it is replaced.</summary>
    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _starting = new(1, 1);
    private CopilotClient? _client;

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        CopilotClient client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        IList<ModelInfo> models;
        try
        {
            models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRuntimeFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            await DropIfDeadAsync(client).ConfigureAwait(false);
            throw;
        }

        return models
            .Where(m => !string.Equals(m.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToArray();
    }

    public async IAsyncEnumerable<CopilotUpdate> RunAsync(CopilotTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CopilotClient client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        var config = new SessionConfig
        {
            Model = turn.Model,
            Streaming = true,
            SystemMessage = turn.SystemPrompt is null ? null : new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = turn.SystemPrompt },
            Tools = turn.Tools.Select(Declare).ToList(),
            AvailableTools = turn.Tools.Select(t => t.Name).ToList(),
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = (request, _) => Task.FromResult(request is PermissionRequestCustomTool
                ? PermissionDecision.ApproveOnce() // approving hands the call to us; nothing runs inside Copilot
                : PermissionDecision.Reject("Only the caller's tools are available.")),
            OnAutoModeSwitchRequest = (_, _) => Task.FromResult(AutoModeSwitchResponse.No), // the router picks the next model itself
            OnEvent = e => events.Writer.TryWrite(e),
        };

        CopilotSession session;
        try
        {
            session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRuntimeFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            await DropIfDeadAsync(client).ConfigureAwait(false);
            throw;
        }

        bool idle = false;
        try
        {
            // The runtime sends no event when it dies or hangs: the caller's stall guard cancels this read, and the
            // cleanup below then finds out whether the runtime has to go.
            await session.SendAsync(new MessageOptions { Prompt = turn.Prompt }, cancellationToken).ConfigureAwait(false);
            bool toolsRequested = false;
            TimeSpan? retryAfter = null;
            await foreach (SessionEvent evt in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt.AgentId is { Length: > 0 })
                {
                    continue; // a sub-agent's events, not the answer
                }

                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        yield return new CopilotUpdate.Text(delta.Data.DeltaContent);
                        break;
                    case AssistantReasoningDeltaEvent reasoning when turn.Reasoning:
                        yield return new CopilotUpdate.Thinking(reasoning.Data.DeltaContent);
                        break;
                    case AssistantMessageEvent message when message.Data.ToolRequests is { Length: > 0 } requests:
                        foreach (AssistantMessageToolRequest call in requests)
                        {
                            yield return new CopilotUpdate.ToolCall(call.ToolCallId, call.Name, call.Arguments?.GetRawText() ?? "{}");
                        }

                        toolsRequested = true;
                        break;
                    case AssistantUsageEvent usage when usage.Data.Initiator is null:
                        yield return new CopilotUpdate.Usage(ToInt(usage.Data.InputTokens), ToInt(usage.Data.OutputTokens), ToInt(usage.Data.CacheReadTokens), ToInt(usage.Data.CacheWriteTokens));
                        break;
                    case AutoModeSwitchRequestedEvent autoSwitch when autoSwitch.Data.RetryAfterSeconds is { } seconds:
                        retryAfter = TimeSpan.FromSeconds(seconds);
                        break;
                    case SessionErrorEvent error:
                        yield return new CopilotUpdate.Error(error.Data.ErrorType, error.Data.ErrorCode, error.Data.StatusCode, error.Data.Message, retryAfter);
                        yield break;
                    case ExternalToolRequestedEvent or PermissionRequestedEvent when toolsRequested:
                        yield break; // the turn now waits for tool results, which the client sends with its next request
                    case SessionIdleEvent:
                        idle = true;
                        yield break;
                }
            }
        }
        finally
        {
            await EndAsync(client, session, idle).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _client, null) is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _starting.Dispose();
    }

    /// <summary>
    /// What the SDK throws when the runtime is gone: <c>ConnectionLostException</c> (an <see cref="IOException"/>), or
    /// <see cref="ObjectDisposedException"/> (an <see cref="InvalidOperationException"/>) once the connection was closed.
    /// </summary>
    private static bool IsRuntimeFailure(Exception ex) => ex is IOException or InvalidOperationException;

    private async Task<CopilotClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _client) is { } started)
        {
            return started;
        }

        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _client) is { } current)
            {
                return current;
            }

            CliExecutableLookup runtime = CliExecutableLocator.Find(string.IsNullOrWhiteSpace(executable) ? "copilot" : executable);
            if (runtime.Path is not { } path)
            {
                throw new FileNotFoundException(runtime.Problem("copilot") ?? "the copilot CLI was not found", runtime.Requested);
            }

            string sessions = CliScratch.Create("copilot");
            StdioRuntimeConnection connection = RuntimeConnection.ForStdio(path);
            connection.Environment = environment; // replaces the router's environment; the SDK adds COPILOT_HOME to it
            var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                Connection = connection,
                BaseDirectory = string.IsNullOrWhiteSpace(homeDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot")
                    : homeDirectory,
                WorkingDirectory = sessions, // an empty directory: no project files, instructions or MCP configuration
                Logger = logger,
                LogLevel = CopilotLogLevel.Warning,
                ClientInfo = new CopilotClientInfo { ApplicationName = "SemanticRouter" },
            });
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopQuietlyAsync(client).ConfigureAwait(false);
                throw;
            }

            LogStarted(logger, path);
            Volatile.Write(ref _client, client);
            return client;
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    /// Stops an unfinished turn and deletes the session, so nothing of the conversation stays with the runtime. Bounded by
    /// <see cref="CleanupTimeout"/>, so a runtime that hangs cannot hold the connector's slot: one that does not answer is
    /// checked and, if dead, replaced.
    /// </summary>
    private async Task EndAsync(CopilotClient client, CopilotSession session, bool idle)
    {
        string id = session.SessionId;
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            if (!idle)
            {
                await session.AbortAsync(timeout.Token).ConfigureAwait(false);
            }

            await session.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
            await client.DeleteSessionAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            LogCleanupFailed(logger, id, ex is OperationCanceledException ? "no answer in time" : ex.Message);
            await DropIfDeadAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pings <paramref name="client"/>'s runtime; if it does not answer within <see cref="PingTimeout"/>, stops it and
    /// forgets it, so the next request starts a fresh one. Turns still on it fail, but they could not finish anyway.
    /// </summary>
    private async Task DropIfDeadAsync(CopilotClient client)
    {
        using var timeout = new CancellationTokenSource(PingTimeout);
        try
        {
            await client.PingAsync(null, timeout.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref _client, null, client) != client)
            {
                return; // already dropped by another request
            }

            LogRuntimeLost(logger, ex is OperationCanceledException ? "no answer to a ping" : ex.Message);
        }

        await StopQuietlyAsync(client).ConfigureAwait(false);
    }

    /// <summary>Kills the runtime process and releases the client. It is being given up on, so failures are only logged.</summary>
    private async Task StopQuietlyAsync(CopilotClient client)
    {
        try
        {
            await client.ForceStopAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or AggregateException or OperationCanceledException)
        {
            LogStopFailed(logger, ex.Message);
        }
    }

    private static AIFunctionDeclaration Declare(ToolDefinition tool)
    {
        using JsonDocument schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(tool.InputSchemaJson) ? "{\"type\":\"object\"}" : tool.InputSchemaJson);
        return AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, schema.RootElement.Clone());
    }

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Min(value.Value, int.MaxValue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot runtime started ({Path}).")]
    private static partial void LogStarted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Copilot session {Session} could not be cleaned up: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string session, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Copilot runtime stopped answering ({Error}); the next request starts a new one.")]
    private static partial void LogRuntimeLost(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The Copilot runtime could not be stopped cleanly: {Error}")]
    private static partial void LogStopFailed(ILogger logger, string error);
}
