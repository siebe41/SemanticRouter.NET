using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LmStudioClaudePoc;

/// <summary>
/// Exercises the running router: forces each backend once (so real errors surface instead of a silent fallback),
/// then sends prompts with "model": "auto" and shows which backend the router picked, then streams from Claude.
/// </summary>
internal static class Demo
{
    private const string System = "Answer in at most two sentences.";

    private static readonly (string Label, string Prompt)[] RoutingPrompts =
    [
        ("easy", "Hello! How are you today?"),
        ("easy", "Summarize this in one sentence: the client meeting moved from Tuesday to Friday because the client is traveling."),
        ("hard", "Compare the trade-offs between event sourcing and a traditional CRUD data model for a billing system."),
        ("hard", "Why can calling .Result on a Task deadlock in an ASP.NET app with a synchronization context? Explain step by step."),
        ("private", "Look up the customer record for account number 4471."),
    ];

    public static async Task<int> RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        bool healthy = true;
        bool degraded = false;

        Console.WriteLine("1. Each backend, called directly (\"model\": \"lmstudio\" / \"claude\")");
        Console.WriteLine("-------------------------------------------------------------------");
        foreach (string model in new[] { "lmstudio", "claude" })
        {
            Result result = await SendAsync(http, model, "Reply with exactly one word: pong");
            healthy &= result.Ok;
            Console.WriteLine(result.Ok
                ? $"[ OK ] {model,-9} {result.Seconds,5:0.0}s  {Clip(result.Text, 70)}"
                : $"[FAIL] {model,-9} {result.Seconds,5:0.0}s  {Clip(result.Text, 300)}");
        }

        Console.WriteLine();
        Console.WriteLine("2. Automatic routing (\"model\": \"auto\")");
        Console.WriteLine("---------------------------------------");
        Console.WriteLine($"{"kind",-8} {"tier",-4} {"reason",-21} {"answered by",-11} {"try",-3} {"time",6}  reply");
        foreach ((string label, string prompt) in RoutingPrompts)
        {
            Result result = await SendAsync(http, "auto", prompt);
            string model = !result.Ok ? "-" : result.Degraded ? result.Model + "*" : result.Model;
            Console.WriteLine(
                $"{label,-8} {result.Tier,-4} {result.Reason,-21} {model,-11} {result.Attempt,-3} {result.Seconds,5:0.0}s  {Clip(result.Text, 60)}");
            healthy &= result.Ok;
            degraded |= result.Degraded;
        }

        if (degraded)
        {
            Console.WriteLine("* answered by a less capable model than the prompt needed, because the better one was unavailable.");
        }

        Console.WriteLine();
        Console.WriteLine("3. Streaming from Claude (\"stream\": true)");
        Console.WriteLine("-----------------------------------------");
        await StreamAsync(http);

        Console.WriteLine();
        Console.WriteLine(healthy
            ? "Proof of concept OK. Point any OpenAI-compatible client at " + baseUrl + "/v1 with model \"auto\"."
            : "Something failed above. The [FAIL] line shows the backend's own error; the router log has details.");
        return healthy ? 0 : 1;
    }

    private static async Task<Result> SendAsync(HttpClient http, string model, string prompt)
    {
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model,
            messages = new object[] { new { role = "system", content = System }, new { role = "user", content = prompt } },
        });
        string body = await response.Content.ReadAsStringAsync();
        double seconds = stopwatch.Elapsed.TotalSeconds;

        string Header(string name) => response.Headers.TryGetValues("X-Prompt-Route-" + name, out var v) ? v.First() : "-";
        bool isDegraded = Header("Degraded") == "true";

        if (!response.IsSuccessStatusCode)
        {
            string detail = TryRead(body, root => root.GetProperty("detail").GetString()) ?? body;
            return new Result(false, $"HTTP {(int)response.StatusCode}: {detail}", Header("Tier"), Header("Reason"), Header("Model"), Header("Attempt"), seconds, isDegraded);
        }

        string text = TryRead(body, root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()) ?? body;
        return new Result(true, text, Header("Tier"), Header("Reason"), Header("Model"), Header("Attempt"), seconds, isDegraded);
    }

    private static async Task StreamAsync(HttpClient http)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "claude",
                stream = true,
                messages = new object[] { new { role = "user", content = "Count from 1 to 10, one number per line." } },
            }),
        };

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[FAIL] HTTP {(int)response.StatusCode}: {Clip(await response.Content.ReadAsStringAsync(), 300)}");
            return;
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        double? firstToken = null;
        int chunks = 0;
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]")
            {
                continue;
            }

            string? content = TryRead(line[6..], root =>
                root.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out JsonElement c) ? c.GetString() : null);
            if (!string.IsNullOrEmpty(content))
            {
                firstToken ??= stopwatch.Elapsed.TotalSeconds;
                chunks++;
                text.Append(content);
            }
        }

        Console.WriteLine($"[ OK ] first token after {firstToken ?? 0:0.0}s, {chunks} chunks in {stopwatch.Elapsed.TotalSeconds:0.0}s: {Clip(text.ToString(), 60)}");
    }

    private static string? TryRead(string json, Func<JsonElement, string?> read)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private sealed record Result(bool Ok, string Text, string Tier, string Reason, string Model, string Attempt, double Seconds, bool Degraded);
}
