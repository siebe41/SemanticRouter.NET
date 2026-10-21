using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Errors written in the client's protocol, with the error type its SDK maps the status to, and a reply that cannot be
/// finished ended so the client cannot take it for a whole answer.
/// </summary>
public sealed class ProtocolErrorsTests
{
    private static (DefaultHttpContext Context, MemoryStream Body, Lifetime Lifetime) NewContext(Stream? body = null)
    {
        var lifetime = new Lifetime();
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var buffer = new MemoryStream();
        context.Response.Body = body ?? buffer;
        return (context, buffer, lifetime);
    }

    private static JsonElement Json(MemoryStream body) => JsonDocument.Parse(body.ToArray()).RootElement.Clone();

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    [InlineData(413, "invalid_request_error")]
    [InlineData(429, "rate_limit_exceeded")]
    [InlineData(500, "server_error")]
    [InlineData(503, "server_error")]
    public async Task Responses_clients_get_the_openai_error_envelope(int status, string type)
    {
        (DefaultHttpContext context, MemoryStream body, _) = NewContext();

        await ProtocolErrors.WriteAsync(context, ChatProtocol.Responses, status, "Upstream failed", "boom", CancellationToken.None);

        context.Response.StatusCode.Should().Be(status);
        context.Response.ContentType.Should().Be("application/json");
        JsonElement error = Json(body).GetProperty("error");
        (error.GetProperty("message").GetString(), error.GetProperty("type").GetString()).Should().Be(("Upstream failed: boom", type));
    }

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    [InlineData(413, "request_too_large")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(503, "overloaded_error")]
    [InlineData(529, "overloaded_error")]
    [InlineData(500, "api_error")]
    [InlineData(502, "api_error")]
    public async Task Anthropic_clients_get_the_anthropic_error_envelope(int status, string type)
    {
        (DefaultHttpContext context, MemoryStream body, _) = NewContext();

        await ProtocolErrors.WriteAsync(context, ChatProtocol.Anthropic, status, "Upstream failed", "boom", CancellationToken.None);

        context.Response.StatusCode.Should().Be(status);
        JsonElement root = Json(body);
        root.GetProperty("type").GetString().Should().Be("error");
        JsonElement error = root.GetProperty("error");
        (error.GetProperty("type").GetString(), error.GetProperty("message").GetString()).Should().Be((type, "Upstream failed: boom"));
    }

    [Fact]
    public async Task OpenAI_clients_get_problem_details()
    {
        (DefaultHttpContext context, MemoryStream body, _) = NewContext();

        await ProtocolErrors.WriteAsync(context, ChatProtocol.OpenAI, 502, "Upstream failed", "boom", CancellationToken.None);

        context.Response.StatusCode.Should().Be(502);
        context.Response.ContentType.Should().Be("application/problem+json");
        JsonElement problem = Json(body);
        (problem.GetProperty("title").GetString(), problem.GetProperty("detail").GetString(), problem.GetProperty("status").GetInt32())
            .Should().Be(("Upstream failed", "boom", 502));
    }

    [Theory]
    [InlineData(ChatProtocol.OpenAI, """data: {"error":{"message":"stalled","type":"server_error"}}""")]
    [InlineData(ChatProtocol.Anthropic, "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"stalled\"}}")]
    [InlineData(ChatProtocol.Responses, "event: error\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"stalled\",\"param\":null}")]
    public async Task A_streamed_reply_that_cannot_be_finished_ends_with_an_error_event(ChatProtocol protocol, string expected)
    {
        (DefaultHttpContext context, MemoryStream body, Lifetime lifetime) = NewContext();
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        await ProtocolErrors.EndStreamWithErrorAsync(context, new ChatCompletionRequest { Protocol = protocol }, "stalled");

        Encoding.UTF8.GetString(body.ToArray()).Should().Be(expected + "\n\n");
        lifetime.Aborted.Should().BeFalse();
    }

    [Fact]
    public async Task A_reply_that_is_not_streamed_is_broken_off_without_a_word()
    {
        (DefaultHttpContext context, MemoryStream body, Lifetime lifetime) = NewContext();
        context.Response.ContentType = "application/json";

        await ProtocolErrors.EndStreamWithErrorAsync(context, new ChatCompletionRequest { Protocol = ChatProtocol.OpenAI }, "stalled");

        lifetime.Aborted.Should().BeTrue("a truncated JSON body must not pass for an answer");
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task When_the_client_is_gone_too_the_connection_is_broken_off()
    {
        (DefaultHttpContext context, _, Lifetime lifetime) = NewContext(new GoneStream());
        context.Response.ContentType = "text/event-stream";

        await ProtocolErrors.EndStreamWithErrorAsync(context, new ChatCompletionRequest { Protocol = ChatProtocol.Anthropic }, "stalled");

        lifetime.Aborted.Should().BeTrue();
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    /// <summary>A response stream whose client has disconnected.</summary>
    private sealed class GoneStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The client disconnected."));
    }
}
