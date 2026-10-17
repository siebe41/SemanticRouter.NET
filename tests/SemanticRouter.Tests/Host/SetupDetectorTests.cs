using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>What the dashboard's setup finds on this machine (init's detection), and the host's root redirect.</summary>
public sealed class SetupDetectorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-detect").FullName;

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task On_windows_a_cli_installed_only_as_an_npm_shim_is_shown_with_what_to_do()
    {
        // npm's global install on Windows: codex.cmd and copilot.ps1, no .exe; claude.exe from the native installer.
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "npm")).FullName;
        File.WriteAllText(Path.Combine(bin, "codex.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "copilot.ps1"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "claude.exe"), string.Empty);
        var runner = new FakeCliProcessRunner(_ => new FakeCliProcess(["1.0.0"]));
        using ServiceProvider services = new ServiceCollection().AddSingleton<ICliProcessRunner>(runner).BuildServiceProvider();
        var probes = new InitProbes(new HttpClient(StubHttpMessageHandler.Throws()), bin, Windows: true);

        IReadOnlyList<SetupDetection> found = await SetupDetector.DetectAsync(services, probes, CancellationToken.None);

        found.Single(d => d.Id == "claude-code").Problem.Should().BeNull();
        SetupDetection codex = found.Single(d => d.Id == "codex");
        codex.Detail.Should().Be($"Found {Path.Combine(bin, "codex.cmd")}");
        codex.Problem.Should().Contain("only a script wrapper was found").And.Contain("https://github.com/openai/codex/releases")
            .And.Contain("SemanticRouter:Connectors:codex-cli:Executable");
        codex.Models.Should().BeEmpty("it cannot be enabled as it is");
        found.Single(d => d.Id == "copilot-cli").Problem.Should().Contain("winget install GitHub.Copilot");

        var blocked = probes with { Policy = MachinePolicy.FromJson("""{ "DisabledPresets": [ "Codex" ] }""") };
        (await SetupDetector.DetectAsync(services, blocked, CancellationToken.None)).Should().NotContain(d => d.Id == "codex", "the policy turns it off anyway");
    }

    [Fact]
    public async Task Finds_what_init_would_and_says_whether_each_cli_is_signed_in()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        foreach (string cli in new[] { "claude", "codex", "copilot" })
        {
            File.WriteAllText(Path.Combine(bin, cli), string.Empty);
        }

        var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.Port == 1234
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"qwen/qwen3-coder-30b"},{"id":"qwen/qwen3-8b"}]}""", Encoding.UTF8, "application/json"),
            }
            : throw new HttpRequestException("Connection refused"));
        var runner = new FakeCliProcessRunner(invocation => new FakeCliProcess(
            (Path.GetFileName(invocation.Executable), string.Join(' ', invocation.Arguments)) switch
            {
                ("claude", "--version") => ["2.1.3 (Claude Code)"],
                ("claude", "auth status") => ["""{"loggedIn":true,"authMethod":"claude.ai"}"""],
                ("codex", "--version") => ["codex-cli 0.50.0"],
                ("codex", "login status") => ["Not logged in"],
                _ => ["1.0.2"],
            }));
        using ServiceProvider services = new ServiceCollection().AddSingleton<ICliProcessRunner>(runner).BuildServiceProvider();

        IReadOnlyList<SetupDetection> found = await SetupDetector.DetectAsync(services, new InitProbes(new HttpClient(handler), bin, Windows: false), CancellationToken.None);

        found.Select(d => (d.Id, d.Kind, d.ConnectorName)).Should().BeEquivalentTo(new[]
        {
            ("lmstudio", "local", "lmstudio"), ("claude-code", "cli", "claude-cli"), ("codex", "cli", "codex-cli"), ("copilot-cli", "cli", "copilot-cli"),
        });
        SetupDetection lmstudio = found.Single(d => d.Id == "lmstudio");
        lmstudio.Detail.Should().Be("Running at localhost:1234 with 2 chat models: qwen/qwen3-coder-30b, qwen/qwen3-8b");
        lmstudio.Models.Should().OnlyContain(m => m.Connector == "lmstudio" && m.Enabled);

        SetupDetection claude = found.Single(d => d.Id == "claude-code");
        claude.Detail.Should().Be($"Signed in · claude 2.1.3 at {Path.Combine(bin, "claude")}");
        claude.Consent.Should().StartWith("Uses your Claude subscription, on the account claude is signed in to.");
        claude.Connector.ToolCalling.Should().Be(CliToolCalling.Prompted, "as init sets it up");
        claude.Models.Select(m => (m.Model, m.Enabled)).Should().Equal(("haiku", true), ("sonnet", true), ("opus", true), ("claude-fable-5-1", false));
        claude.Aliases.Should().ContainKey("*opus*").And.ContainKey("claude-*");

        found.Single(d => d.Id == "codex").Detail.Should().Be($"Not signed in: run `codex login` · codex 0.50.0 at {Path.Combine(bin, "codex")}");
        found.Single(d => d.Id == "copilot-cli").Models.Should().OnlyContain(m => m.Enabled, "enabling it from the setup is the consent init asks for");
        runner.Invocations.Should().OnlyContain(i => i.Arguments.Count <= 2, "only version and sign-in checks run");
    }

    [Fact]
    public async Task Offline_the_setup_does_not_even_start_a_cli()
    {
        HostSettings settings = HostSettings.Parse(
            ["--config-dir", _directory, "--urls", "http://127.0.0.1:0", "--SemanticRouter:Embeddings:Provider=Hashing", "--SemanticRouter:Network:Offline=true"]);
        await using RunningRouter? router = await RouterHost.StartAsync(settings, TextWriter.Null, b => b.Logging.ClearProviders());
        MachinePolicy policy = SetupDetector.PolicyOf(router!.App.Services);
        policy.Offline.Should().BeTrue("the router's own Network:Offline counts, not only a machine policy");

        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        foreach (string cli in new[] { "claude", "codex", "copilot" })
        {
            File.WriteAllText(Path.Combine(bin, cli), string.Empty);
        }

        var runner = new FakeCliProcessRunner(_ => new FakeCliProcess(["1.0.0"]));
        using ServiceProvider services = new ServiceCollection().AddSingleton<ICliProcessRunner>(runner).BuildServiceProvider();
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));

        IReadOnlyList<SetupDetection> found = await SetupDetector.DetectAsync(services, new InitProbes(new HttpClient(handler), bin, Windows: false, policy), CancellationToken.None);

        found.Should().BeEmpty();
        runner.Invocations.Should().BeEmpty("offline, a CLI would be turned off anyway, so it is not started to ask its version");
    }

    [Fact]
    public async Task The_host_registers_the_detector_and_sends_the_root_to_the_dashboard()
    {
        HostSettings settings = HostSettings.Parse(["--config-dir", _directory, "--urls", "http://127.0.0.1:5603", "--SemanticRouter:Embeddings:Provider=Hashing"]);
        await using RunningRouter? router = await RouterHost.StartAsync(settings, TextWriter.Null, b => b.Logging.ClearProviders());
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(router!.LocalUrl) };

        HttpResponseMessage root = await client.GetAsync("/");

        router.App.Services.GetService<IRouterSetupDetector>().Should().BeOfType<SetupDetector>();
        root.StatusCode.Should().Be(HttpStatusCode.Redirect);
        root.Headers.Location!.ToString().Should().Be("/router/");
    }
}
