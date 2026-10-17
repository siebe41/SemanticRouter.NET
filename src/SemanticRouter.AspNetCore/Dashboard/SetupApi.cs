using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Finds the model servers and CLIs on the machine the router runs on, for the dashboard's first-run setup
/// (<c>GET api/setup</c>). The <c>semantic-router</c> tool and the desktop app register one that finds what
/// <c>semantic-router init</c> would; without one, the setup offers only its list of providers.
/// </summary>
public interface IRouterSetupDetector
{
    /// <summary>
    /// What is installed or running here, each with the connector and models that would use it. Only looks: it never
    /// changes the configuration. May start programs (a CLI's <c>--version</c>) and probe local ports.
    /// </summary>
    Task<IReadOnlyList<SetupDetection>> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Something <see cref="IRouterSetupDetector"/> found that the router could use.</summary>
/// <param name="Id">A stable id, e.g. <c>claude-code</c>, <c>lmstudio</c>.</param>
/// <param name="Title">What to call it, e.g. <c>Claude Code</c>.</param>
/// <param name="Kind"><c>cli</c> (a subscription through a program here) or <c>local</c> (a model server on this machine).</param>
/// <param name="ConnectorName">The connector's name, e.g. <c>claude-cli</c>.</param>
/// <param name="Connector">The connector's settings.</param>
public sealed record SetupDetection(string Id, string Title, string Kind, string ConnectorName, ConnectorOptions Connector)
{
    /// <summary>What was found, e.g. <c>Signed in · claude 2.1.3 at /usr/local/bin/claude</c>.</summary>
    public string? Detail { get; init; }

    /// <summary>What enabling it means for the user's account, shown before they enable it (CLIs).</summary>
    public string? Consent { get; init; }

    /// <summary>The catalog models that would use it.</summary>
    public IReadOnlyList<ModelDefinition> Models { get; init; } = [];

    /// <summary>Model aliases that go with it (client model name to target), e.g. Claude Code's model names.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// <c>GET api/setup</c>: whether the router still needs a model, what is found on this machine, and the providers a
/// connector can be added for in one click. Only describes: enabling one is a configuration change the dashboard applies.
/// </summary>
internal static partial class SetupApi
{
    private static readonly TimeSpan DetectionLifetime = TimeSpan.FromSeconds(30);

    // A refresh within this long of the last look reuses it: refreshing must not become a way to start programs in a loop.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    public static void Map(RouteGroupBuilder api)
    {
        // Admin only, like a connector check: detection starts programs and probes ports. Remembered for a short while,
        // so a page that asks on every render does not start them again each time. Marked as acting, so it is refused
        // from other sites like a change (DashboardAccess), although the dashboard calls it with GET.
        var cache = new DetectionCache();
        api.MapGet("/setup", async (RouterRuntimeProvider provider, IServiceProvider services, bool? refresh, CancellationToken cancellationToken) =>
            {
                RouterConfiguration config = provider.Current.Configuration;
                MachinePolicy policy = provider.Policy;
                IReadOnlyList<SetupDetection> found = [];
                string? problem = null;
                if (services.GetService<IRouterSetupDetector>() is { } detector)
                {
                    try
                    {
                        TimeProvider time = services.GetService<TimeProvider>() ?? TimeProvider.System;
                        found = await cache.GetAsync(detector, refresh == true, time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Whatever went wrong while looking (a program that would not start, a probe that timed out), the
                        // presets still work: say what happened and answer with them.
                        problem = "Could not look for models on this machine: " + ex.Message;
                    }
                }

                var detected = new JsonArray();
                foreach (SetupDetection detection in found.Where(d => policy.Blocks(d.Connector) is null))
                {
                    string name = FreeName(config, detection.ConnectorName, detection.Connector);
                    JsonObject entry = Entry(detection.Id, detection.Title, detection.Detail, detection.Kind, name, detection.Connector, detection.Models);
                    entry["consent"] = detection.Consent;
                    entry["aliases"] = new JsonObject(detection.Aliases.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value)));
                    detected.Add(entry);
                }

                bool copilotSdk = services.GetServices<IChatConnectorFactory>().Any(f => f.Type == ConnectorType.Copilot);
                var presets = new JsonArray();
                foreach (SetupPreset preset in Presets(copilotSdk).Where(p => policy.Blocks(p.Connector) is null))
                {
                    string name = FreeName(config, preset.Name, preset.Connector);
                    JsonObject entry = Entry(preset.Id, preset.Title, preset.Detail, preset.Kind, name, preset.Connector, models: null);
                    entry["needsKey"] = preset.KeyVariable is not null;
                    if (preset.KeyVariable is { } variable)
                    {
                        // The key is referenced from the environment; say whether it is there, so the page can ask for it if not.
                        entry["keyVariable"] = variable;
                        entry["keySet"] = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));
                    }

                    entry["consent"] = preset.Consent;
                    presets.Add(entry);
                }

