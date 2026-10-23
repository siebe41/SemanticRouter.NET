using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.AspNetCore.SecretsTests;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The fluent setup returned by <c>AddSemanticPromptRouting()</c>: each method registers what it says, over the
/// default, and refuses a missing argument.
/// </summary>
public sealed class SemanticRouterBuilderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sr-builder").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static (ServiceCollection Services, SemanticRouterBuilder Builder) NewBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return (services, services.AddSemanticPromptRouting());
    }

    [Fact]
    public void A_custom_embedding_provider_replaces_the_configured_one()
    {
        (ServiceCollection byType, SemanticRouterBuilder typed) = NewBuilder();
        typed.UseEmbeddingProvider<FixedEmbeddings>();
        (ServiceCollection byFactory, SemanticRouterBuilder factory) = NewBuilder();
        var instance = new FixedEmbeddings();
        factory.UseEmbeddingProvider(_ => instance);

        using ServiceProvider fromType = byType.BuildServiceProvider();
        using ServiceProvider fromFactory = byFactory.BuildServiceProvider();

        fromType.GetRequiredService<IEmbeddingProvider>().Should().BeOfType<FixedEmbeddings>();
        fromFactory.GetRequiredService<IEmbeddingProvider>().Should().BeSameAs(instance);
        byType.Count(d => d.ServiceType == typeof(IEmbeddingProvider)).Should().Be(1, "it replaces the default, not adds to it");
    }

    [Fact]
    public void OpenAI_compatible_embeddings_are_selected_and_configured()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        builder.UseOpenAICompatibleEmbeddings(o => o.Model = "nomic-embed-text");
        (ServiceCollection plain, SemanticRouterBuilder plainBuilder) = NewBuilder();
        plainBuilder.UseOpenAICompatibleEmbeddings();

        using ServiceProvider provider = services.BuildServiceProvider();
        using ServiceProvider plainProvider = plain.BuildServiceProvider();

        EmbeddingProviderOptions options = provider.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
        (options.Provider, options.Model).Should().Be((EmbeddingProviderKinds.OpenAICompatible, "nomic-embed-text"));
        plainProvider.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.Provider.Should().Be(EmbeddingProviderKinds.OpenAICompatible);
    }

    [Fact]
    public void Files_for_the_evaluation_set_model_families_and_client_turns_are_used_where_given()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        string evaluation = Path.Combine(_directory, "labels.jsonl");
        builder
            .UseEvaluationSetFile(evaluation)
            .UseModelFamilyFile(Path.Combine(_directory, "families.json"))
            .UseClientTurnFile(Path.Combine(_directory, "turns.json"));

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEvaluationSetStore>().Should().BeOfType<JsonLinesEvaluationSetStore>().Which.Location.Should().Be(evaluation);
        provider.GetRequiredService<ModelFamilyFile>().Path.Should().Be(Path.Combine(_directory, "families.json"));
        provider.GetRequiredService<ClientTurnFile>().Path.Should().Be(Path.Combine(_directory, "turns.json"));
    }

    [Fact]
    public void Model_family_and_client_turn_paths_are_made_absolute()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        builder.UseModelFamilyFile("families.json").UseClientTurnFile("turns.json");

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ModelFamilyFile>().Path.Should().Be(Path.GetFullPath("families.json"));
        provider.GetRequiredService<ClientTurnFile>().Path.Should().Be(Path.GetFullPath("turns.json"));
    }

    [Fact]
    public void The_secret_file_sits_next_to_the_configuration_file_unless_a_path_is_given()
    {
        string configuration = Path.Combine(_directory, "router.json");
        (ServiceCollection beside, SemanticRouterBuilder besideBuilder) = NewBuilder();
        besideBuilder.UseConfigurationFile(configuration).UseSecretFile();
        (ServiceCollection given, SemanticRouterBuilder givenBuilder) = NewBuilder();
        givenBuilder.UseConfigurationFile(configuration).UseSecretFile(Path.Combine(_directory, "keys.json"));
        (ServiceCollection alone, SemanticRouterBuilder aloneBuilder) = NewBuilder();
        aloneBuilder.UseSecretFile();

        using ServiceProvider besideProvider = beside.BuildServiceProvider();
        using ServiceProvider givenProvider = given.BuildServiceProvider();
        using ServiceProvider aloneProvider = alone.BuildServiceProvider();

        besideProvider.GetRequiredService<IRouterSecretStore>().Location.Should().Be(Path.Combine(_directory, "router.secrets.json"));
        givenProvider.GetRequiredService<IRouterSecretStore>().Location.Should().Be(Path.Combine(_directory, "keys.json"));
        aloneProvider.GetRequiredService<IRouterSecretStore>().Location.Should().Be(Path.GetFullPath("semantic-router.secrets.json"));
    }

    [Fact]
    public void Custom_secret_and_configuration_stores_and_connectors_are_registered()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        builder.UseSecretStore<MemorySecretStore>().UseConfigurationStore<JsonFileRouterConfigurationStore>().AddConnector<UnavailableConnector>();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRouterSecretStore>().Should().BeOfType<MemorySecretStore>();
        services.Where(d => d.ServiceType == typeof(IRouterConfigurationStore)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<JsonFileRouterConfigurationStore>();
        services.Should().Contain(d => d.ServiceType == typeof(IChatConnector) && d.ImplementationType == typeof(UnavailableConnector) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void Memory_sources_are_added_by_configuration_type_and_instance()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        var graph = new KnowledgeGraphMemorySource("graph", Path.Combine(_directory, "memory.jsonl"));
        builder
            .AddMemorySource("vault", MemorySourceType.Markdown, "~/notes")
            .AddMemorySource<EmptyMemorySource>()
            .AddMemorySource(graph);

        using ServiceProvider provider = services.BuildServiceProvider();

        MemorySourceOptions configured = provider.GetRequiredService<IOptions<MemoryOptions>>().Value.Sources.Should().ContainSingle().Subject;
        (configured.Name, configured.Type, configured.Path).Should().Be(("vault", MemorySourceType.Markdown, "~/notes"));
        IMemorySource[] sources = [.. provider.GetServices<IMemorySource>()];
        sources.Should().ContainSingle(s => s is EmptyMemorySource);
        sources.Should().Contain(graph);
    }

    [Fact]
    public void A_model_added_in_code_joins_the_catalog()
    {
        (ServiceCollection services, SemanticRouterBuilder builder) = NewBuilder();
        var model = new ModelDefinition { Name = "local-small", Tier = 1, Connector = "local" };
        builder.AddModel(model);

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<SemanticRouterOptions>>().Value.Models.Should().Contain(model);
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        (_, SemanticRouterBuilder builder) = NewBuilder();

        FluentActions.Invoking(() => builder.UseMachinePolicy(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.UseEmbeddingProvider((Func<IServiceProvider, IEmbeddingProvider>)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.UseConfigurationFile(" ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.UseEvaluationSetFile("")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.UseModelFamilyFile(" ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.UseClientTurnFile(null!)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.AddConnector("", _ => { })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.AddConnector("local", null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.AddMemorySource(" ", MemorySourceType.Markdown)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.AddMemorySource((IMemorySource)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.AddModel(null!)).Should().Throw<ArgumentNullException>();
    }

    private sealed class FixedEmbeddings : IEmbeddingProvider
    {
        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0 });

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([.. texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 0 })]);
    }

    private sealed class EmptyMemorySource : IMemorySource
    {
        public string Name => "empty";

        public string Kind => "Test";

        public string? Location => null;

        public Task<IReadOnlyList<MemoryDocument>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MemoryDocument>>([]);
    }
}
