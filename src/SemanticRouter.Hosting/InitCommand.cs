using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

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
            ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync(name, connector, probes.Http, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (probe.Failure is ConnectorProbeFailure.AuthenticationRequired or ConnectorProbeFailure.CredentialsRejected)
            {
                document.Connectors[name] = connector;
                notes.Add($"{name}: found at {address}, but it requires an API token. Set SemanticRouter:Connectors:{name}:ApiKey (e.g. \"${{env:LM_API_TOKEN}}\"), then add its models on the dashboard (Models → Discover models).");
                continue;
            }

            if (!probe.Ok)
            {
                continue;
            }

            // Every chat model the server runs itself, each on the tier its size suggests; loaded ones preferred when
            // the server loads the others on demand. Cloud models a local server forwards (Ollama's) are left out.
            ModelLineupEntry[] lineup = ModelLineup.Suggest(name, connector, probe.Details, probe.Loading, document.Models)
                .Where(e => e.Suggestion is not null && e.Model.Listed)
                .ToArray();
            ModelDefinition[] chat = lineup.Where(e => e.Model.Remote != true).Select(e => e.Suggestion!).ToArray();
            string[] cloud = lineup.Where(e => e.Model.Remote == true).Select(e => e.Model.Id).ToArray();
            if (cloud.Length > 0)
            {
                notes.Add($"{name}: left out {string.Join(", ", cloud)}: {(cloud.Length == 1 ? "it runs" : "they run")} in the cloud, not on this machine. Add {(cloud.Length == 1 ? "it" : "them")} on the dashboard if you want to.");
            }

            string? embedding = probe.Details.FirstOrDefault(m => m.Kind == DiscoveredModelKind.Embedding && m.Listed)?.Id;
            if (chat.Length == 0 && embedding is null)
            {
                notes.Add($"{name}: running at {address} with no model {(probe.Loading == ModelLoading.LoadedOnly ? "loaded" : "available")}; load one and add it on the dashboard.");
                continue;
            }

            document.Connectors[name] = connector;
            foreach (ModelDefinition model in chat)
            {
                document.Models.Add(model);
            }

            if (chat.Length > 0)
            {
                notes.Add($"{name}: {address}{(probe.Server is null ? string.Empty : $" ({probe.Server})")}, {chat.Length} chat model(s): " +
                          string.Join(", ", chat.Select(m => $"{m.Model} (tier {m.Tier})")) + ". Check the tiers on the dashboard's Models page.");
            }

            if (probe.LoadingNote is not null)
            {
                notes.Add($"{name}: {probe.LoadingNote}");
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
            notes.Add($"embeddings: '{embeddingModel}' on {embeddingEndpoint}; similarity threshold {document.DefaultSimilarityThreshold:0.00} (calibrate on the dashboard's Evaluate page).");
        }
        else
        {
            // No embedding server: a small model in this process beats lexical hashing.
            OnnxModelInfo model = OnnxEmbeddingModels.Find(OnnxEmbeddingModels.DefaultModel)!;
            document.Embeddings.Provider = EmbeddingProviderKind.Onnx;
            document.Embeddings.Model = model.Name;
            document.DefaultSimilarityThreshold = model.SuggestedThreshold;
            notes.Add($"embeddings: no embedding server found, so '{model.Name}' runs in this process ({Math.Round(model.ModelBytes / 1e6):0} MB, downloaded on first start); " +
                      $"similarity threshold {model.SuggestedThreshold:0.00} (calibrate on the dashboard's Evaluate page).");
        }

        AddCli(document, probes, notes, "claude", "claude-cli", CliPreset.ClaudeCode, maxConcurrency: 2, enabled: true);
        AddCli(document, probes, notes, "codex", "codex-cli", CliPreset.Codex, maxConcurrency: 1, enabled: true);
        AddCli(document, probes, notes, "copilot", "copilot-cli", CliPreset.GitHubCopilot, maxConcurrency: 2, enabled: false);
    }

    private static void AddCli(
        RouterConfiguration document, InitProbes probes, List<string> notes, string executable, string connector, CliPreset preset, int maxConcurrency, bool enabled)
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

        var options = new ConnectorOptions
        {
            Type = ConnectorType.Cli,
            Preset = preset,
            MaxConcurrency = maxConcurrency,
            Timeout = TimeSpan.FromMinutes(3),
            ToolCalling = preset == CliPreset.ClaudeCode ? CliToolCalling.Prompted : CliToolCalling.None,
        };
        document.Connectors[connector] = options;
        string description = $"{executable} CLI on your own subscription (personal, local use; see docs/TERMS.md).";

        if (preset == CliPreset.ClaudeCode)
        {
            // Claude's lineup, one tier each: Haiku 2, Sonnet 3, Opus 4, Fable 5 (disabled until you enable it).
            foreach (ModelLineupEntry entry in ModelLineup.Suggest(
                         connector, options, ModelLineup.CliModelIds(preset).Select(id => new DiscoveredModel(id)).ToArray(), ModelLoading.Unknown, document.Models))
            {
                ModelDefinition model = entry.Suggestion!;
                model.Description = description;
                document.Models.Add(model);
            }

            notes.Add($"{executable}: {lookup.Path} as '{connector}' with " +
                      string.Join(", ", document.Models.Where(m => m.Connector == connector).Select(m => $"{m.Model} (tier {m.Tier}{(m.Enabled ? string.Empty : ", disabled")})")) +
                      " (uses your own login; answers only requests from this machine).");
            return;
        }

        // The CLI's own default model: Codex and Copilot change theirs often, so no model id is pinned.
        document.Models.Add(new ModelDefinition
        {
            Name = executable, Connector = connector, Tier = 4, Priority = 2, Enabled = enabled,
            Capabilities = { "chat", "code", "reasoning" },
            Description = description,
        });
        notes.Add(enabled
            ? $"{executable}: {lookup.Path} as '{connector}' (uses your own login; answers only requests from this machine)."
            : $"{executable}: {lookup.Path} as '{connector}', disabled (it spends your plan's premium requests); enable it on the dashboard.");
    }
}
