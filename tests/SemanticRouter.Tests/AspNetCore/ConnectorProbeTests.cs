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
