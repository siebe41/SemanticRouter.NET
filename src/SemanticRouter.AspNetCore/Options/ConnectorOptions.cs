using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>Built-in connector implementations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorType>))]
public enum ConnectorType
{
    /// <summary>
    /// HTTP <c>POST /v1/chat/completions</c> pass-through: OpenAI, Azure OpenAI, Anthropic (OpenAI-compatible endpoint),
    /// GitHub Models, Gemini API, OpenRouter, Groq, Mistral, LM Studio, Ollama, vLLM, LiteLLM, …
    /// </summary>
    OpenAICompatible = 0,

    /// <summary>Runs a vendor CLI (Claude Code, GitHub Copilot CLI, Codex CLI, or any command) as a subprocess.</summary>
    Cli = 1,

    /// <summary>
    /// HTTP <c>POST /v1/messages</c> (the Anthropic Messages API) with an API key. Requests from Anthropic-protocol
    /// clients such as Claude Code pass through untouched; OpenAI-protocol requests are translated.
    /// </summary>
    Anthropic = 2,

    /// <summary>
    /// HTTP <c>POST /v1/responses</c> (the OpenAI Responses API) with an API key. Requests from Responses clients such
    /// as Codex CLI pass through untouched (hosted tools and reasoning items included); other clients are translated.
    /// </summary>
    Responses = 3,

    /// <summary>
    /// GitHub Copilot through GitHub's official Copilot SDK, on your own Copilot sign-in: native streaming, tool calls
    /// and model discovery. Needs the <c>SemanticRouter.Connectors.Copilot</c> package (<c>.AddCopilotConnector()</c>;
    /// the <c>semantic-router</c> tool includes it) and the <c>copilot</c> CLI, which the SDK runs as its runtime.
    /// </summary>
    Copilot = 4,
}

/// <summary>How a CLI connector handles requests that offer tools (agentic clients such as Claude Code).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CliToolCalling>))]
public enum CliToolCalling
{
    /// <summary>Decline such requests; they fall through to a model whose connector can carry tools. Default.</summary>
    None = 0,

    /// <summary>
    /// Describe the tools in the prompt and ask the model to answer with a fenced <c>tool_call</c> block, which the
    /// router turns back into a structured tool call for the client. Text in, text out: the CLI's own tools stay off.
    /// </summary>
    Prompted = 1,
}

/// <summary>Ready-made settings for well-known CLIs. See <c>docs/providers.md</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CliPreset>))]
public enum CliPreset
{
    /// <summary>No preset: you supply <see cref="ConnectorOptions.Executable"/> and <see cref="ConnectorOptions.Arguments"/>.</summary>
    Custom = 0,

    /// <summary>Anthropic Claude Code (<c>claude -p</c>), using your own Claude login.</summary>
    ClaudeCode = 1,

    /// <summary>GitHub Copilot CLI (<c>copilot -p</c>), using your own GitHub Copilot login.</summary>
    GitHubCopilot = 2,

    /// <summary>OpenAI Codex CLI (<c>codex exec</c>), using your own ChatGPT login.</summary>
    Codex = 3,
}

/// <summary>How a custom CLI receives the prompt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CliPromptInput>))]
public enum CliPromptInput
{
    /// <summary>The prompt is written to standard input.</summary>
    Stdin = 0,

    /// <summary>The prompt replaces the <c>{prompt}</c> placeholder in <see cref="ConnectorOptions.Arguments"/>.</summary>
    Argument = 1,
}

/// <summary>
/// One connector: a way of reaching one or more models. Bound from <c>SemanticRouter:Connectors:{name}</c>;
/// <see cref="ModelDefinition.Connector"/> refers to it by that name.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Which implementation to use. Default <see cref="ConnectorType.OpenAICompatible"/>.</summary>
    public ConnectorType Type { get; set; } = ConnectorType.OpenAICompatible;

    // ---- OpenAICompatible -------------------------------------------------------------------------------------

    /// <summary>HTTP: scheme, host and optional port, e.g. <c>http://localhost:1234</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>HTTP: request path. Default <c>/v1/chat/completions</c>.</summary>
    public string RequestPath { get; set; } = "/v1/chat/completions";

    /// <summary>HTTP: sent as <c>Authorization: Bearer {ApiKey}</c>. Load it from user secrets or environment variables.</summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP: extra request headers (e.g. Azure's <c>api-key</c>, GitHub's <c>X-GitHub-Api-Version</c>).</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HTTP: client request headers copied to the upstream request (e.g. <c>anthropic-beta</c>). Authorization headers
    /// are never forwarded; credentials always come from configuration.
    /// </summary>
    public IList<string> ForwardHeaders { get; } = new List<string>();

    /// <summary>Anthropic: the <c>anthropic-version</c> header. Default <c>2023-06-01</c>.</summary>
    public string AnthropicVersion { get; set; } = "2023-06-01";

    // ---- Cli --------------------------------------------------------------------------------------------------

    /// <summary>CLI: preset for a well-known tool. Default <see cref="CliPreset.Custom"/>.</summary>
    public CliPreset Preset { get; set; } = CliPreset.Custom;

    /// <summary>
    /// CLI: executable name or path. Defaults to the preset's command (<c>claude</c>, <c>copilot</c>, <c>codex</c>).
    /// It is started directly, never through a shell, so prompts cannot inject commands; on Windows point this at the
    /// tool's <c>.exe</c> rather than an npm <c>.cmd</c> shim.
    /// </summary>
    public string? Executable { get; set; }

    /// <summary>
    /// CLI: for <see cref="CliPreset.Custom"/>, the full argument list (placeholders <c>{model}</c> and <c>{prompt}</c>);
    /// for presets, extra arguments appended to the preset's own.
    /// </summary>
