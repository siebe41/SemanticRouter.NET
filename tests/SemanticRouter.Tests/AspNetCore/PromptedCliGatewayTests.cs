using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Prompted tool calling through a CLI: streaming before the call, repairing a broken call, buffering when asked.</summary>
public sealed class PromptedCliGatewayTests : RouterHostTest
{
    private const string ToolsRequest = """
        {"model":"auto","stream":STREAM,"messages":[{"role":"user","content":"summarize the readme"}],
         "tools":[{"type":"function","function":{"name":"Read","description":"Reads a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}]}
        """;

    public PromptedCliGatewayTests() => ClassifyAs(tier: 4);

    private static string Esc(string text) => JsonSerializer.Serialize(text)[1..^1];

    private static StringContent Request(bool stream) => Json(ToolsRequest.Replace("STREAM", stream ? "true" : "false", StringComparison.Ordinal));

    private Task<HttpClient> StartPromptedAsync(Action<ConnectorOptions>? connector = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            o => o.Models.First(m => m.Name == "claude-cli").Capabilities.Add("tools"),
            s => s.Configure<SemanticRouterConnectorsOptions>(o =>
            {
                o.Connectors["claude"].ToolCalling = CliToolCalling.Prompted;
                connector?.Invoke(o.Connectors["claude"]);
            }),
            withCli: true);

    private RoutingActivityEntry LastActivity() => Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();

    [Fact]
    public async Task Text_reaches_the_client_before_the_model_has_finished_its_tool_call()
    {
        var gate = new TaskCompletionSource();
        Cli = new FakeCliProcessRunner(_ => new GatedCliProcess(
            [ClaudeStream.Init, ClaudeStream.Delta(Esc("I'll read the README first.\n"))],
            gate.Task,
            [
                ClaudeStream.Delta(Esc("```tool_call\n{\"name\": \"Read\", \"input\": {\"path\": \"README.md\"}}\n```")),
                ClaudeStream.Success(Esc("I'll read the README first.\n```tool_call\n{\"name\": \"Read\", \"input\": {\"path\": \"README.md\"}}\n```")),
            ]));
        HttpClient client = await StartPromptedAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Request(stream: true) };
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var before = new StringBuilder();
        while (!before.ToString().Contains("README first.", StringComparison.Ordinal))
        {
            string? line = await reader.ReadLineAsync(timeout.Token);
            line.Should().NotBeNull("the text arrives while the CLI is still running");
            before.AppendLine(line);
        }

        gate.SetResult(); // only now does the model "finish" the call
        string after = await reader.ReadToEndAsync(timeout.Token);

