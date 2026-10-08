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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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
            ("lmstudio", "qwen/qwen3-8b", 1, true),
            ("lmstudio-coder", "qwen/qwen3-coder-30b", 2, true),
            ("claude", "sonnet", 4, true),
            ("claude-opus", "opus", 5, true),
            ((string, string?, int, bool))("copilot", null, 4, false),
        });
        written.Models.Single(m => m.Name == "lmstudio").IsLocal.Should().BeTrue();
        written.Connectors["claude-cli"].ToolCalling.Should().Be(CliToolCalling.Prompted);
        written.Embeddings.Provider.Should().Be(EmbeddingProviderKind.OpenAICompatible);
        written.Embeddings.Model.Should().Be("text-embedding-nomic-embed-text-v1.5");
        written.DefaultSimilarityThreshold.Should().BeApproximately(0.62f, 0.001f);
        output.ToString().Should().Contain("lmstudio: http://localhost:1234/").And.Contain("copilot").And.Contain("disabled");
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