                var response = new JsonObject
                {
                    ["needed"] = !config.Models.Any(m => m.Enabled),
                    ["detected"] = detected,
                    ["presets"] = presets,
                    ["detectionProblem"] = problem,
                };
                return Results.Content(response.ToJsonString(), "application/json");
            })
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("looked for models on this machine");
    }

    /// <summary>One <c>detected</c> or <c>presets</c> entry; settings and models in the configuration document's shape.</summary>
    private static JsonObject Entry(
        string id, string title, string? detail, string kind, string name, ConnectorOptions connector, IReadOnlyList<ModelDefinition>? models)
    {
        var document = new RouterConfiguration();
        document.Connectors[name] = connector;
        foreach (ModelDefinition model in models ?? [])
        {
            document.Models.Add(model);
        }

        JsonNode shaped = JsonNode.Parse(document.ToJson())!;
        var entry = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["detail"] = detail,
            ["kind"] = kind,
            ["connector"] = new JsonObject { ["name"] = name, ["settings"] = shaped["Connectors"]![name]!.DeepClone() },
        };
        if (models is not null)
        {
            var list = (JsonArray)shaped["Models"]!.DeepClone();
            foreach (JsonNode? model in list)
            {
                model!["Connector"] = name;
            }

            entry["models"] = list;
        }

        return entry;
    }

    /// <summary>
    /// <paramref name="name"/>, unless the configuration has a connector of that name pointing somewhere else: then the
    /// first free <c>name-2</c>, <c>name-3</c>, … so adding it does not replace that one.
    /// </summary>
    internal static string FreeName(RouterConfiguration config, string name, ConnectorOptions connector)
    {
        string candidate = name;
        for (int n = 2; config.Connectors.TryGetValue(candidate, out ConnectorOptions? existing) && !RouterConfiguration.SameDestination(existing, connector); n++)
        {
            candidate = $"{name}-{n}";
        }

        return candidate;
    }

    /// <summary>A provider the setup offers, with the connector settings <c>init</c> and docs/providers.md use.</summary>
    /// <param name="Id">A stable id, e.g. <c>openai</c>.</param>
    /// <param name="Title">What to call it.</param>
    /// <param name="Detail">One line on what it is and what it needs.</param>
    /// <param name="Kind"><c>local</c>, <c>cloud</c> (an API billed per token) or <c>cli</c> (a subscription through a program here).</param>
    /// <param name="Name">The connector's name.</param>
    /// <param name="Connector">The connector's settings.</param>
    /// <param name="KeyVariable">The environment variable its API key is referenced from; <see langword="null"/> when it needs none.</param>
    /// <param name="Consent">What enabling it means for the user's account (subscriptions).</param>
    internal sealed record SetupPreset(
        string Id, string Title, string Detail, string Kind, string Name, ConnectorOptions Connector, string? KeyVariable = null, string? Consent = null);

    /// <summary>
    /// Whether <paramref name="variable"/> is the key of a preset whose address is <paramref name="destination"/>'s scheme,
    /// host and port: <c>OPENAI_API_KEY</c> for <c>https://api.openai.com</c>, as the setup writes it.
    /// </summary>
    internal static bool IsPresetKey(string variable, Uri? destination) =>
        destination is { IsAbsoluteUri: true }
        && Presets(copilotSdk: false).Any(p => string.Equals(p.KeyVariable, variable, StringComparison.Ordinal)
            && p.Connector.BaseAddress is { } address
            && Uri.Compare(address, destination, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0);

    /// <summary>The providers the setup offers. With the Copilot SDK package, GitHub Copilot goes through it (docs/providers.md prefers it).</summary>
    internal static IReadOnlyList<SetupPreset> Presets(bool copilotSdk) =>
    [
        new("lmstudio", "LM Studio", "Local models from LM Studio's server (localhost:1234)", "local", "lmstudio",
            new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234"), Timeout = TimeSpan.FromMinutes(2) }),
        new("ollama", "Ollama", "Local models from Ollama (localhost:11434)", "local", "ollama",
            new ConnectorOptions { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(2) }),
        new("openai", "OpenAI", "OpenAI's API (api.openai.com), billed per token to your API key", "cloud", "openai",
            new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com"), ApiKey = "${env:OPENAI_API_KEY}" }, "OPENAI_API_KEY"),
        new("anthropic", "Anthropic", "Anthropic's API (api.anthropic.com), billed per token to your API key", "cloud", "anthropic",
            new ConnectorOptions { Type = ConnectorType.Anthropic, BaseAddress = new Uri("https://api.anthropic.com"), ApiKey = "${env:ANTHROPIC_API_KEY}" }, "ANTHROPIC_API_KEY"),
        new("openrouter", "OpenRouter", "Many vendors' models through one OpenRouter key (openrouter.ai)", "cloud", "openrouter",
            new ConnectorOptions { BaseAddress = new Uri("https://openrouter.ai"), RequestPath = "/api/v1/chat/completions", ApiKey = "${env:OPENROUTER_API_KEY}" },
            "OPENROUTER_API_KEY"),
        new("gemini", "Google Gemini", "Gemini models through Google's OpenAI-compatible endpoint, with an AI Studio key", "cloud", "gemini",
            new ConnectorOptions
            {
                BaseAddress = new Uri("https://generativelanguage.googleapis.com"), RequestPath = "/v1beta/openai/chat/completions", ApiKey = "${env:GEMINI_API_KEY}",
            },
            "GEMINI_API_KEY"),
        new("claude-code", "Claude Code", "Your Claude subscription through the claude CLI (install it and sign in first)", "cli", "claude-cli",
            new ConnectorOptions
            {
                Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode, MaxConcurrency = 2, Timeout = TimeSpan.FromMinutes(3), ToolCalling = CliToolCalling.Prompted,
            },
            Consent: Consent(CliPreset.ClaudeCode)),
        new("codex", "Codex", "Your ChatGPT plan through the codex CLI (install it and sign in first)", "cli", "codex-cli",
            new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.Codex, MaxConcurrency = 1, Timeout = TimeSpan.FromMinutes(3), ToolCalling = CliToolCalling.None },
            Consent: Consent(CliPreset.Codex)),
        copilotSdk
            ? new("copilot", "GitHub Copilot", "Your GitHub Copilot plan through the Copilot SDK (install the copilot CLI and sign in first)", "cli", "copilot",
                new ConnectorOptions { Type = ConnectorType.Copilot, MaxConcurrency = 2 }, Consent: Consent(CliPreset.GitHubCopilot))
            : new("copilot", "GitHub Copilot", "Your GitHub Copilot plan through the copilot CLI (install it and sign in first)", "cli", "copilot-cli",
                new ConnectorOptions
                {
                    Type = ConnectorType.Cli, Preset = CliPreset.GitHubCopilot, MaxConcurrency = 2, Timeout = TimeSpan.FromMinutes(3), ToolCalling = CliToolCalling.None,
                },
                Consent: Consent(CliPreset.GitHubCopilot)),
    ];

    /// <summary>What enabling a subscription CLI means for the user's account (the setup shows it before enabling).</summary>
    public static string? Consent(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => "Uses your Claude subscription, on the account claude is signed in to. On a work machine, check your company's AI policy first.",
        CliPreset.Codex => "Uses your ChatGPT plan, on the account codex is signed in to. On a work machine, check your company's AI policy first.",
        CliPreset.GitHubCopilot => "Uses your GitHub Copilot plan's premium requests, on the account copilot is signed in to. On a work machine, check your company's AI policy first.",
        _ => null,
    };

    /// <summary>
    /// What a CLI found at <paramref name="path"/> is, for the setup: <c>Signed in · claude 2.1.3 at /usr/local/bin/claude</c>.
    /// Runs its <c>--version</c>, and for Claude Code and Codex the command that reports the sign-in.
    /// </summary>
    public static async Task<string> DescribeCliAsync(
        string executable, string path, ConnectorOptions connector, ICliProcessRunner runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        (int exit, string output) = await RouterDoctor.RunAsync(runner, path, ["--version"], connector, cancellationToken).ConfigureAwait(false);
        string line = RouterDoctor.FirstLine(output);
        string version = exit == 0 && VersionNumber().Match(line) is { Success: true } number ? $"{executable} {number.Value}" : executable;
        string where = $"{version} at {path}";
        if (exit != 0)
        {
            return $"Found, but `{executable} --version` failed: {Clip(line)} · {path}";
        }

        string? signIn = null;
        if (connector.Type == ConnectorType.Cli && connector.Preset == CliPreset.ClaudeCode)
        {
            (int authExit, string auth) = await RouterDoctor.RunAsync(runner, path, ["auth", "status"], connector, cancellationToken).ConfigureAwait(false);
            (bool? loggedIn, string? method) = RouterDoctor.ReadClaudeAuthStatus(auth);
            signIn = authExit != 0 || loggedIn == false
                ? "Not signed in: run `claude` once and use /login"
                : method is "api_key" or "api_key_helper" or "third_party"
                    ? "Signed in with an API key, so requests are billed per token, not to your subscription"
                    : loggedIn == true ? "Signed in" : null;
        }
        else if (connector.Type == ConnectorType.Cli && connector.Preset == CliPreset.Codex)
        {
            // `codex login status` says "Logged in using ChatGPT", "… using an API key" or "Not logged in"; anything else is
            // left unsaid. Its output is not quoted: with an API key it shows part of the key.
            (int statusExit, string status) = await RouterDoctor.RunAsync(runner, path, ["login", "status"], connector, cancellationToken).ConfigureAwait(false);
            signIn = status.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ? "Not signed in: run `codex login`"
                : statusExit == 0 && status.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "Signed in with an API key, so requests are billed to that key, not to a ChatGPT plan"
                : statusExit == 0 && status.Contains("Logged in", StringComparison.OrdinalIgnoreCase) ? "Signed in"
                : null;
        }

        return signIn is null ? where : $"{signIn} · {where}";
    }

    private static string Clip(string text) => text.Length <= 120 ? text : text[..117] + "…";

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionNumber();

    /// <summary>The latest detection, shared by callers within <see cref="DetectionLifetime"/>; one runs at a time.</summary>
    private sealed class DetectionCache
    {
        private readonly Lock _gate = new();
        private Task<IReadOnlyList<SetupDetection>>? _running;
        private DateTimeOffset _at;

        public Task<IReadOnlyList<SetupDetection>> GetAsync(IRouterSetupDetector detector, bool refresh, TimeProvider time, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                bool usable = _running is { IsFaulted: false, IsCanceled: false }
                    && (!_running.IsCompleted || now - _at < (refresh ? RefreshInterval : DetectionLifetime));
                if (!usable)
                {
                    // Not tied to this request: another caller may be waiting for the same run.
                    _running = detector.DetectAsync(CancellationToken.None);
                    _at = now;
                }

                return _running!.WaitAsync(cancellationToken);
            }
        }
    }
}
