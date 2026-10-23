using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>A kind of problem in a route's utterances.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RouteHealthKind>))]
public enum RouteHealthKind
{
    /// <summary>The route has too few utterances to form a robust cluster.</summary>
    FewUtterances = 0,

    /// <summary>Two utterances of the route say the same thing: one adds nothing.</summary>
    DuplicateUtterances = 1,

    /// <summary>An utterance is nearly identical to one of another route: whichever scores higher wins by noise.</summary>
    ConflictingUtterances = 2,

    /// <summary>
    /// Many of a route's utterances match another route almost as well as their own (both above their thresholds), so
    /// ambiguity escalation fires constantly and changes the decision (another tier, local-only flag or capabilities).
    /// </summary>
    Overlap = 3,
}

/// <summary>One problem found in the routes.</summary>
public sealed record RouteHealthFinding
{
    /// <summary>What is wrong.</summary>
    public required RouteHealthKind Kind { get; init; }

    /// <summary>The route it concerns.</summary>
    public required string Route { get; init; }

    /// <summary>The other route involved, if any.</summary>
    public string? OtherRoute { get; init; }

    /// <summary>What is wrong and what to do about it.</summary>
    public required string Message { get; init; }

    /// <summary>The utterances involved.</summary>
    public required IReadOnlyList<string> Utterances { get; init; }

    /// <summary>The similarity behind the finding, or the overlapping fraction for <see cref="RouteHealthKind.Overlap"/>.</summary>
    public float? Similarity { get; init; }
}

/// <summary>How close two routes are.</summary>
public sealed record RouteOverlap
{
    /// <summary>One route.</summary>
    public required string Route { get; init; }

    /// <summary>The other route.</summary>
    public required string OtherRoute { get; init; }

    /// <summary>Cosine similarity of the routes' mean utterance vectors.</summary>
    public required float CentroidSimilarity { get; init; }

    /// <summary>
    /// The larger of the two directions: the fraction of a route's utterances that would trigger ambiguity escalation between
    /// the two routes: their best match in the other route is within <see cref="SemanticRouterOptions.AmbiguityMargin"/> of
    /// their best match in their own route, and both clear their route's similarity threshold.
    /// </summary>
    public required float AmbiguousFraction { get; init; }
}

/// <summary>The findings of <see cref="RouteHealthAnalyzer"/>.</summary>
public sealed record RouteHealthReport
{
    /// <summary>Problems, most serious kinds first.</summary>
    public required IReadOnlyList<RouteHealthFinding> Findings { get; init; }

    /// <summary>Every pair of routes, closest first.</summary>
    public required IReadOnlyList<RouteOverlap> Overlaps { get; init; }

    /// <summary>Utterances embedded.</summary>
    public required int UtteranceCount { get; init; }
}

