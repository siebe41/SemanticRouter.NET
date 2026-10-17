using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Host;

/// <summary>
/// Where <c>init</c> looks for local servers and CLIs, and the machine policy it keeps to (<see cref="MachinePolicy"/>:
/// a connector type or CLI preset it turns off is not added); replaceable in tests.
/// </summary>
internal sealed record InitProbes(HttpClient Http, string? SearchPath, bool Windows, MachinePolicy? Policy = null)
{
    public static readonly Uri LmStudio = new("http://localhost:1234");
    public static readonly Uri Ollama = new("http://localhost:11434");

    /// <exception cref="InvalidDataException">A machine policy is set but cannot be read.</exception>
    public static InitProbes ForThisMachine(HttpClient http) =>
        new(http, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), MachinePolicyLoader.Load());
}

/// <summary>
/// Whether <c>init</c> turns on the subscription CLIs it finds. Each one answers with whatever account that CLI is signed
/// in to, which may be a personal plan, so it is enabled only on an explicit yes: <c>--yes</c>, its name in
/// <c>--enable-cli</c>, or an answer at the terminal.
/// </summary>
/// <param name="Yes"><c>--yes</c>: enable them without asking.</param>
/// <param name="Input">The terminal to ask on; <see langword="null"/> when there is none (input or output redirected).</param>
/// <param name="Output">Where the questions are written.</param>
/// <param name="Named">
/// <c>--enable-cli</c>: enable just these, without asking; <see langword="null"/> when not given. It wins over
/// <c>--yes</c>, as the narrower choice.
/// </param>
internal sealed record CliConsent(bool Yes, TextReader? Input, TextWriter Output, IReadOnlySet<string>? Named = null)
{
    /// <summary>Which of the CLIs found (executable and path) to enable. Anything but <c>y</c> or <c>yes</c> means no.</summary>
    /// <param name="found">The CLIs to ask about.</param>
    /// <param name="nothingElse">Nothing else was found to answer prompts, so saying no to all of them leaves the router without a model.</param>
    /// <param name="cancellationToken">Cancels the questions.</param>
    public async Task<IReadOnlySet<string>> AskAsync(IReadOnlyList<(string Executable, string Path)> found, bool nothingElse, CancellationToken cancellationToken)
    {
        if (Named is not null)
        {
            return found.Select(f => f.Executable).Where(Named.Contains).ToHashSet();
        }

        if (Yes)
        {
            return found.Select(f => f.Executable).ToHashSet();
        }

        var enabled = new HashSet<string>();
        if (found.Count == 0)
        {
            return enabled;
        }

        if (Input is null)
        {
            // Said here, where the questions would have been, so a script's log shows why nothing was enabled.
            string names = string.Join(",", found.Select(f => f.Executable));
            await Output.WriteLineAsync(
                $"No terminal to ask on (input or output is redirected), so {string.Join(" and ", found.Select(f => f.Executable))} will be added disabled. " +
                $"To choose without one, run init with --yes (every CLI found), --enable-cli {names} (only those you name) or --no-cli (none).").ConfigureAwait(false);
            return enabled;
        }

        await Output.WriteLineAsync("Found " + string.Join(", ", found.Select(f => $"{f.Executable} ({f.Path})")) + ".").ConfigureAwait(false);
        await Output.WriteLineAsync("A CLI connector answers prompts with whatever account that CLI is signed in to, which may be a personal subscription.").ConfigureAwait(false);
        await Output.WriteLineAsync($"On a work machine, check your company's AI policy first: {SiteLinks.WorkUse}").ConfigureAwait(false);
        if (nothingElse)
        {
            await Output.WriteLineAsync(found.Count == 1
                ? $"Nothing else was found to answer prompts (no LM Studio or Ollama with a model), so without {found[0].Executable} the router has no model to use."
                : "Nothing else was found to answer prompts (no LM Studio or Ollama with a model), so without one of these the router has no model to use.").ConfigureAwait(false);
        }

        foreach ((string executable, _) in found)
        {
            await Output.WriteAsync($"Enable {executable} on the account it is signed in to? [y/N] ").ConfigureAwait(false);
            await Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? answer = await Input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                await Output.WriteLineAsync().ConfigureAwait(false);
                break;
            }

            if (answer.Trim().ToUpperInvariant() is "Y" or "YES")
            {
                enabled.Add(executable);
            }
        }

