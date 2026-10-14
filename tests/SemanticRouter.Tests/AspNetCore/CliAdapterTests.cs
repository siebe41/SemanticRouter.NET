using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

public sealed class CliAdapterTests : IDisposable
{
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("cli-adapter-tests").FullName;

    public void Dispose() => Directory.Delete(_workingDirectory, recursive: true);

    private CliRequest Request(string? model, params ChatMessage[] messages) =>
        new(model, messages, _workingDirectory, new ConnectorOptions { Type = ConnectorType.Cli, MaxArgumentLength = 100 });

    [Fact]
    public void Claude_puts_the_prompt_straight_after_print_and_disables_every_tool()
    {
        (List<string> args, string? stdin) = new ClaudeCodeCliAdapter().Build(Request("sonnet", new ChatMessage("user", "hello there")));

        args.Take(2).Should().Equal("-p", "hello there");
        args.Should().ContainInOrder("--output-format", "stream-json", "--verbose", "--include-partial-messages");
        args.Should().ContainInOrder("--tools", string.Empty);
        args.Should().ContainInOrder("--disallowedTools", "mcp__*");
        args.Should().Contain(["--strict-mcp-config", "--no-session-persistence"]);
        args.Should().ContainInOrder("--permission-mode", "dontAsk");
        args.Should().ContainInOrder("--model", "sonnet");
        args.Should().NotContain("--bare", "bare mode never uses the subscription login");
        stdin.Should().BeNull();
    }

    [Fact]
    public void Claude_sends_system_messages_as_the_system_prompt()
    {
        (List<string> args, _) = new ClaudeCodeCliAdapter().Build(Request(null,
            new ChatMessage("system", "Be terse."), new ChatMessage("user", "hi")));

        args.Should().ContainInOrder("--system-prompt", "Be terse.");
        args.Should().NotContain("--system-prompt-file", "Claude Code 2.1.293 has no such flag");
        args.Should().NotContain("--model");
    }

    [Fact]
    public void Claude_inlines_system_prompts_too_long_for_an_argument()
    {
        string system = new('s', 500);

        (List<string> args, string? stdin) = new ClaudeCodeCliAdapter().Build(Request(null,
            new ChatMessage("system", system), new ChatMessage("user", "hi")));

        args.Should().NotContain("--system-prompt");
        stdin.Should().StartWith("[system]\n" + system);
    }

    [Fact]
    public void Claude_moves_long_prompts_to_standard_input()
    {
        string prompt = new('x', 500);

        (List<string> args, string? stdin) = new ClaudeCodeCliAdapter().Build(Request(null, new ChatMessage("user", prompt)));

        args[1].Should().Contain("standard input");
        stdin.Should().Be(prompt);
    }

    [Fact]
    public void Prompts_starting_with_a_dash_are_not_read_as_options()
    {
        (List<string> args, _) = new ClaudeCodeCliAdapter().Build(Request(null, new ChatMessage("user", "--help me")));

        args[1].Should().Be(" --help me");
    }

    [Fact]
    public void Claude_parser_streams_text_deltas_and_reads_the_result()
    {
        ICliOutputParser parser = new ClaudeCodeCliAdapter().CreateParser();

        parser.Parse(ClaudeStream.Init).Kind.Should().Be(CliEventKind.None);
        parser.Parse(ClaudeStream.Delta("Hel")).Should().Be(new CliEvent(CliEventKind.Delta, "Hel"));
        parser.Parse("not json").Kind.Should().Be(CliEventKind.None);
        parser.Parse(ClaudeStream.Success("Hello"))
            .Should().Be(new CliEvent(CliEventKind.Final, "Hello", InputTokens: 12, OutputTokens: 3));
    }

