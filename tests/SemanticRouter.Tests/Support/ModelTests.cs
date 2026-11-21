namespace SemanticRouter.Tests.Support;

/// <summary>
/// Tests that run a real ONNX embedding model. The first run downloads the pinned models (about 57 MB) from huggingface.co
/// into <c>SEMANTIC_ROUTER_TEST_MODELS</c> (or the temp directory), so a plain <c>dotnet test</c> skips them with
/// <see cref="Reason"/> and needs no network. They run with <c>SEMANTIC_ROUTER_MODEL_TESTS=1</c>, as CI's
/// <c>Model tests</c> step does, and carry the trait <c>Category=Model</c> to select them:
/// <c>SEMANTIC_ROUTER_MODEL_TESTS=1 dotnet test --filter Category=Model</c>.
/// </summary>
internal static class ModelTests
{
    /// <summary>The trait name and value every model test carries.</summary>
    public const string Category = "Category";

    /// <inheritdoc cref="Category"/>
    public const string Model = "Model";

    /// <summary>The environment variable that turns the model tests on.</summary>
    public const string Variable = "SEMANTIC_ROUTER_MODEL_TESTS";

    public const string Reason = "Downloads the ONNX embedding models (57 MB) from huggingface.co: set " + Variable + "=1 to run it (dotnet test --filter Category=Model).";

    /// <summary>Whether the model tests run: <c>SEMANTIC_ROUTER_MODEL_TESTS</c> is <c>1</c> or <c>true</c>.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A <see cref="FactAttribute"/> for a test that needs a real model, skipped unless <see cref="ModelTests.Enabled"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (!ModelTests.Enabled)
        {
            Skip = ModelTests.Reason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> for a test that needs a real model, skipped unless <see cref="ModelTests.Enabled"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ModelTheoryAttribute : TheoryAttribute
{
    public ModelTheoryAttribute()
    {
        if (!ModelTests.Enabled)
        {
            Skip = ModelTests.Reason;
        }
    }
}
