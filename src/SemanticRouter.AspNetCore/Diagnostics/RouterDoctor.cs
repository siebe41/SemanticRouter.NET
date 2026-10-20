using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>How a <see cref="RouterCheck"/> came out.</summary>
internal enum RouterCheckStatus
{
    /// <summary>Works.</summary>
    Ok = 0,

    /// <summary>Works, or may work, but something deserves attention.</summary>
    Warning = 1,

    /// <summary>Does not work; the message names what to fix.</summary>
    Failed = 2,
}

/// <summary>One finding of <see cref="RouterDoctor.CheckAsync"/>.</summary>
/// <param name="Subject">What was checked, e.g. <c>connector lmstudio</c>.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Message">What was found, and for problems the setting to change. Never contains a secret.</param>
internal sealed record RouterCheck(string Subject, RouterCheckStatus Status, string Message);

/// <summary>
/// Checks the active configuration against the machine it runs on, without calling any model: unresolved secret
/// references, the embedding provider, every HTTP connector (reachable, credentials accepted, configured model ids
/// offered) and every CLI connector (installed, starts, and for Claude Code signed in). This is what
/// <c>semantic-router doctor</c> prints; any host can call it.
/// </summary>
internal static class RouterDoctor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The exit code <see cref="RunAsync"/> reports when the CLI's working directory could not be made.</summary>
    private const int NoWorkingDirectory = int.MinValue;

    /// <summary>Runs every check against the router registered in <paramref name="services"/>.</summary>
    public static async Task<IReadOnlyList<RouterCheck>> CheckAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        RouterRuntime runtime = services.GetRequiredService<RouterRuntimeProvider>().Current;
        RouterConfiguration resolved = runtime.Resolved;
        var checks = new List<RouterCheck>();

        foreach (string warning in runtime.Warnings)
        {
            checks.Add(new RouterCheck("configuration", RouterCheckStatus.Warning, warning));
        }

        checks.Add(await CheckEmbeddingsAsync(runtime, services.GetServices<IEmbeddingProviderFactory>(), cancellationToken).ConfigureAwait(false));

        foreach (string name in resolved.Connectors.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            checks.AddRange(await CheckConnectorAsync(services, runtime, name, cancellationToken).ConfigureAwait(false));
        }

        return checks;
    }

    /// <summary>
    /// Runs the checks of one connector: reachable and credentials accepted (HTTP), installed and signed in (CLI), the
    /// runtime starts (Copilot). The dashboard's endpoint check runs this.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No connector of that name is configured.</exception>
    public static Task<IReadOnlyList<RouterCheck>> CheckConnectorAsync(IServiceProvider services, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RouterRuntime runtime = services.GetRequiredService<RouterRuntimeProvider>().Current;
        return runtime.Resolved.Connectors.ContainsKey(name)
            ? CheckConnectorAsync(services, runtime, name, cancellationToken)
            : throw new KeyNotFoundException($"Connector '{name}' is not configured.");
    }

    private static async Task<IReadOnlyList<RouterCheck>> CheckConnectorAsync(
        IServiceProvider services, RouterRuntime runtime, string name, CancellationToken cancellationToken)
    {
        RouterConfiguration resolved = runtime.Resolved;
        ConnectorOptions connector = resolved.Connectors[name];
        IHttpClientFactory http = services.GetRequiredService<IHttpClientFactory>();
        ModelDefinition[] models = resolved.Models
            .Where(m => m.Enabled && string.Equals(m.Connector, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (models.Length == 0)
        {
            if (connector.RunsProgram)
            {
                return [new RouterCheck($"connector {name}", RouterCheckStatus.Ok, "no enabled model uses it; not checked.")];
            }

            // Still worth a probe (it is cheap): it shows whether the server is up and accepts the credentials.
            return [.. (await CheckHttpAsync(name, connector, [], resolved, http, cancellationToken).ConfigureAwait(false)).Select(check => check with
            {
                Status = check.Status == RouterCheckStatus.Failed ? RouterCheckStatus.Warning : check.Status,
                Message = check.Message + " (No enabled model uses it yet.)",
            })];
        }

        if (connector.Type == ConnectorTypes.Cli)
        {
            return [.. await CheckCliAsync(name, connector, services.GetRequiredService<ICliProcessRunner>(), cancellationToken).ConfigureAwait(false)];
        }

        if (!ConnectorTypes.IsBuiltIn(connector.Type))
        {
            bool installed = services.GetServices<IChatConnectorFactory>().Any(f => connector.IsType(f.Type));
            runtime.Connectors.TryGet(name, out IChatConnector running);
            if (!installed || running is IModelDiscovery || connector.BaseAddress is null)
            {
                return [await CheckPackagedAsync(name, connector.Type, installed ? running as IModelDiscovery : null, installed, runtime, models, cancellationToken)
                    .ConfigureAwait(false)];
            }
        }

        return [.. await CheckHttpAsync(name, connector, models, resolved, http, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// A connector of a type from another package (Copilot): lists the models through it, which proves its runtime
    /// starts and the sign-in works.
    /// </summary>
    private static async Task<RouterCheck> CheckPackagedAsync(
        string name, string type, IModelDiscovery? discovery, bool installed, RouterRuntime runtime, ModelDefinition[] models, CancellationToken cancellationToken)
    {
        string subject = $"connector {name}";
        if (!installed)
        {
            return new RouterCheck(subject, RouterCheckStatus.Failed,
                $"{ConnectorCatalog.NotInstalled(type)}. Add the package that provides it, or change SemanticRouter:Connectors:{name}:Type.");
        }

        if (discovery is null)
        {
            return new RouterCheck(subject, RouterCheckStatus.Ok, $"{type} connectors cannot list their models, so this one is not checked.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60)); // the first call may start the connector's runtime (Copilot)
            IReadOnlyList<string> available = await discovery.ListModelsAsync(timeout.Token).ConfigureAwait(false);
            string[] missing = models.Where(m => m.Model is { Length: > 0 } id && !available.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Select(m => $"{m.Name} ({m.Model})").ToArray();
            return missing.Length == 0
                ? new RouterCheck(subject, RouterCheckStatus.Ok, $"{type} answers; {available.Count} models available.")
                : new RouterCheck(subject, RouterCheckStatus.Warning,
                    $"{type} answers, but these models are not offered to this account: {string.Join(", ", missing)}. Available: {string.Join(", ", available)}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception
                                   && !cancellationToken.IsCancellationRequested)
        {
            string reason = ex is OperationCanceledException ? "timed out" : runtime.Redactor.Redact(ex.Message);
            return new RouterCheck(subject, RouterCheckStatus.Failed, $"the {type} connector could not list models: {reason.TrimEnd('.')}.");
        }
    }

    private static async Task<RouterCheck> CheckEmbeddingsAsync(RouterRuntime runtime, IEnumerable<IEmbeddingProviderFactory> factories, CancellationToken cancellationToken)
    {
        EmbeddingProviderOptions embeddings = runtime.Resolved.Embeddings;
        if (embeddings.Provider == EmbeddingProviderKinds.Hashing)
        {
            // A packaged provider (Onnx) is suggested only where it can run (ONNX Runtime has no build for some platforms, e.g. Intel Macs).
            IEmbeddingProviderFactory[] installed = [.. factories];
            string[] runnable = [.. installed.Where(f => f.Unavailable is null).Select(f => f.Kind).Distinct(StringComparer.OrdinalIgnoreCase)];
            string notHere = string.Join(" ", installed.Select(f => f.Unavailable).OfType<string>().Distinct());
            return new RouterCheck("embeddings", RouterCheckStatus.Warning, runnable.Length > 0
                ? $"the Hashing provider only matches shared words. For meaning-based routing set SemanticRouter:Embeddings:Provider to {string.Join(" or ", runnable)} " +
                  "(no server needed) or to an OpenAICompatible endpoint (LM Studio, Ollama, OpenAI) on the dashboard under Providers → Embeddings, " +
                  $"and raise DefaultSimilarityThreshold ({ThresholdsLink})."
                : "the Hashing provider only matches shared words. For meaning-based routing set SemanticRouter:Embeddings:Provider to an OpenAICompatible " +
                  "endpoint (an embedding model in LM Studio or Ollama) on the dashboard under Providers → Embeddings, " +
                  $"and raise DefaultSimilarityThreshold ({ThresholdsLink}). {notHere}".TrimEnd());
        }

        // A provider from another package (Onnx) runs a model it provides; the built-in OpenAICompatible one calls a server.
        bool packaged = !EmbeddingProviderKinds.IsBuiltIn(embeddings.Provider);
        IEmbeddingProviderFactory? factory = packaged
            ? factories.FirstOrDefault(f => string.Equals(f.Kind, embeddings.Provider, StringComparison.OrdinalIgnoreCase))
            : null;
        string what = packaged
            ? $"{embeddings.Provider} model {embeddings.ModelPath ?? embeddings.Model ?? (factory is { Models: [var first, ..] } ? first.Name : "(its default)")}"
            : $"{embeddings.Model} at {AddressDisplay.Show(embeddings.Endpoint)}";
        if (factory?.Unavailable is { } unavailable)
        {
            return new RouterCheck("embeddings", RouterCheckStatus.Failed,
                $"{what} cannot run on this machine, so no prompt is classified. {unavailable} (SemanticRouter:Embeddings:Provider; dashboard: Providers → Embeddings)");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // The first call of a packaged provider may download its model.
            timeout.CancelAfter(packaged ? TimeSpan.FromMinutes(5) : ProbeTimeout);
            ReadOnlyMemory<float> vector = await runtime.EmbeddingProvider.GenerateEmbeddingAsync("hello", timeout.Token).ConfigureAwait(false);
            return new RouterCheck("embeddings", RouterCheckStatus.Ok, $"{what}: {vector.Length} dimensions.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException
                                   && !cancellationToken.IsCancellationRequested)
        {
            string reason = ex is OperationCanceledException ? "timed out" : runtime.Redactor.Redact(ex.Message);
            return new RouterCheck("embeddings", RouterCheckStatus.Failed, packaged
                ? $"{what} failed: {reason} (SemanticRouter:Embeddings:Provider / Model / ModelPath; dashboard: Providers → Embeddings)."
                : $"{what} failed: {reason} (SemanticRouter:Embeddings:Endpoint / Model / ApiKey; dashboard: Providers → Embeddings).");
        }
    }

    private const string ThresholdsLink = "https://siebe41.github.io/SemanticRouter.NET/overview#thresholds-by-embedding-model";

    private static async Task<IEnumerable<RouterCheck>> CheckHttpAsync(
        string name, ConnectorOptions connector, ModelDefinition[] models, RouterConfiguration resolved, IHttpClientFactory http, CancellationToken cancellationToken)
    {
        string subject = $"connector {name}";
        ConnectorProbeResult probe = await ConnectorProbe.DiscoverAsync(
            name, connector, http.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + name), ProbeTimeout, cancellationToken).ConfigureAwait(false);

        switch (probe.Failure)
        {
            case ConnectorProbeFailure.None:
                break;
            case ConnectorProbeFailure.HttpError or ConnectorProbeFailure.InvalidResponse:
                // Some APIs (Azure OpenAI deployments, a few gateways) have no model list; chat may still work.
                return [new RouterCheck(subject, RouterCheckStatus.Warning, probe.Message + " Could not list models; chat requests may still work.")];
            default:
                return [new RouterCheck(subject, RouterCheckStatus.Failed, probe.Message)];
        }

        var checks = new List<RouterCheck> { new(subject, RouterCheckStatus.Ok, probe.LoadingNote is null ? probe.Message : $"{probe.Message} {probe.LoadingNote}") };
        foreach (ModelDefinition model in models)
        {
            if (string.IsNullOrEmpty(model.Model))
            {
                continue;
            }

            int index = resolved.Models.IndexOf(model);
            string modelSubject = $"model {model.Name}";
            DiscoveredModel? found = probe.Details.FirstOrDefault(d => string.Equals(d.Id, model.Model, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                checks.Add(new RouterCheck(modelSubject, RouterCheckStatus.Failed,
                    $"'{model.Model}' is not offered by {name}; set SemanticRouter:Models:{index}:Model (dashboard: Providers → Models → {model.Name} → Model) to one of: " +
                    string.Join(", ", probe.Models.Take(12)) + (probe.Models.Count > 12 ? ", …" : string.Empty) + "."));
                continue;
            }

            if (!found.Listed)
            {
                checks.Add(new RouterCheck(modelSubject, RouterCheckStatus.Failed,
                    $"'{model.Model}' is downloaded but not loaded, and {probe.Server ?? name} only serves loaded models: load it (`lms load {model.Model}`) " +
                    "or turn on just-in-time loading in the server settings."));
                continue;
            }

            string state = found.Loaded switch
            {
                true => " (loaded)",
                false when probe.Loading == ModelLoading.OnDemand => " (not loaded: its first request waits for it to load)",
                _ => string.Empty,
            };
            checks.Add(new RouterCheck(modelSubject, RouterCheckStatus.Ok, $"'{model.Model}' is offered by {name}{state}."));
            if (found.LoadedContextLength is { } loaded && model.ContextWindow > loaded)
            {
                checks.Add(new RouterCheck(modelSubject, RouterCheckStatus.Warning,
                    $"ContextWindow {model.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)} is larger than the {loaded.ToString("N0", CultureInfo.InvariantCulture)} " +
                    $"tokens it is loaded with, so longer requests are sent to it, fail and cool it down; set SemanticRouter:Models:{index}:ContextWindow to " +
                    $"{loaded.ToString(CultureInfo.InvariantCulture)} or load it with more context."));
            }
        }

        return checks;
    }

    private static async Task<IEnumerable<RouterCheck>> CheckCliAsync(
        string name, ConnectorOptions connector, ICliProcessRunner runner, CancellationToken cancellationToken)
    {
        string subject = $"connector {name}";
        string executable = string.IsNullOrWhiteSpace(connector.Executable) ? CliAdapter.For(connector.Preset).DefaultExecutable : connector.Executable;
        CliExecutableLookup lookup = CliExecutableLocator.Find(executable);
        if (lookup.Path is not { } path)
        {
            return [new RouterCheck(subject, RouterCheckStatus.Failed, lookup.Problem(name)!)];
        }

        (int exit, string output) = await RunAsync(runner, path, ["--version"], connector, cancellationToken).ConfigureAwait(false);
        if (exit == NoWorkingDirectory)
        {
            // Not the CLI's fault: the router could not make the private folder every CLI request runs in.
            return [new RouterCheck(subject, RouterCheckStatus.Failed,
                $"the router could not create the private working directory CLIs run in: {output.TrimEnd('.')}. Every request to {name} needs it: " +
                "make sure your user owns and can write to it (on Linux it is under XDG_RUNTIME_DIR or ~/.cache/semantic-router).")];
        }

        if (exit != 0)
        {
            return [new RouterCheck(subject, RouterCheckStatus.Failed, $"{path} --version failed (exit {exit}): {FirstLine(output)}")];
        }

        string version = FirstLine(output);
        var checks = new List<RouterCheck>();
        if (connector.Preset == CliPreset.Codex)
        {
            // `codex login status` answers without asking anything: "Logged in using ChatGPT", or "Not logged in".
            (int loginExit, string login) = await RunAsync(runner, path, ["login", "status"], connector, cancellationToken).ConfigureAwait(false);
            checks.Add(CodexSignIn(subject, version, path, loginExit, login));
        }
        else if (connector.Preset != CliPreset.ClaudeCode)
        {
            checks.Add(new RouterCheck(subject, RouterCheckStatus.Ok, $"{version} at {path}. Sign-in is not checked for this CLI; run it once yourself to log in."));
        }
        else
        {
            // `claude auth status` prints JSON such as {"loggedIn":true,"authMethod":"claude.ai",...}.
            (int authExit, string auth) = await RunAsync(runner, path, ["auth", "status"], connector, cancellationToken).ConfigureAwait(false);
            (bool? loggedIn, string? method) = ReadClaudeAuthStatus(auth);
            if (authExit != 0 || loggedIn == false)
            {
                checks.Add(new RouterCheck(subject, RouterCheckStatus.Failed, $"{version} at {path} is not signed in. Run `claude` once and use /login."));
            }
            else if (method is "api_key" or "api_key_helper" or "third_party")
            {
                checks.Add(new RouterCheck(subject, RouterCheckStatus.Warning,
                    $"{version} at {path} is signed in with {method}, so requests are not billed to a Claude subscription. Use /logout and /login to change that."));
            }
            else
            {
                checks.Add(new RouterCheck(subject, RouterCheckStatus.Ok, $"{version} at {path}, signed in ({method ?? "unknown method"})."));
            }
        }

        return checks;
    }

    /// <summary>
    /// What <c>codex login status</c> says about the sign-in. Its output is not repeated: with an API key it shows part
    /// of the key. A version without the command (or an answer not understood) is not held against the connector.
    /// </summary>
    internal static RouterCheck CodexSignIn(string subject, string version, string path, int exitCode, string output)
    {
        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return new RouterCheck(subject, RouterCheckStatus.Failed, $"{version} at {path} is not signed in. Run `codex login` once (it signs in with your ChatGPT account).");
        }

        if (exitCode == 0 && output.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return new RouterCheck(subject, RouterCheckStatus.Warning,
                $"{version} at {path} is signed in with an API key, so requests are billed to that key, not to a ChatGPT plan. Run `codex logout`, then `codex login`, to change that.");
        }

        return exitCode == 0 && output.Contains("logged in", StringComparison.OrdinalIgnoreCase)
            ? new RouterCheck(subject, RouterCheckStatus.Ok, $"{version} at {path}, signed in{(output.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? " with ChatGPT" : string.Empty)}.")
            : new RouterCheck(subject, RouterCheckStatus.Ok, $"{version} at {path}. Its sign-in could not be checked (`codex login status` gave no answer); run `codex login` once yourself.");
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(
        ICliProcessRunner runner, string executable, string[] arguments, ConnectorOptions connector, CancellationToken cancellationToken)
    {
        string directory;
        try
        {
            directory = CliScratch.Create("doctor-" + Guid.NewGuid().ToString("N"));
        }
        catch (IOException ex)
        {
            return (NoWorkingDirectory, ex.Message);
        }

        try
        {
            // The same environment requests get, so the result reflects what the router will use.
            var invocation = new CliInvocation
            {
                Executable = executable,
                Arguments = arguments,
                WorkingDirectory = directory,
                SetEnvironment = CliAdapter.For(connector.Preset).EnvironmentFor(connector),
                RemoveEnvironment = ((IEnumerable<string>?)connector.RemoveEnvironmentVariables ?? CliAdapter.For(connector.Preset).DefaultRemovedEnvironment).ToArray(),
            };

            ICliProcess process;
            try
            {
                process = runner.Start(invocation);
            }
            catch (FileNotFoundException ex)
            {
                return (-1, ex.Message);
            }

            await using (process.ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var output = new System.Text.StringBuilder();
                    await foreach (string line in process.ReadOutputLinesAsync(timeout.Token).ConfigureAwait(false))
                    {
                        output.AppendLine(line);
                    }

                    int exit = await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    return (exit, output.Append(process.StandardError).ToString());
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (-1, "timed out after 20 s");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static (bool? LoggedIn, string? Method) ReadClaudeAuthStatus(string output)
    {
        int start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(output[start..]);
            JsonElement root = json.RootElement;
            bool? loggedIn = root.TryGetProperty("loggedIn", out JsonElement l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? l.GetBoolean()
                : null;
            return (loggedIn, root.GetString("authMethod"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    internal static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}