    [Fact]
    public void Claude_parser_reports_errors_and_quota_retries()
    {
        ICliOutputParser parser = new ClaudeCodeCliAdapter().CreateParser();

        parser.Parse("""{"type":"system","subtype":"api_retry","attempt":1,"error":"rate_limit"}""").QuotaSignal.Should().BeTrue();
        parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["boom"]}""")
            .Should().Match<CliEvent>(e => e.IsError && e.Text == "boom");
    }

    [Fact]
    public void Claude_removes_api_credentials_and_base_url_by_default()
    {
        new ClaudeCodeCliAdapter().DefaultRemovedEnvironment
            .Should().BeEquivalentTo("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL");
    }

    [Fact]
    public void Copilot_uses_single_token_prompt_and_denies_tools()
    {
        (List<string> args, string? stdin) = new CopilotCliAdapter().Build(Request("claude-sonnet-5.5",
            new ChatMessage("system", "Be terse."), new ChatMessage("user", "-hi")));

        args[0].Should().Be("--prompt=[system]\nBe terse.\n\n-hi");
        args.Should().Contain(["-s", "--no-color", "--no-ask-user", "--deny-tool=shell", "--deny-tool=write",
            "--deny-tool=url", "--disable-builtin-mcps", "--no-custom-instructions", "--model=claude-sonnet-5.5"]);
        args.Should().NotContain("--allow-all-tools");
        stdin.Should().BeNull();
    }

    [Fact]
    public void Codex_reads_the_prompt_from_stdin_in_a_read_only_sandbox()
    {
        (List<string> args, string? stdin) = new CodexCliAdapter().Build(Request("gpt-6.1-sol", new ChatMessage("user", "hi")));

        args[0].Should().Be("exec");
        args.Should().ContainInOrder("--sandbox", "read-only");
        args.Should().Contain(["--json", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config"]);
        args.Should().ContainInOrder("-m", "gpt-6.1-sol");
        args[^1].Should().Be("-");
        stdin.Should().Be("hi");
        new CodexCliAdapter().DefaultRemovedEnvironment.Should().Equal("CODEX_API_KEY");
    }

    [Fact]
    public void Codex_parser_returns_the_last_agent_message_on_turn_completion()
    {
        ICliOutputParser parser = new CodexCliAdapter().CreateParser();

        parser.Parse("""{"type":"thread.started","thread_id":"t"}""").Kind.Should().Be(CliEventKind.None);
        parser.Parse("""{"type":"item.completed","item":{"id":"1","type":"agent_message","text":"draft"}}""");
        parser.Parse("""{"type":"item.completed","item":{"id":"2","type":"agent_message","text":"final answer"}}""");
        parser.Parse("""{"type":"turn.completed","usage":{"input_tokens":20,"output_tokens":4}}""")
            .Should().Be(new CliEvent(CliEventKind.Final, "final answer", InputTokens: 20, OutputTokens: 4));
        parser.Parse("""{"type":"turn.failed","error":{"message":"You’ve hit your usage limit."}}""")
            .Should().Match<CliEvent>(e => e.IsError && e.Text == "You’ve hit your usage limit.");
    }

    [Fact]
    public void Custom_adapter_fills_placeholders()
    {
        var options = new ConnectorOptions
        {
            Type = ConnectorType.Cli,
            PromptInput = CliPromptInput.Argument,
            Arguments = ["run", "{model}", "--prompt", "{prompt}"],
        };

        (List<string> args, string? stdin) = new CustomCliAdapter().Build(
            new CliRequest("llama3", [new ChatMessage("user", "hi")], _workingDirectory, options));

        args.Should().Equal("run", "llama3", "--prompt", "hi");
        stdin.Should().BeNull();

        options.PromptInput = CliPromptInput.Stdin;
        options.Arguments = ["run", "-m", "{model}", "--quiet"];
        (args, stdin) = new CustomCliAdapter().Build(new CliRequest(null, [new ChatMessage("user", "hi")], _workingDirectory, options));
        args.Should().Equal(["run", "--quiet"], "a missing model drops both the placeholder and its option");
        stdin.Should().Be("hi");
    }

    [Fact]
    public void Custom_adapter_keeps_a_prompt_starting_with_a_dash_from_becoming_an_option()
    {
        var options = new ConnectorOptions
        {
            Type = ConnectorType.Cli,
            PromptInput = CliPromptInput.Argument,
            Arguments = ["run", "{prompt}", "--prompt={prompt}"],
        };

        (List<string> args, _) = new CustomCliAdapter().Build(
            new CliRequest(null, [new ChatMessage("user", "--exec rm -rf ~")], _workingDirectory, options));

        args.Should().Equal("run", " --exec rm -rf ~", "--prompt=--exec rm -rf ~");
    }

    [Fact]
    public void Transcript_passes_single_messages_through_and_labels_conversations()
    {
        ChatTranscript.Render([new ChatMessage("user", "hi")]).Should().Be((null, "hi"));

        (string? system, string prompt) = ChatTranscript.Render(
        [
            new ChatMessage("system", "S"), new ChatMessage("user", "Q1"), new ChatMessage("assistant", "A1"), new ChatMessage("user", "Q2"),
        ]);

        system.Should().Be("S");
        prompt.Should().Contain("[user]\nQ1").And.Contain("[assistant]\nA1").And.EndWith("[user]\nQ2\n");
    }
}
