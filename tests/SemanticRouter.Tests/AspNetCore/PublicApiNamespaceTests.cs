using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Storage.Sqlite;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The library packages keep their public types in their own namespaces; only static classes of setup extension methods
/// are in Microsoft's (docs/architecture.md#namespaces).
/// </summary>
public sealed class PublicApiNamespaceTests
{
    private static readonly string[] MicrosoftNamespaces = ["Microsoft.AspNetCore.Builder", "Microsoft.Extensions.DependencyInjection"];

    private static readonly Dictionary<string, (Assembly Assembly, string Namespace)> Assemblies = new()
    {
        ["SemanticRouter.Core"] = (typeof(PromptRouter).Assembly, "SemanticRouter"),
        ["SemanticRouter.AspNetCore"] = (typeof(SemanticRouterBuilder).Assembly, "SemanticRouter.AspNetCore"),
        ["SemanticRouter.Storage.Sqlite"] = (typeof(SqliteStateOptions).Assembly, "SemanticRouter.Storage.Sqlite"),
        ["SemanticRouter.Embeddings.Onnx"] = (typeof(OnnxEmbeddingOptions).Assembly, "SemanticRouter.Embeddings.Onnx"),
        ["SemanticRouter.Connectors.Copilot"] = (typeof(CopilotConnectorBuilderExtensions).Assembly, "SemanticRouter.Connectors.Copilot"),
    };

    public static TheoryData<string> Packages() => new(Assemblies.Keys);

    [Theory]
    [MemberData(nameof(Packages))]
    public void Microsoft_namespaces_hold_only_static_extension_classes(string package)
    {
        Type[] misplaced = Assemblies[package].Assembly.GetExportedTypes()
            .Where(t => MicrosoftNamespaces.Contains(t.Namespace))
            .Where(t => t.IsNested || !(t.IsAbstract && t.IsSealed) || !t.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .ToArray();

        misplaced.Should().BeEmpty("only static classes of extension methods belong in Microsoft's namespaces");
    }

    [Theory]
    [MemberData(nameof(Packages))]
    public void Every_other_public_type_is_in_the_package_namespace(string package)
    {
        (Assembly assembly, string own) = Assemblies[package];
        string[] siblings = Assemblies.Values.Select(a => a.Namespace).Where(n => n != own && n.StartsWith(own + ".", StringComparison.Ordinal)).ToArray();

        Type[] misplaced = assembly.GetExportedTypes()
            .Where(t => !MicrosoftNamespaces.Contains(t.Namespace))
            .Where(t => t.Namespace is not { } ns
                || !(ns == own || ns.StartsWith(own + ".", StringComparison.Ordinal))
                || siblings.Any(s => ns == s || ns.StartsWith(s + ".", StringComparison.Ordinal)))
            .ToArray();

        misplaced.Should().BeEmpty($"public types of {package} belong in {own}");
    }

    [Fact]
    public void Http_request_and_response_bodies_are_not_public()
    {
        typeof(Microsoft.AspNetCore.Builder.RouterDashboardEndpoints).GetNestedTypes(BindingFlags.Public).Should().BeEmpty();
        typeof(SemanticRouterBuilder).Assembly.GetExportedTypes().Select(t => t.Name).Should().NotContain(
            ["ClassifyPromptRequest", "PromptClassificationResponse", "RouterModelInfo", "RouterModelList", "TokenCountResponse"]);
    }

    [Fact]
    public void Helpers_only_first_party_code_calls_are_not_public()
    {
        // #200: implementation details that the tool, the dashboard and the tests reach through InternalsVisibleTo.
        typeof(SemanticRouterBuilder).Assembly.GetExportedTypes().Select(t => t.Name).Should().NotContain(
        [
            "ActivityReports", "RoutingFeedbackLog", "MemoryExposureLog", "SecretRedactor",
            "ModelFamily", "ModelFamilyBand", "ModelFamilyFile", "ModelFamilyTable",
            "InMemoryEvaluationSetStore", "JsonLinesEvaluationSetStore", "ProtectedFileSecretStore",
            "ClaudeCodeMemorySource", "KnowledgeGraphMemorySource", "MarkdownMemorySource",
            "ClientTurnFile", "DashboardTheme", "ThemeContrastIssue",
            "RouterDoctor", "RouterCheck", "RouterCheckStatus", "ModelLineup", "ModelLineupEntry", "ModelLineupReport",
        ]);
    }
}
