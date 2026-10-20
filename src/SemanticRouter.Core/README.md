# SemanticRouter.Core

The routing primitives of [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET), with no dependencies: SIMD cosine similarity, a tokenizer-free token estimator, complexity heuristics, the semantic route index and the route and model selectors. It references no NuGet packages and is AOT-compatible.

Most applications want [SemanticRouter.AspNetCore](https://www.nuget.org/packages/SemanticRouter.AspNetCore) instead, which builds a router from configuration on top of this package. To route your own tools' prompts without writing code, install the [`semantic-router` tool](https://www.nuget.org/packages/SemanticRouter.Host).

```bash
dotnet add package SemanticRouter.Core
```

```csharp
using SemanticRouter;

ComplexityAnalysis analysis = new HeuristicComplexityAnalyzer().Analyze("Refactor this class and explain the trade-offs.");
Console.WriteLine($"{analysis.EstimatedTokens} tokens, complexity {analysis.Score:F2}");

float similarity = VectorMath.CosineSimilarity([0.1f, 0.7f, 0.2f], [0.2f, 0.6f, 0.1f]);
```

- [How it works](https://github.com/siebe41/SemanticRouter.NET#how-it-works) and [Architecture](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/architecture.md)
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence. The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
