using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Connector types and embedding providers are names, so a package outside this repository adds one with a factory and
/// no change to SemanticRouter.AspNetCore (#201).
/// </summary>
public sealed class ConnectorTypeExtensibilityTests : RouterHostTest
{
    // Lower case on purpose: type names match case-insensitively, as the enum they replace did.
    private const string Settings = """
        {
          "SemanticRouter": {
            "Connectors": {
              "aws": { "Type": "bedrock", "MaxConcurrency": 2 }
            }
          }
        }
        """;

    private readonly BedrockFactory _bedrock = new();

    public ConnectorTypeExtensibilityTests() => ClassifyAs(tier: 4);

    private Task<HttpClient> StartBedrockAsync(bool withFactory) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
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
                IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Settings))).Build();
                s.Configure<SemanticRouterConnectorsOptions>(configuration.GetSection(SemanticRouterOptions.SectionName));
                if (withFactory)
                {
                    s.AddSingleton<IChatConnectorFactory>(_bedrock);
                }
            });

    [Fact]
    public async Task A_third_party_factory_serves_a_type_bound_from_json()
    {
        HttpClient client = await StartBedrockAsync(withFactory: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"bedrock"}""");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("bedrock-sonnet");
        _bedrock.Created.Should().ContainSingle().Which.Should().Be(("aws", "bedrock", 2));
        _bedrock.Served.Should().Equal("anthropic.claude-sonnet");
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should().NotContain(w => w.Contains("aws", StringComparison.Ordinal));
        CloudHttp.Requests.Should().BeEmpty();
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
    }

    [Fact]
    public async Task A_type_no_package_provides_is_skipped_with_the_reason()
    {
        HttpClient client = await StartBedrockAsync(withFactory: false);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().NotContain("bedrock\"}");
        const string reason = "no installed connector package provides connector type 'bedrock' (built in: OpenAICompatible, Cli, Anthropic, Responses)";
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
    public void A_packaged_type_is_an_http_connector_with_an_address_and_a_program_without_one()
    {
        var program = new ConnectorOptions { Type = "Bedrock" };
        var http = new ConnectorOptions { Type = "Bedrock", BaseAddress = new Uri("https://bedrock.example.com") };
        MachinePolicy offline = MachinePolicy.FromJson("""{"Offline": true}""");

        program.RunsProgram.Should().BeTrue();
        program.EffectiveTimeout.Should().Be(TimeSpan.FromMinutes(5));
        offline.Blocks(program).Should().Contain("offline mode");
        http.RunsProgram.Should().BeFalse();
        http.EffectiveTimeout.Should().Be(TimeSpan.FromSeconds(100));
        offline.Blocks(http).Should().NotBeNull("an offline policy refuses the address like any HTTP connector's");
        offline.Blocks(new ConnectorOptions { Type = "Bedrock", BaseAddress = new Uri("http://localhost:8080") }).Should().BeNull();
        RouterDashboardEndpoints.Destination(program).Should().Be("Bedrock");
        RouterDashboardEndpoints.Destination(http).Should().Be("https://bedrock.example.com:443");
    }

    [Fact]
    public void A_policy_turns_off_a_packaged_type_by_name_in_any_case()
    {
        MachinePolicy policy = MachinePolicy.FromJson("""{"DisabledConnectorTypes": ["BEDROCK"]}""");

        policy.Blocks(new ConnectorOptions { Type = "bedrock", BaseAddress = new Uri("https://bedrock.example.com") })
            .Should().Be("bedrock connectors are turned off by your organization's policy");
        policy.Blocks(new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode }).Should().BeNull();
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"DisabledConnectorTypes": [" "]}"""))
            .Should().Throw<InvalidDataException>().WithMessage("*DisabledConnectorTypes may not hold an empty name*");
    }

    [Fact]
    public async Task A_policy_entry_no_package_provides_is_reported()
    {
        await StartAsync(services: s => s.AddSingleton(MachinePolicy.FromJson("""{"DisabledConnectorTypes": ["Cli", "Copliot"]}""")));

        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should()
            .Contain("The machine policy turns off connector type 'Copliot', which no installed package provides; check its spelling.")
            .And.NotContain(w => w.Contains("'Cli'", StringComparison.Ordinal));
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

    private sealed class NamedEmbeddingFactory(string kind) : IEmbeddingProviderFactory
    {
        public IEmbeddingProvider Provider { get; } = new HashingEmbeddingProvider();

        public string Kind => kind;

        public IEmbeddingProvider Create(EmbeddingProviderOptions options) => Provider;
    }
}
