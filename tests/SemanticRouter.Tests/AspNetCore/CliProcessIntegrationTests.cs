using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Runs real child processes through <see cref="SystemCliProcessRunner"/>. The stand-in CLI is a small .NET program
/// (<c>tests/SemanticRouter.FakeCli</c>, copied next to the tests), so the same tests run on Windows, macOS and Linux:
/// argument quoting, stdin encoding, environment scrubbing, working directories and process-tree termination.
/// </summary>
public sealed class CliProcessIntegrationTests : IDisposable
{
    private static readonly string FakeCli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-cli.exe" : "fake-cli");

    private readonly string _scratch = Directory.CreateTempSubdirectory("fake-cli").FullName;

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private static ConnectorOptions Options(string mode, CliPreset preset = CliPreset.Custom, string? executable = null)
    {
        var options = new ConnectorOptions { Type = ConnectorType.Cli, Preset = preset, Executable = executable ?? FakeCli };
        options.EnvironmentVariables["FAKE_CLI_MODE"] = mode;
        return options;
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
    public void The_stand_in_cli_was_built_next_to_the_tests()
    {
        File.Exists(FakeCli).Should().BeTrue($"the FakeCli project reference copies its executable to {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task Claude_preset_runs_with_scrubbed_environment_in_an_empty_directory()
    {
        Environment.SetEnvironmentVariable("SR_TEST_SECRET", "leaked");
        try
        {
            ConnectorOptions options = Options("claude-echo", CliPreset.ClaudeCode);
            options.RemoveEnvironmentVariables = ["SR_TEST_SECRET"];
            options.EnvironmentVariables["SR_TEST_SET"] = "yes";
            using var connector = new CliChatConnector("claude", options, new SystemCliProcessRunner(), NullLogger.Instance);

            (ConnectorResult result, string body) = await SendAsync(connector, "hello there");

            result.Outcome.Should().Be(ConnectorOutcome.Completed);
            using JsonDocument report = JsonDocument.Parse(Content(body));
            report.RootElement.GetProperty("prompt").GetString().Should().BeNull("the prompt is not on the command line");
            report.RootElement.GetProperty("stdin").GetString().Should().Be("hello there");
            report.RootElement.GetProperty("secret").GetString().Should().Be("unset", "RemoveEnvironmentVariables is honoured");
            report.RootElement.GetProperty("set").GetString().Should().Be("yes");
            report.RootElement.GetProperty("files").GetInt32().Should().Be(0, "each run gets a fresh empty directory");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SR_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Argument_prompts_arrive_after_print()
    {
        ConnectorOptions options = Options("claude-echo", CliPreset.ClaudeCode);
        options.PromptInput = CliPromptInput.Argument;
        using var connector = new CliChatConnector("claude", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (_, string body) = await SendAsync(connector, "hello there");

        using JsonDocument report = JsonDocument.Parse(Content(body));
        report.RootElement.GetProperty("prompt").GetString().Should().Be("hello there");
        report.RootElement.GetProperty("stdinLength").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Long_non_ascii_prompts_go_through_stdin_and_system_prompts_as_arguments()
    {
        ConnectorOptions options = Options("claude-echo", CliPreset.ClaudeCode);
        options.PromptInput = CliPromptInput.Argument;
        options.MaxArgumentLength = 50;
        using var connector = new CliChatConnector("claude", options, new SystemCliProcessRunner(), NullLogger.Instance);
        string prompt = string.Concat(Enumerable.Repeat("Grüße, 世界 — ", 400));

        (ConnectorResult result, string body) = await SendAsync(connector, prompt, system: "Be terse: \"quoted\" & spaced.");

        result.Outcome.Should().Be(ConnectorOutcome.Completed);
        using JsonDocument report = JsonDocument.Parse(Content(body));
        report.RootElement.GetProperty("system").GetString().Should().Be("Be terse: \"quoted\" & spaced.");
        report.RootElement.GetProperty("stdinLength").GetInt32().Should().Be(prompt.Length, "UTF-8 on stdin, no BOM, no lost characters");
    }

    [Fact]
    public async Task Arguments_with_quotes_spaces_dashes_and_unicode_arrive_intact()
    {
        // The hard cases for Windows command-line quoting, plus an empty argument (Claude Code's "--tools ''").
        string prompt = "-rf \"quoted words\" back\\slash\\\" trailing\\ ünïcödé 日本 $HOME %PATH% `cmd` & | ; > <";
        ConnectorOptions options = Options("args-json");
        options.PromptInput = CliPromptInput.Argument;
        options.Arguments = ["--flag", string.Empty, "{prompt}"];
        using var connector = new CliChatConnector("custom", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, string body) = await SendAsync(connector, prompt);

        result.Outcome.Should().Be(ConnectorOutcome.Completed);
        string firstLine = Content(body).Split('\n')[0];
        JsonSerializer.Deserialize<string[]>(firstLine).Should().Equal(["--flag", string.Empty, " " + prompt], "a leading space keeps the dash from reading as an option");
    }

    [Fact]
    public async Task Custom_cli_quota_message_and_exit_code_are_classified()
    {
        using var connector = new CliChatConnector("custom", Options("quota"), new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, string body) = await SendAsync(connector, "hi");

        result.Should().Match<ConnectorResult>(r =>
            r.Outcome == ConnectorOutcome.Failed && r.FailureKind == ModelFailureKind.QuotaExceeded && r.Detail!.StartsWith("exit code 3"));
        body.Should().BeEmpty("nothing is written when the router may still fail over");
    }

    [Fact]
    public async Task Custom_cli_text_output_is_the_reply()
    {
        using var connector = new CliChatConnector("custom", Options("cat"), new SystemCliProcessRunner(), NullLogger.Instance);

        (_, string body) = await SendAsync(connector, "echo me, ça va?");

        Content(body).Should().Be("echo me, ça va?\n(done)");
    }

    [Fact]
    public async Task Silent_processes_time_out_and_are_killed()
    {
        string marker = Path.Combine(_scratch, "still-running");
        ConnectorOptions options = Options("slow");
        options.EnvironmentVariables["FAKE_CLI_MARKER"] = marker;
        options.Timeout = TimeSpan.FromMilliseconds(300);
        using var connector = new CliChatConnector("slow", options, new SystemCliProcessRunner(), NullLogger.Instance);

        var stopwatch = Stopwatch.StartNew();
        (ConnectorResult result, _) = await SendAsync(connector, "hi");

        result.Should().Be(ConnectorResult.Failed(ModelFailureKind.Unavailable, "timed out"));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        File.Exists(marker).Should().BeFalse("the process tree is killed on timeout");
    }

    [Fact]
    public async Task Missing_executable_is_a_failure_that_names_the_setting()
    {
        ConnectorOptions options = Options("cat", CliPreset.Codex, Path.Combine(_scratch, "does-not-exist"));
        using var connector = new CliChatConnector("codex", options, new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, _) = await SendAsync(connector, "hi");

        result.Outcome.Should().Be(ConnectorOutcome.Failed);
        result.Detail.Should().Contain("could not start").And.Contain("SemanticRouter:Connectors:codex:Executable");
    }

    [Fact]
    public async Task Batch_file_shims_are_refused_with_the_fix()
    {
        string shim = Path.Combine(_scratch, "claude.cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\nnode claude.js %*\r\n");
        using var connector = new CliChatConnector("claude-cli", Options("cat", CliPreset.ClaudeCode, shim), new SystemCliProcessRunner(), NullLogger.Instance);

        (ConnectorResult result, string body) = await SendAsync(connector, "hi & del *");

        result.Outcome.Should().Be(ConnectorOutcome.Failed);
        result.Detail.Should().Contain("script").And.Contain("claude.exe").And.Contain("SemanticRouter:Connectors:claude-cli:Executable");
        body.Should().BeEmpty();
    }

    [Fact]
    public void Locator_searches_path_adds_exe_on_windows_and_explains_npm_shims()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_scratch, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "codex.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "claude.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "tool"), string.Empty);
        string path = "/nowhere" + Path.PathSeparator + bin;

        CliExecutableLocator.Find("codex", path, windows: true).Path.Should().Be(Path.Combine(bin, "codex.exe"));
        CliExecutableLocator.Find("tool", path, windows: false).Path.Should().Be(Path.Combine(bin, "tool"));
        CliExecutableLocator.Find("codex", path, windows: false).Found.Should().BeFalse("POSIX adds no extension");

        CliExecutableLookup claude = CliExecutableLocator.Find("claude", path, windows: true);
        claude.Found.Should().BeFalse();
        claude.ScriptPath.Should().Be(Path.Combine(bin, "claude.cmd"));
        claude.Problem("claude-cli").Should().Contain("never through cmd.exe").And.Contain("claude.exe");

        CliExecutableLocator.Find("gemini", path, windows: true).Problem("g").Should().Contain("not found on PATH");
        CliExecutableLocator.Find(FakeCli).Found.Should().BeTrue();
    }

    [Fact]
    public void Locator_skips_relative_and_empty_path_entries()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_scratch, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "tool"), string.Empty);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, bin);
        if (Path.IsPathRooted(relative))
        {
            return; // on another drive than the current directory (Windows): there is no relative form
        }

        CliExecutableLocator.Find("tool", relative, windows: false).Found.Should().BeFalse("a relative entry depends on the current directory");
        CliExecutableLocator.Find("tool", string.Join(Path.PathSeparator, ".", string.Empty, relative, bin), windows: false)
            .Path.Should().Be(Path.Combine(bin, "tool"));
    }

