using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Problems the router reports say what to do about them: start a server, where the key goes, how to sign in.</summary>
public sealed class ActionableMessagesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-messages").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("lmstudio", "http://localhost:1234", "Start LM Studio's local server (in LM Studio: Developer → Start server")]
    [InlineData("my-box", "http://127.0.0.1:1234", "Start LM Studio's local server")]
    [InlineData("ollama", "http://localhost:11434", "Start Ollama (open the Ollama app, or run `ollama serve`)")]
    [InlineData("vllm", "http://localhost:8000", "Start the server, or correct the address: SemanticRouter:Connectors:vllm:BaseAddress (dashboard: Providers → Connectors → vllm → Base address)")]
    [InlineData("openai", "https://api.openai.com", "Check the address, SemanticRouter:Connectors:openai:BaseAddress")]
    public async Task An_unreachable_server_says_how_to_start_it_or_where_its_address_is(string name, string address, string hint)
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));

        ConnectorProbeResult probe = await ConnectorProbe.ListModelsAsync(
            name, new ConnectorOptions { BaseAddress = new Uri(address) }, new HttpClient(handler), TimeSpan.FromSeconds(5));

        probe.Failure.Should().Be(ConnectorProbeFailure.Unreachable);
        probe.Message.Should().Contain("is not reachable (Connection refused)").And.Contain(hint);
    }

    [Fact]
    public void Rejected_credentials_name_the_dashboard_field_beside_the_setting() =>
        OpenAICompatibleConnector.CredentialAdvice("openai", 401, hasKey: true)
            .Should().Contain("SemanticRouter:Connectors:openai:ApiKey").And.Contain("Providers → Connectors → openai → API key");

    [Theory]
    [InlineData(0, "Logged in using ChatGPT", RouterCheckStatus.Ok, "signed in with ChatGPT")]
    [InlineData(1, "Not logged in", RouterCheckStatus.Failed, "Run `codex login`")]
    [InlineData(0, "Logged in using an API key - sk-proj-***abcd", RouterCheckStatus.Warning, "billed to that key")]
    [InlineData(2, "error: unrecognized subcommand 'login'", RouterCheckStatus.Ok, "could not be checked")]
    public void Codex_sign_in_is_read_from_codex_login_status(int exit, string output, RouterCheckStatus status, string says)
    {
        RouterCheck check = RouterDoctor.CodexSignIn("connector codex", "codex-cli 0.161.0", "/usr/bin/codex", exit, output);

        check.Status.Should().Be(status);
        check.Message.Should().Contain(says).And.NotContain("sk-proj", "part of a key is still a secret");
    }

    [Fact]
    public async Task A_failed_model_download_names_the_site_the_proxy_settings_and_where_to_copy_the_file()
    {
        OnnxModelInfo model = OnnxEmbeddingModels.Find(null)!;
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused (proxy.corp:3128)"));

        Func<Task> download = () => OnnxModelDownloader.EnsureAsync(model, _directory, new HttpClient(handler));

        (await download.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should()
            .Contain("Could not download model.onnx from huggingface.co and its CDN (*.hf.co) (Connection refused (proxy.corp:3128))")
            .And.Contain("HTTPS_PROXY").And.Contain("SemanticRouter:Network:CaBundle")
            .And.Contain(Path.Combine(_directory, model.Name, "model.onnx")).And.Contain(model.ModelSha256);
    }
}