/// <summary>
/// Checks the routes' utterances for problems that hurt routing: too few examples, duplicates, utterances that also
/// belong to another route, and routes so close that ambiguity escalation fires on most prompts.
/// </summary>
public sealed class RouteHealthAnalyzer
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly SemanticRouterOptions _options;

    /// <summary>Creates an analyzer for a configuration.</summary>
    public RouteHealthAnalyzer(IEmbeddingProvider embeddings, SemanticRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(options);
        _embeddings = embeddings;
        _options = options;
    }

    /// <summary>Fewer utterances than this is reported. Default 5.</summary>
    public int MinUtterances { get; init; } = 5;

    /// <summary>Utterances at least this similar count as the same. Default 0.97.</summary>
    public float DuplicateSimilarity { get; init; } = 0.97f;

    /// <summary>A pair is reported when this fraction of either route's utterances is ambiguous. Default 0.5.</summary>
    public float OverlapFraction { get; init; } = 0.5f;

    /// <summary>Embeds every utterance (one batch) and checks the routes.</summary>
    public async Task<RouteHealthReport> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        RouteDefinition[] routes = _options.Routes.ToArray();
        var texts = new List<string>();
        var owner = new List<int>();
        for (int r = 0; r < routes.Length; r++)
        {
            foreach (string utterance in routes[r].Utterances.Where(u => !string.IsNullOrWhiteSpace(u)))
            {
                texts.Add(utterance.Trim());
                owner.Add(r);
            }
        }

        var findings = new List<RouteHealthFinding>();
        foreach (RouteDefinition route in routes)
        {
            int count = route.Utterances.Count(u => !string.IsNullOrWhiteSpace(u));
            if (count < MinUtterances)
            {
                findings.Add(new RouteHealthFinding
                {
                    Kind = RouteHealthKind.FewUtterances,
                    Route = route.Name,
                    Message = $"'{route.Name}' has {count} utterance{(count == 1 ? string.Empty : "s")}; {MinUtterances} or more varied examples make a more robust route.",
                    Utterances = [],
                });
            }
        }

        if (texts.Count == 0)
        {
            return new RouteHealthReport { Findings = findings, Overlaps = [], UtteranceCount = 0 };
        }

        IReadOnlyList<ReadOnlyMemory<float>> raw = await _embeddings.GenerateEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);
        float[][] vectors = raw.Select(v => VectorMath.ToNormalized(v.Span)).ToArray();
        float Similarity(int a, int b) => VectorMath.Dot(vectors[a], vectors[b]);

        // Duplicates within a route, and utterances shared with another route.
        for (int a = 0; a < texts.Count; a++)
        {
            for (int b = a + 1; b < texts.Count; b++)
            {
                bool same = string.Equals(texts[a], texts[b], StringComparison.OrdinalIgnoreCase);
                float similarity = same ? 1f : Similarity(a, b);
                if (similarity < DuplicateSimilarity)
                {
                    continue;
                }

                RouteDefinition first = routes[owner[a]];
                RouteDefinition second = routes[owner[b]];
                findings.Add(owner[a] == owner[b]
                    ? new RouteHealthFinding
                    {
                        Kind = RouteHealthKind.DuplicateUtterances,
                        Route = first.Name,
                        Message = $"'{texts[a]}' and '{texts[b]}' in '{first.Name}' are near-duplicates; replace one with a different phrasing.",
                        Utterances = [texts[a], texts[b]],
                        Similarity = similarity,
                    }
                    : new RouteHealthFinding
                    {
                        Kind = RouteHealthKind.ConflictingUtterances,
                        Route = first.Name,
                        OtherRoute = second.Name,
                        Message = $"'{texts[a]}' ({first.Name}, tier {first.Tier}) and '{texts[b]}' ({second.Name}, tier {second.Tier}) are near-identical; keep it in one route.",
                        Utterances = [texts[a], texts[b]],
                        Similarity = similarity,
                    });
            }
        }

        // Pairwise overlap: how often an utterance's own route barely beats the other one.
        var overlaps = new List<RouteOverlap>();
        float[][] centroids = routes.Select((_, r) => Centroid(vectors, owner, r)).ToArray();
        for (int a = 0; a < routes.Length; a++)
        {
            for (int b = a + 1; b < routes.Length; b++)
            {
                if (centroids[a].Length == 0 || centroids[b].Length == 0)
                {
                    continue;
                }

                float fraction = Math.Max(Ambiguous(a, b), Ambiguous(b, a));
                overlaps.Add(new RouteOverlap
                {
                    Route = routes[a].Name,
                    OtherRoute = routes[b].Name,
                    CentroidSimilarity = VectorMath.Dot(centroids[a], centroids[b]),
                    AmbiguousFraction = fraction,
                });

                // Escalating between routes with the same requirements changes nothing, so it is not worth reporting.
                if (fraction >= OverlapFraction && !SameRequirements(routes[a], routes[b]))
                {
                    string percent = (fraction * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    findings.Add(new RouteHealthFinding
                    {
                        Kind = RouteHealthKind.Overlap,
                        Route = routes[a].Name,
                        OtherRoute = routes[b].Name,
                        Message = $"{percent} of the utterances of '{routes[a].Name}' (tier {routes[a].Tier}) or '{routes[b].Name}' (tier {routes[b].Tier}) match the other route almost as well, so prompts like them get tier {Math.Max(routes[a].Tier, routes[b].Tier)}. Make the utterances more distinct, or merge the routes.",
                        Utterances = [],
                        Similarity = fraction,
                    });
                }
            }
        }

        return new RouteHealthReport
        {
            Findings = findings.OrderByDescending(f => f.Kind).ThenBy(f => f.Route, StringComparer.OrdinalIgnoreCase).ToArray(),
            Overlaps = overlaps.OrderByDescending(o => o.AmbiguousFraction).ThenByDescending(o => o.CentroidSimilarity).ToArray(),
            UtteranceCount = texts.Count,
        };

        // Leave-one-out: each utterance of route x against the rest of x and against route y.
        float Ambiguous(int x, int y)
        {
            int total = 0, ambiguous = 0;
            for (int i = 0; i < owner.Count; i++)
            {
                if (owner[i] != x)
                {
                    continue;
                }

                float own = float.MinValue, other = float.MinValue;
                for (int j = 0; j < owner.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    if (owner[j] == x)
                    {
                        own = Math.Max(own, Similarity(i, j));
                    }
                    else if (owner[j] == y)
                    {
                        other = Math.Max(other, Similarity(i, j));
                    }
                }

                if (own == float.MinValue)
                {
                    continue; // a single utterance has no own neighbour to compare with
                }

                total++;
                bool bothMatch = own >= _options.GetThreshold(routes[x]) && other >= _options.GetThreshold(routes[y]);
                ambiguous += bothMatch && other >= own - _options.AmbiguityMargin ? 1 : 0;
            }

            return total == 0 ? 0f : (float)ambiguous / total;
        }
    }

    private static bool SameRequirements(RouteDefinition a, RouteDefinition b) =>
        a.Tier == b.Tier && a.LocalOnly == b.LocalOnly
        && a.RequiredCapabilities.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.RequiredCapabilities.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static float[] Centroid(float[][] vectors, List<int> owner, int route)
    {
        float[]? sum = null;
        for (int i = 0; i < owner.Count; i++)
        {
            if (owner[i] != route)
            {
                continue;
            }

            sum ??= new float[vectors[i].Length];
            for (int d = 0; d < sum.Length; d++)
            {
                sum[d] += vectors[i][d];
            }
        }

        if (sum is null)
        {
            return [];
        }

        VectorMath.Normalize(sum);
        return sum;
    }
}