        return enabled;
    }
}

/// <summary>
/// <c>semantic-router init</c>: writes the template to the configuration directory with the models found on this
/// machine: LM Studio and Ollama on their default ports (chat and embedding models), and the <c>claude</c>,
/// <c>codex</c> and <c>copilot</c> CLIs on PATH.
/// </summary>
internal static class InitCommand
{
    /// <param name="input">The terminal to ask on before enabling a subscription CLI; <see langword="null"/> when there is none.</param>
    public static async Task<int> RunAsync(
        HostSettings settings, InitProbes probes, TextWriter output, TextReader? input = null, CancellationToken cancellationToken = default)
    {
        // A file that still holds the built-in template (the router writes it on its first start) has nothing to lose.
        bool replacingTemplate = false;
        if (File.Exists(settings.ConfigurationFile) && !settings.Force)
        {
            if (!FirstRun.IsUntouchedTemplate(settings.ConfigurationFile))
            {
                await output.WriteLineAsync($"{settings.ConfigurationFile} already exists. Edit it on the dashboard, or run `semantic-router init --force` to start over.").ConfigureAwait(false);
                return 1;
            }

            replacingTemplate = true;
        }

        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        var notes = new List<string>();
        if (!settings.NoDetect)
        {
            // A model-family table in the configuration directory (from an earlier setup) is used here too.
            ModelFamilyTable families = ModelFamilyTable.Load(ModelFamilyTable.FileNextTo(settings.ConfigurationFile));
            CliConsent? cli = settings.NoCli ? null : new CliConsent(settings.Yes, input, output, settings.EnableCli);
            await DetectAsync(document, probes, notes, cli, cancellationToken, families).ConfigureAwait(false);
        }

        IReadOnlyList<string> errors = document.GetDocumentValidationErrors();
        if (errors.Count > 0)
        {
            await output.WriteLineAsync("The generated configuration is invalid: " + string.Join("; ", errors)).ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(settings.ConfigurationDirectory);
        new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(document);

        await output.WriteLineAsync($"Wrote {settings.ConfigurationFile}{(replacingTemplate ? " (it held only the built-in template)" : string.Empty)}").ConfigureAwait(false);
        string evaluation = SemanticRouterServiceCollectionExtensions.EvaluationSetFileNextTo(settings.ConfigurationFile);
        if (!File.Exists(evaluation))
        {
            // Labelled prompts for the starting routes: the dashboard's Evaluate page runs them.
            await File.WriteAllTextAsync(evaluation, RouterHost.EvaluationTemplate(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Wrote {evaluation}").ConfigureAwait(false);
        }
        else if (TemplateMigration.UpgradeEvaluationSet(evaluation) is { } updated)
        {
            await output.WriteLineAsync(updated).ConfigureAwait(false);
        }

        if (document.Models.Count > 0)
        {
            // The notes give each model's tier by number.
            await output.WriteLineAsync("  Tiers: " + string.Join(" · ", document.EffectiveTiers.Select(t => $"{t.Level} {t.Name}")) +
                                        " (a prompt goes to a model on the tier it needs, or a higher one).").ConfigureAwait(false);
        }

        foreach (string note in notes)
        {
            await output.WriteLineAsync("  " + note).ConfigureAwait(false);
        }

        string router = RouterHost.LocalAddress(settings);
        if (!document.Models.Any(m => m.Enabled))
        {
            await output.WriteLineAsync(document.Models.Count > 0
                ? "  No model is enabled, so the router has nothing to answer with yet."
                : "  No model was found: no LM Studio or Ollama server with a model answered, and no subscription CLI was added.").ConfigureAwait(false);
            await output.WriteLineAsync($"Next: start the router with `semantic-router` and set up a model on its dashboard: {router}/router#/overview").ConfigureAwait(false);
            await output.WriteLineAsync(document.Models.Count > 0
                ? "Or enable one there under Providers → Models."
                : $"Or start LM Studio's server (Developer → Start server) or Ollama with a model, and run `semantic-router init{(document.Connectors.Count > 0 ? " --force" : string.Empty)}` again.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"Next: `semantic-router doctor` checks every connector; `semantic-router` starts the router and its dashboard ({router}/router).").ConfigureAwait(false);
        await output.WriteLineAsync($"Then start Claude Code through it: {RouterHost.ClaudeCodeCommand(router, document.Models, probes.Windows)}").ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The embeddings <c>init</c> picks when it finds no embedding server: a small model in this process, which beats
    /// lexical hashing. <see cref="FirstRun.IsUntouchedTemplate"/> recognises a file with only this change.
    /// </summary>
    internal static OnnxModelInfo UseInProcessEmbeddings(RouterConfiguration document)
    {
        OnnxModelInfo model = OnnxEmbeddingModels.Find(OnnxEmbeddingModels.DefaultModel)!;
        document.Embeddings.Provider = EmbeddingProviderKind.Onnx;
        document.Embeddings.Model = model.Name;
        document.DefaultSimilarityThreshold = model.SuggestedThreshold;
        return model;
    }

    /// <summary>
    /// Adds connectors, models and embeddings for what is found. Notes say what was added and why. Without
    /// <paramref name="cli"/> (<c>--no-cli</c>) no subscription CLI connector is added.
    /// </summary>
    internal static async Task DetectAsync(
        RouterConfiguration document, InitProbes probes, List<string> notes, CliConsent? cli, CancellationToken cancellationToken, ModelFamilyTable? families = null)
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
                notes.Add($"{name}: found at {address}, but it requires an API token. Set SemanticRouter:Connectors:{name}:ApiKey (e.g. \"${{env:LM_API_TOKEN}}\"), then add its models on the dashboard (Providers → Models → Discover models).");
                continue;
            }

            if (!probe.Ok)
            {
                continue;
            }

            // Every chat model the server runs itself, each on the tier its family (or else its size) suggests; loaded
            // ones preferred when the server loads the others on demand. Cloud models a local server forwards (Ollama's)
            // are left out.
            ModelLineupEntry[] lineup = ModelLineup.Suggest(name, connector, probe.Details, probe.Loading, document.Models, families)
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
            notes.Add($"embeddings: '{embeddingModel}' on {embeddingEndpoint}, to recognise what each prompt asks for.");
        }
        else
        {
            OnnxModelInfo model = UseInProcessEmbeddings(document);
            notes.Add($"embeddings: no embedding server found, so '{model.Name}' runs in this process to recognise what each prompt asks for " +
                      $"({Math.Round(model.ModelBytes / 1e6):0} MB, downloaded from huggingface.co on first start).");
        }

        if (cli is null)
        {
            return;
        }

        // Claude and Codex are enabled only on a yes. Copilot is not asked about: it is added disabled because it spends
        // the plan's premium requests.
        (string Executable, string Connector, CliPreset Preset, int MaxConcurrency, bool Ask)[] clis =
        [
            ("claude", "claude-cli", CliPreset.ClaudeCode, 2, true),
            ("codex", "codex-cli", CliPreset.Codex, 1, true),
            ("copilot", "copilot-cli", CliPreset.GitHubCopilot, 2, false),
        ];
        var found = new List<(string Executable, string Connector, CliPreset Preset, int MaxConcurrency, bool Ask, string Path)>();
        var explained = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string executable, string connector, CliPreset preset, int maxConcurrency, bool ask) in clis)
        {
            CliExecutableLookup lookup = CliExecutableLocator.Find(executable, probes.SearchPath, probes.Windows);
            if (lookup.Found && probes.Policy?.Blocks(new ConnectorOptions { Type = ConnectorType.Cli, Preset = preset }) is { } blocked)
            {
                // Not added, not asked about: the router would turn it off at start-up anyway.
                notes.Add($"{executable}: found at {lookup.Path}, but not added: {blocked}.");
                explained.Add(executable);
                continue;
            }

            if (lookup.Found)
            {
                found.Add((executable, connector, preset, maxConcurrency, ask, lookup.Path!));
            }
            else if (lookup.ScriptPath is not null)
            {
                notes.Add($"{executable}: only a script wrapper was found ({lookup.ScriptPath}); install the native {executable}.exe to use it.");
                explained.Add(executable);
            }
        }

        // A CLI named in --enable-cli that is not on this machine: said, rather than silently left out.
        foreach (string missing in cli.Named?.Where(n => !explained.Contains(n) && !found.Any(f => f.Executable == n)) ?? [])
        {
            notes.Add($"{missing}: not found on PATH, so --enable-cli could not enable it.");
        }

        // Whether the CLIs are all there is: then saying no leaves the router with nothing to answer, and the question says so.
        bool nothingElse = !document.Models.Any(m => m.Enabled);
        IReadOnlySet<string> yes = await cli.AskAsync(found.Where(f => f.Ask).Select(f => (f.Executable, f.Path)).ToArray(), nothingElse, cancellationToken).ConfigureAwait(false);
        foreach ((string executable, string connector, CliPreset preset, int maxConcurrency, bool ask, string path) in found)
        {
            // Why a CLI is added disabled, and how to turn it on. Copilot is never asked about, but naming it enables it.
            string? off = cli.Named?.Contains(executable) == true ? null
                : !ask ? "(it spends your plan's premium requests); enable it on the dashboard"
                : yes.Contains(executable) ? null
                : cli.Named is not null ? "(not named in --enable-cli); enable it on the dashboard when you want it"
                : cli.Input is null ? "(there was no terminal to ask on)"
                : "(you did not say yes); enable it on the dashboard when you want it";
            AddCli(document, notes, executable, path, connector, preset, maxConcurrency, off);
        }

        string[] unasked = cli.Input is null && !cli.Yes && cli.Named is null ? found.Where(f => f.Ask).Select(f => f.Executable).ToArray() : [];
        if (unasked.Length > 0)
        {
            notes.Add($"{string.Join(", ", unasked)}: init asks before enabling a subscription CLI, because it answers on whatever account that CLI is signed in to, " +
                      $"which may be a personal plan (at work, see {SiteLinks.WorkUse}). " +
                      $"Enable {(unasked.Length == 1 ? "it" : "them")} on the dashboard, or run `semantic-router init --force --enable-cli {string.Join(",", unasked)}` " +
                      $"to enable {(unasked.Length == 1 ? "it" : "them")} without asking.");
        }
    }

    private static void AddCli(
        RouterConfiguration document, List<string> notes, string executable, string path, string connector, CliPreset preset, int maxConcurrency, string? off)
    {
        bool enabled = off is null;
        var options = new ConnectorOptions
        {
            Type = ConnectorType.Cli,
            Preset = preset,
            MaxConcurrency = maxConcurrency,
            Timeout = TimeSpan.FromMinutes(3),
            ToolCalling = preset == CliPreset.ClaudeCode ? CliToolCalling.Prompted : CliToolCalling.None,
        };
        document.Connectors[connector] = options;
        string description = $"{executable} CLI on your own subscription (personal, local use; see {SiteLinks.Terms}).";

        if (preset == CliPreset.ClaudeCode)
        {
            // Claude's lineup, one tier each: Haiku 2, Sonnet 3, Opus 4, Fable 5 (disabled until you enable it).
            foreach (ModelLineupEntry entry in ModelLineup.Suggest(
                         connector, options, ModelLineup.CliModelIds(preset).Select(id => new DiscoveredModel(id)).ToArray(), ModelLoading.Unknown, document.Models))
            {
                ModelDefinition model = entry.Suggestion!;
                model.Description = description;
                model.Enabled &= enabled;
                document.Models.Add(model);
            }

            notes.Add($"{executable}: {path} as '{connector}' with " +
                      string.Join(", ", document.Models.Where(m => m.Connector == connector).Select(m => $"{m.Model} (tier {m.Tier}{(m.Enabled || !enabled ? string.Empty : ", disabled")})")) +
                      (enabled ? " (uses your own login; answers only requests from this machine)." : $", all disabled {off}."));
            return;
        }

        // The CLI's own default model: Codex and Copilot change theirs often, so no model id is pinned.
        document.Models.Add(new ModelDefinition
        {
            Name = executable,
            Connector = connector,
            Tier = 4,
            Priority = 2,
            Enabled = enabled,
            Capabilities = { "chat", "code", "reasoning" },
            Description = description,
        });
        notes.Add(enabled
            ? $"{executable}: {path} as '{connector}' (uses your own login; answers only requests from this machine)."
            : $"{executable}: {path} as '{connector}', disabled {off}.");
    }
}
