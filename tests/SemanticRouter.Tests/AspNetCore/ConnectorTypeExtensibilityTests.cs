using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Connector types and embedding providers are names, so a package outside this repository adds one with a factory and
/// no change to SemanticRouter.AspNetCore (#201).
/// </summary>
/// <remarks>
/// The router remembers the factories' type names for the whole process (so <c>"copilot"</c> binds as <c>Copilot</c>
/// everywhere), so each test that registers a factory gives it a name no other test uses with different traits.
/// </remarks>
public sealed class ConnectorTypeExtensibilityTests : RouterHostTest
{
    // Lower case on purpose: type names match case-insensitively, as the enum they replace did.
    private static string Settings(string type, bool allowRemoteClients = false) => $$"""
        {
          "SemanticRouter": {
            "Connectors": {
              "aws": { "Type": "{{type}}", "MaxConcurrency": 2, "AllowRemoteClients": {{(allowRemoteClients ? "true" : "false")}} }
            }
          }
        }
        """;

    private readonly BedrockFactory _bedrock = new();

    public ConnectorTypeExtensibilityTests() => ClassifyAs(tier: 4);

    private Task<HttpClient> StartBedrockAsync(
        bool withFactory, string type = "bedrock", IChatConnectorFactory? other = null, bool remote = false, bool allowRemoteClients = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.AllowUnauthenticatedRemoteClients = remote;
            },
            o =>
            {
                o.Models.Remove(o.Models.Single(m => m.Name == "cloud"));
                ModelDefinition bedrock = Model("bedrock-sonnet", 4, connector: "aws");
                bedrock.Model = "anthropic.claude-sonnet";
                o.Models.Add(bedrock);
            },
            s =>
            {
                IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Settings(type, allowRemoteClients)))).Build();
                s.Configure<SemanticRouterConnectorsOptions>(configuration.GetSection(SemanticRouterOptions.SectionName));
                if (withFactory)
                {
                    s.AddSingleton<IChatConnectorFactory>(_bedrock);
                }

                if (other is not null)
                {
                    s.AddSingleton(other);
                }
            },
            beforeRouter: remote
                ? app => app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
                    return next(context);
                })
                : null);

    [Fact]
    public async Task A_third_party_factory_serves_a_type_bound_from_json()
    {
        HttpClient client = await StartBedrockAsync(withFactory: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"bedrock"}""");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("bedrock-sonnet");
        _bedrock.Created.Should().ContainSingle().Which.Should().Be(("aws", "Bedrock", 2), "a packaged name binds in its package's spelling");
        _bedrock.Served.Should().Equal("anthropic.claude-sonnet");
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should().NotContain(w => w.Contains("aws", StringComparison.Ordinal));
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task A_packaged_type_that_runs_a_program_serves_other_machines_only_when_allowed(bool remote, bool allowRemoteClients, bool served)
    {
        // #338: the Bedrock connector checks nothing itself, and runs a program as far as the router knows (its factory
        // does not declare UsesBaseAddress); the router keeps other machines away from it unless AllowRemoteClients.
        HttpClient client = await StartBedrockAsync(withFactory: true, remote: remote, allowRemoteClients: allowRemoteClients);

        await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        _bedrock.Served.Should().HaveCount(served ? 1 : 0);
        if (!served)
        {
            Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Detail.Should()
                .Contain("bedrock-sonnet: skipped (connector 'aws' runs a program on this machine and serves only requests from it (AllowRemoteClients is false))");
        }
    }

    [Theory]
    [InlineData(true, new[] { "Bedrock" })]
    [InlineData(false, new string[0])]
    public async Task The_dashboard_offers_the_built_in_types_and_those_installed_packages_provide(bool withFactory, string[] installed)
    {
        HttpClient client = await StartBedrockAsync(withFactory);

        JsonElement types = JsonDocument.Parse(await client.GetStringAsync(new Uri("/router/api/status", UriKind.Relative))).RootElement.GetProperty("connectorTypes");

        types.GetProperty("builtIn").EnumerateArray().Select(t => t.GetString()).Should().Equal(ConnectorTypes.BuiltIn);
        types.GetProperty("installed").EnumerateArray().Select(t => t.GetString()).Should().Equal(installed);
        types.GetProperty("usesBaseAddress").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task The_dashboard_learns_which_installed_types_call_their_base_address()
    {
        HttpClient client = await StartBedrockAsync(withFactory: true, other: new GatewayFactory());

        JsonElement types = JsonDocument.Parse(await client.GetStringAsync(new Uri("/router/api/status", UriKind.Relative))).RootElement.GetProperty("connectorTypes");

        types.GetProperty("installed").EnumerateArray().Select(t => t.GetString()).Should().Equal("Bedrock", "LocalGateway");
        types.GetProperty("usesBaseAddress").EnumerateArray().Select(t => t.GetString()).Should().Equal("LocalGateway");
    }

    [Fact]
    public async Task A_type_no_package_provides_is_skipped_with_the_reason()
    {
        // A name no test registers a factory for, so it stays as written.
        HttpClient client = await StartBedrockAsync(withFactory: false, type: "vertex");

        await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        const string reason = "no installed connector package provides connector type 'vertex' (built in: OpenAICompatible, Cli, Anthropic, Responses)";
        Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single().Detail.Should().Contain(reason);
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should().Contain($"Connector 'aws' is skipped: {reason}.");
        RouterCheck check = (await RouterDoctor.CheckConnectorAsync(Services, "aws")).Single();
        check.Status.Should().Be(RouterCheckStatus.Failed);
        check.Message.Should().StartWith(reason).And.Contain("SemanticRouter:Connectors:aws:Type");
    }

    [Fact]
    public void Built_in_names_bind_in_any_case_and_keep_their_canonical_spelling()
    {
        new ConnectorOptions { Type = "cli" }.Type.Should().Be(ConnectorTypes.Cli);
        new ConnectorOptions { Type = " RESPONSES " }.Type.Should().Be(ConnectorTypes.Responses);
        new ConnectorOptions { Type = null }.Type.Should().Be(ConnectorTypes.OpenAICompatible, "an empty type is the default, as before");
        new ConnectorOptions { Type = "Bedrock" }.Should().Match<ConnectorOptions>(c => c.Type == "Bedrock" && c.IsType("BEDROCK"));
        new EmbeddingProviderOptions { Provider = "hashing" }.Provider.Should().Be(EmbeddingProviderKinds.Hashing);
        new EmbeddingProviderOptions { Provider = "" }.Provider.Should().Be(EmbeddingProviderKinds.Hashing);

        // Configuration files and the dashboard's JSON keep the names the enum wrote.
        JsonSerializer.Serialize(new ConnectorOptions { Type = "anthropic" }).Should().Contain("\"Type\":\"Anthropic\"");
        JsonSerializer.Deserialize<ConnectorOptions>("""{"Type":"cli"}""")!.Type.Should().Be(ConnectorTypes.Cli);
    }

    [Fact]
    public void A_packaged_type_runs_a_program_unless_its_factory_says_it_calls_its_address()
    {
        ConnectorTypes.Register([new GatewayFactory()]);
        var program = new ConnectorOptions { Type = "Bedrock", BaseAddress = new Uri("http://localhost:8080") };
        var http = new ConnectorOptions { Type = "localgateway", BaseAddress = new Uri("https://gateway.example.com") };
        MachinePolicy offline = MachinePolicy.FromJson("""{"Offline": true}""");

        program.RunsProgram.Should().BeTrue("its factory does not say it calls BaseAddress, so the address proves nothing");
        program.EffectiveTimeout.Should().Be(TimeSpan.FromMinutes(5));
        offline.Blocks(program).Should().Contain("offline mode");
        RouterDashboardEndpoints.Destination(program).Should().Be("Bedrock");

        http.Type.Should().Be("LocalGateway");
        http.RunsProgram.Should().BeFalse();
        http.EffectiveTimeout.Should().Be(TimeSpan.FromSeconds(100));
        offline.Blocks(http).Should().NotBeNull("an offline policy refuses the address like any HTTP connector's");
        offline.Blocks(new ConnectorOptions { Type = "LocalGateway", BaseAddress = new Uri("http://localhost:8080") }).Should().BeNull();
        RouterDashboardEndpoints.Destination(http).Should().Be("https://gateway.example.com:443");
        RouterConfiguration.FromJson("""{"Connectors": {"gw": {"Type": "LocalGateway"}}}""").GetDocumentValidationErrors()
            .Should().Contain("Connector 'gw' needs an absolute BaseAddress.");
    }

    [Fact]
    public void A_base_address_does_not_take_copilot_past_offline_mode_or_local_hosts()
    {
        // The Copilot SDK never reads BaseAddress: a loopback one must not make it look like a local server.
        var configuration = new RouterConfiguration();
        configuration.Connectors["copilot"] = new ConnectorOptions { Type = CopilotConnector.Type, BaseAddress = new Uri("http://localhost:1") };
        configuration.Models.Add(Model("copilot-gpt", 4, local: true, connector: "copilot"));

        MachinePolicy.FromJson("""{"Offline": true}""").Blocks(configuration.Connectors["copilot"])
            .Should().Be("Copilot connectors reach their vendor, so they are off in offline mode (your organization's policy)");
        MachinePolicy.FromJson("""{"LocalHosts": [], "RequireLocalOnly": true}""").Enforce(configuration).Should().Equal(
            "Model 'copilot-gpt' does not count as local: its connector is not a server in your organization's policy.",
            "Model 'copilot-gpt' is turned off: your organization's policy allows local models only.");
    }

    [Fact]
    public void A_policy_turns_off_a_packaged_type_by_name_in_any_case()
    {
        MachinePolicy policy = MachinePolicy.FromJson("""{"DisabledConnectorTypes": [" SAGEMAKER ", "Cli "]}""");

        policy.Blocks(new ConnectorOptions { Type = "SageMaker", BaseAddress = new Uri("https://sagemaker.example.com") })
            .Should().Be("SageMaker connectors are turned off by your organization's policy");
        policy.Blocks(new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode })
            .Should().Be("Cli connectors are turned off by your organization's policy", "an entry is read without its spaces, as a type is");
        policy.Blocks(new ConnectorOptions { Type = ConnectorTypes.Anthropic, BaseAddress = new Uri("https://api.anthropic.com") }).Should().BeNull();
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"DisabledConnectorTypes": [" "]}"""))
            .Should().Throw<InvalidDataException>().WithMessage("*DisabledConnectorTypes may not hold an empty name*");
    }

    [Fact]
    public async Task A_policy_entry_no_package_provides_is_reported()
    {
        await StartAsync(services: s => s.AddSingleton(MachinePolicy.FromJson("""{"DisabledConnectorTypes": ["Cli ", " Copliot"]}""")));

        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should()
            .Contain("The machine policy turns off connector type 'Copliot', which no installed package provides; check its spelling.")
            .And.NotContain(w => w.Contains("'Cli", StringComparison.Ordinal));

        // The doctor fails it: a misspelt entry turns nothing off, so the policy is not doing what it says.
        IReadOnlyList<RouterCheck> checks = await RouterDoctor.CheckAsync(Services);
        checks.Should().ContainSingle(c => c.Subject == "machine policy").Which.Should().Match<RouterCheck>(c =>
            c.Status == RouterCheckStatus.Failed && c.Message.StartsWith("DisabledConnectorTypes names 'Copliot'", StringComparison.Ordinal));
        checks.Should().NotContain(c => c.Message.Contains("check its spelling;", StringComparison.Ordinal), "the failure replaces the warning");
    }

    [Fact]
    public async Task A_third_party_embedding_provider_is_chosen_by_name()
    {
        var factory = new NamedEmbeddingFactory("Custom");
        await StartAsync(services: s => s.AddSingleton<IEmbeddingProviderFactory>(factory));
        RouterRuntimeProvider runtime = Services.GetRequiredService<RouterRuntimeProvider>();

        runtime.CreateEmbeddingProvider(new EmbeddingProviderOptions { Provider = "custom" }).Should().BeSameAs(factory.Provider);
        IEmbeddingProvider missing = runtime.CreateEmbeddingProvider(new EmbeddingProviderOptions { Provider = "Absent" });
        await FluentActions.Awaiting(async () => await missing.GenerateEmbeddingAsync("hi", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No installed package provides the embedding provider 'Absent' (built in: Hashing, OpenAICompatible).");
    }

    [Fact]
    public async Task The_dashboard_learns_which_packaged_embedding_providers_run_in_process()
    {
        HttpClient client = await StartAsync(services: s =>
        {
            s.AddSingleton<IEmbeddingProviderFactory>(new NamedEmbeddingFactory("InHouseModel", runsInProcess: true));
            s.AddSingleton<IEmbeddingProviderFactory>(new NamedEmbeddingFactory("RemoteEmbed"));
        });

        JsonElement embeddings = JsonDocument.Parse(await client.GetStringAsync(new Uri("/router/api/status", UriKind.Relative))).RootElement.GetProperty("embeddings");

        embeddings.GetProperty("available").EnumerateArray().Select(k => k.GetString()).Should().Contain(["InHouseModel", "RemoteEmbed"]);
        embeddings.GetProperty("runsInProcess").EnumerateArray().Select(k => k.GetString()).Should().Equal("InHouseModel");
    }

    [Fact]
    public void A_packaged_embedding_provider_keeps_prompts_local_only_in_process_or_when_declared_local()
    {
        EmbeddingProviderKinds.Register([new NamedEmbeddingFactory("InHouseModel", runsInProcess: true)]);

        new EmbeddingProviderOptions { Provider = "RemoteEmbed" }.KeepsPromptsLocal.Should().BeFalse("a factory that does not say it runs here may send text anywhere");
        new EmbeddingProviderOptions { Provider = "RemoteEmbed", IsLocal = true }.KeepsPromptsLocal.Should().BeTrue();
        new EmbeddingProviderOptions { Provider = "inhousemodel" }.Should()
            .Match<EmbeddingProviderOptions>(e => e.Provider == "InHouseModel" && e.KeepsPromptsLocal);
    }

    [Theory]
    [InlineData("""{"RequireLocalOnly": true}""", null, null, "The RemoteEmbed embeddings are not allowed by your organization's policy, which allows local models only: routing uses the in-process hashing embeddings.")]
    [InlineData("""{"Offline": true}""", true, "http://localhost:9000", "The RemoteEmbed embeddings are not used (RemoteEmbed embeddings may reach their vendor, so they are off in offline mode (your organization's policy)): routing uses the in-process hashing embeddings.")]
    [InlineData("""{"AllowedEndpoints": ["embed.example.com"]}""", null, "https://other.example.com", "The RemoteEmbed embeddings are not used (other.example.com is not an allowed endpoint in your organization's policy): routing uses the in-process hashing embeddings.")]
    [InlineData("""{"AllowedEndpoints": ["embed.example.com"]}""", null, null, "The RemoteEmbed embeddings are not used (RemoteEmbed embeddings name no Endpoint, and your organization's policy allows only listed endpoints): routing uses the in-process hashing embeddings.")]
    public void The_policy_replaces_a_packaged_embedding_provider_it_cannot_vouch_for(string policy, bool? isLocal, string? endpoint, string change)
    {
        var configuration = new RouterConfiguration();
        configuration.Embeddings.Provider = "RemoteEmbed";
        configuration.Embeddings.IsLocal = isLocal;
        configuration.Embeddings.Endpoint = endpoint is null ? null : new Uri(endpoint);

        MachinePolicy.FromJson(policy).Enforce(configuration).Should().Contain(change);
        configuration.Embeddings.Provider.Should().Be(EmbeddingProviderKinds.Hashing);
    }

    [Fact]
    public void The_policy_keeps_a_packaged_embedding_provider_it_can_vouch_for()
    {
        EmbeddingProviderKinds.Register([new NamedEmbeddingFactory("InHouseModel", runsInProcess: true)]);
        var inProcess = new RouterConfiguration();
        inProcess.Embeddings.Provider = "InHouseModel";
        var listed = new RouterConfiguration();
        listed.Embeddings.Provider = "RemoteEmbed";
        listed.Embeddings.Endpoint = new Uri("https://embed.example.com");

        MachinePolicy.FromJson("""{"Offline": true, "RequireLocalOnly": true}""").Enforce(inProcess).Should().BeEmpty();
        inProcess.Embeddings.Provider.Should().Be("InHouseModel");
        MachinePolicy.FromJson("""{"AllowedEndpoints": ["embed.example.com"]}""").Enforce(listed).Should().BeEmpty();
        listed.Embeddings.Provider.Should().Be("RemoteEmbed");
    }

    private sealed class BedrockFactory : IChatConnectorFactory
    {
        public List<(string Name, string Type, int MaxConcurrency)> Created { get; } = [];

        public List<string?> Served { get; } = [];

        public string Type => "Bedrock";

        public IChatConnector Create(string name, ConnectorOptions options)
        {
            Created.Add((name, options.Type, options.MaxConcurrency));
            return new BedrockConnector(name, Served);
        }
    }

    private sealed class BedrockConnector(string name, List<string?> served) : IChatConnector
    {
        public string Name => name;

        public async Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken)
        {
            served.Add(request.Model.Model);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"from":"bedrock"}""", cancellationToken);
            return ConnectorResult.Completed;
        }
    }

    private sealed class GatewayFactory : IChatConnectorFactory
    {
        public string Type => "LocalGateway";

        public bool UsesBaseAddress => true;

        public IChatConnector Create(string name, ConnectorOptions options) => new BedrockConnector(name, []);
    }

    private sealed class NamedEmbeddingFactory(string kind, bool runsInProcess = false) : IEmbeddingProviderFactory
    {
        public IEmbeddingProvider Provider { get; } = new HashingEmbeddingProvider();

        public string Kind => kind;

        public bool RunsInProcess => runsInProcess;

        public IEmbeddingProvider Create(EmbeddingProviderOptions options) => Provider;
    }
}
