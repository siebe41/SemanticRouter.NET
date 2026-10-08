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
/// runtime is the user's own <c>copilot</c> CLI, and sign-in is the runtime's own: the router never sees a token.
/// </remarks>
internal sealed partial class SdkCopilotBackend(string? executable, string? homeDirectory, ILogger logger) : ICopilotBackend, IAsyncDisposable
{
    private readonly SemaphoreSlim _starting = new(1, 1);
    private CopilotClient? _client;

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        CopilotClient client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        IList<ModelInfo> models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
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

        CopilotSession session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        bool idle = false;
        try
        {
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
                        yield return new CopilotUpdate.Usage(ToInt(usage.Data.InputTokens), ToInt(usage.Data.OutputTokens), ToInt(usage.Data.CacheReadTokens));
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
        if (_client is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _starting.Dispose();
    }

    private async Task<CopilotClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } started)
        {
            return started;
        }

        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                CliExecutableLookup runtime = CliExecutableLocator.Find(string.IsNullOrWhiteSpace(executable) ? "copilot" : executable);
                if (runtime.Path is not { } path)
                {
                    throw new FileNotFoundException(runtime.Problem("copilot") ?? "the copilot CLI was not found", runtime.Requested);
                }

                string sessions = Path.Combine(Path.GetTempPath(), "semantic-router", "copilot");
                Directory.CreateDirectory(sessions);
                var client = new CopilotClient(new CopilotClientOptions
                {
                    Mode = CopilotClientMode.Empty,
                    Connection = RuntimeConnection.ForStdio(path),
                    BaseDirectory = string.IsNullOrWhiteSpace(homeDirectory)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot")
                        : homeDirectory,
                    WorkingDirectory = sessions, // an empty directory: no project files, instructions or MCP configuration
                    Logger = logger,
                    LogLevel = CopilotLogLevel.Warning,
                    ClientInfo = new CopilotClientInfo { ApplicationName = "SemanticRouter" },
                });
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                LogStarted(logger, path);
                _client = client;
            }

            return _client;
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>Stops an unfinished turn and deletes the session, so nothing of the conversation stays with the runtime.</summary>
    private async Task EndAsync(CopilotClient client, CopilotSession session, bool idle)
    {
        string id = session.SessionId;
        try
        {
            if (!idle)
            {
                await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await session.DisposeAsync().ConfigureAwait(false);
            await client.DeleteSessionAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            LogCleanupFailed(logger, id, ex.Message);
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
}
