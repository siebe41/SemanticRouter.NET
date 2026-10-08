using System.Text;
using System.Text.Json;

// Behaviour is chosen with FAKE_CLI_MODE so the router's own argument and stdin handling is what gets exercised.
//   claude-echo  Claude Code stream-json reply describing what the process received (args, stdin, env, cwd).
//   args-json    Plain-text reply: the arguments as a JSON array, then stdin.
//   cat          Plain-text reply: stdin, then a line "(done)".
//   quota        Writes a quota message to stderr and exits with code 3.
//   slow         Sleeps, then creates the file named by FAKE_CLI_MARKER (which must not happen once killed).
//   claude-agent A simulated Claude Code agent for latency benchmarks: after FAKE_CLI_STARTUP_MS (default 1500) it streams
//                a reply word by word, FAKE_CLI_WORD_MS apart (default 30). Without a tool result in the prompt the reply
//                explains its plan and ends in a tool_call block; with one, it answers.
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);
string mode = Environment.GetEnvironmentVariable("FAKE_CLI_MODE") ?? "cat";

switch (mode)
{
    case "claude-echo":
    {
        string input = await ReadStdinAsync();
        string? system = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--system-prompt")
            {
                system = args[i + 1];
            }
        }

        // Claude Code puts the prompt after "-p"/"--print" when it fits on the command line.
        int print = Array.FindIndex(args, a => a is "-p" or "--print");
        string? prompt = print >= 0 && print + 1 < args.Length && !args[print + 1].StartsWith("--", StringComparison.Ordinal) ? args[print + 1] : null;
        var report = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["system"] = system,
            ["stdinLength"] = input.Length,
            ["stdin"] = input.Length <= 200 ? input : null,
            ["secret"] = Environment.GetEnvironmentVariable("SR_TEST_SECRET") ?? "unset",
            ["set"] = Environment.GetEnvironmentVariable("SR_TEST_SET") ?? "unset",
            ["files"] = Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Count(),
        };
        string reply = JsonSerializer.Serialize(report);
        WriteLine(JsonSerializer.Serialize(new { type = "system", subtype = "init", apiKeySource = "none" }));
        WriteLine(JsonSerializer.Serialize(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "text_delta", text = reply } } }));
        WriteLine(JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = reply }));
        return 0;
    }

    case "args-json":
    {
        string input = await ReadStdinAsync();
        Console.Out.Write(JsonSerializer.Serialize(args) + "\n" + input);
        return 0;
    }

    case "quota":
        await Console.Error.WriteLineAsync("Error: You have exceeded your daily quota");
        return 3;

    case "slow":
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Environment.GetEnvironmentVariable("FAKE_CLI_MARKER") is { Length: > 0 } marker)
        {
            await File.WriteAllTextAsync(marker, "still running");
        }

        return 0;

    case "claude-agent":
    {
        string input = await ReadStdinAsync() + string.Join(' ', args);
        int startup = int.TryParse(Environment.GetEnvironmentVariable("FAKE_CLI_STARTUP_MS"), out int s) ? s : 1500;
        int perWord = int.TryParse(Environment.GetEnvironmentVariable("FAKE_CLI_WORD_MS"), out int w) ? w : 30;
        bool answered = input.Contains("[tool_result", StringComparison.Ordinal);
        string reply = answered
            ? "The README describes a semantic router that classifies each prompt by meaning and complexity, then sends it to the cheapest model that can handle it: local models first, subscriptions next, paid APIs last. It also covers failover, budgets and the dashboard."
            : "I'll start by reading the README so the summary is based on what it actually says, then I'll pull out the main points: what the project does, how routing works, and how to run it.\n```tool_call\n{\"name\": \"Read\", \"input\": {\"path\": \"README.md\"}}\n```";
        WriteLine(JsonSerializer.Serialize(new { type = "system", subtype = "init", apiKeySource = "none" }));
        await Task.Delay(startup);
        string[] words = reply.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string text = (i == 0 ? string.Empty : " ") + words[i];
            WriteLine(JsonSerializer.Serialize(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "text_delta", text } } }));
            await Task.Delay(perWord);
        }

        WriteLine(JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = reply, usage = new { input_tokens = input.Length / 4, output_tokens = words.Length } }));
        return 0;
    }

    default:
        Console.Out.Write(await ReadStdinAsync() + "\n(done)\n");
        return 0;
}

static async Task<string> ReadStdinAsync()
{
    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    return await reader.ReadToEndAsync();
}

static void WriteLine(string line) => Console.Out.Write(line + "\n");