    [Fact]
    public void The_runner_starts_the_resolved_full_path_never_a_file_in_the_current_directory()
    {
        // .NET (and Windows) would start a bare name found in the current directory before searching PATH.
        string name = "sr-planted-" + Guid.NewGuid().ToString("N");
        string planted = Path.Combine(Environment.CurrentDirectory, name);
        File.WriteAllText(planted, "#!/bin/sh\necho planted\n");
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Action start = () => SystemCliProcessRunner.CreateStartInfo(Invocation(name));
            start.Should().Throw<FileNotFoundException>().WithMessage($"*'{name}'*not found*");
            CliExecutableLocator.Find(name, ".", OperatingSystem.IsWindows()).Found.Should().BeFalse();
        }
        finally
        {
            File.Delete(planted);
        }

        SystemCliProcessRunner.CreateStartInfo(Invocation(FakeCli)).FileName.Should().Be(FakeCli);
        string shell = SystemCliProcessRunner.CreateStartInfo(Invocation(OperatingSystem.IsWindows() ? "cmd" : "sh")).FileName;
        Path.IsPathFullyQualified(shell).Should().BeTrue(shell);

        CliInvocation Invocation(string executable) => new() { Executable = executable, Arguments = [], WorkingDirectory = _scratch };
    }
}
