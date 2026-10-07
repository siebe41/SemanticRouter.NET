using Moq;

namespace SemanticRouter.Tests.Support;

/// <summary>Builds Moq embedding providers backed by an explicit text → vector table.</summary>
internal static class TestEmbeddings
{
    public static Mock<IEmbeddingProvider> FromTable(IReadOnlyDictionary<string, float[]> table)
    {
        var mock = new Mock<IEmbeddingProvider>(MockBehavior.Strict);

        mock.Setup(p => p.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken _) => ValueTask.FromResult<ReadOnlyMemory<float>>(Lookup(table, text)));

        mock.Setup(p => p.GenerateEmbeddingsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<string> texts, CancellationToken _) =>
                ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                    texts.Select(t => new ReadOnlyMemory<float>(Lookup(table, t))).ToArray()));

        return mock;
    }

    private static float[] Lookup(IReadOnlyDictionary<string, float[]> table, string text) =>
        table.TryGetValue(text, out float[]? vector)
            ? vector
            : throw new KeyNotFoundException($"No test embedding registered for '{text}'.");

    public static RouteDefinition Route(string name, int tier, params string[] utterances)
    {
        var route = new RouteDefinition { Name = name, Tier = tier };
        foreach (string utterance in utterances)
        {
            route.Utterances.Add(utterance);
        }

        return route;
    }

    public static ModelDefinition Model(
        string name, int tier, decimal costPerMillion = 0, bool local = false, string connector = "test", params string[] capabilities)
    {
        var model = new ModelDefinition
        {
            Name = name,
            Connector = connector,
            Tier = tier,
            IsLocal = local,
            InputCostPerMillionTokens = costPerMillion,
            OutputCostPerMillionTokens = costPerMillion,
        };
        foreach (string capability in capabilities)
        {
            model.Capabilities.Add(capability);
        }

        return model;
    }

    public static SemanticRouterOptions Options(params RouteDefinition[] routes)
    {
        var options = new SemanticRouterOptions();
        foreach (RouteDefinition route in routes)
        {
            options.Routes.Add(route);
        }

        return options;
    }
}
