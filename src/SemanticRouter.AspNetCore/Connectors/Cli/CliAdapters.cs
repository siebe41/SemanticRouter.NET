using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>What the connector needs to build one CLI invocation.</summary>
internal sealed record CliRequest(
    string? Model,
    IReadOnlyList<ChatMessage> Messages,
    string WorkingDirectory,
    ConnectorOptions Options);

internal enum CliOutputMode
{
    /// <summary>Standard output is the whole reply; it is buffered and checked against the exit code.</summary>
    Text,

    /// <summary>Standard output is JSON Lines; the adapter's parser turns lines into events.</summary>
    JsonLines,
}

internal enum CliEventKind
{
    None,
    Delta,
    Final,
}

/// <summary>A parsed output line.</summary>
internal readonly record struct CliEvent(
    CliEventKind Kind,
    string? Text = null,
    bool IsError = false,
    bool QuotaSignal = false,
    int? InputTokens = null,
    int? OutputTokens = null)
{
    public static CliEvent None => default;
}

/// <summary>Parses one process's output. Stateful; one instance per run.</summary>
internal interface ICliOutputParser
{
    CliEvent Parse(string line);
}

/// <summary>Knows how to drive one CLI: arguments, input, environment and output format.</summary>
internal abstract class CliAdapter
{
    public abstract string DefaultExecutable { get; }

    public abstract CliOutputMode OutputMode { get; }

    /// <summary>Output fragments that mean the account's quota or rate limit is exhausted.</summary>
    public virtual IReadOnlyList<string> DefaultQuotaPatterns => ["rate limit", "quota", "429"];

    /// <summary>Environment variables removed by default (e.g. API keys that would switch billing).</summary>
    public virtual IReadOnlyList<string> DefaultRemovedEnvironment => [];

    /// <summary>Builds the argument list and optional standard input.</summary>
    public abstract (List<string> Arguments, string? StandardInput) Build(CliRequest request);

    public virtual ICliOutputParser CreateParser() => throw new NotSupportedException();

    public static CliAdapter For(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => new ClaudeCodeCliAdapter(),
        CliPreset.GitHubCopilot => new CopilotCliAdapter(),
        CliPreset.Codex => new CodexCliAdapter(),
        _ => new CustomCliAdapter(),
    };

    /// <summary>A CLI would read an argument starting with '-' as an option; a leading space keeps it a value.</summary>
    protected static string AsArgument(string prompt) => prompt.StartsWith('-') ? " " + prompt : prompt;

    protected static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    protected static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
}

/// <summary>
/// Anthropic Claude Code in print mode (https://code.claude.com/docs/en/headless, /cli-reference):
/// <c>claude -p &lt;prompt&gt; --output-format stream-json --verbose --include-partial-messages --tools ""
/// --disallowedTools "mcp__*" --strict-mcp-config --no-session-persistence --permission-mode dontAsk</c>.
/// Built-in and MCP tools are removed so it answers as a plain model; client system messages replace the default
/// system prompt via <c>--system-prompt-file</c>.
/// </summary>
internal sealed class ClaudeCodeCliAdapter : CliAdapter
{
    private const string StdinInstruction =
        "Reply to the conversation provided on standard input as the assistant. Output only your reply.";

    public override string DefaultExecutable => "claude";

    public override CliOutputMode OutputMode => CliOutputMode.JsonLines;

    // Message texts from https://code.claude.com/docs/en/errors (e.g. "You've hit your session limit · resets 3:45pm").
    public override IReadOnlyList<string> DefaultQuotaPatterns =>
        ["hit your", "usage limit", "spend limit", "limiting requests", "Request rejected (429)", "rate limit"];

