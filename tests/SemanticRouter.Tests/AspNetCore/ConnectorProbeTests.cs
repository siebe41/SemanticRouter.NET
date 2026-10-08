using System.Net;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Probing an HTTP connector's model list, including servers that require authentication (LM Studio).</summary>
public sealed class ConnectorProbeTests
{
    private static readonly ConnectorOptions LmStudio = new() { BaseAddress = new Uri("http://localhost:1234") };

    [Fact]
    public async Task Lists_models_and_sends_the_bearer_token()
    {
        var handler = StubHttpMessageHandler.Json("""{"data":[{"id":"qwen3-8b"},{"id":"text-embedding-nomic"}]}""");
        var connector = new ConnectorOptions { BaseAddress = LmStudio.BaseAddress, ApiKey = "lm-token-123" };

        ConnectorProbeResult result = await ConnectorProbe.ListModelsAsync("lmstudio", connector, new HttpClient(handler), TimeSpan.FromSeconds(5));

        result.Ok.Should().BeTrue();
        result.Models.Should().Equal("qwen3-8b", "text-embedding-nomic");
        handler.Requests.Single().Request.RequestUri!.ToString().Should().Be("http://localhost:1234/v1/models");
        handler.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer lm-token-123");
    }

    [Fact]
    public async Task A_401_without_a_key_says_authentication_is_required_and_names_the_setting()
    {
        var handler = StubHttpMessageHandler.Json("""{"error":"Unauthorized"}""", HttpStatusCode.Unauthorized);

        ConnectorProbeResult result = await ConnectorProbe.ListModelsAsync("lmstudio", LmStudio, new HttpClient(handler), TimeSpan.FromSeconds(5));

        result.Failure.Should().Be(ConnectorProbeFailure.AuthenticationRequired);
        result.StatusCode.Should().Be(401);
        result.Message.Should().Contain("requires authentication").And.Contain("SemanticRouter:Connectors:lmstudio:ApiKey");
        handler.Requests.Single().Request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task A_401_with_a_key_says_the_credentials_were_rejected_without_echoing_them()
    {
        var handler = StubHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
        var connector = new ConnectorOptions { BaseAddress = LmStudio.BaseAddress, ApiKey = "wrong-token-456" };

        ConnectorProbeResult result = await ConnectorProbe.ListModelsAsync("lmstudio", connector, new HttpClient(handler), TimeSpan.FromSeconds(5));

        result.Failure.Should().Be(ConnectorProbeFailure.CredentialsRejected);
        result.Message.Should().Contain("rejected the credentials").And.NotContain("wrong-token-456");
    }

    [Fact]
    public async Task Anthropic_connectors_use_x_api_key_and_unreachable_servers_are_reported()
    {
        var handler = StubHttpMessageHandler.Json("""{"data":[{"id":"claude-sonnet-5-5"}]}""");
        var anthropic = new ConnectorOptions { Type = ConnectorType.Anthropic, BaseAddress = new Uri("https://api.anthropic.com"), ApiKey = "sk-ant-1" };

        ConnectorProbeResult ok = await ConnectorProbe.ListModelsAsync("anthropic", anthropic, new HttpClient(handler), TimeSpan.FromSeconds(5));
        ConnectorProbeResult down = await ConnectorProbe.ListModelsAsync("lmstudio", LmStudio, new HttpClient(StubHttpMessageHandler.Throws()), TimeSpan.FromSeconds(5));

        ok.Models.Should().Equal("claude-sonnet-5-5");
        handler.Requests.Single().Request.Headers.GetValues("x-api-key").Should().Equal("sk-ant-1");
        handler.Requests.Single().Request.Headers.Authorization.Should().BeNull();
        down.Failure.Should().Be(ConnectorProbeFailure.Unreachable);
        down.Message.Should().Contain("not reachable");
    }

    [Fact]
    public async Task LM_Studio_with_just_in_time_loading_reports_load_state_context_and_on_demand_loading()
    {
        var handler = LocalServers.LmStudio(justInTime: true);

        ConnectorProbeResult result = await ConnectorProbe.DiscoverAsync("lmstudio", LmStudio, new HttpClient(handler), TimeSpan.FromSeconds(5));

        result.Ok.Should().BeTrue();
        result.Server.Should().Be("LM Studio");
        result.Loading.Should().Be(ModelLoading.OnDemand, "models that are not loaded are listed, so asking for one loads it");
        result.LoadingNote.Should().Contain("just-in-time loading is on");
        result.Message.Should().EndWith("LM Studio, 1 loaded.");
        DiscoveredModel chat = result.Details.Single(m => m.Id == "qwen/qwen3-8b");
        chat.Should().Match<DiscoveredModel>(m => m.Loaded == true && m.Listed && m.LoadedContextLength == 32768 && m.MaxContextLength == 40960
                                                 && m.ParametersBillions == 8 && m.Quantization == "Q4_K_M");
        chat.Capabilities.Should().Equal("tools");
        result.Details.Single(m => m.Id == "qwen/qwen3-coder-30b").Should().Match<DiscoveredModel>(m => m.Loaded == false && m.ParametersBillions == 30);
        result.Details.Single(m => m.Id == "google/gemma-3-12b").Kind.Should().Be(DiscoveredModelKind.Vision);
        result.Details.Single(m => m.Id.StartsWith("text-embedding", StringComparison.Ordinal)).Kind.Should().Be(DiscoveredModelKind.Embedding);
        handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).Should().Equal("/v1/models", "/api/v0/models");
    }