        before.ToString().Should().NotContain("tool_calls");
        after.Should().Contain("\"tool_calls\"").And.Contain("\"name\":\"Read\"").And.Contain("\"finish_reason\":\"tool_calls\"");
        after.Should().NotContain("```");
    }

    [Fact]
    public async Task A_broken_tool_call_is_repaired_in_a_short_second_run()
    {
        int runs = 0;
        Cli = new FakeCliProcessRunner(_ => ++runs == 1
            ? new FakeCliProcess([ClaudeStream.Success(Esc("Reading it.\n```tool_call\n{\"name\": \"Read\", \"input\": {path: \"README.md\"}}\n```"))])
            : new FakeCliProcess([ClaudeStream.Success(Esc("```tool_call\n{\"name\": \"Read\", \"input\": {\"path\": \"README.md\"}}\n```"))]));
        HttpClient client = await StartPromptedAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Request(stream: false));

        JsonElement message = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("content").GetString().Should().Be("Reading it.");
        message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"path\":\"README.md\"}");

        Cli.Invocations.Should().HaveCount(2);
        string repair = string.Join(" ", Cli.Invocations[1].Arguments) + Cli.Invocations[1].StandardInput;
        repair.Should().Contain("could not be used").And.Contain("{path: \"README.md\"}").And.Contain("\"required\":[\"path\"]");
        repair.Should().NotContain("summarize the readme", "the conversation is not sent again");
        LastActivity().Notes.Should().Be("tool call repaired");
    }

    [Fact]
    public async Task A_call_that_cannot_be_repaired_is_sent_as_text_and_recorded()
    {
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Success(Esc("```tool_call\n{\"name\": \"Write\", \"input\": {}}\n```")));
        HttpClient client = await StartPromptedAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Request(stream: false));

        JsonElement choice = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("stop");
        choice.GetProperty("message").GetProperty("content").GetString().Should().Contain("\"Write\"");
        Cli.Invocations.Should().HaveCount(2, "one repair attempt, no more");
        LastActivity().Notes.Should().StartWith("malformed tool call: there is no tool named 'Write'");
    }

    [Fact]
    public async Task Repair_can_be_turned_off()
    {
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Success(Esc("```tool_call\n{\"name\": \"Read\", \"input\": [}\n```")));
        HttpClient client = await StartPromptedAsync(c => c.RepairToolCalls = false);

        await client.PostAsync("/v1/chat/completions", Request(stream: false));

        Cli.Invocations.Should().ContainSingle();
        LastActivity().Notes.Should().StartWith("malformed tool call: invalid JSON");
    }

    [Fact]
    public async Task With_streaming_off_a_run_that_fails_after_text_still_fails_over()
    {
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Delta("Let me think"), ClaudeStream.Error("API Error: overloaded"));
        CloudHttp = StubHttpMessageHandler.Json("""{"choices":[{"message":{"role":"assistant","content":"from cloud"},"finish_reason":"stop"}]}""");
        HttpClient client = await StartPromptedAsync(c => c.StreamPromptedReplies = false);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Request(stream: true));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Let me think");
    }

    [Fact]
    public async Task The_overview_reports_latency_per_connector()
    {
        HttpClient client = await StartPromptedAsync();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        JsonElement status = JsonDocument.Parse(await client.GetStringAsync("/router/api/status")).RootElement;

        JsonElement latency = status.GetProperty("activity").GetProperty("latency").EnumerateArray().Single();
        latency.GetProperty("connector").GetString().Should().Be("claude");
        latency.GetProperty("kind").GetString().Should().Be("Cli/ClaudeCode");
        latency.GetProperty("requests").GetInt32().Should().Be(1);
        latency.GetProperty("firstByteP50").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task The_overview_reports_each_endpoint_and_checks_one_on_demand()
    {
        HttpClient client = await StartPromptedAsync();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        JsonElement status = JsonDocument.Parse(await client.GetStringAsync("/router/api/status")).RootElement;
        JsonElement claude = status.GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("connector").GetString() == "claude");
        claude.GetProperty("program").GetString().Should().Be("claude");
        claude.GetProperty("via").GetString().Should().Be("Anthropic");
        claude.GetProperty("state").GetString().Should().Be("ok");
        claude.GetProperty("attempts").GetInt32().Should().Be(1);
        claude.GetProperty("successRate").GetDouble().Should().Be(1);
        status.GetProperty("machine").GetString().Should().Be(Environment.MachineName);

        HttpResponseMessage check = await client.PostAsync("/router/api/connectors/claude/check", null);
        check.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        JsonElement result = JsonDocument.Parse(await check.Content.ReadAsStringAsync()).RootElement;
        result.GetProperty("messages").GetArrayLength().Should().BeGreaterThan(0);
        JsonElement after = JsonDocument.Parse(await client.GetStringAsync("/router/api/status")).RootElement
            .GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("connector").GetString() == "claude");
        after.GetProperty("check").GetProperty("status").GetString().Should().Be(result.GetProperty("status").GetString());

        (await client.PostAsync("/router/api/connectors/nope/check", null)).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    /// <summary>A CLI that writes some lines, then waits for the test before writing the rest.</summary>
    private sealed class GatedCliProcess(IReadOnlyList<string> first, Task gate, IReadOnlyList<string> rest) : ICliProcess
    {
        public string StandardError => string.Empty;

        public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string line in first)
            {
                yield return line;
            }

            await gate.WaitAsync(cancellationToken);
            foreach (string line in rest)
            {
                yield return line;
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
