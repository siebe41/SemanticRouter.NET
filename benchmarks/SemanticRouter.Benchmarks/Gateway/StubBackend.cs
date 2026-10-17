using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.Benchmarks.Gateway;

/// <summary>
/// An OpenAI-compatible backend on loopback that answers at once, so what is measured is the router. The requested
/// model picks the behaviour:
/// <c>ok</c> answers (streamed or not); <c>500</c> and <c>429</c> fail; <c>hang</c> never sends headers;
/// <c>stall</c> sends a chunk and then nothing; <c>drop</c> sends a chunk and drops the connection;
/// <c>slowstream</c> sends a chunk every 200 ms for 4 s.
/// </summary>
internal sealed class StubBackend : IAsyncDisposable
{
    private static readonly byte[] Done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");

    private readonly WebApplication _app;

    private StubBackend(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<StubBackend> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapPost("/v1/chat/completions", AnswerAsync);
        await app.StartAsync();
        string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new StubBackend(app, new Uri(url));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task AnswerAsync(HttpContext context)
    {
        using JsonDocument request = await JsonDocument.ParseAsync(context.Request.Body);
        string model = request.RootElement.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? "ok" : "ok";
        bool stream = request.RootElement.TryGetProperty("stream", out JsonElement s) && s.ValueKind == JsonValueKind.True;
        CancellationToken aborted = context.RequestAborted;
        try
        {
            switch (model)
            {
                case "500":
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                case "429":
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = "60";
                    return;
                case "hang":
                    await Task.Delay(Timeout.Infinite, aborted);
                    return;
            }

            if (!stream)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"id":"stub","object":"chat.completion","model":"stub","choices":[{"index":0,"message":{"role":"assistant","content":"Hello from the stub."},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":5,"total_tokens":17}}""",
                    aborted);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            await ChunkAsync(context, "Hel", aborted);
            switch (model)
            {
                case "stall":
                    await Task.Delay(Timeout.Infinite, aborted);
                    return;
                case "drop":
                    context.Abort();
                    return;
                case "slowstream":
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(200, aborted);
                        await ChunkAsync(context, " more", aborted);
                    }

                    break;
            }

            await ChunkAsync(context, "lo from the stub.", aborted);
            await context.Response.WriteAsync(
                """data: {"id":"stub","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":5,"total_tokens":17}}""" + "\n\n",
                aborted);
            await context.Response.Body.WriteAsync(Done, aborted);
        }
        catch (OperationCanceledException)
        {
            // The router gave up on the request: what the scenario is about.
        }
    }

    private static async Task ChunkAsync(HttpContext context, string text, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(
            "data: " + JsonSerializer.Serialize(new { id = "stub", @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta = new { content = text } } } }) + "\n\n",
            cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
