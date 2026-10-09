using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

        int exit = await InitCommand.RunAsync(Settings("init"), new InitProbes(new HttpClient(handler), bin, Windows: false), output);

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
        EvaluationSet.Parse(File.ReadAllText(Path.Combine(_directory, "semantic-router.evaluation.jsonl"))).Should().NotBeEmpty("init writes the sample set next to the configuration");
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
        report.Should().Contain("[FAIL] model typo: 'qwen3-8 b' is not offered by lmstudio; set SemanticRouter:Models:1:Model to one of: qwen3-8b.");
        report.Should().Contain("[FAIL] connector openai:").And.Contain("rejected the credentials; check SemanticRouter:Connectors:openai:ApiKey");
        report.Should().Contain("[ OK ] connector tool:");
        report.Should().Contain("[FAIL] connector missing:").And.Contain("SemanticRouter:Connectors:missing:Executable");
        report.Should().Contain("[WARN] embeddings: the Hashing provider");
        report.Should().NotContain("sk-doctor-test");
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
}
