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
            "FallbackTarget": "Local",
            "ComplexityShortCircuitThreshold": 2,
            "ScoringStrategy": "Centroid",
            "Complexity": { "TokenSaturation": 1000, "ReasoningKeywords": [ "audit" ] },
            "Routes": [
              { "Name": "chat", "Target": "Local", "Utterances": [ "hi", "hello" ] },
              { "Name": "pii", "Target": "Local", "AllowEscalation": false, "SimilarityThreshold": 0.6, "Utterances": [ "ssn" ] }
            ],
            "Embeddings": { "Provider": "OpenAICompatible", "Endpoint": "http://localhost:11434", "Model": "nomic-embed-text" },
            "Middleware": { "Mode": "Dispatch", "PathPrefixes": [ "/v1/chat/completions" ], "MessageSelection": "AllMessages" },
            "Endpoints": {
              "Local": { "BaseAddress": "http://localhost:11434", "Model": "llama3.2" },
              "Cloud": { "BaseAddress": "https://api.anthropic.com", "Timeout": "00:00:30", "Headers": { "anthropic-version": "2023-06-01" } }
            }
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
    public void Binds_routes_and_thresholds()
    {
        using ServiceProvider provider = Build();

        SemanticRouterOptions options = provider.GetRequiredService<IOptions<SemanticRouterOptions>>().Value;

        options.DefaultSimilarityThreshold.Should().Be(0.8f);
        options.FallbackTarget.Should().Be(RouteTarget.Local);
        options.ComplexityShortCircuitThreshold.Should().Be(2);
        options.ScoringStrategy.Should().Be(RouteScoringStrategy.Centroid);
        options.Complexity.TokenSaturation.Should().Be(1000);
        options.Complexity.ReasoningKeywords.Should().Equal("audit");
        options.Routes.Should().HaveCount(2);
        options.Routes[0].Utterances.Should().Equal("hi", "hello");
        options.Routes[1].AllowEscalation.Should().BeFalse();
        options.Routes[1].SimilarityThreshold.Should().Be(0.6f);
    }

    [Fact]
    public void Binds_child_sections()
    {
        using ServiceProvider provider = Build();

        provider.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.Model.Should().Be("nomic-embed-text");

        PromptRouterMiddlewareOptions middleware = provider.GetRequiredService<IOptions<PromptRouterMiddlewareOptions>>().Value;
        middleware.Mode.Should().Be(PromptRouterMode.Dispatch);
        middleware.PathPrefixes.Should().Equal("/v1/chat/completions");
        middleware.MessageSelection.Should().Be(PromptMessageSelection.AllMessages);

        PromptDispatchOptions endpoints = provider.GetRequiredService<IOptions<PromptDispatchOptions>>().Value;
        endpoints.Local.BaseAddress.Should().Be(new Uri("http://localhost:11434"));
        endpoints.Cloud.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        endpoints.Cloud.Headers.Should().Contain("anthropic-version", "2023-06-01");
    }

    [Fact]
    public void Resolves_the_configured_embedding_provider()
    {
        using ServiceProvider provider = Build();

        provider.GetRequiredService<IEmbeddingProvider>().Should().BeOfType<OpenAICompatibleEmbeddingProvider>();
        provider.GetRequiredService<IPromptClassifier>().Should().BeOfType<SemanticPromptClassifier>();
    }

    [Fact]
    public void Builder_overrides_the_embedding_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting().UseHashingEmbeddings(dimensions: 64);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingProvider>()
            .Should().BeOfType<HashingEmbeddingProvider>()
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
