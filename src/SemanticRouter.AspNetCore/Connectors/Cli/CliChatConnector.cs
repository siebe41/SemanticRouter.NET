using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Serves chat requests by running a vendor CLI (Claude Code, GitHub Copilot CLI, Codex CLI or a custom command) as a
/// subprocess, using whatever login that CLI already has. The connector never reads, stores or forwards credentials.
/// </summary>
/// <remarks>
/// Each run gets a fresh empty working directory (so no project instructions, hooks or MCP configuration are picked
/// up), the preset's tool-disabling flags, a concurrency limit and a time-to-first-output limit. Quota and rate-limit
/// messages are reported as <see cref="ModelFailureKind.QuotaExceeded"/>, so the router falls through to the next model
/// until the cooldown expires.
/// </remarks>
internal sealed partial class CliChatConnector : IChatConnector, IDisposable
{
    // A CLI is signed in to one account, so its quota errors are account-wide (every model on the connector cools
    // down) unless the message names a per-model limit: Claude's "You've hit your Opus limit" or Codex's
    // "usage limit for {model}. Switch to another model now".
    private static readonly string[] ModelSpecificLimitPatterns = ["Opus limit", "Sonnet limit", "usage limit for", "another model"];

    private readonly ConnectorOptions _options;
    private readonly CliAdapter _adapter;
    private readonly ICliProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly string _executable;
    private readonly string[] _quotaPatterns;
    private readonly string[] _removedEnvironment;

    public CliChatConnector(string name, ConnectorOptions options, ICliProcessRunner runner, ILogger logger)
    {
        Name = name;
        _options = options;
        _adapter = CliAdapter.For(options.Preset);
        _runner = runner;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        _executable = string.IsNullOrWhiteSpace(options.Executable) ? _adapter.DefaultExecutable : options.Executable;
        _quotaPatterns = _adapter.DefaultQuotaPatterns.Concat(options.QuotaErrorPatterns).ToArray();
        _removedEnvironment = ((IEnumerable<string>?)options.RemoveEnvironmentVariables ?? _adapter.DefaultRemovedEnvironment).ToArray();

        if (string.IsNullOrWhiteSpace(_executable))
        {
            throw new InvalidOperationException($"SemanticRouter:Connectors:{name}:Executable is required for a Custom CLI connector.");
        }
    }

    public string Name { get; }

    public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        if (!_options.AllowRemoteClients && !IsLocalRequest(context))
        {
            return ConnectorResult.Skipped("CLI connectors only serve requests from this machine (AllowRemoteClients is false)");
        }