    // In print mode ANTHROPIC_API_KEY is "always used when present", which would bill the API instead of your
    // subscription; ANTHROPIC_BASE_URL could point back at this router and loop.
    public override IReadOnlyList<string> DefaultRemovedEnvironment =>
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    public override (List<string> Arguments, string? StandardInput) Build(CliRequest request)
    {
        (string? system, string prompt) = ChatTranscript.Render(request.Messages);
        bool useStdin = prompt.Length > request.Options.MaxArgumentLength;

        // The prompt comes straight after -p, before any variadic option that could swallow it.
        var arguments = new List<string> { "-p", useStdin ? StdinInstruction : AsArgument(prompt) };
        arguments.AddRange(
        [
            "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--tools", string.Empty,
            "--disallowedTools", "mcp__*",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--permission-mode", "dontAsk",
        ]);

        if (!string.IsNullOrEmpty(request.Model))
        {
            arguments.AddRange(["--model", request.Model]);
        }

        if (system is not null)
        {
            string file = Path.Combine(request.WorkingDirectory, "system-prompt.txt");
            File.WriteAllText(file, system);
            arguments.AddRange(["--system-prompt-file", file]);
        }

        return (arguments, useStdin ? prompt : null);
    }

    public override ICliOutputParser CreateParser() => new Parser();

    private sealed class Parser : ICliOutputParser
    {
        public CliEvent Parse(string line)
        {
            if (!line.StartsWith('{'))
            {
                return CliEvent.None;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                switch (GetString(root, "type"))
                {
                    case "stream_event"
                        when root.TryGetProperty("event", out JsonElement evt)
                             && evt.TryGetProperty("delta", out JsonElement delta)
                             && GetString(delta, "type") == "text_delta":
                        return new CliEvent(CliEventKind.Delta, GetString(delta, "text"));

                    case "system" when GetString(root, "subtype") == "api_retry":
                        return new CliEvent(CliEventKind.None, QuotaSignal: GetString(root, "error") is "rate_limit" or "billing_error");

                    case "result":
                        bool isError = (root.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True)
                                       || GetString(root, "subtype") is { } subtype && subtype != "success";
                        string? text = GetString(root, "result");
                        if (isError && root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
                        {
                            text = string.Join("; ", errors.EnumerateArray().Select(x => x.ToString()).Prepend(text).Where(x => !string.IsNullOrEmpty(x)));
                        }

                        root.TryGetProperty("usage", out JsonElement usage);
                        return new CliEvent(
                            CliEventKind.Final,
                            text,
                            isError,
                            InputTokens: GetInt(usage, "input_tokens"),
                            OutputTokens: GetInt(usage, "output_tokens"));

                    default:
                        return CliEvent.None;
                }
            }
            catch (JsonException)
            {
                return CliEvent.None;
            }
        }
    }
}

/// <summary>
/// GitHub Copilot CLI in programmatic mode
/// (https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference):
/// <c>copilot --prompt=&lt;prompt&gt; -s --no-color --stream off --no-ask-user --deny-tool=shell --deny-tool=write
/// --deny-tool=url --deny-tool=memory --disable-builtin-mcps --no-custom-instructions --no-auto-update</c>.
/// Without <c>--allow-all-tools</c> nothing is pre-approved, and deny rules win over any allow rule.
/// </summary>
internal sealed class CopilotCliAdapter : CliAdapter
{
    public override string DefaultExecutable => "copilot";

    public override CliOutputMode OutputMode => CliOutputMode.Text;

    public override IReadOnlyList<string> DefaultQuotaPatterns =>
        ["quota", "rate limit", "AI credits", "budget", "429"];

    public override (List<string> Arguments, string? StandardInput) Build(CliRequest request)
    {
        string prompt = ChatTranscript.RenderInline(request.Messages);
        bool useStdin = prompt.Length > request.Options.MaxArgumentLength;

        // "--prompt=<value>" keeps the prompt a single token even when it starts with '-'. Piped input is the
        // documented alternative for long prompts ("echo ... | copilot").
        var arguments = new List<string>();
        if (!useStdin)
        {
            arguments.Add("--prompt=" + prompt);
        }

        arguments.AddRange(
        [
            "-s", "--no-color", "--stream", "off", "--no-ask-user",
            "--deny-tool=shell", "--deny-tool=write", "--deny-tool=url", "--deny-tool=memory",
            "--disable-builtin-mcps", "--no-custom-instructions", "--no-auto-update",
        ]);

        if (!string.IsNullOrEmpty(request.Model))
        {
            arguments.Add("--model=" + request.Model);
        }

        return (arguments, useStdin ? prompt : null);
    }
}

/// <summary>
/// OpenAI Codex CLI non-interactive mode (https://learn.chatgpt.com/docs/non-interactive-mode):
/// <c>codex exec --json --sandbox read-only --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules
/// --color never -c features.shell_tool=false -c web_search="disabled" -</c>, prompt on standard input. The final
/// <c>agent_message</c> item is the reply.
/// </summary>
internal sealed class CodexCliAdapter : CliAdapter
{
    public override string DefaultExecutable => "codex";

