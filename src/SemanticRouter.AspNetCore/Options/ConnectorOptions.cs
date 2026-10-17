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

/// <summary>How strictly a prompted-tool-calling reply is read for tool calls.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptedToolParsing>))]
public enum PromptedToolParsing
{
    /// <summary>
    /// Also accept the near-misses models write: another tool-ish fence label, a <c>json</c> or unlabelled fence or bare
    /// JSON that names a tool, and calls in the middle of the reply. Default.
    /// </summary>
    Lenient = 0,

    /// <summary>
    /// Only a fence with the request's own label, which carries a random token the conversation has never shown, naming
    /// a tool of the request, at the end of the reply. Anything else stays text. Harder for text a tool returned to pass
    /// for a call; models that cannot follow the format answer in text instead.
    /// </summary>
    Strict = 1,
}

/// <summary>Whether the router marks the requests it builds for an Anthropic backend for prompt caching.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptCaching>))]
public enum PromptCaching
{
    /// <summary>
    /// Mark translated requests whose prompt is long enough to cache: one breakpoint on the system prompt (tools and
    /// system) and Anthropic's automatic caching for the conversation. Default.
    /// </summary>
    Auto = 0,

    /// <summary>Send no cache markers of the router's own. For servers that reject them.</summary>
    Off = 1,
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

/// <summary>How a CLI receives the prompt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CliPromptInput>))]
public enum CliPromptInput
{
    /// <summary>The prompt is written to standard input, where other local users cannot read it.</summary>
    Stdin = 0,

    /// <summary>
    /// The prompt is a command-line argument, which other local users can read while the process runs: it replaces the
    /// <c>{prompt}</c> placeholder in <see cref="ConnectorOptions.Arguments"/> (custom), follows <c>-p</c> (Claude Code) or
    /// is <c>--prompt=</c> (Copilot), up to <see cref="ConnectorOptions.MaxArgumentLength"/>. Codex always reads stdin.
    /// </summary>
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

    /// <summary>
    /// Anthropic: cache markers on the requests the router translates (from OpenAI and Responses clients). Default
    /// <see cref="PromptCaching.Auto"/>. Requests from Anthropic clients pass through with their own markers.
    /// </summary>
    public PromptCaching PromptCaching { get; set; } = PromptCaching.Auto;

    /// <summary>
    /// Anthropic, with <see cref="PromptCaching.Auto"/>: how long a cache entry lives, <c>5m</c> (default) or <c>1h</c>.
    /// A one-hour entry costs twice the input price to write instead of 1.25 times, so it only pays off when turns are
    /// often more than five minutes apart. Conversation routing counts the cache warm for as long
    /// (<see cref="ConversationRoutingOptions.CacheLifetime"/>).
    /// </summary>
    public string CacheTtl { get; set; } = DefaultCacheTtl;

    /// <summary>The <see cref="CacheTtl"/> used when it is not set.</summary>
    public const string DefaultCacheTtl = "5m";

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

    /// <summary>CLI: how the prompt is passed. Default <see cref="CliPromptInput.Stdin"/>.</summary>
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
    /// CLI, prompted tool calling: how strictly the reply is read for tool calls. Default <see cref="PromptedToolParsing.Lenient"/>,
    /// until <see cref="PromptedToolParsing.Strict"/> has been measured on smaller models (see <c>docs/providers.md</c>).
    /// The run's random fence label, defused tool results and refusing calls copied from a tool result apply in both.
    /// </summary>
    public PromptedToolParsing PromptedToolParsing { get; set; } = PromptedToolParsing.Lenient;

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
    /// CLI: with <see cref="CliPromptInput.Argument"/>, prompts longer than this many characters are sent on standard
    /// input instead, to stay within operating-system command-line limits. A Claude Code system prompt longer than this
    /// is moved into the conversation text. Default 24,000.
    /// </summary>
    public int MaxArgumentLength { get; set; } = 24_000;

    /// <summary>CLI: maximum processes running at once; further requests wait. Default 2.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>
    /// CLI and Copilot: when this many requests are already waiting for a free process, the next one goes to another
    /// model instead of queueing (a long queue on a subscription CLI is slower than a different model, and an unbounded
    /// one lets a burst of requests hold the router's memory and connections). <see langword="null"/>:
    /// <see cref="DefaultMaxQueue"/>; 0: never wait.
    /// </summary>
    public int? MaxQueue { get; set; }

    /// <summary>The queue length used when <see cref="MaxQueue"/> is not set.</summary>
    public const int DefaultMaxQueue = 16;

    /// <summary><see cref="MaxQueue"/>, or <see cref="DefaultMaxQueue"/> when it is not set.</summary>
    [JsonIgnore]
    public int EffectiveMaxQueue => MaxQueue ?? DefaultMaxQueue;

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

    /// <summary>
    /// Time allowed between two pieces of a reply once it has started (HTTP: reads of the response body; CLI: lines of
    /// output). A backend that stops sending for longer is given up on: before anything reached the client the next model
    /// answers; after, the client gets an error and the model cools down. Default: the same as <see cref="Timeout"/>.
    /// </summary>
    public TimeSpan StallTimeout { get; set; }

    internal TimeSpan EffectiveStallTimeout => StallTimeout > TimeSpan.Zero ? StallTimeout : EffectiveTimeout;

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
