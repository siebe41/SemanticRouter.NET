using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Tests.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Onnx;

/// <summary>
/// Where ONNX Runtime cannot load (no build for Intel Macs; macOS before 14), the in-process embeddings say so as an
/// embedding problem, and the dashboard and doctor point to an embedding server instead (#209).
/// </summary>
public sealed class OnnxAvailabilityTests : RouterHostTest, IDisposable
{
    private const string Reason = "In-process (Onnx) embeddings are not available on an Intel Mac: ONNX Runtime publishes no build for it. Use an embedding model in LM Studio or Ollama instead (Embeddings: OpenAICompatible).";

    private readonly string _directory = Directory.CreateTempSubdirectory("router-onnx-availability").FullName;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly StubHttpMessageHandler _downloads = StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private OnnxEmbeddingProviderFactory Factory(IServiceProvider services) =>
        new(services, Options.Create(new OnnxEmbeddingOptions { ModelsDirectory = _directory }), () => Reason);

    private Task<HttpClient> StartAsync(string embeddings) => StartAsync(
        routing: o => o.Routes.Add(TestEmbeddings.Route("smalltalk", 1, "hello there")),
        services: s =>
        {
            s.AddSingleton<ILoggerProvider>(_logs);
            s.AddHttpClient(OnnxEmbeddingOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _downloads);
            s.AddSingleton<IEmbeddingProviderFactory>(Factory);
            s.Configure<EmbeddingProviderOptions>(o => o.Provider = embeddings);
        },
        mockClassifier: false);

    [Fact]
    public async Task The_factory_says_why_before_downloading_anything()
    {
        ServiceProvider services = new ServiceCollection().AddHttpClient(OnnxEmbeddingOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _downloads).Services.BuildServiceProvider();
        await using (services)
        {
            using OnnxEmbeddingProviderFactory factory = Factory(services);
            IEmbeddingProvider provider = factory.Create(new EmbeddingProviderOptions { Provider = OnnxEmbeddings.Provider });

            factory.Unavailable.Should().Be(Reason);
            (await FluentActions.Awaiting(() => provider.GenerateEmbeddingAsync("hi").AsTask()).Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*Intel Mac*LM Studio or Ollama*");
            _downloads.Requests.Should().BeEmpty("a model that can never run is not downloaded");
        }
    }

    [Fact]
    public void Native_load_failures_are_recognised_and_explained_per_platform()
    {
        OnnxRuntimeEnvironment.IsLoadFailure(new DllNotFoundException("libonnxruntime")).Should().BeTrue();
        OnnxRuntimeEnvironment.IsLoadFailure(new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException("libonnxruntime"))).Should().BeTrue();
        OnnxRuntimeEnvironment.IsLoadFailure(new EntryPointNotFoundException("OrtGetApiBase")).Should().BeTrue();
        OnnxRuntimeEnvironment.IsLoadFailure(new InvalidCastException()).Should().BeFalse();
        OnnxRuntimeEnvironment.IsLoadFailure(new IOException("model.onnx")).Should().BeFalse();

        OnnxRuntimeEnvironment.Explain(null, macOS: true, Architecture.X64, new Version(15, 2)).Should().Contain("Intel Mac").And.Contain("LM Studio or Ollama");
        OnnxRuntimeEnvironment.Explain(null, macOS: true, Architecture.Arm64, new Version(13, 6)).Should().Contain("macOS 14 or later").And.Contain("this is 13.6");
        OnnxRuntimeEnvironment.Explain(
                new TypeInitializationException("NativeMethods", new DllNotFoundException("Unable to load shared library 'onnxruntime'.")), macOS: false, Architecture.X64, new Version(6, 1))
            .Should().Contain("could not be loaded (Unable to load shared library 'onnxruntime')").And.Contain("LM Studio or Ollama");
    }

    [Fact]
    public void On_this_machine_onnx_runtime_loads_wherever_it_ships()
    {
        // The test machines are Linux x64, Windows x64 and Apple silicon on a current macOS: all have a build.
        if (OperatingSystem.IsMacOS() && (RuntimeInformation.ProcessArchitecture == Architecture.X64 || Environment.OSVersion.Version.Major < 14))
        {
            OnnxRuntimeEnvironment.Unavailable.Should().NotBeNull();
            return;
        }

        OnnxRuntimeEnvironment.Unavailable.Should().BeNull();
    }

    [Fact]
    public async Task A_request_falls_back_with_the_reason_in_the_log_and_no_stack_trace()
    {
        HttpClient client = await StartAsync(OnnxEmbeddings.Provider);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(ChatJson, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string failure = _logs.Messages.Single(m => m.Contains("Prompt classification failed", StringComparison.Ordinal));
        failure.Should().Contain("Intel Mac").And.NotContain("   at ", "an embedding problem is logged by its message");
    }

    [Fact]
    public async Task A_lowercase_provider_reaches_the_dashboard_in_the_package_spelling()
    {
        // The dashboard shows the Onnx model fields only for 'Onnx', compared exactly.
        HttpClient client = await StartAsync("onnx");

        JsonNode config = JsonNode.Parse(await client.GetStringAsync("/router/api/config"))!["config"]!;

        ((string?)config["Embeddings"]!["Provider"]).Should().Be(OnnxEmbeddings.Provider);
    }

    [Fact]
    public async Task The_dashboard_status_says_onnx_cannot_run_here()
    {
        HttpClient client = await StartAsync(EmbeddingProviderKinds.Hashing);

        JsonNode status = JsonNode.Parse(await client.GetStringAsync(new Uri("/router/api/status", UriKind.Relative)))!;

        status["embeddings"]!["available"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Contain("Onnx");
        status["embeddings"]!["unavailable"]!["Onnx"]!.GetValue<string>().Should().Be(Reason);
    }

    [Fact]
    public async Task Doctor_suggests_an_embedding_server_instead_and_fails_an_onnx_choice()
    {
        await StartAsync(EmbeddingProviderKinds.Hashing);
        RouterCheck hashing = (await RouterDoctor.CheckAsync(Services)).Single(c => c.Subject == "embeddings");
        await DisposeAsync();

        await StartAsync(OnnxEmbeddings.Provider);
        RouterCheck onnx = (await RouterDoctor.CheckAsync(Services)).Single(c => c.Subject == "embeddings");

        hashing.Status.Should().Be(RouterCheckStatus.Warning);
        hashing.Message.Should().Contain("OpenAICompatible endpoint (an embedding model in LM Studio or Ollama)").And.Contain("Intel Mac")
            .And.NotContain("Provider to Onnx");
        onnx.Status.Should().Be(RouterCheckStatus.Failed);
        onnx.Message.Should().Contain("cannot run on this machine").And.Contain("Intel Mac").And.Contain("SemanticRouter:Embeddings:Provider");
    }
}
