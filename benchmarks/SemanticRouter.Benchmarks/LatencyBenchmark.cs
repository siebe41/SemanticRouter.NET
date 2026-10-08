// Time to first output and total time per turn of a fixed agentic task, per model, through a running router.
//
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks -- latency http://localhost:5000 local-qwen claude-cli gpt-mini
//
// Every model gets the same three turns, sent as streaming OpenAI chat completions with one tool (Read):
//   1. "Summarize the README": an agent answers with some text and a Read call.
//   2. The same conversation plus a scripted Read call and its result: the agent answers.
//   3. A follow-up question on top of that.
// The conversation is scripted, so each model sees exactly the same requests whatever it answered before. Time to first
// output is when the first text or tool-call delta arrives; input tokens are what the backend reported.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SemanticRouter.Benchmarks;

internal static class LatencyBenchmark
{
    private const string Readme = "SemanticRouter.NET routes each prompt to the cheapest model that can handle it. It classifies prompts by meaning (embeddings) and complexity, prefers local models, then subscriptions, then paid APIs, fails over on errors and quota limits, tracks usage and budgets, and has a dashboard with hot reload.";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: latency <router url> <model> [<model> …] [--rounds N]");
            return 2;
        }

        var router = new Uri(args[1]);
        int rounds = 3;
        var models = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--rounds" && i + 1 < args.Length)
            {
                rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else
            {
                models.Add(args[i]);
            }
        }

        using var http = new HttpClient { BaseAddress = router, Timeout = TimeSpan.FromMinutes(10) };
        Console.WriteLine($"{rounds} round(s) of a 3-turn agentic task against {router}");
        Console.WriteLine();
        Console.WriteLine("| Model | Turn | Served by | First output p50 | Total p50 | Input tokens | Tool call |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (string model in models)
        {
            for (int turn = 1; turn <= 3; turn++)
            {
                var results = new List<TurnResult>();
                for (int round = 0; round < rounds; round++)
                {
                    results.Add(await SendAsync(http, Request(model, turn)));
                }

                TurnResult last = results[^1];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {model} | {turn} | {last.ServedBy ?? "?"} | {Median(results.Select(r => r.FirstOutput)):0} ms | {Median(results.Select(r => r.Total)):0} ms | {last.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "–"} | {(last.ToolCall ? "yes" : "no")} |"));
            }
        }

        return 0;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static JsonObject Request(string model, int turn)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent working in a repository. Use the tools to look at files before answering." },
            new JsonObject { ["role"] = "user", ["content"] = "Summarize the README of this repository in a few sentences." },
        };
        if (turn >= 2)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "I'll read the README first.",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "Read", ["arguments"] = "{\"path\":\"README.md\"}" } },
                },
            });
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_1", ["content"] = Readme });
        }

        if (turn >= 3)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "It routes each prompt to the cheapest capable model, local first, with failover, budgets and a dashboard." });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Which models does it prefer, and why?" });
        }

        return new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = messages,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "Read",
                        ["description"] = "Reads a file from the repository.",
                        ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
                    },
                },
            },
        };
    }

    private static async Task<TurnResult> SendAsync(HttpClient http, JsonObject body)
    {
        long started = Stopwatch.GetTimestamp();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        string? servedBy = response.Headers.TryGetValues("X-Prompt-Route-Model", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        double? firstOutput = null;
        int? inputTokens = null;
        bool toolCall = false;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]")
            {
                continue;
            }

            using JsonDocument chunk = JsonDocument.Parse(line[6..]);
            JsonElement root = chunk.RootElement;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out JsonElement prompt))
            {
                inputTokens = prompt.GetInt32();
            }

            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out JsonElement delta))
            {
                continue;
            }

            bool text = delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String && content.GetString()!.Length > 0;
            bool calls = delta.TryGetProperty("tool_calls", out _);
            toolCall |= calls;
            if ((text || calls) && firstOutput is null)
            {
                firstOutput = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        double total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new TurnResult(servedBy, firstOutput ?? total, total, inputTokens, toolCall);
    }

    private sealed record TurnResult(string? ServedBy, double FirstOutput, double Total, int? InputTokens, bool ToolCall);
}
