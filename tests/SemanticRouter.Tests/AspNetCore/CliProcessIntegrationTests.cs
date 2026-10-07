using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Runs real child processes (POSIX shell scripts standing in for the vendor CLIs) through
/// <see cref="SystemCliProcessRunner"/>. Skipped on Windows.
/// </summary>
public sealed class CliProcessIntegrationTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("fake-cli").FullName;

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    private string Script(string name, string body)
    {
        string path = Path.Combine(_bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static async Task<(ConnectorResult Result, string Body)> SendAsync(
        CliChatConnector connector, string prompt, string? system = null, bool stream = false)
    {
        var messages = new List<ChatMessage>();
        if (system is not null)
        {
            messages.Add(new ChatMessage("system", system));
        }

        messages.Add(new ChatMessage("user", prompt));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        ConnectorResult result = await connector.SendAsync(
            new ConnectorRequest(new ChatCompletionRequest { Messages = messages, Stream = stream }, Model("m", 1), default),
            context,
            CancellationToken.None);

        return (result, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private static string Content(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;

    [Fact]
    public async Task Claude_preset_runs_with_scrubbed_environment_in_an_empty_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Echoes what it received back as a Claude Code stream-json reply.
        string claude = Script("claude", """
            input=$(cat)
            files=$(ls -A | wc -l | tr -d ' ')
            reply="prompt=$2 secret=${SR_TEST_SECRET:-unset} set=${SR_TEST_SET:-unset} files=$files stdin=$input"
            echo '{"type":"system","subtype":"init","apiKeySource":"none"}'
            printf '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"%s"}}}\n' "$reply"
            printf '{"type":"result","subtype":"success","is_error":false,"result":"%s"}\n' "$reply"
            """);

        Environment.SetEnvironmentVariable("SR_TEST_SECRET", "leaked");
        try
        {
            var options = new ConnectorOptions
            {
                Type = ConnectorType.Cli,
                Preset = CliPreset.ClaudeCode,
                Executable = claude,
                RemoveEnvironmentVariables = ["SR_TEST_SECRET"],
            };
            options.EnvironmentVariables["SR_TEST_SET"] = "yes";
            using var connector = new CliChatConnector("claude", options, new SystemCliProcessRunner(), NullLogger.Instance);

            (ConnectorResult result, string body) = await SendAsync(connector, "hello there");

            result.Outcome.Should().Be(ConnectorOutcome.Completed);
            Content(body).Should().Be("prompt=hello there secret=unset set=yes files=0 stdin=");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SR_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Long_prompts_and_system_prompts_reach_the_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string claude = Script("claude", """
            input=$(cat)
            while [ $# -gt 0 ]; do
              if [ "$1" = "--system-prompt-file" ]; then system=$(cat "$2"); fi
              shift
            done
            printf '{"type":"result","subtype":"success","is_error":false,"result":"system=%s stdin=%s"}\n' "$system" "${#input}"
            """);
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode, Executable = claude, MaxArgumentLength = 50 };
        using var connector = new CliChatConnector("claude", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, string body) = await SendAsync(connector, new string('x', 5000), system: "Be terse.");

        result.Outcome.Should().Be(ConnectorOutcome.Completed);
        Content(body).Should().Be("system=Be terse. stdin=5000");
    }

    [Fact]
    public async Task Custom_cli_quota_message_and_exit_code_are_classified()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string tool = Script("llm", """
            echo "Error: You have exceeded your daily quota" >&2
            exit 3
            """);
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Executable = tool, Arguments = ["{prompt}"] };
        using var connector = new CliChatConnector("custom", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, string body) = await SendAsync(connector, "hi");

        result.Should().Match<ConnectorResult>(r =>
            r.Outcome == ConnectorOutcome.Failed && r.FailureKind == ModelFailureKind.QuotaExceeded && r.Detail!.StartsWith("exit code 3"));
        body.Should().BeEmpty("nothing is written when the router may still fail over");
    }

    [Fact]
    public async Task Custom_cli_text_output_is_the_reply()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string tool = Script("llm", """
            cat
            echo
            echo "(done)"
            """);
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Executable = tool };
        using var connector = new CliChatConnector("custom", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (_, string body) = await SendAsync(connector, "echo me");

        Content(body).Should().Be("echo me\n(done)");
    }

    [Fact]
    public async Task Silent_processes_time_out_and_are_killed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string marker = Path.Combine(_bin, "still-running");
        string tool = Script("slow", $"""
            sleep 5
            touch "{marker}"
            """);
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Executable = tool, Timeout = TimeSpan.FromMilliseconds(300) };
        using var connector = new CliChatConnector("slow", options, new SystemCliProcessRunner(), NullLogger.Instance);

        var stopwatch = Stopwatch.StartNew();
        (ConnectorResult result, _) = await SendAsync(connector, "hi");

        result.Should().Be(ConnectorResult.Failed(ModelFailureKind.Unavailable, "timed out"));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        File.Exists(marker).Should().BeFalse("the process tree is killed on timeout");
    }

    [Fact]
    public async Task Missing_executable_is_a_failure_not_an_exception()
    {
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.Codex, Executable = Path.Combine(_bin, "does-not-exist") };
        using var connector = new CliChatConnector("codex", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, _) = await SendAsync(connector, "hi");

        result.Outcome.Should().Be(ConnectorOutcome.Failed);
        result.Detail.Should().Contain("could not start");
    }
}
