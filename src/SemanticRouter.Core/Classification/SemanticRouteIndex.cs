namespace SemanticRouter;

/// <summary>
/// An immutable, pre-normalized embedding matrix for a set of routes.
/// </summary>
/// <remarks>
/// Utterance (or centroid) vectors are stored unit-length in one contiguous row-major array, so
/// scoring a prompt is a cache-friendly sweep of SIMD dot products with a single norm computation
/// for the query. Instances are thread-safe.
/// </remarks>
public sealed class SemanticRouteIndex
{
    private readonly RouteDefinition[] _routes;
    private readonly float[] _matrix;
    private readonly int[] _rowToRoute;

    private SemanticRouteIndex(RouteDefinition[] routes, float[] matrix, int[] rowToRoute, int dimensions)
    {
        _routes = routes;
        _matrix = matrix;
        _rowToRoute = rowToRoute;
        Dimensions = dimensions;
    }

    /// <summary>The indexed routes, in configuration order.</summary>
    public IReadOnlyList<RouteDefinition> Routes => _routes;

    /// <summary>Embedding dimensionality, or 0 for an empty index.</summary>
    public int Dimensions { get; }

    /// <summary>Number of stored vectors (utterances, or routes when using centroids).</summary>
    public int VectorCount => _rowToRoute.Length;

    /// <summary>Embeds every route utterance (in one batch) and builds an index.</summary>
    /// <exception cref="InvalidOperationException">The provider returned inconsistent embeddings.</exception>
    public static async Task<SemanticRouteIndex> BuildAsync(
        IEnumerable<RouteDefinition> routes,
        IEmbeddingProvider embeddingProvider,
        RouteScoringStrategy strategy = RouteScoringStrategy.MaxSimilarity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(embeddingProvider);

        RouteDefinition[] routeArray = routes.ToArray();
        var texts = new List<string>();
        var owners = new List<int>();
        for (int r = 0; r < routeArray.Length; r++)
        {
            foreach (string utterance in routeArray[r].Utterances)
            {
                if (!string.IsNullOrWhiteSpace(utterance))
                {
                    texts.Add(utterance.Trim());
                    owners.Add(r);
                }
            }
        }

        if (texts.Count == 0)
        {
            return new SemanticRouteIndex(routeArray, [], [], 0);
        }

        IReadOnlyList<ReadOnlyMemory<float>> embeddings =
            await embeddingProvider.GenerateEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);

        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"Embedding provider returned {embeddings.Count} vectors for {texts.Count} utterances.");
        }

        int dimensions = embeddings[0].Length;
        if (dimensions == 0)
        {
            throw new InvalidOperationException("Embedding provider returned an empty vector.");
        }

        var rows = new float[texts.Count * dimensions];
        for (int i = 0; i < embeddings.Count; i++)
        {
            if (embeddings[i].Length != dimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding provider returned vectors of inconsistent size ({embeddings[i].Length} vs {dimensions}).");
            }

            Span<float> row = rows.AsSpan(i * dimensions, dimensions);
            embeddings[i].Span.CopyTo(row);
            VectorMath.Normalize(row);
        }

        if (strategy == RouteScoringStrategy.MaxSimilarity)
        {
            return new SemanticRouteIndex(routeArray, rows, owners.ToArray(), dimensions);
        }

        // Centroid: average the unit vectors of each route, then re-normalize.
        var centroids = new float[routeArray.Length * dimensions];
        for (int i = 0; i < owners.Count; i++)
        {
            Span<float> centroid = centroids.AsSpan(owners[i] * dimensions, dimensions);
            ReadOnlySpan<float> row = rows.AsSpan(i * dimensions, dimensions);
            for (int d = 0; d < dimensions; d++)
            {
                centroid[d] += row[d];
            }
        }

        for (int r = 0; r < routeArray.Length; r++)
        {
            VectorMath.Normalize(centroids.AsSpan(r * dimensions, dimensions));
        }

        return new SemanticRouteIndex(routeArray, centroids, Enumerable.Range(0, routeArray.Length).ToArray(), dimensions);
    }

    /// <summary>
    /// Scores <paramref name="embedding"/> against every route and returns the scores best first.
    /// Ties keep configuration order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedding's size does not match the index.</exception>
    public RouteScore[] Score(ReadOnlySpan<float> embedding)
    {
        if (_routes.Length == 0)
        {
            return [];
        }

        if (embedding.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Prompt embedding has {embedding.Length} dimensions but the route index was built with {Dimensions}. " +
                "The embedding provider must use the same model for utterances and prompts.");
        }

        var best = new float[_routes.Length];
        best.AsSpan().Fill(-1f);

        float queryNorm = VectorMath.Norm(embedding);
        if (queryNorm > 0f && float.IsFinite(queryNorm))
        {
            // Rows are unit length, so cosine(row, q) = dot(row, q) / |q|.
            float inverseNorm = 1f / queryNorm;
            ReadOnlySpan<float> matrix = _matrix;
            for (int row = 0; row < _rowToRoute.Length; row++)
            {
                float similarity = VectorMath.Dot(matrix.Slice(row * Dimensions, Dimensions), embedding) * inverseNorm;
                int route = _rowToRoute[row];
                if (similarity > best[route])
                {
                    best[route] = similarity;
                }
            }
        }
        else
        {
            best.AsSpan().Clear();
        }

        var order = new int[_routes.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int bySimilarity = best[b].CompareTo(best[a]);
            return bySimilarity != 0 ? bySimilarity : a.CompareTo(b);
        });

        var scores = new RouteScore[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            int route = order[i];
            scores[i] = new RouteScore(_routes[route], Math.Clamp(best[route], -1f, 1f));
        }

        return scores;
    }
}
