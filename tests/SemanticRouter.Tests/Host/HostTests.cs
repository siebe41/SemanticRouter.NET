using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>The <c>semantic-router</c> tool: command line, configuration directory, template, init and doctor.</summary>
public sealed class HostTests : IDisposable
{
    private static readonly string FakeCli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-cli.exe" : "fake-cli");

    private readonly string _directory = Directory.CreateTempSubdirectory("router-host").FullName;

    public void Dispose()
    {
        // The host keeps its state database open in the connection pool; Windows cannot delete an open file.
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private HostSettings Settings(params string[] args) => HostSettings.Parse(["--config-dir", _directory, .. args]);

    [Fact]
    public void Command_line_picks_the_command_and_passes_host_options_through()
    {
        HostSettings run = HostSettings.Parse(["--urls", "http://localhost:6000", "--SemanticRouter:FallbackTier=2"], _ => null);
        HostSettings init = HostSettings.Parse(["init", "--force", "--config-dir=/tmp/x"], _ => null);
        HostSettings unknown = HostSettings.Parse(["serve"], _ => null);

        run.Command.Should().Be("run");
        run.HostArguments.Should().Equal("--urls", "http://localhost:6000", "--SemanticRouter:FallbackTier=2");
        init.Command.Should().Be("init");
        init.Force.Should().BeTrue();
        init.ConfigurationDirectory.Should().Be(Path.GetFullPath("/tmp/x"));
        unknown.Error.Should().Contain("Unknown command 'serve'");
        HostSettings.Parse(["--help"], _ => null).Command.Should().Be("help");
        HostSettings.Parse(["models", "lmstudio", "--config-dir=/tmp/x"], _ => null).Should().Match<HostSettings>(s => s.Command == "models" && s.Connector == "lmstudio");
        HostSettings.Parse(["models"], _ => null).Connector.Should().BeNull();
        HostSettings.Parse(["init", "--yes"], _ => null).Should().Match<HostSettings>(s => s.Yes && !s.NoCli);
        HostSettings.Parse(["init", "-y", "--no-cli"], _ => null).Should().Match<HostSettings>(s => s.Yes && s.NoCli);
        HostSettings.Parse(["init"], _ => null).Should().Match<HostSettings>(s => !s.Yes && !s.NoCli && s.EnableCli == null);
    }

    [Fact]
    public void Enable_cli_takes_a_comma_separated_list_of_known_clis()
    {
        HostSettings.Parse(["init", "--enable-cli", "claude,codex"], _ => null).EnableCli!.Should().BeEquivalentTo("claude", "codex");
        HostSettings.Parse(["init", "--enable-cli=Codex", "--enable-cli", " copilot "], _ => null).EnableCli!.Should().BeEquivalentTo("codex", "copilot");
        HostSettings.Parse(["init", "--enable-cli", "claude,gemini"], _ => null).Error.Should().Be("Unknown CLI 'gemini' for --enable-cli. Use claude, codex, copilot.");
        HostSettings.Parse(["init", "--enable-cli"], _ => null).Error.Should().Contain("--enable-cli needs the CLIs to enable");
        HostSettings.Parse(["init", "--enable-cli", "--force"], _ => null).Error.Should().Contain("--enable-cli needs the CLIs to enable", "an option is not a list of CLIs");
        HostSettings.Parse(["init", "--enable-cli=,"], _ => null).Error.Should().Contain("--enable-cli needs the CLIs to enable");
        HostSettings.Parse(["init", "--enable-cli", "claude"], _ => null).Error.Should().BeNull();
    }

    [Fact]
    public void Configuration_directory_follows_each_operating_system_and_the_override()
    {
        Func<string, string?> env = name => name switch
        {
            "HOME" => "/home/ada",
            "USERPROFILE" => "C:/Users/ada",
            "APPDATA" => "C:/Users/ada/AppData/Roaming",
            _ => null,
        };

        HostSettings.DefaultConfigurationDirectory(env, windows: false, macOS: false).Should().Be(Path.Combine("/home/ada", ".config", "semantic-router"));
        HostSettings.DefaultConfigurationDirectory(env, windows: false, macOS: true).Should().Be(Path.Combine("/home/ada", "Library", "Application Support", "semantic-router"));
        HostSettings.DefaultConfigurationDirectory(env, windows: true, macOS: false).Should().Be(Path.Combine("C:/Users/ada/AppData/Roaming", "semantic-router"));
        HostSettings.DefaultConfigurationDirectory(n => n == "XDG_CONFIG_HOME" ? "/xdg" : env(n), false, false).Should().Be(Path.Combine("/xdg", "semantic-router"));
        HostSettings.DefaultConfigurationDirectory(n => n == HostSettings.HomeVariable ? "/data" : env(n), true, false).Should().Be("/data");
    }

    [Fact]
    public void The_template_is_valid_neutral_and_dispatches()
    {
        RouterConfiguration template = RouterConfiguration.FromJson(RouterHost.Template());

        template.GetDocumentValidationErrors().Should().BeEmpty();
        template.Models.Should().BeEmpty("models are added by init or the dashboard");
        template.Connectors.Should().BeEmpty();
        template.EffectiveTiers.Should().HaveCount(5);
        template.Routes.Should().Contain(r => r.Name == "agentic-coding").And.Contain(r => r.Name == "smalltalk");
        template.Middleware.Mode.Should().Be(PromptRouterMode.Dispatch);
        template.Middleware.ModelAliases.Should().ContainKey("*opus*");
    }

    [Fact]
    public void The_template_routes_claude_codes_default_opus_name_and_keeps_opus_as_a_choice()
    {
        IDictionary<string, string> aliases = RouterConfiguration.FromJson(RouterHost.Template()).Middleware.ModelAliases;

        aliases["*opus*"].Should().Be("auto", "Claude Code sends its default Opus name whether or not you chose Opus");
        aliases["*sonnet*"].Should().Be("auto");
        aliases["claude-*"].Should().Be("auto");
        aliases["*haiku*"].Should().Be("auto:cheapest");
        aliases["opus-pinned"].Should().Be("family:opus");
        aliases.Values.Should().ContainSingle(v => v.StartsWith("family:", StringComparison.Ordinal), "only the explicit name pins a family");
    }

    [Fact]
    public void Claude_code_is_told_the_smallest_context_window_among_the_models_that_carry_tools()
    {
        static ModelDefinition Model(string name, int context, bool tools = true, bool enabled = true)
        {
            var model = new ModelDefinition { Name = name, ContextWindow = context, Enabled = enabled };
            if (tools)
            {
                model.Capabilities.Add("tools");
            }

            return model;
        }

        RouterHost.ClaudeCodeContextTokens([Model("coder", 131_072), Model("opus", 200_000), Model("small", 8_192, tools: false), Model("off", 4_096, enabled: false)])
            .Should().Be(131_072, "models without tools and disabled models never get a Claude Code turn");
        RouterHost.ClaudeCodeContextTokens([Model("coder", 0), Model("opus", 200_000)]).Should().BeNull("an unknown context window is not guessed");
        RouterHost.ClaudeCodeContextTokens([Model("small", 8_192, tools: false)]).Should().BeNull();

        // One line with the variables inline, as docs/clients.md has it; PowerShell sets them for the session instead.
        RouterHost.ClaudeCodeCommand("http://localhost:5080", [Model("opus", 200_000)], windows: false).Should().Be(
            "ANTHROPIC_BASE_URL=http://localhost:5080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=200000 CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router");
        RouterHost.ClaudeCodeCommand("http://localhost:5080", [Model("opus", 200_000)], windows: true).Should().Be(
            "$env:ANTHROPIC_BASE_URL=\"http://localhost:5080\"; $env:ANTHROPIC_AUTH_TOKEN=\"semantic-router\"; $env:CLAUDE_CODE_MAX_CONTEXT_TOKENS=\"200000\"; " +
            "$env:CLAUDE_CODE_AUTO_MODE_SERVER=\"0\"; claude --model semantic-router");
        RouterHost.ClaudeCodeCommand("http://localhost:5080", [], windows: false).Should().Be(
            "ANTHROPIC_BASE_URL=http://localhost:5080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router");
    }

    [Fact]
    public async Task The_sample_evaluation_set_runs_against_the_template()
    {
        RouterConfiguration template = RouterConfiguration.FromJson(RouterHost.Template());
        IReadOnlyList<EvaluationCase> cases = EvaluationSet.Parse(RouterHost.EvaluationTemplate());
        var evaluator = new RoutingEvaluator(new HashingEmbeddingProvider(template.Embeddings.Dimensions), template);
        PreparedEvaluation prepared = await evaluator.PrepareAsync(cases);

        EvaluationReport report = evaluator.Evaluate(prepared);
        CalibrationReport calibration = evaluator.Calibrate(prepared);

        cases.Should().HaveCountGreaterThan(20);
        cases.Where(c => c.ExpectedRoute is not null).Select(c => c.ExpectedRoute).Should().OnlyContain(r => template.Routes.Any(t => t.Name == r));
        report.PerTier.Select(t => t.Tier).Should().Equal(1, 2, 3, 4);
        report.Accuracy.Should().BeGreaterThan(0.5, "the template is tuned for these embeddings");
        calibration.Recommended.Correct.Should().BeGreaterThanOrEqualTo(report.Correct);
    }

    [Fact]
    public async Task Init_finds_local_servers_embeddings_and_clis()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "claude"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "copilot"), string.Empty);
        var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.Port == 1234
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"qwen/qwen3-coder-30b"},{"id":"qwen/qwen3-8b"},{"id":"text-embedding-nomic-embed-text-v1.5"}]}""",
                    Encoding.UTF8, "application/json"),
            }
            : throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        int exit = await InitCommand.RunAsync(Settings("init", "--yes"), new InitProbes(new HttpClient(handler), bin, Windows: false), output);

        exit.Should().Be(0, output.ToString());
        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.GetDocumentValidationErrors().Should().BeEmpty();
        written.Connectors.Keys.Should().BeEquivalentTo("lmstudio", "claude-cli", "copilot-cli");
        written.Models.Select(m => (m.Name, m.Model, m.Tier, m.Enabled)).Should().BeEquivalentTo(new[]
        {
            ("lmstudio-qwen3-coder-30b", "qwen/qwen3-coder-30b", 2, true),
            ("lmstudio-qwen3-8b", "qwen/qwen3-8b", 1, true),
            ("claude-haiku", "haiku", 2, true),
            ("claude-sonnet", "sonnet", 3, true),
            ("claude-opus", "opus", 4, true),
            ("claude-fable-5-1", "claude-fable-5-1", 5, false),
            ((string, string?, int, bool))("copilot", null, 4, false),
        }, "every local chat model is added on the tier its size suggests, and Claude gets one tier per family");
        written.Models.Single(m => m.Name == "lmstudio-qwen3-8b").IsLocal.Should().BeTrue();
        written.Models.Single(m => m.Name == "lmstudio-qwen3-coder-30b").Capabilities.Should().Contain(["code", "tools"]);
        written.Models.Where(m => m.Connector == "claude-cli").Should().OnlyContain(m => m.Capabilities.Contains("tools") && m.ContextWindow == 200_000);
        written.Connectors["claude-cli"].ToolCalling.Should().Be(CliToolCalling.Prompted);
        written.Embeddings.Provider.Should().Be(EmbeddingProviderKind.OpenAICompatible);
        written.Embeddings.Model.Should().Be("text-embedding-nomic-embed-text-v1.5");
        written.DefaultSimilarityThreshold.Should().BeApproximately(0.62f, 0.001f);
        output.ToString().Should().Contain("lmstudio: http://localhost:1234/").And.Contain("copilot").And.Contain("disabled");
        output.ToString().Should().Contain("ANTHROPIC_BASE_URL=http://localhost:5080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router")
            .And.NotContain("CLAUDE_CODE_MAX_CONTEXT_TOKENS", "the local coder model's context window is unknown")
            .And.NotContain("export ", "the command is one line that changes nothing in the shell");
        output.ToString().Should().Contain("Tiers: 1 Local small · 2 Local coder / mini · 3 Strong general · 4 Frontier · 5 Frontier + deep reasoning")
            .And.NotContain("similarity threshold", "a threshold means nothing yet on a first run");
        EvaluationSet.Parse(File.ReadAllText(Path.Combine(_directory, "semantic-router.evaluation.jsonl"))).Should().NotBeEmpty("init writes the sample set next to the configuration");
        File.ReadAllText(Path.Combine(_directory, "semantic-router.json")).Should().NotContain("Effective", "computed properties are not settings");
    }

    [Theory]
    [InlineData("interactive yes", new[] { "init" }, "y\nyes\n", true, true)]
    [InlineData("interactive no", new[] { "init" }, "n\n\n", false, true)]
    [InlineData("interactive, input ends", new[] { "init" }, "", false, true)]
    [InlineData("no terminal", new[] { "init" }, null, false, true)]
    [InlineData("--yes", new[] { "init", "--yes" }, null, true, true)]
    [InlineData("--no-cli", new[] { "init", "--no-cli" }, "y\ny\n", false, false)]
    [InlineData("--no-cli wins over --yes", new[] { "init", "--yes", "--no-cli" }, null, false, false)]
    public async Task Init_enables_a_subscription_cli_only_on_an_explicit_yes(string scenario, string[] args, string? answers, bool enabled, bool added)
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        foreach (string cli in new[] { "claude", "codex", "copilot" })
        {
            File.WriteAllText(Path.Combine(bin, cli), string.Empty);
        }

        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        int exit = await InitCommand.RunAsync(
            Settings(args), new InitProbes(new HttpClient(handler), bin, Windows: false), output, answers is null ? null : new StringReader(answers));

        exit.Should().Be(0, output.ToString());
        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.GetDocumentValidationErrors().Should().BeEmpty();
        string[] subscription = ["claude-cli", "codex-cli", "copilot-cli"];
        if (!added)
        {
            written.Connectors.Keys.Should().NotContain(subscription, scenario);
            written.Models.Should().BeEmpty(scenario);
            output.ToString().Should().NotContain("Enable claude", "--no-cli asks nothing");
            return;
        }

        written.Connectors.Keys.Should().Contain(subscription, scenario);
        written.Connectors["claude-cli"].ToolCalling.Should().Be(CliToolCalling.Prompted);
        written.Models.Where(m => m.Connector == "claude-cli" && m.Tier < 5).Should().OnlyContain(m => m.Enabled == enabled, scenario);
        written.Models.Single(m => m.Connector == "codex-cli").Enabled.Should().Be(enabled, scenario);
        written.Models.Where(m => m.Connector == "copilot-cli").Should().OnlyContain(m => !m.Enabled, "Copilot is enabled by init only when --enable-cli names it");
        written.Models.Single(m => m.Connector == "claude-cli" && m.Tier == 5).Enabled.Should().BeFalse("init never enables Claude's top tier");

        string report = output.ToString();
        if (answers is null)
        {
            report.Should().NotContain("Enable claude", "without a terminal init asks nothing");
            if (!enabled)
            {
                report.Should().Contain("claude, codex: init asks before enabling").And.Contain("whatever account").And.Contain(SiteLinks.WorkUse)
                    .And.Contain("init --force --enable-cli claude,codex");
                report.Should().Contain("No terminal to ask on (input or output is redirected), so claude and codex will be added disabled. " +
                                        "To choose without one, run init with --yes (every CLI found), --enable-cli claude,codex (only those you name) or --no-cli (none).");
            }
        }
        else
        {
            report.Should().Contain("whatever account that CLI is signed in to").And.Contain(SiteLinks.WorkUse)
                .And.Contain("Enable claude on the account it is signed in to? [y/N]").And.NotContain("Enable copilot")
                .And.Contain("Nothing else was found to answer prompts", "no local server answered, so saying no leaves the router without a model");
        }

        if (!enabled)
        {
            report.Should().Contain("all disabled").And.Contain(answers is null ? "no terminal to ask on" : "you did not say yes").And.Contain("No model is enabled")
                .And.Contain("http://localhost:5080/router#/overview").And.NotContain("claude --model", "there is no model to start Claude Code against");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Init_enables_just_the_clis_named_in_enable_cli_without_asking(bool withYes)
    {
        string[] args = withYes ? ["init", "--yes", "--enable-cli", "claude,copilot"] : ["init", "--enable-cli", "claude,copilot"];
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        foreach (string cli in new[] { "claude", "codex", "copilot" })
        {
            File.WriteAllText(Path.Combine(bin, cli), string.Empty);
        }

        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        (await InitCommand.RunAsync(Settings(args), new InitProbes(new HttpClient(handler), bin, Windows: false), output, new StringReader("y\ny\n")))
            .Should().Be(0, output.ToString());

        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.Models.Where(m => m.Connector == "claude-cli" && m.Tier < 5).Should().OnlyContain(m => m.Enabled);
        written.Models.Single(m => m.Connector == "codex-cli").Enabled.Should().BeFalse("codex was not named, and --enable-cli wins over --yes");
        written.Models.Single(m => m.Connector == "copilot-cli").Enabled.Should().BeTrue("naming Copilot is the yes it otherwise never gets");
        string report = output.ToString();
        report.Should().NotContain("Enable claude", "--enable-cli asks nothing, even at a terminal").And.NotContain("No terminal to ask on");
        report.Should().Contain("codex: ").And.Contain("(not named in --enable-cli)");
    }

    [Fact]
    public async Task Init_says_when_a_cli_named_in_enable_cli_is_not_on_this_machine()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "claude"), string.Empty);
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        (await InitCommand.RunAsync(Settings("init", "--enable-cli", "claude,codex"), new InitProbes(new HttpClient(handler), bin, Windows: false), output))
            .Should().Be(0, output.ToString());

        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.Connectors.Keys.Should().Contain("claude-cli").And.NotContain("codex-cli");
        output.ToString().Should().Contain("codex: not found on PATH, so --enable-cli could not enable it.");
    }

    [Theory]
    [InlineData("""{"DisabledConnectorTypes":["Cli"]}""", new string[0])]
    [InlineData("""{"DisabledPresets":["Codex","GitHubCopilot"]}""", new[] { "claude-cli" })]
    public async Task Init_leaves_out_the_clis_the_machine_policy_turns_off(string policy, string[] kept)
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        foreach (string cli in new[] { "claude", "codex", "copilot" })
        {
            File.WriteAllText(Path.Combine(bin, cli), string.Empty);
        }

        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();
        var probes = new InitProbes(new HttpClient(handler), bin, Windows: false, MachinePolicy.FromJson(policy));

        (await InitCommand.RunAsync(Settings("init", "--yes"), probes, output, new StringReader("y\ny\n"))).Should().Be(0, output.ToString());

        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.Connectors.Keys.Where(k => k.EndsWith("-cli", StringComparison.Ordinal)).Should().BeEquivalentTo(kept, "even --yes does not add what the policy turns off");
        string report = output.ToString();
        report.Should().Contain("codex: found at").And.Contain("but not added").And.Contain("your organization's policy");
        report.Should().NotContain("Enable codex", "it is not asked about either");
    }

    [Fact]
    public async Task Init_explains_an_lm_studio_that_wants_a_token_and_never_overwrites_without_force()
    {
        var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.Port == 1234
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : throw new HttpRequestException("Connection refused"));
        var probes = new InitProbes(new HttpClient(handler), string.Empty, Windows: false);
        var first = new StringWriter();
        var second = new StringWriter();

        (await InitCommand.RunAsync(Settings("init"), probes, first)).Should().Be(0);
        (await InitCommand.RunAsync(Settings("init"), probes, second)).Should().Be(1);
        (await InitCommand.RunAsync(Settings("init", "--force", "--no-detect"), probes, new StringWriter())).Should().Be(0);

        first.ToString().Should().Contain("requires an API token").And.Contain("SemanticRouter:Connectors:lmstudio:ApiKey").And.Contain("No model was found");
        second.ToString().Should().Contain("already exists").And.Contain("--force");
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!.Connectors.Should().BeEmpty("--no-detect wrote the bare template");
    }

    [Fact]
    public async Task Doctor_checks_every_connector_and_names_the_setting_to_fix()
    {
        var document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com"), ApiKey = "sk-doctor-test" };
        document.Connectors["tool"] = new ConnectorOptions { Type = ConnectorType.Cli, Executable = FakeCli };
        document.Connectors["missing"] = new ConnectorOptions { Type = ConnectorType.Cli, Executable = Path.Combine(_directory, "no-such-cli") };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "lmstudio", Model = "qwen3-8b", IsLocal = true });
        document.Models.Add(new ModelDefinition { Name = "typo", Connector = "lmstudio", Model = "qwen3-8 b", IsLocal = true });
        document.Models.Add(new ModelDefinition { Name = "cloud", Connector = "openai", Model = "gpt-x", Tier = 3 });
        document.Models.Add(new ModelDefinition { Name = "tool", Connector = "tool", Tier = 2 });
        document.Models.Add(new ModelDefinition { Name = "gone", Connector = "missing", Tier = 2 });
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(document);
        var output = new StringWriter();

        int exit = await DoctorCommand.RunAsync(Settings("doctor"), output, builder =>
        {
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio")
                .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Json("""{"data":[{"id":"qwen3-8b"}]}"""));
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "openai")
                .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized));
        });

        string report = output.ToString();
        exit.Should().Be(1, report);
        report.Should().Contain("[ OK ] model small: 'qwen3-8b' is offered by lmstudio.");
        report.Should().Contain("[FAIL] model typo: 'qwen3-8 b' is not offered by lmstudio; set SemanticRouter:Models:1:Model (dashboard: Providers → Models → typo → Model) to one of: qwen3-8b.");
        report.Should().Contain("[FAIL] connector openai:").And.Contain("rejected the credentials; check SemanticRouter:Connectors:openai:ApiKey (dashboard: Providers → Connectors → openai → API key)");
        report.Should().NotContain("[FAIL] models:", "models are enabled");
        report.Should().Contain("[ OK ] connector tool:");
        report.Should().Contain("[FAIL] connector missing:").And.Contain("SemanticRouter:Connectors:missing:Executable");
        report.Should().Contain("[WARN] embeddings: the Hashing provider");
        report.Should().NotContain("sk-doctor-test");
    }

    [Fact]
    public async Task Doctor_lists_every_host_the_router_will_contact_without_webhook_tokens()
    {
        var document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com:8443/v1"), ApiKey = "sk-doctor-test" };
        document.Connectors["codex"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.Codex, Executable = FakeCli };
        document.Connectors["copilot"] = new ConnectorOptions { Type = ConnectorType.Copilot };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "lmstudio", Model = "qwen3-8b", IsLocal = true });
        document.Models.Add(new ModelDefinition { Name = "cloud", Connector = "openai", Model = "gpt-x", Tier = 3 });
        document.Models.Add(new ModelDefinition { Name = "codex", Connector = "codex", Tier = 4 });
        document.Embeddings = new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx };
        document.Notifications.Webhooks["ops"] = new WebhookOptions { Url = "https://hooks.example.com/services/T000/B000/hook-secret?token=abc" };
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(document);
        string bundle = Path.Combine(_directory, "corporate-root.pem");
        using (var key = System.Security.Cryptography.ECDsa.Create())
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Test Root", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllTextAsync(bundle, root.ExportCertificatePem());
        }

        void Stub(WebApplicationBuilder builder)
        {
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio")
                .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Json("""{"data":[{"id":"qwen3-8b"}]}"""));
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "openai")
                .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Json("""{"data":[{"id":"gpt-x"}]}"""));

            // The embeddings check would download the ONNX model; nothing may leave the test.
            builder.Services.AddHttpClient(SemanticRouter.Embeddings.Onnx.OnnxEmbeddingOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));
        }

        var first = new StringWriter();
        await DoctorCommand.RunAsync(Settings("doctor", $"--SemanticRouter:Network:CaBundle={bundle}"), first, Stub);

        string models = Path.Combine(_directory, "models", "all-MiniLM-L6-v2");
        Directory.CreateDirectory(models);
        await File.WriteAllTextAsync(Path.Combine(models, "model.onnx"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(models, "vocab.txt"), "placeholder");
        var second = new StringWriter();
        await DoctorCommand.RunAsync(Settings("doctor"), second, Stub);

        string report = first.ToString();
        report.Should().Contain("[ OK ] egress: connector lmstudio: localhost:1234 (this machine).");
        report.Should().Contain("[ OK ] egress: connector openai: api.example.com:8443.");
        report.Should().Contain($"[ OK ] egress: connector codex: CLI: {FakeCli}, which contacts OpenAI.");
        report.Should().Contain("[ OK ] egress: connector copilot: CLI: copilot (the Copilot SDK's runtime), which contacts GitHub (no enabled model uses it yet).");
        report.Should().Contain("[ OK ] egress: embeddings: huggingface.co, once, to download ONNX model all-MiniLM-L6-v2 into " + models);
        report.Should().Contain("[ OK ] egress: ONNX Runtime telemetry: off (ORT_DISABLE_TELEMETRY=1, set before ONNX Runtime loads): mobile.events.data.microsoft.com is never contacted");
        report.Should().Contain("[ OK ] egress: webhook ops: hooks.example.com.");
        report.Should().Contain("[ OK ] egress: price list: openrouter.ai, only when the dashboard looks up cloud models' prices");
        report.Should().Contain("[ OK ] egress: desktop app updates: api.github.com, github.com and objects.githubusercontent.com (only the installed desktop app");
        report.Should().Contain($"[ OK ] network: HTTPS also trusts the 1 certificate(s) in {bundle}");
        report.Should().NotContain("hook-secret").And.NotContain("token=abc").And.NotContain("/services/").And.NotContain("sk-doctor-test");
        second.ToString().Should().Contain($"[ OK ] egress: embeddings: ONNX model all-MiniLM-L6-v2 is in {models}; nothing is downloaded.")
            .And.NotContain("network: HTTPS also trusts");
    }

    [Fact]
    public void Doctor_warns_when_onnx_runtime_telemetry_is_left_on()
    {
        EgressList.OnnxTelemetry("1").Status.Should().Be(RouterCheckStatus.Ok);
        RouterCheck on = EgressList.OnnxTelemetry("0");
        on.Status.Should().Be(RouterCheckStatus.Warning);
        on.Message.Should().Contain("ORT_DISABLE_TELEMETRY is '0'").And.Contain("mobile.events.data.microsoft.com").And.Contain("Set it to 1");
        EgressList.OnnxTelemetry(null).Message.Should().Contain("is not set");
    }

    [Fact]
    public async Task Doctor_names_a_ca_bundle_it_cannot_load()
    {
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(RouterConfiguration.FromJson(RouterHost.Template()));
        var output = new StringWriter();

        int exit = await DoctorCommand.RunAsync(Settings("doctor", "--SemanticRouter:Network:CaBundle=missing.pem"), output);

        exit.Should().Be(1, output.ToString());
        output.ToString().Should().Contain("[FAIL] network: SemanticRouter:Network:CaBundle: " + Path.Combine(_directory, "missing.pem") + " could not be read");
    }

    [Fact]
    public async Task Doctor_warns_when_the_identity_header_has_no_proxy_to_come_from()
    {
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(RouterConfiguration.FromJson(RouterHost.Template()));
        var alone = new StringWriter();
        var behind = new StringWriter();

        await DoctorCommand.RunAsync(Settings("doctor", "--SemanticRouter:Dashboard:TrustedIdentityHeader=X-Forwarded-User"), alone);
        await DoctorCommand.RunAsync(
            Settings("doctor", "--SemanticRouter:Dashboard:TrustedIdentityHeader=X-Forwarded-User", "--SemanticRouter:ForwardedHeaders:KnownProxies:0=10.0.0.5",
                "--SemanticRouter:Dashboard:TrustedIdentityRoles:*=ReadOnly"),
            behind);

        alone.ToString().Should().Contain("[WARN] identity: Dashboard:TrustedIdentityHeader is X-Forwarded-User, but SemanticRouter:ForwardedHeaders names no proxy");
        behind.ToString().Should().Contain("[ OK ] identity: the dashboard takes the user from X-Forwarded-User on requests from the known proxies; TrustedIdentityRoles names 1.");
    }

    [Theory]
    [InlineData("127.0.0.1", "alice@corp.example")]
    [InlineData("10.9.9.9", "this machine")]
    public async Task The_host_believes_the_identity_header_only_from_a_known_proxy(string proxy, string identity)
    {
        HostSettings settings = Settings(
            "--urls", "http://127.0.0.1:0", "--SemanticRouter:Embeddings:Provider=Hashing", $"--SemanticRouter:ForwardedHeaders:KnownProxies:0={proxy}",
            "--SemanticRouter:Dashboard:TrustedIdentityHeader=X-Forwarded-User", "--SemanticRouter:Dashboard:TrustedIdentityRoles:alice@corp.example=Admin");
        await using RunningRouter? router = await RouterHost.StartAsync(settings, TextWriter.Null, b => b.Logging.ClearProviders());
        using var client = new HttpClient { BaseAddress = new Uri(router!.App.Urls.First()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/router/api/session");
        request.Headers.Add("X-Forwarded-User", "alice@corp.example");

        System.Text.Json.Nodes.JsonNode session = System.Text.Json.Nodes.JsonNode.Parse(await (await client.SendAsync(request)).Content.ReadAsStringAsync())!;

        session["identity"]!.GetValue<string>().Should().Be(identity);
    }

    [Fact]
    public async Task Init_adds_every_chat_model_a_local_server_offers_and_says_how_it_loads_them()
    {
        var handler = LocalServers.LmStudio(justInTime: true);
        var output = new StringWriter();

        int exit = await InitCommand.RunAsync(Settings("init"), new InitProbes(new HttpClient(handler), string.Empty, Windows: false), output);

        exit.Should().Be(0, output.ToString());
        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.Models.Select(m => (m.Name, m.Tier, m.Priority, m.ContextWindow)).Should().Equal(
            ("lmstudio-qwen3-8b", 1, 0, 32768),
            ("lmstudio-qwen3-coder-30b", 2, 1, 0),
            ("lmstudio-gemma-3-12b", 2, 1, 0));
        written.Embeddings.Model.Should().Be("text-embedding-nomic-embed-text-v1.5");
        output.ToString().Should().Contain("(LM Studio), 3 chat model(s)").And.Contain("just-in-time loading is on");
    }

    [Fact]
    public async Task Without_an_embedding_server_init_picks_onnx_only_where_onnx_runtime_loads()
    {
        var nothing = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var here = new StringWriter();
        var intelMac = new StringWriter();
        const string reason = "In-process (Onnx) embeddings are not available on an Intel Mac: ONNX Runtime publishes no build for it. Use an embedding model in LM Studio or Ollama instead (Embeddings: OpenAICompatible).";

        (await InitCommand.RunAsync(Settings("init"), new InitProbes(new HttpClient(nothing), string.Empty, Windows: false), here)).Should().Be(0);
        RouterConfiguration onnx = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        (await InitCommand.RunAsync(Settings("init", "--force"), new InitProbes(new HttpClient(nothing), string.Empty, Windows: false, OnnxUnavailable: reason), intelMac))
            .Should().Be(0);
        RouterConfiguration hashing = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;

        onnx.Embeddings.Provider.Should().Be(EmbeddingProviderKind.Onnx);
        here.ToString().Should().Contain("'all-MiniLM-L6-v2' runs in this process");
        hashing.Embeddings.Provider.Should().Be(EmbeddingProviderKind.Hashing, "a model that cannot load would leave every prompt unclassified");
        hashing.DefaultSimilarityThreshold.Should().Be(RouterConfiguration.FromJson(RouterHost.Template()).DefaultSimilarityThreshold);
        intelMac.ToString().Should().Contain("embeddings: no embedding server found, so prompts are matched by shared words (Hashing). " + reason)
            .And.Contain("Providers → Embeddings").And.NotContain("runs in this process");
        FirstRun.IsUntouchedTemplate(Path.Combine(_directory, "semantic-router.json")).Should().BeTrue("init may run again once a server is there");
    }

    [Fact]
    public async Task Init_leaves_out_ollama_cloud_models()
    {
        var ollama = LocalServers.Ollama();
        var handler = new StubHttpMessageHandler((request, body) => request.RequestUri!.Port == 11434
            ? ollama.Respond(request, body)
            : throw new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        (await InitCommand.RunAsync(Settings("init"), new InitProbes(new HttpClient(handler), string.Empty, Windows: false), output)).Should().Be(0, output.ToString());

        RouterConfiguration written = new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Load()!;
        written.Models.Select(m => m.Model).Should().BeEquivalentTo("llama3.2:latest", "qwen3:30b-a3b");
        written.Models.Should().OnlyContain(m => m.IsLocal);
        output.ToString().Should().Contain("left out gpt-oss:120b-cloud").And.Contain("Ollama loads a model on its first request");
    }

    [Fact]
    public async Task Models_lists_what_each_connector_offers_with_load_state_and_suggestions()
    {
        var document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Connectors["claude-cli"] = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode, ToolCalling = CliToolCalling.Prompted };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "lmstudio", Model = "qwen/qwen3-8b", IsLocal = true });
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(document);
        void Stub(WebApplicationBuilder builder) => builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio")
            .ConfigurePrimaryHttpMessageHandler(() => LocalServers.LmStudio(justInTime: false));
        var all = new StringWriter();
        var one = new StringWriter();
        var unknown = new StringWriter();

        HostSettings Models(params string[] args) => HostSettings.Parse(["models", .. args, "--config-dir", _directory]);
        (await ModelsCommand.RunAsync(Models(), all, Stub)).Should().Be(0, all.ToString());
        (await ModelsCommand.RunAsync(Models("claude-cli"), one, Stub)).Should().Be(0);
        (await ModelsCommand.RunAsync(Models("nope"), unknown, Stub)).Should().Be(1);

        string report = all.ToString();
        report.Should().Contain("lmstudio (OpenAICompatible, LM Studio)").And.Contain("just-in-time loading is off");
        report.Should().MatchRegex(@"qwen/qwen3-8b\s+loaded\s+32,768\s+8B\s+small \(T1\)");
        report.Should().MatchRegex(@"qwen/qwen3-coder-30b\s+not served\s+262,144\s+30B\s+\+ lmstudio-qwen3-coder-30b T2 \(family\) chat,code,tools \(disabled\)");
        report.Should().Contain("Tiers come from the model-family table (built-in v1)");
        report.Should().MatchRegex(@"text-embedding-nomic-embed-text-v1.5\s+embedding");
        report.Should().Contain("claude-cli (Cli)").And.Contain("+ claude-opus T4");
        report.Should().MatchRegex(@"\n\s+why: \S", "each suggestion's reason follows on its own line, in words rather than an arrow glyph");
        one.ToString().Should().Contain("claude-cli").And.NotContain("lmstudio");
        unknown.ToString().Should().Contain("'nope' is not configured");
    }

    [Fact]
    public async Task Models_uses_a_model_family_table_in_the_configuration_directory()
    {
        var document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(document);
        await File.WriteAllTextAsync(Path.Combine(_directory, "model-families.json"), """
            { "families": [ { "family": "Qwen3 Coder", "match": ["qwen3-coder"], "tier": 3, "capabilities": ["code", "tools"] } ] }
            """);
        var output = new StringWriter();

        (await ModelsCommand.RunAsync(HostSettings.Parse(["models", "--config-dir", _directory]), output, builder =>
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio")
                .ConfigurePrimaryHttpMessageHandler(() => LocalServers.LmStudio(justInTime: true)))).Should().Be(0, output.ToString());

        string report = output.ToString();
        report.Should().MatchRegex(@"\+ lmstudio-qwen3-coder-30b T3 \(family\)", "the local table wins over the built-in one");
        report.Should().MatchRegex(@"\+ lmstudio-qwen3-8b T1 \(size\)", "Qwen3 has capabilities in the table but no tiers: the size decides");
        report.Should().Contain("model-families.json (1 family)");
    }

    [Fact]
    public async Task Doctor_explains_models_a_server_will_not_load_and_context_larger_than_loaded()
    {
        var document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        document.Models.Add(new ModelDefinition { Name = "small", Connector = "lmstudio", Model = "qwen/qwen3-8b", IsLocal = true, ContextWindow = 65536 });
        document.Models.Add(new ModelDefinition { Name = "coder", Connector = "lmstudio", Model = "qwen/qwen3-coder-30b", IsLocal = true, Tier = 2 });
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(document);
        var output = new StringWriter();

        int exit = await DoctorCommand.RunAsync(Settings("doctor"), output, builder =>
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "lmstudio")
                .ConfigurePrimaryHttpMessageHandler(() => LocalServers.LmStudio(justInTime: false)));

        string report = output.ToString();
        exit.Should().Be(1, report);
        report.Should().Contain("[ OK ] connector lmstudio:").And.Contain("LM Studio, 1 loaded.").And.Contain("just-in-time loading is off");
        report.Should().Contain("[ OK ] model small: 'qwen/qwen3-8b' is offered by lmstudio (loaded).");
        report.Should().Contain("[WARN] model small: ContextWindow 65,536 is larger than the 32,768 tokens it is loaded with")
            .And.Contain("SemanticRouter:Models:0:ContextWindow to 32768");
        report.Should().Contain("[FAIL] model coder: 'qwen/qwen3-coder-30b' is downloaded but not loaded, and LM Studio only serves loaded models");
    }

    [Fact]
    public void Listen_addresses_are_classified_and_printed_for_local_clients()
    {
        RouterHost.IsLoopback("http://localhost:5080").Should().BeTrue();
        RouterHost.IsLoopback("http://127.0.0.1:5080").Should().BeTrue();
        RouterHost.IsLoopback("http://[::1]:5080").Should().BeTrue();
        RouterHost.IsLoopback("http://0.0.0.0:8080").Should().BeFalse();
        RouterHost.IsLoopback("http://*:8080").Should().BeFalse();
        RouterHost.IsLoopback("http://router.lan:8080").Should().BeFalse();

        RouterHost.LocalAddress("http://0.0.0.0:8080").Should().Be("http://localhost:8080");
        RouterHost.LocalAddress("http://+:8080").Should().Be("http://localhost:8080");
        RouterHost.LocalAddress("http://127.0.0.1:5080/").Should().Be("http://127.0.0.1:5080");
    }

    [Fact]
    public void Forwarded_headers_are_trusted_only_from_the_configured_proxies()
    {
        WebApplicationBuilder none = WebApplication.CreateBuilder();
        RouterHost.ConfigureForwardedHeaders(none).Should().BeFalse("without proxies nothing is forwarded");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["SemanticRouter:ForwardedHeaders:KnownProxies:0"] = "10.1.2.3";
        builder.Configuration["SemanticRouter:ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12";

        RouterHost.ConfigureForwardedHeaders(builder).Should().BeTrue();

        using ServiceProvider services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        options.ForwardedHeaders.Should().Be(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            "X-Forwarded-Host would get past the Host check against DNS rebinding");
        options.KnownProxies.Should().Equal(IPAddress.Parse("10.1.2.3"));
        options.KnownIPNetworks.Should().Equal(System.Net.IPNetwork.Parse("172.16.0.0/12"));

        WebApplicationBuilder broken = WebApplication.CreateBuilder();
        broken.Configuration["SemanticRouter:ForwardedHeaders:KnownNetworks:0"] = "everyone";
        FluentActions.Invoking(() => RouterHost.ConfigureForwardedHeaders(broken)).Should().Throw<InvalidOperationException>().WithMessage("*KnownNetworks*everyone*");
    }

    [Fact]
    public async Task A_state_database_from_a_newer_release_stops_the_router_and_fails_doctor()
    {
        string state = Path.Combine(_directory, HostSettings.StateFileName);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={state};Pooling=False"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta (key, value) VALUES ('schema_version', '99');";
            create.ExecuteNonQuery();
        }

        var output = new StringWriter();
        (await RouterHost.RunAsync(Settings("--urls", "http://127.0.0.1:0"), output, b => b.Logging.ClearProviders())).Should().Be(RouterHost.StateTooNewExitCode);
        output.ToString().Should().Contain($"The router's state database {state} was written by a newer SemanticRouter (state schema 99");
        await FluentActions.Awaiting(() => RouterHost.StartAsync(Settings("--urls", "http://127.0.0.1:0"), TextWriter.Null, b => b.Logging.ClearProviders()))
            .Should().ThrowAsync<InvalidOperationException>("the desktop app shows the message of an InvalidOperationException as why it could not start");
        File.Exists(RouterInstance.PathIn(_directory)).Should().BeFalse();

        var report = new StringWriter();
        (await DoctorCommand.RunAsync(Settings("doctor"), report)).Should().Be(1);
        report.ToString().Should().Contain($"[FAIL] state: The router's state database {state} was written by a newer SemanticRouter")
            .And.NotContain("[WARN] configuration: The router's state database", "the state line says it once");
    }

    [Fact]
    public async Task A_state_database_that_cannot_be_used_is_a_runtime_warning_and_a_doctor_failure()
    {
        string state = Path.Combine(_directory, HostSettings.StateFileName);
        await File.WriteAllTextAsync(state, new string('x', 4096));

        await using (WebApplication app = RouterHost.Build(Settings(), b => b.Logging.ClearProviders()))
        {
            app.Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings
                .Should().Contain(w => w.StartsWith($"The router's state database {state} cannot be used", StringComparison.Ordinal) && w.Contains("budgets count from zero", StringComparison.Ordinal));
        }

        var report = new StringWriter();
        (await DoctorCommand.RunAsync(Settings("doctor"), report)).Should().Be(1);
        report.ToString().Should().Contain($"[FAIL] state: The router's state database {state} cannot be used");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(state);
        var healthy = new StringWriter();
        await DoctorCommand.RunAsync(Settings("doctor"), healthy);
        healthy.ToString().Should().Contain($"[ OK ] state: {state}: budgets, usage, history and the audit log are kept.");
    }
}
