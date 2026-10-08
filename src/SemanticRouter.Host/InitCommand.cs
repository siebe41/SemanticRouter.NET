using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>Where <c>init</c> looks for local servers and CLIs; replaceable in tests.</summary>
internal sealed record InitProbes(HttpClient Http, string? SearchPath, bool Windows)
{
    public static readonly Uri LmStudio = new("http://localhost:1234");
    public static readonly Uri Ollama = new("http://localhost:11434");

    public static InitProbes ForThisMachine(HttpClient http) =>
        new(http, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
}

/// <summary>
/// <c>semantic-router init</c>: writes the template to the configuration directory with the models found on this
/// machine: LM Studio and Ollama on their default ports (chat and embedding models), and the <c>claude</c>,
/// <c>codex</c> and <c>copilot</c> CLIs on PATH.
/// </summary>
internal static class InitCommand
{
    public static async Task<int> RunAsync(HostSettings settings, InitProbes probes, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (File.Exists(settings.ConfigurationFile) && !settings.Force)
        {
            await output.WriteLineAsync($"{settings.ConfigurationFile} already exists. Edit it on the dashboard, or run `semantic-router init --force` to start over.").ConfigureAwait(false);
            return 1;
        }

        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        var notes = new List<string>();
        if (!settings.NoDetect)
        {
            await DetectAsync(document, probes, notes, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> errors = document.GetDocumentValidationErrors();
        if (errors.Count > 0)
        {
            await output.WriteLineAsync("The generated configuration is invalid: " + string.Join("; ", errors)).ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(settings.ConfigurationDirectory);
        new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(document);

        await output.WriteLineAsync($"Wrote {settings.ConfigurationFile}").ConfigureAwait(false);
        string evaluation = SemanticRouterServiceCollectionExtensions.EvaluationSetFileNextTo(settings.ConfigurationFile);
        if (!File.Exists(evaluation))
        {
            // Labelled prompts for the starting routes: the dashboard's Evaluate page runs them.
            await File.WriteAllTextAsync(evaluation, RouterHost.EvaluationTemplate(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Wrote {evaluation}").ConfigureAwait(false);
        }

        foreach (string note in notes)
        {
            await output.WriteLineAsync("  " + note).ConfigureAwait(false);
        }

        if (!document.Models.Any(m => m.Enabled))
        {
            await output.WriteLineAsync("  No model was found. Start LM Studio or Ollama, install a CLI, or add an API model on the dashboard (Models and Connectors).").ConfigureAwait(false);
        }

        await output.WriteLineAsync("Next: `semantic-router doctor` checks every connector; `semantic-router` starts the router and the dashboard.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Adds connectors, models and embeddings for what is found. Notes say what was added and why.</summary>
    internal static async Task DetectAsync(RouterConfiguration document, InitProbes probes, List<string> notes, CancellationToken cancellationToken)
    {
        string? embeddingModel = null;
        Uri? embeddingEndpoint = null;
        foreach ((string name, Uri address) in new[] { ("lmstudio", InitProbes.LmStudio), ("ollama", InitProbes.Ollama) })
        {
            var connector = new ConnectorOptions { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };
            ConnectorProbeResult probe = await ConnectorProbe.ListModelsAsync(name, connector, probes.Http, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (probe.Failure is ConnectorProbeFailure.AuthenticationRequired or ConnectorProbeFailure.CredentialsRejected)
            {
                document.Connectors[name] = connector;
                notes.Add($"{name}: found at {address}, but it requires an API token. Set SemanticRouter:Connectors:{name}:ApiKey (e.g. \"${{env:LM_API_TOKEN}}\"), then add its models on the dashboard.");
                continue;
            }

            if (!probe.Ok)
            {
                continue;
            }

            string[] chat = probe.Models.Where(id => !IsEmbedding(id)).ToArray();
            string? embedding = probe.Models.FirstOrDefault(IsEmbedding);
            if (chat.Length == 0 && embedding is null)
            {
                notes.Add($"{name}: running at {address} with no model loaded; load one and add it on the dashboard.");
                continue;
            }

            document.Connectors[name] = connector;
            if (chat.Length > 0)
            {
                string general = chat.FirstOrDefault(id => !id.Contains("coder", StringComparison.OrdinalIgnoreCase)) ?? chat[0];
                document.Models.Add(new ModelDefinition
                {
                    Name = name, Connector = name, Model = general, Tier = 1, IsLocal = true, Capabilities = { "chat" },
                    Description = $"Local model on {address}.",
                });
                string? coder = chat.FirstOrDefault(id => id.Contains("coder", StringComparison.OrdinalIgnoreCase) && id != general);
                if (coder is not null)
                {
                    document.Models.Add(new ModelDefinition
                    {
                        Name = name + "-coder", Connector = name, Model = coder, Tier = 2, IsLocal = true, Capabilities = { "chat", "code" },
                        Description = $"Local coder model on {address}. Add \"tools\" if it supports function calling.",
                    });
                }

                notes.Add($"{name}: {address}, using '{general}'{(coder is null ? string.Empty : $" and '{coder}'")} (of {chat.Length} chat model(s)).");
            }

            if (embedding is not null && embeddingModel is null)
            {
                embeddingModel = embedding;
                embeddingEndpoint = address;
            }
        }

        if (embeddingModel is not null)
        {
            document.Embeddings.Provider = EmbeddingProviderKind.OpenAICompatible;
            document.Embeddings.Endpoint = embeddingEndpoint;
            document.Embeddings.Model = embeddingModel;
            document.DefaultSimilarityThreshold = embeddingModel.Contains("mxbai", StringComparison.OrdinalIgnoreCase) ? 0.68f : 0.62f;
            notes.Add($"embeddings: '{embeddingModel}' on {embeddingEndpoint}; similarity threshold {document.DefaultSimilarityThreshold:0.00} (calibrate with the dashboard's prompt tester).");
        }

        AddCli(document, probes, notes, "claude", "claude-cli", CliPreset.ClaudeCode, maxConcurrency: 2, enabled: true,
            ("claude", "sonnet", 4, 0), ("claude-opus", "opus", 5, 1));
        AddCli(document, probes, notes, "codex", "codex-cli", CliPreset.Codex, maxConcurrency: 1, enabled: true,
            ("codex", null, 4, 2));
        AddCli(document, probes, notes, "copilot", "copilot-cli", CliPreset.GitHubCopilot, maxConcurrency: 2, enabled: false,
            ("copilot", null, 4, 2));
    }

    private static void AddCli(
        RouterConfiguration document, InitProbes probes, List<string> notes, string executable, string connector, CliPreset preset,
        int maxConcurrency, bool enabled, params (string Name, string? Model, int Tier, int Priority)[] models)
    {
        CliExecutableLookup lookup = CliExecutableLocator.Find(executable, probes.SearchPath, probes.Windows);
        if (!lookup.Found)
        {
            if (lookup.ScriptPath is not null)
            {
                notes.Add($"{executable}: only a script wrapper was found ({lookup.ScriptPath}); install the native {executable}.exe to use it.");
            }

            return;
        }

        document.Connectors[connector] = new ConnectorOptions
        {
            Type = ConnectorType.Cli,
            Preset = preset,
            MaxConcurrency = maxConcurrency,
            Timeout = TimeSpan.FromMinutes(3),
            ToolCalling = preset == CliPreset.ClaudeCode ? CliToolCalling.Prompted : CliToolCalling.None,
        };

        foreach ((string name, string? model, int tier, int priority) in models)
        {
            document.Models.Add(new ModelDefinition
            {
                Name = name, Connector = connector, Model = model, Tier = tier, Priority = priority, Enabled = enabled,
                ContextWindow = preset == CliPreset.ClaudeCode ? 200_000 : 0,
                Capabilities = { "chat", "code", "reasoning" },
                Description = $"{executable} CLI on your own subscription (personal, local use; see docs/TERMS.md).",
            });
        }

        if (preset == CliPreset.ClaudeCode)
        {
            foreach (ModelDefinition model in document.Models.Where(m => m.Connector == connector))
            {
                model.Capabilities.Add("tools");
            }
        }

        notes.Add(enabled
            ? $"{executable}: {lookup.Path} as '{connector}' (uses your own login; answers only requests from this machine)."
            : $"{executable}: {lookup.Path} as '{connector}', disabled (it spends your plan's premium requests); enable it on the dashboard.");
    }

    private static bool IsEmbedding(string id) => id.Contains("embed", StringComparison.OrdinalIgnoreCase);
}