    [Fact]
    public async Task LM_Studio_without_just_in_time_loading_only_serves_loaded_models()
    {
        ConnectorProbeResult result = await ConnectorProbe.DiscoverAsync("lmstudio", LmStudio, new HttpClient(LocalServers.LmStudio(justInTime: false)), TimeSpan.FromSeconds(5));

        result.Loading.Should().Be(ModelLoading.LoadedOnly);
        result.LoadingNote.Should().Contain("just-in-time loading is off").And.Contain("lms load");
        result.Models.Should().Equal(["qwen/qwen3-8b"], "Models is what the chat endpoint serves");
        result.Details.Where(m => !m.Listed).Select(m => m.Id).Should().Contain("qwen/qwen3-coder-30b", "downloaded models are still reported");
    }

    [Fact]
    public async Task Ollama_reports_loaded_models_sizes_capabilities_and_loads_on_demand()
    {
        var connector = new ConnectorOptions { BaseAddress = new Uri("http://localhost:11434") };

        ConnectorProbeResult result = await ConnectorProbe.DiscoverAsync("ollama", connector, new HttpClient(LocalServers.Ollama()), TimeSpan.FromSeconds(5));

        result.Server.Should().Be("Ollama");
        result.Loading.Should().Be(ModelLoading.OnDemand);
        result.LoadingNote.Should().Contain("keep-alive");
        result.Details.Single(m => m.Id == "llama3.2:latest").Should().Match<DiscoveredModel>(m =>
            m.Loaded == true && m.LoadedContextLength == 8192 && m.MaxContextLength == 131072 && m.ParametersBillions == 3.2);
        DiscoveredModel qwen = result.Details.Single(m => m.Id == "qwen3:30b-a3b");
        qwen.Should().Match<DiscoveredModel>(m => m.Loaded == false && m.LoadedContextLength == null && m.ParametersBillions == 30.5);
        qwen.Capabilities.Should().BeEquivalentTo("tools", "reasoning");
        result.Details.Single(m => m.Id == "nomic-embed-text:latest").Kind.Should().Be(DiscoveredModelKind.Embedding);
        result.Details.Single(m => m.Id == "gpt-oss:120b-cloud").Remote.Should().BeTrue("Ollama forwards cloud models to ollama.com");
        result.Details.Where(m => m.Id != "gpt-oss:120b-cloud").Should().OnlyContain(m => m.Remote == false);
    }

