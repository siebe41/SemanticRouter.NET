using System.Net;
using System.Text;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The endpoints that read a whole body outside the routing middleware keep to <c>Middleware:MaxRequestBodyBytes</c>
/// too, also when the body is sent without a length (#402).
/// </summary>
public sealed class RequestBodyLimitTests : RouterHostTest
{
    private const string Messages = """{"model":"claude","max_tokens":10,"messages":[{"role":"user","content":"hello there, how are you today?"}]}""";

    private Task<HttpClient> StartLimitedAsync() => StartAsync(o =>
    {
        o.Mode = PromptRouterMode.Dispatch;
        o.PathPrefixes.Add("/v1/chat/completions");
        o.MaxRequestBodyBytes = 64;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Counting_tokens_keeps_to_the_body_limit(bool chunked)
    {
        HttpClient client = await StartLimitedAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages/count_tokens", Body(Messages, chunked));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Contain("MaxRequestBodyBytes");
    }

    [Fact]
    public async Task Counting_tokens_still_answers_within_the_limit()
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages/count_tokens", Body(Messages, chunked: true));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Analyzing_a_chunked_body_keeps_to_the_body_limit()
    {
        HttpClient client = await StartLimitedAsync();

        HttpResponseMessage response = await client.PostAsync("/router/api/context/analyze", Body(Messages, chunked: true));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("/router/api/config")]
    [InlineData("/router/api/config/validate")]
    public async Task A_configuration_document_keeps_to_the_body_limit(string path)
    {
        HttpClient client = await StartLimitedAsync();
        string document = """{"Models": [], "Routes": [], "Connectors": {}, "Middleware": {"MaxRequestBodyBytes": 64}}""";

        using var request = new HttpRequestMessage(path.EndsWith("validate", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Put, path)
        {
            Content = Body(document, chunked: true),
        };
        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    private static HttpContent Body(string json, bool chunked) => chunked ? new UnsizedContent(Encoding.UTF8.GetBytes(json)) : Json(json);

    /// <summary>A body sent without a <c>Content-Length</c>, as a chunked upload is.</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnsizedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