#pragma warning disable CA2227 // Settable so configuration binding replaces the list.
    public IList<string>? Arguments { get; set; }
#pragma warning restore CA2227

    /// <summary>CLI (custom only): how the prompt is passed. Default <see cref="CliPromptInput.Stdin"/>.</summary>
    public CliPromptInput PromptInput { get; set; } = CliPromptInput.Stdin;

    /// <summary>CLI: what to do with requests that offer tools. Default <see cref="CliToolCalling.None"/>.</summary>
    public CliToolCalling ToolCalling { get; set; } = CliToolCalling.None;

    /// <summary>
    /// CLI, prompted tool calling: send the reply's text while the model is still writing, holding back only what may
    /// be a tool call. Default <see langword="true"/>. Off: the whole reply is buffered, so a failed run can still fail
    /// over to another model even after it produced text.
    /// </summary>
    public bool StreamPromptedReplies { get; set; } = true;

    /// <summary>
    /// CLI, prompted tool calling: when the model writes a tool call that cannot be read, ask it once, in a short
    /// separate run, to rewrite just that call. Default <see langword="true"/>. Either way the activity entry says so.
    /// </summary>
    public bool RepairToolCalls { get; set; } = true;

    /// <summary>
    /// CLI: output fragments (case-insensitive) that mean "quota or rate limit exhausted". They put the model in the
    /// longer quota cooldown. Presets supply sensible defaults; entries here are added to them.
    /// </summary>
    public IList<string> QuotaErrorPatterns { get; } = new List<string>();

    /// <summary>CLI: environment variables to set for the child process.</summary>
    public IDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// CLI: environment variables to remove from the child process. When <see langword="null"/>, the preset's defaults
    /// apply; for Claude Code these stop an inherited API key from switching billing away from your subscription and
    /// stop <c>ANTHROPIC_BASE_URL</c> from looping requests back into this router.
    /// </summary>
#pragma warning disable CA2227 // Settable so configuration binding replaces the preset defaults.
    public IList<string>? RemoveEnvironmentVariables { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// CLI: prompts longer than this many characters are sent on standard input instead of as an argument (where the
    /// CLI supports it) to stay within operating-system command-line limits. Default 24,000.
    /// </summary>
    public int MaxArgumentLength { get; set; } = 24_000;

    /// <summary>CLI: maximum processes running at once; further requests wait. Default 2.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>
    /// CLI only: when this many requests are already waiting for a free process, the next one goes to another model
    /// instead of queueing (a long queue on a subscription CLI is slower than a different model). <see langword="null"/>:
    /// always wait.
    /// </summary>
    public int? MaxQueue { get; set; }

    /// <summary>
    /// Copilot only: the Copilot runtime's home directory (<c>COPILOT_HOME</c>): where its own sign-in lives when it is not
    /// in the system keychain. Default <c>~/.copilot</c>, the same as the <c>copilot</c> CLI, so its login is reused.
    /// </summary>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// CLI: serve requests from other machines. Default <see langword="false"/>: a CLI connector signed in with your
    /// personal subscription only answers requests that originate on this machine, and remote callers fall through to
    /// the next model. See <c>docs/TERMS.md</c> before changing this.
    /// </summary>
    public bool AllowRemoteClients { get; set; }

    // ---- Both -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Time allowed before the first output arrives (HTTP: response headers; CLI: first token or the whole reply for
    /// non-streaming CLIs). Default 100 seconds for HTTP, 5 minutes for CLIs when left at zero.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    internal TimeSpan EffectiveTimeout =>
        Timeout > TimeSpan.Zero ? Timeout : Type is ConnectorType.Cli or ConnectorType.Copilot ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(100);

    /// <summary>The wire protocol an HTTP connector speaks.</summary>
    internal ChatProtocol Protocol => Type switch
    {
        ConnectorType.Anthropic => ChatProtocol.Anthropic,
        ConnectorType.Responses => ChatProtocol.Responses,
        _ => ChatProtocol.OpenAI,
    };

    /// <summary>The effective request path: the configured one, or the protocol's default when left at the OpenAI default.</summary>
    internal string EffectiveRequestPath => RequestPath != "/v1/chat/completions"
        ? RequestPath
        : Type switch
        {
            ConnectorType.Anthropic => "/v1/messages",
            ConnectorType.Responses => "/v1/responses",
            _ => RequestPath,
        };
}

/// <summary>The connector catalog, bound from <c>SemanticRouter:Connectors</c>.</summary>
public sealed class SemanticRouterConnectorsOptions
{
    /// <summary>Connectors by name (case-insensitive).</summary>
    public IDictionary<string, ConnectorOptions> Connectors { get; } =
        new Dictionary<string, ConnectorOptions>(StringComparer.OrdinalIgnoreCase);
}
