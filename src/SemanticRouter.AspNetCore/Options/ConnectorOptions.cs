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
        Timeout > TimeSpan.Zero ? Timeout : Type == ConnectorType.Cli ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(100);
}

/// <summary>The connector catalog, bound from <c>SemanticRouter:Connectors</c>.</summary>
public sealed class SemanticRouterConnectorsOptions
{
    /// <summary>Connectors by name (case-insensitive).</summary>
    public IDictionary<string, ConnectorOptions> Connectors { get; } =
        new Dictionary<string, ConnectorOptions>(StringComparer.OrdinalIgnoreCase);
}