        if (request.Chat.HasTools)
        {
            return ConnectorResult.Skipped("CLI connectors cannot execute client-defined tools");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string workingDirectory = Path.Combine(Path.GetTempPath(), "semantic-router", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingDirectory);
            return await RunAsync(request, context, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            TryDelete(workingDirectory);
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ConnectorResult> RunAsync(
        ConnectorRequest request, HttpContext context, string workingDirectory, CancellationToken cancellationToken)
    {
        var cliRequest = new CliRequest(request.Model.Model, request.Chat.Messages, workingDirectory, _options);
        (List<string> arguments, string? standardInput) = _adapter.Build(cliRequest);
        if (_options.Preset != CliPreset.Custom && _options.Arguments is { Count: > 0 } extra)
        {
            arguments.AddRange(extra);
        }

        var invocation = new CliInvocation
        {
            Executable = _executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = standardInput,
            SetEnvironment = new Dictionary<string, string>(_options.EnvironmentVariables),
            RemoveEnvironment = _removedEnvironment,
        };

        using var firstOutputTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstOutputTimeout.CancelAfter(_options.EffectiveTimeout);
        var writer = new ChatCompletionWriter(context, request.Model.Name, request.Chat.Stream);

        ICliProcess process;
        try
        {
            process = _runner.Start(invocation);
        }
        catch (FileNotFoundException ex)
        {
            LogStartFailed(_logger, Name, _executable, ex.InnerException?.Message ?? ex.Message);
            return ConnectorResult.Failed(ModelFailureKind.Unavailable, $"could not start '{_executable}'");
        }

        await using (process.ConfigureAwait(false))
        {
            try
            {
                return await ReadAsync(process, writer, firstOutputTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !writer.HasStarted)
            {
                LogTimedOut(_logger, Name, _options.EffectiveTimeout);
                return ConnectorResult.Failed(ModelFailureKind.Unavailable, "timed out");
            }
        }
    }

    private async Task<ConnectorResult> ReadAsync(
        ICliProcess process, ChatCompletionWriter writer, CancellationTokenSource firstOutputTimeout, CancellationToken cancellationToken)
    {
        ICliOutputParser? parser = _adapter.OutputMode == CliOutputMode.JsonLines ? _adapter.CreateParser() : null;
        var text = new StringBuilder();
        CliEvent? final = null;
        bool quotaSignal = false;

        await foreach (string line in process.ReadOutputLinesAsync(firstOutputTimeout.Token).ConfigureAwait(false))
        {
            if (parser is null)
            {
                text.AppendLine(line);
                continue;
            }

            CliEvent evt = parser.Parse(line);
            quotaSignal |= evt.QuotaSignal;
            if (evt.Kind == CliEventKind.Delta && !string.IsNullOrEmpty(evt.Text))
            {
                if (!writer.HasStarted)
                {
                    // Committed: from here on the client owns the stream and only the request's own token applies.
                    firstOutputTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                await writer.WriteDeltaAsync(evt.Text, cancellationToken).ConfigureAwait(false);
            }
            else if (evt.Kind == CliEventKind.Final)
            {
                final = evt;
            }
        }

        int exitCode = await process.WaitForExitAsync(firstOutputTimeout.Token).ConfigureAwait(false);
        string reply = parser is null ? text.ToString().Trim() : final?.Text ?? string.Empty;
        // An empty "successful" reply is treated as a failure so the router can try another model.
        bool succeeded = parser is null
            ? exitCode == 0 && reply.Length > 0
            : (final is { IsError: false } && (writer.HasStarted || reply.Length > 0))
              || (final is null && exitCode == 0 && writer.HasStarted);

        if (succeeded)
        {
            if (!writer.HasStarted && reply.Length > 0)
            {
                await writer.WriteDeltaAsync(reply, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(final?.InputTokens, final?.OutputTokens, cancellationToken).ConfigureAwait(false);
            return ConnectorResult.Completed;
        }

        string diagnostics = string.Join(" | ", new[] { reply, process.StandardError.Trim() }.Where(s => s.Length > 0));
        if (writer.HasStarted)
        {
            // Too late to fail over: end the stream cleanly and record what happened.
            LogMidStreamFailure(_logger, Name, exitCode, Truncate(diagnostics));
            await writer.CompleteAsync(null, null, cancellationToken).ConfigureAwait(false);
            return ConnectorResult.Completed;
        }

        bool quota = quotaSignal || _quotaPatterns.Any(p => diagnostics.Contains(p, StringComparison.OrdinalIgnoreCase));
        LogRunFailed(_logger, Name, exitCode, quota, Truncate(diagnostics));
        return ConnectorResult.Failed(
            quota ? ModelFailureKind.QuotaExceeded : ModelFailureKind.Unavailable,
            $"exit code {exitCode}: {Truncate(diagnostics, 200)}",
            affectsAllConnectorModels: quota && !ModelSpecificLimitPatterns.Any(p => diagnostics.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        // In-process callers (e.g. TestServer) have no remote address.
        IPAddress? remote = context.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote);
    }

    private static string Truncate(string value, int max = 1000) => value.Length <= max ? value : value[..max] + "…";

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: could not start '{Executable}' ({Error}). Is it installed and on PATH?")]
    private static partial void LogStartFailed(ILogger logger, string connector, string executable, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: no output within {Timeout}; trying the next model.")]
    private static partial void LogTimedOut(ILogger logger, string connector, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: exited with {ExitCode} (quota: {Quota}): {Output}")]
    private static partial void LogRunFailed(ILogger logger, string connector, int exitCode, bool quota, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {Connector}: failed mid-stream with exit code {ExitCode}: {Output}")]
    private static partial void LogMidStreamFailure(ILogger logger, string connector, int exitCode, string output);
}
