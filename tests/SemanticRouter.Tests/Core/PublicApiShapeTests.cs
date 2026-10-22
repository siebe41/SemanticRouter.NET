using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Storage.Sqlite;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// The library packages grow by addition (#199; CONTRIBUTING.md, "Adding a field or a dependency without a break"):
/// their records are not positional, except the frozen ones, no public constructor has optional parameters, and every
/// enum has a zero member, except the documented ones.
/// </summary>
public sealed class PublicApiShapeTests
{
    private static readonly Assembly[] Packages =
    [
        typeof(PromptRouter).Assembly,
        typeof(OnnxEmbeddingOptions).Assembly,
        typeof(RoutingActivityLog).Assembly,
        typeof(SqliteStateOptions).Assembly,
        typeof(CopilotConnector).Assembly,
    ];

    // Complete by definition, and documented as frozen.
    private static readonly Type[] FrozenPositionalRecords = [typeof(RouteScore), typeof(TimeWindow)];

    // A plain-text reply is no ResponseFormat at all, and ResponseFormat.Kind is required (see its remarks).
    private static readonly Type[] EnumsWithoutZero = [typeof(ResponseFormatKind)];

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
    public void Public_enums_define_zero_except_the_documented_ones()
    {
        Type[] withoutZero = Packages.SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsEnum && !Enum.GetValuesAsUnderlyingType(t).Cast<object>().Any(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) == 0))
            .ToArray();

        withoutZero.Should().BeEquivalentTo(EnumsWithoutZero, "default(T) of an enum is zero, which should be a value the type defines (CA1008)");
    }

    [Fact]
    public async Task Collaborators_in_a_dependencies_object_are_used_and_null_means_the_defaults()
    {
        var time = new ManualTimeProvider();
        var health = new ModelHealthTracker(null, new ModelHealthTrackerDependencies { Time = time });
        var options = new SemanticRouterOptions();
        options.Models.Add(TestEmbeddings.Model("cooling", 1));
        options.Models.Add(TestEmbeddings.Model("ready", 1));
        var classifier = new Mock<IPromptClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = 1,
                Reason = RoutingReason.SemanticMatch,
                Complexity = new ComplexityAnalysis { EstimatedTokens = 10 },
            });
        var router = new PromptRouter(classifier.Object, options, new PromptRouterDependencies { Health = health });

        // The router skips a model in cooldown on the health it was given, which measures it with the clock it was given.
        health.ReportFailure(options.Models[0], ModelFailureKind.Unavailable);
        (await router.RouteAsync(new PromptRoutingRequest { Prompt = "hello" })).Candidates.Select(c => c.Model.Name)
            .Should().Equal("ready");
        time.Advance(TimeSpan.FromHours(1));
        (await router.RouteAsync(new PromptRoutingRequest { Prompt = "hello" })).Candidates.Select(c => c.Model.Name)
            .Should().Contain("cooling");

        // No dependencies object is the same as an empty one.
        new PromptRouter(classifier.Object, options, null).Models.Should().HaveCount(2);
        new SemanticPromptClassifier(new HashingEmbeddingProvider(), new SemanticRouterOptions(), null).Should().NotBeNull();
        new ModelHealthTracker(null, null).IsAvailable(options.Models[0]).Should().BeTrue();
        new DefaultModelSelector(null, null).RankCandidates(new ModelRequirements { RequiredTier = 1 }, [.. options.Models])
            .Should().HaveCount(2);
    }

    [Fact]
    public void Converted_records_keep_the_defaults_their_parameters_had()
    {
        // The one converted member whose default is neither zero nor null, so dropping it fails here. A struct's
        // initializers run only through a constructor: default(ActivityPurgeResult) skips them and is not Complete.
        new ActivityPurgeResult { Removed = 1 }.Complete.Should().BeTrue();
        default(ActivityPurgeResult).Complete.Should().BeFalse();
    }

    // The compiler generates Deconstruct only for a positional record's primary constructor.
    private static bool IsPositionalRecord(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "Deconstruct" && m.IsDefined(typeof(CompilerGeneratedAttribute)));
}
