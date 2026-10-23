using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Storage.Sqlite;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// docs/api-surface.md has a line for every public type of each library package, under the package's heading, and no
/// line for a type that is not public, so the page cannot drift from the API (CONTRIBUTING.md, Public API and package
/// validation).
/// </summary>
public sealed partial class ApiSurfaceDocumentationTests
{
    private static readonly Dictionary<string, Assembly> Packages = new()
    {
        ["SemanticRouter.Core"] = typeof(PromptRouter).Assembly,
        ["SemanticRouter.AspNetCore"] = typeof(RoutingActivityLog).Assembly,
        ["SemanticRouter.Embeddings.Onnx"] = typeof(OnnxEmbeddingOptions).Assembly,
        ["SemanticRouter.Storage.Sqlite"] = typeof(SqliteStateOptions).Assembly,
        ["SemanticRouter.Connectors.Copilot"] = typeof(CopilotConnector).Assembly,
    };

    public static TheoryData<string> PackageNames() => new(Packages.Keys);

    [Theory]
    [MemberData(nameof(PackageNames))]
    public void Every_public_type_has_a_line_and_nothing_else_does(string package)
    {
        string[] documented = DocumentedTypes(package);
        string[] exported = Packages[package].GetExportedTypes().Select(DisplayName).ToArray();

        documented.Should().OnlyHaveUniqueItems("each type has one line");
        exported.Except(documented).Should().BeEmpty($"every public type of {package} has a line in docs/api-surface.md");
        documented.Except(exported).Should().BeEmpty($"docs/api-surface.md lists only public types of {package}");
    }

    // The type names the package's section lists, one per "- `Name`: purpose" line.
    private static string[] DocumentedTypes(string package)
    {
        string page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "api-surface.md"));
        int start = page.IndexOf($"\n## {package}\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"docs/api-surface.md has a section for {package}");
        int end = page.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = end < 0 ? page[start..] : page[start..end];
        return TypeLine().Matches(section).Select(m => m.Groups["name"].Value).ToArray();
    }

    // As the page writes it: no namespace, a nested type after its declaring type, no generic arity.
    private static string DisplayName(Type type)
    {
        string name = type.Name.Split('`')[0];
        return type.DeclaringType is { } outer ? $"{DisplayName(outer)}.{name}" : name;
    }

    [GeneratedRegex(@"^- `(?<name>[\w.]+)`:", RegexOptions.Multiline)]
    private static partial Regex TypeLine();
}
