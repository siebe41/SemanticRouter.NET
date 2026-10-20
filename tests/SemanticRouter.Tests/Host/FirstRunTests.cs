using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// The first minutes with the tool: <c>init</c> over the template the router wrote, <c>doctor</c> and the banner without
/// a model, a port that is taken, a quiet console, and a container that seeds connectors from its API keys.
/// </summary>
public sealed class FirstRunTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-first-run").FullName;

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private string ConfigurationFile => Path.Combine(_directory, "semantic-router.json");

    private HostSettings Settings(params string[] args) => HostSettings.Parse(["--config-dir", _directory, .. args], _ => null);

    private static InitProbes NothingRunning() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"))), string.Empty, Windows: false);

    [Fact]
    public async Task Init_replaces_the_untouched_template_without_force_but_never_a_changed_file()
    {
        // As the router writes it on its first start, then reformatted: it is compared as a configuration, not byte by byte.
        var store = new JsonFileRouterConfigurationStore(ConfigurationFile);
        store.Save(RouterConfiguration.FromJson(RouterHost.Template()));
        File.WriteAllText(ConfigurationFile, File.ReadAllText(ConfigurationFile).Replace("\n", "\r\n", StringComparison.Ordinal));
        FirstRun.IsUntouchedTemplate(ConfigurationFile, exactly: true).Should().BeTrue();

        var first = new StringWriter();
        (await InitCommand.RunAsync(Settings("init"), NothingRunning(), first)).Should().Be(0, first.ToString());
        first.ToString().Should().Contain("it held only the built-in template").And.Contain("run `semantic-router init` again");

        // That init found nothing and chose in-process embeddings; running it again, as it suggests, still works.
        FirstRun.IsUntouchedTemplate(ConfigurationFile, exactly: true).Should().BeFalse("init changed the embeddings");
        var again = new StringWriter();
        (await InitCommand.RunAsync(Settings("init"), NothingRunning(), again)).Should().Be(0, again.ToString());

        RouterConfiguration changed = store.Load()!;
        changed.Routes.RemoveAt(0);
        store.Save(changed);
        var refused = new StringWriter();
        (await InitCommand.RunAsync(Settings("init"), NothingRunning(), refused)).Should().Be(1);
        refused.ToString().Should().Contain("already exists").And.Contain("--force");
        store.Load()!.Routes.Should().HaveCount(changed.Routes.Count, "a changed file is never replaced without --force");
    }

    [Fact]
    public async Task Init_keeps_a_template_whose_embeddings_someone_chose()
    {
        RouterConfiguration chosen = RouterConfiguration.FromJson(RouterHost.Template());
        chosen.Embeddings.Provider = EmbeddingProviderKinds.OpenAICompatible;
        chosen.Embeddings.Endpoint = new Uri("http://embeddings.lan:8080");
        chosen.Embeddings.Model = "nomic-embed-text";
        var store = new JsonFileRouterConfigurationStore(ConfigurationFile);
        store.Save(chosen);

        FirstRun.IsUntouchedTemplate(ConfigurationFile).Should().BeFalse("only the embeddings an init that found nothing picks are the template's");
        var refused = new StringWriter();
        (await InitCommand.RunAsync(Settings("init"), NothingRunning(), refused)).Should().Be(1);
        refused.ToString().Should().Contain("--force");
        store.Load()!.Embeddings.Model.Should().Be("nomic-embed-text");

        // What an init that found nothing writes is still the template, so running init again, as it suggests, works.
        RouterConfiguration initialised = RouterConfiguration.FromJson(RouterHost.Template());
        InitCommand.UseInProcessEmbeddings(initialised);
        store.Save(initialised);
        FirstRun.IsUntouchedTemplate(ConfigurationFile).Should().BeTrue();
        FirstRun.IsUntouchedTemplate(ConfigurationFile, exactly: true).Should().BeFalse();
    }

    [Fact]
    public async Task Init_with_nothing_found_points_to_the_dashboard_setup_and_not_to_claude_code()
    {
        var output = new StringWriter();

        (await InitCommand.RunAsync(Settings("init", "--urls", "http://localhost:5612"), NothingRunning(), output)).Should().Be(0);

        string report = output.ToString();
        report.Should().Contain("No model was found").And.Contain("http://localhost:5612/router#/overview").And.Contain("Developer → Start server");
        report.Should().NotContain("claude --model").And.NotContain("similarity threshold").And.NotContain("Tiers:", "there is no model to give a tier");
    }

    [Fact]
    public async Task Init_says_what_no_means_when_a_cli_is_all_there_is()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "claude"), string.Empty);
        var output = new StringWriter();

        await InitCommand.RunAsync(Settings("init"), NothingRunning() with { SearchPath = bin }, output, new StringReader("n\n"));

        output.ToString().Should().Contain("so without claude the router has no model to use").And.Contain("Enable claude on the account it is signed in to? [y/N]");
    }

    [Fact]
    public async Task Doctor_fails_without_an_enabled_model_and_names_a_step_that_helps()
    {
        var template = new StringWriter();
        (await DoctorCommand.RunAsync(Settings("doctor", "--urls", "http://localhost:5612"), template)).Should().Be(1, template.ToString());
        template.ToString().Should().Contain("[FAIL] models: no model is enabled, so the router cannot answer any prompt.")
            .And.Contain("Run `semantic-router init`").And.Contain("http://localhost:5612/router#/overview")
            .And.NotContain("[WARN] configuration: No model is enabled", "one line says it");

        // After init ran (other embeddings, a disabled CLI model), running init again would not help.
        RouterConfiguration initialised = RouterConfiguration.FromJson(RouterHost.Template());
        initialised.Embeddings.Dimensions = 256;
        initialised.Connectors["tool"] = new ConnectorOptions { Type = ConnectorTypes.Cli, Executable = "no-such-cli" };
        initialised.Models.Add(new ModelDefinition { Name = "tool", Connector = "tool", Tier = 2, Enabled = false });
        new JsonFileRouterConfigurationStore(ConfigurationFile).Save(initialised);
        var after = new StringWriter();

        (await DoctorCommand.RunAsync(Settings("doctor"), after)).Should().Be(1);

        after.ToString().Should().Contain("[FAIL] models: no model is enabled (1 configured, all disabled)").And.Contain("Providers → Models")
            .And.Contain("http://localhost:5080/router#/overview").And.NotContain("semantic-router init");
    }

    [Fact]
    public void The_banner_says_how_to_try_the_router_or_where_to_set_it_up()
    {
        RouterConfiguration empty = RouterConfiguration.FromJson(RouterHost.Template());
        string setup = RouterHost.Banner("1.2.3", ["http://127.0.0.1:5080"], "/c/semantic-router.json", empty, windows: false);
        setup.Should().Contain("No model is enabled yet").And.Contain("Set one up on the dashboard: http://127.0.0.1:5080/router#/overview")
            .And.NotContain("claude --model").And.NotContain("curl");

        RouterConfiguration ready = RouterConfiguration.FromJson(RouterHost.Template());
        ready.Connectors["local"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        ready.Models.Add(new ModelDefinition { Name = "coder", Connector = "local", Model = "qwen", ContextWindow = 131_072, Capabilities = { "chat", "code", "tools" } });
        string banner = RouterHost.Banner("1.2.3", ["http://0.0.0.0:8080"], "/c/semantic-router.json", ready, windows: false);

        banner.Should().Contain("Try it").And.Contain("curl http://localhost:8080/v1/chat/completions -H 'Content-Type: application/json' -d '{\"model\":\"auto\",")
            .And.Contain("Watch it: http://localhost:8080/router#/live")
            .And.Contain("ANTHROPIC_BASE_URL=http://localhost:8080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=131072 CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router")
            .And.Contain("Listening beyond this machine").And.Contain(SiteLinks.Terms).And.Contain(SiteLinks.ListeningBeyond)
            .And.NotContain("docs/").And.NotContain("No model is enabled");
        RouterHost.Banner("1.2.3", ["http://localhost:5080"], "c", ready, windows: true).Should()
            .Contain("Invoke-RestMethod http://localhost:5080/v1/chat/completions -Method Post").And.Contain("$env:ANTHROPIC_BASE_URL=\"http://localhost:5080\"");
    }

    [Fact]
    public void A_taken_address_is_explained_without_a_stack_trace_and_our_own_router_is_named()
    {
        RouterHost.AddressInUse("Failed to bind to address http://127.0.0.1:5080: address already in use.", null)
            .Should().StartWith("Failed to bind").And.Contain("Another program").And.Contain("--urls");

        const string taken = "Failed to bind to address http://127.0.0.1:5080: address already in use.";
        RouterHost.AddressInUse(taken, new RouterInstance(4242, ["http://127.0.0.1:5080"], DateTimeOffset.UtcNow, "1.2.3"))
            .Should().Contain("already running for this configuration (process 4242, version 1.2.3)").And.Contain("http://127.0.0.1:5080/router").And.NotContain("Failed to bind");
        RouterHost.AddressInUse(taken, new RouterInstance(4242, ["http://[::]:5080"], DateTimeOffset.UtcNow, "1.2.3"))
            .Should().Contain("already running for this configuration", "a router on every address holds this one too");

        // This configuration's router listens elsewhere: something else holds the address.
        RouterHost.AddressInUse(taken, new RouterInstance(4242, ["http://127.0.0.1:5090"], DateTimeOffset.UtcNow, "1.2.3"))
            .Should().StartWith("Failed to bind").And.Contain("Another program").And.NotContain("already running");
        RouterHost.AddressInUse(taken, new RouterInstance(4242, ["http://192.168.1.20:5080"], DateTimeOffset.UtcNow, "1.2.3"))
            .Should().Contain("Another program");

        RouterHost.LocalAddress(Settings("--urls", "http://0.0.0.0:9000;https://0.0.0.0:9001")).Should().Be("http://localhost:9000");
        RouterHost.LocalAddress(Settings()).Should().Be("http://localhost:5080");
    }

    [Fact]
    public async Task The_console_is_quiet_by_default_and_appsettings_can_turn_it_up()
    {
        await using (WebApplication quiet = RouterHost.Build(Settings()))
        {
            ILoggerFactory loggers = quiet.Services.GetRequiredService<ILoggerFactory>();
            loggers.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").IsEnabled(LogLevel.Information).Should().BeFalse("ASP.NET Core's lines per request are noise");
            loggers.CreateLogger("Microsoft.Hosting.Lifetime").IsEnabled(LogLevel.Information).Should().BeFalse("the banner says where it listens");
            loggers.CreateLogger("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager").IsEnabled(LogLevel.Warning).Should().BeFalse("the key ring reports itself");
            loggers.CreateLogger("SemanticRouter.AspNetCore.PromptRoutingMiddleware").IsEnabled(LogLevel.Information).Should().BeTrue("the router keeps its own line per request");

            // On Windows the host adds the Event Log with a Warning filter of its own, which outranks a category level
            // that names no provider: a stand-in under the Event Log's alias, with that filter, gets the same rules here.
            using ServiceProvider eventLog = new ServiceCollection().AddLogging(logging => logging
                .AddConfiguration(quiet.Configuration.GetSection("Logging"))
                .AddProvider(new EventLogStandIn())
                .AddFilter<EventLogStandIn>(level => level >= LogLevel.Warning)).BuildServiceProvider();
            ILoggerFactory windows = eventLog.GetRequiredService<ILoggerFactory>();
            windows.CreateLogger("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager").IsEnabled(LogLevel.Warning).Should().BeFalse("the key ring reports itself, in the Event Log too");
            windows.CreateLogger("SemanticRouter.AspNetCore.RouterRuntimeProvider").IsEnabled(LogLevel.Warning).Should().BeTrue("the router's own warnings still reach the Event Log");
        }

        await using WebApplication loud = RouterHost.Build(Settings("--Logging:LogLevel:Microsoft.AspNetCore=Information"));
        loud.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    [Fact]
    public void A_container_seeds_a_connector_for_each_api_key_and_keeps_the_key_in_the_environment()
    {
        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        FirstRun.SeedFromEnvironment(document, n => n == "OPENAI_API_KEY" ? "sk-seed-test" : null).Should().BeEmpty("only the container image turns seeding on");

        var environment = new Dictionary<string, string?> { [HostSettings.SeedVariable] = "true", ["OPENAI_API_KEY"] = "sk-seed-test", ["ANTHROPIC_API_KEY"] = "sk-ant-seed-test" };
        FirstRun.SeedFromEnvironment(document, environment.GetValueOrDefault).Should().Equal("openai", "anthropic");

        document.Connectors["openai"].Should().BeEquivalentTo(new { Type = ConnectorTypes.OpenAICompatible, BaseAddress = new Uri("https://api.openai.com"), ApiKey = "${env:OPENAI_API_KEY}" });
        document.Connectors["anthropic"].Should().BeEquivalentTo(new { Type = ConnectorTypes.Anthropic, BaseAddress = new Uri("https://api.anthropic.com"), ApiKey = "${env:ANTHROPIC_API_KEY}" });
        document.ToJson().Should().NotContain("sk-seed-test").And.NotContain("sk-ant-seed-test");
        document.GetDocumentValidationErrors().Should().BeEmpty();
        FirstRun.Describe("/data/semantic-router.json", ["openai", "anthropic"])
            .Should().Contain("openai and anthropic connectors for OPENAI_API_KEY and ANTHROPIC_API_KEY").And.Contain("Discover models");
    }

    [Fact]
    public async Task The_router_seeds_connectors_only_on_the_start_that_creates_the_file()
    {
        var environment = new Dictionary<string, string?> { [HostSettings.SeedVariable] = "1", ["ANTHROPIC_API_KEY"] = "sk-ant-seed-test" };
        HostSettings settings = Settings("--urls", "http://127.0.0.1:0", "--SemanticRouter:Embeddings:Provider=Hashing");
        var output = new StringWriter();

        await using (RunningRouter? router = await RouterHost.StartAsync(settings, output, b => b.Logging.ClearProviders(), environment.GetValueOrDefault))
        {
            router.Should().NotBeNull();
        }

        output.ToString().Should().Contain("Created").And.Contain("anthropic connector for ANTHROPIC_API_KEY");
        RouterConfiguration seeded = new JsonFileRouterConfigurationStore(ConfigurationFile).Load()!;
        seeded.Connectors["anthropic"].ApiKey.Should().Be("${env:ANTHROPIC_API_KEY}");

        // Once a connector is there, the file is not the template any more: a key added later is left to the dashboard.
        environment["OPENAI_API_KEY"] = "sk-seed-test";
        await using (RunningRouter? router = await RouterHost.StartAsync(settings, TextWriter.Null, b => b.Logging.ClearProviders(), environment.GetValueOrDefault))
        {
            router.Should().NotBeNull();
        }

        new JsonFileRouterConfigurationStore(ConfigurationFile).Load()!.Connectors.Keys.Should().Equal("anthropic");

        // Deleting the seeded connector leaves the template again; that sticks on the next start.
        new JsonFileRouterConfigurationStore(ConfigurationFile).Save(RouterConfiguration.FromJson(RouterHost.Template()));
        var later = new StringWriter();
        await using (RunningRouter? router = await RouterHost.StartAsync(settings, later, b => b.Logging.ClearProviders(), environment.GetValueOrDefault))
        {
            router.Should().NotBeNull();
        }

        new JsonFileRouterConfigurationStore(ConfigurationFile).Load()!.Connectors.Should().BeEmpty();
        later.ToString().Should().NotContain("connector");
    }

    /// <summary>
    /// Takes the Event Log provider's alias, so its configuration rules apply on any operating system. Its loggers are
    /// on for every level, so only the filter rules decide what is enabled.
    /// </summary>
    [ProviderAlias("EventLog")]
    private sealed class EventLogStandIn : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        public void Dispose()
        {
        }
    }
}