    public override CliOutputMode OutputMode => CliOutputMode.JsonLines;

    // e.g. "You’ve hit your usage limit. … try again at {time}." (curly apostrophe, so match the tail).
    public override IReadOnlyList<string> DefaultQuotaPatterns =>
        ["hit your usage limit", "Quota exceeded", "out of credits", "at capacity", "rate limit"];

    // CODEX_API_KEY "takes precedence over any other auth method" and switches billing to the API.
    public override IReadOnlyList<string> DefaultRemovedEnvironment => ["CODEX_API_KEY"];

    public override (List<string> Arguments, string? StandardInput) Build(CliRequest request)
    {
        var arguments = new List<string>
        {
            "exec", "--json", "--sandbox", "read-only", "--skip-git-repo-check", "--ephemeral",
            "--ignore-user-config", "--ignore-rules", "--color", "never",
            "-c", "features.shell_tool=false", "-c", "web_search=\"disabled\"",
        };

        if (!string.IsNullOrEmpty(request.Model))
        {
            arguments.AddRange(["-m", request.Model]);
        }

        arguments.Add("-");
        return (arguments, ChatTranscript.RenderInline(request.Messages));
    }

    public override ICliOutputParser CreateParser() => new Parser();

    private sealed class Parser : ICliOutputParser
    {
        private string? _lastMessage;

        public CliEvent Parse(string line)
        {
            if (!line.StartsWith('{'))
            {
                return CliEvent.None;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                switch (GetString(root, "type"))
                {
                    case "item.completed" when root.TryGetProperty("item", out JsonElement item) && GetString(item, "type") == "agent_message":
                        _lastMessage = GetString(item, "text");
                        return CliEvent.None;

                    case "turn.completed":
                        root.TryGetProperty("usage", out JsonElement usage);
                        return new CliEvent(CliEventKind.Final, _lastMessage, InputTokens: GetInt(usage, "input_tokens"), OutputTokens: GetInt(usage, "output_tokens"));

                    case "turn.failed" when root.TryGetProperty("error", out JsonElement error):
                        return new CliEvent(CliEventKind.Final, GetString(error, "message"), IsError: true);

                    case "error":
                        return new CliEvent(CliEventKind.Final, GetString(root, "message"), IsError: true);

                    default:
                        return CliEvent.None;
                }
            }
            catch (JsonException)
            {
                return CliEvent.None;
            }
        }
    }
}

/// <summary>
/// Any other command: <see cref="ConnectorOptions.Arguments"/> is the full argument list with <c>{model}</c> and
/// <c>{prompt}</c> placeholders; standard output is the reply and a non-zero exit code is a failure.
/// </summary>
internal sealed class CustomCliAdapter : CliAdapter
{
    public override string DefaultExecutable => string.Empty;

    public override CliOutputMode OutputMode => CliOutputMode.Text;

    public override (List<string> Arguments, string? StandardInput) Build(CliRequest request)
    {
        string prompt = ChatTranscript.RenderInline(request.Messages);
        bool asArgument = request.Options.PromptInput == CliPromptInput.Argument;

        var arguments = new List<string>();
        foreach (string template in request.Options.Arguments ?? [])
        {
            if (template == "{model}" && string.IsNullOrEmpty(request.Model))
            {
                continue;
            }

            arguments.Add(template
                .Replace("{model}", request.Model ?? string.Empty, StringComparison.Ordinal)
                .Replace("{prompt}", asArgument ? prompt : string.Empty, StringComparison.Ordinal));
        }

        return (arguments, asArgument ? null : prompt);
    }
}
