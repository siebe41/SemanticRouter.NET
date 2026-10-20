using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// Core and Embeddings.Onnx grow by addition (#199; CONTRIBUTING.md, "Adding a field or a dependency without a break"):
/// their records are not positional, except the frozen ones, and no public constructor has optional parameters.
/// </summary>
public sealed class PublicApiShapeTests
{
    private static readonly Assembly[] Packages = [typeof(PromptRouter).Assembly, typeof(OnnxEmbeddingOptions).Assembly];

    // Complete by definition, and documented as frozen.
    private static readonly Type[] FrozenPositionalRecords = [typeof(RouteScore), typeof(TimeWindow)];

    [Fact]
    public void Public_records_are_not_positional_except_the_frozen_ones()
    {
        Type[] positional = Packages.SelectMany(a => a.GetExportedTypes()).Where(IsPositionalRecord).ToArray();

        positional.Should().BeEquivalentTo(FrozenPositionalRecords, "a positional record's constructor and Deconstruct change with every member added");
    }

    [Fact]
    public void Public_constructors_have_no_optional_parameters()
    {
        string[] optional = Packages.SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetConstructors().Where(c => c.GetParameters().Any(p => p.IsOptional))
                .Select(c => $"{t.Name}({string.Join(", ", c.GetParameters().Select(p => p.Name))})"))
            .ToArray();

        optional.Should().BeEmpty("a parameter appended to a shipped constructor removes it; add a dependencies property or an overload");
    }

    [Fact]
    public void Collaborators_with_a_default_can_be_left_out()
    {
        var options = new SemanticRouterOptions();
        var classifier = new SemanticPromptClassifier(new HashingEmbeddingProvider(), options, new SemanticPromptClassifierDependencies());
        var time = new Support.ManualTimeProvider();
        var health = new ModelHealthTracker(options.Selection, new ModelHealthTrackerDependencies { Time = time });

        var router = new PromptRouter(classifier, options, new PromptRouterDependencies { Health = health, Time = time });

        router.Models.Should().BeEmpty();
        new DefaultModelSelector(null, new DefaultModelSelectorDependencies())
            .RankCandidates(new ModelRequirements { RequiredTier = 1 }, []).Should().BeEmpty();
    }

    // The compiler generates Deconstruct only for a positional record's primary constructor.
    private static bool IsPositionalRecord(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "Deconstruct" && m.IsDefined(typeof(CompilerGeneratedAttribute)));
}
