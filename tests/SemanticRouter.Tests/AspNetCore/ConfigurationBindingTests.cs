using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

public class ConfigurationBindingTests
{
    private const string AppSettings = """
        {
          "SemanticRouter": {
            "DefaultSimilarityThreshold": 0.8,
            "FallbackTier": 2,
            "ComplexityShortCircuitThreshold": 2,
            "ComplexityTiers": [ { "MinScore": 0.5, "Tier": 3 } ],
            "ScoringStrategy": "Centroid",
            "Complexity": { "TokenSaturation": 1000, "ReasoningKeywords": [ "audit" ] },
            "Selection": { "Strategy": "MostCapable", "MaxAttempts": 2, "QuotaCooldown": "00:30:00" },
            "Routes": [
              { "Name": "chat", "Tier": 1, "Utterances": [ "hi", "hello" ] },
              { "Name": "pii", "Tier": 1, "LocalOnly": true, "SimilarityThreshold": 0.6, "RequiredCapabilities": [ "code" ], "Utterances": [ "ssn" ] }
            ],
            "Models": [
              { "Name": "lmstudio-qwen", "Connector": "lmstudio", "Model": "qwen/qwen3-8b", "Tier": 1, "IsLocal": true, "ContextWindow": 32768, "Capabilities": [ "code" ] },
              { "Name": "claude-cli", "Connector": "claude", "Model": "sonnet", "Tier": 4, "Priority": -1 },
              { "Name": "gpt-mini", "Connector": "openai", "Tier": 2, "InputCostPerMillionTokens": 0.4, "OutputCostPerMillionTokens": 1.6, "Enabled": false }
            ],
            "Connectors": {
              "lmstudio": { "Type": "OpenAICompatible", "BaseAddress": "http://localhost:1234" },
              "openai": { "BaseAddress": "https://api.openai.com", "ApiKey": "sk", "Headers": { "OpenAI-Organization": "org" } },
              "claude": { "Type": "Cli", "Preset": "ClaudeCode", "MaxConcurrency": 1, "Timeout": "00:02:00", "Arguments": [ "--effort", "low" ] }
            },
            "Embeddings": { "Provider": "OpenAICompatible", "Endpoint": "http://localhost:1234", "Model": "text-embedding-nomic-embed-text-v1.5" },
            "Middleware": { "Mode": "Dispatch", "PathPrefixes": [ "/v1/chat/completions" ], "MessageSelection": "AllMessages" }
          }
        }
        """;

    private static ServiceProvider Build()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(AppSettings)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(configuration.GetSection(SemanticRouterOptions.SectionName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Binds_routes_tiers_and_selection()
    {
        using ServiceProvider provider = Build();

        SemanticRouterOptions options = provider.GetRequiredService<IOptions<SemanticRouterOptions>>().Value;

        options.DefaultSimilarityThreshold.Should().Be(0.8f);
        options.FallbackTier.Should().Be(2);
        options.ComplexityTiers.Should().ContainSingle().Which.Should().BeEquivalentTo(new ComplexityTierRule { MinScore = 0.5, Tier = 3 });
        options.ScoringStrategy.Should().Be(RouteScoringStrategy.Centroid);
        options.Complexity.ReasoningKeywords.Should().Equal("audit");
        options.Selection.Strategy.Should().Be(SelectionStrategy.MostCapable);
        options.Selection.QuotaCooldown.Should().Be(TimeSpan.FromMinutes(30));
        options.Routes[0].Utterances.Should().Equal("hi", "hello");
        options.Routes[1].Should().Match<RouteDefinition>(r => r.LocalOnly && r.SimilarityThreshold == 0.6f);
        options.Routes[1].RequiredCapabilities.Should().Equal("code");
    }

    [Fact]
    public void Binds_the_model_catalog()
    {
        using ServiceProvider provider = Build();

        IList<ModelDefinition> models = provider.GetRequiredService<IOptions<SemanticRouterOptions>>().Value.Models;

        models.Should().HaveCount(3);
        models[0].Should().Match<ModelDefinition>(m => m.IsLocal && m.ContextWindow == 32768 && m.Model == "qwen/qwen3-8b");
        models[0].Capabilities.Should().Equal("code");
        models[1].Priority.Should().Be(-1);
        models[2].Should().Match<ModelDefinition>(m => !m.Enabled && m.InputCostPerMillionTokens == 0.4m && m.OutputCostPerMillionTokens == 1.6m);
    }

    [Fact]
    public void Binds_connectors()
    {
        using ServiceProvider provider = Build();

        IDictionary<string, ConnectorOptions> connectors = provider.GetRequiredService<IOptions<SemanticRouterConnectorsOptions>>().Value.Connectors;

        connectors.Keys.Should().BeEquivalentTo("lmstudio", "openai", "claude");
        connectors["LMSTUDIO"].BaseAddress.Should().Be(new Uri("http://localhost:1234"));
        connectors["openai"].Headers.Should().Contain("OpenAI-Organization", "org");
        connectors["claude"].Should().Match<ConnectorOptions>(c =>
            c.Type == ConnectorType.Cli && c.Preset == CliPreset.ClaudeCode && c.MaxConcurrency == 1 && c.Timeout == TimeSpan.FromMinutes(2));
        connectors["claude"].Arguments.Should().Equal("--effort", "low");
        connectors["claude"].RemoveEnvironmentVariables.Should().BeNull("the preset defaults apply unless overridden");
    }

    [Fact]
    public void Binds_middleware_and_embeddings()
    {
        using ServiceProvider provider = Build();

        PromptRouterMiddlewareOptions middleware = provider.GetRequiredService<IOptions<PromptRouterMiddlewareOptions>>().Value;
        middleware.Mode.Should().Be(PromptRouterMode.Dispatch);
        middleware.PathPrefixes.Should().Equal("/v1/chat/completions");
        middleware.MessageSelection.Should().Be(PromptMessageSelection.AllMessages);
        provider.GetRequiredService<IEmbeddingProvider>().Should().BeOfType<RuntimeEmbeddingProviderFacade>()
            .Which.Inner.Should().BeOfType<OpenAICompatibleEmbeddingProvider>();
        provider.GetRequiredService<IPromptRouter>().Models.Should().HaveCount(3);
    }

    [Fact]
    public void Builder_overrides_the_embedding_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting().UseHashingEmbeddings(dimensions: 64);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingProvider>().Should().BeOfType<RuntimeEmbeddingProviderFacade>()
            .Which.Inner.Should().BeOfType<HashingEmbeddingProvider>()
            .Which.Dimensions.Should().Be(64);
    }

    [Fact]
    public void Custom_registrations_take_precedence()
    {
        var custom = new HashingEmbeddingProvider(32);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingProvider>(custom);
        services.AddSemanticPromptRouting();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingProvider>().Should().BeSameAs(custom);
    }
}
