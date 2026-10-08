using System.Runtime.CompilerServices;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.Support;

/// <summary>Scripted stand-in for real CLI processes. Records every invocation.</summary>
internal sealed class FakeCliProcessRunner(Func<CliInvocation, ICliProcess> start) : ICliProcessRunner
{
    public List<CliInvocation> Invocations { get; } = [];

    public static FakeCliProcessRunner Replying(params string[] lines) => new(_ => new FakeCliProcess(lines));

    public static FakeCliProcessRunner Failing(int exitCode, string stdout = "", string stderr = "") =>
        new(_ => new FakeCliProcess(stdout.Length == 0 ? [] : [stdout], exitCode, stderr));

    public static FakeCliProcessRunner NotInstalled() =>
        new(i => throw new FileNotFoundException("not found", i.Executable));

    public ICliProcess Start(CliInvocation invocation)
    {
        Invocations.Add(invocation);
        return start(invocation);
    }
}

internal sealed class FakeCliProcess(IReadOnlyList<string> lines, int exitCode = 0, string stderr = "", TimeSpan? delay = null, Task? until = null) : ICliProcess
{
    public string StandardError => stderr;

    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        if (until is not null)
        {
            await until.WaitAsync(cancellationToken);
        }

        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(exitCode);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Claude Code stream-json lines, as documented at https://code.claude.com/docs/en/headless.</summary>
internal static class ClaudeStream
{
    public static string Init => """{"type":"system","subtype":"init","session_id":"s","model":"claude-sonnet-5-5","apiKeySource":"none"}""";

    public static string Delta(string text) =>
        $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{text}}}"}},"session_id":"s","parent_tool_use_id":null}""";

    public static string Success(string result) =>
        $$"""{"type":"result","subtype":"success","is_error":false,"result":"{{result}}","usage":{"input_tokens":12,"output_tokens":3},"total_cost_usd":0.001}""";

    public static string Error(string message) =>
        $$"""{"type":"result","subtype":"success","is_error":true,"result":"{{message}}"}""";
}