    [Fact]
    public async Task Cloud_apis_get_one_request_and_their_context_lengths_are_read()
    {
        var handler = StubHttpMessageHandler.Json("""{"data":[{"id":"claude-opus-5-5","display_name":"Claude Opus 5.5","max_input_tokens":1000000},{"id":"vendor/model","context_length":131072}]}""");
        var anthropic = new ConnectorOptions { Type = ConnectorType.OpenAICompatible, BaseAddress = new Uri("https://openrouter.ai"), ApiKey = "k" };

        ConnectorProbeResult result = await ConnectorProbe.DiscoverAsync("openrouter", anthropic, new HttpClient(handler), TimeSpan.FromSeconds(5));

        handler.Requests.Should().ContainSingle("LM Studio and Ollama are only asked for on local addresses");
        result.Server.Should().BeNull();
        result.Loading.Should().Be(ModelLoading.Unknown);
        result.Details.Select(m => (m.Id, m.MaxContextLength, m.DisplayName)).Should().Equal(("claude-opus-5-5", 1_000_000, "Claude Opus 5.5"), ("vendor/model", 131072, null));
    }

    [Fact]
    public async Task A_server_that_is_neither_LM_Studio_nor_Ollama_keeps_the_plain_list()
    {
        var handler = StubHttpMessageHandler.Paths(("/v1/models", _ => """{"data":[{"id":"served-model","max_model_len":16384}]}"""));

        ConnectorProbeResult result = await ConnectorProbe.DiscoverAsync("vllm", new ConnectorOptions { BaseAddress = new Uri("http://gpu-box:8000") }, new HttpClient(handler), TimeSpan.FromSeconds(5));

        result.Ok.Should().BeTrue();
        result.Server.Should().BeNull();
        result.Details.Single().Should().Match<DiscoveredModel>(m => m.Id == "served-model" && m.MaxContextLength == 16384 && m.Loaded == null);
    }

    [Theory]
    [InlineData("qwen/qwen3-8b", 8.0)]
    [InlineData("qwen3:30b-a3b", 30.0)]
    [InlineData("qwen3-0.6b", 0.6)]
    [InlineData("meta-llama-3.1-70b-instruct", 70.0)]
    [InlineData("gemma-3n-e4b", null)]
    [InlineData("mistral-7b-v0.3", 7.0)]
    [InlineData("phi-4", null)]
    [InlineData("model-8bit", null)]
    public void Parameter_counts_are_read_from_model_ids(string id, double? billions) =>
        ConnectorProbe.ParametersFromId(id).Should().Be(billions);

    [Theory]
    [InlineData("http://localhost:1234", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://192.168.1.20:1234", true)]
    [InlineData("http://10.0.0.5:8000", true)]
    [InlineData("http://172.20.0.2:8000", true)]
    [InlineData("http://gpu-box:8000", true)]
    [InlineData("http://studio.local:1234", true)]
    [InlineData("http://host.docker.internal:1234", true)]
    [InlineData("http://[fd00::1]:1234", true)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("http://172.32.0.1:8000", false)]
    [InlineData("http://8.8.8.8", false)]
    public void Local_addresses_are_recognised(string address, bool local) =>
        ConnectorProbe.IsLocalAddress(new Uri(address)).Should().Be(local);

    [Fact]
    public async Task Embeddings_endpoint_401_names_the_embeddings_key()
    {
        var handler = StubHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
        var provider = new OpenAICompatibleEmbeddingProvider(
            new HttpClient(handler), new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.OpenAICompatible, Endpoint = LmStudio.BaseAddress, Model = "nomic" });

        Func<Task> act = async () => await provider.GenerateEmbeddingAsync("hello");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("requires authentication").And.Contain("SemanticRouter:Embeddings:ApiKey");
    }
}
