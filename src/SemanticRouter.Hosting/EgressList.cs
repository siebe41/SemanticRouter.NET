using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Host;

/// <summary>
/// For <c>doctor</c>: every host the router will contact with the current configuration, one <c>egress</c> line each,
/// for a firewall allowlist (docs/network.md). Webhooks show their host only: their URLs carry tokens.
/// </summary>
internal static class EgressList
{
    public const string Subject = "egress";

    /// <summary>
    /// The hosts GitHub Releases updates come from: the API, and the release download, which redirects to
    /// <c>release-assets.githubusercontent.com</c> (<c>objects.githubusercontent.com</c> for older assets).
    /// </summary>
    public const string UpdateHosts = "api.github.com, github.com, release-assets.githubusercontent.com and objects.githubusercontent.com";

    /// <summary>
    /// Lists the destinations. Call it before the embeddings check, which may download the ONNX model.
    /// <paramref name="configurationDirectory"/> is where the model is downloaded to unless <c>ModelsDirectory</c> says otherwise.
    /// </summary>
    public static IReadOnlyList<RouterCheck> For(IServiceProvider services, string configurationDirectory)
    {
        RouterRuntimeProvider provider = services.GetRequiredService<RouterRuntimeProvider>();
        RouterConfiguration resolved = provider.Current.Resolved;
        MachinePolicy policy = provider.Policy;
        var lines = new List<RouterCheck>();

        foreach ((string name, ConnectorOptions connector) in resolved.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool used = resolved.Models.Any(m => m.Enabled && string.Equals(m.Connector, name, StringComparison.OrdinalIgnoreCase));
            string where = connector.Type switch
            {
                ConnectorTypes.Cli => $"CLI: {(string.IsNullOrWhiteSpace(connector.Executable) ? CliAdapter.For(connector.Preset).DefaultExecutable : connector.Executable)}, which contacts {Vendor(connector.Preset)}",
                _ when connector.IsType(CopilotConnector.Type) => $"CLI: {(string.IsNullOrWhiteSpace(connector.Executable) ? "copilot" : connector.Executable)} (the Copilot SDK's runtime), which contacts GitHub",
                _ => Host(connector.BaseAddress) ?? "no BaseAddress",
            };
            string state = policy.Blocks(connector) is { } blocked ? $" (never contacted: {blocked})" : used ? string.Empty : " (no enabled model uses it yet)";
            lines.Add(Line($"connector {name}: {where}{state}."));
        }

        if (Embeddings(services, resolved.Embeddings, configurationDirectory, policy) is ({ } embeddings, bool problem))
        {
            lines.Add(new RouterCheck(Subject, problem ? RouterCheckStatus.Warning : RouterCheckStatus.Ok, $"embeddings: {embeddings}."));
        }

        if (IsOnnx(resolved.Embeddings))
        {
            OnnxRuntimeEnvironment.DisableTelemetry();
            lines.Add(OnnxTelemetry(OnnxRuntimeEnvironment.TelemetrySetting));
        }

        foreach ((string name, WebhookOptions hook) in resolved.Notifications.Webhooks.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(hook.Url) || !(hook.Notices || hook.Digest))
            {
                continue;
            }

            string host = Uri.TryCreate(hook.Url, UriKind.Absolute, out Uri? url) ? Host(url)! : "its URL is not resolved";
            lines.Add(Line($"webhook {name}: {host}{(resolved.Notifications.Enabled ? string.Empty : " (Notifications:Enabled is off, so nothing is sent)")}."));
        }

        Uri prices = ModelPriceList.DefaultSource;
        lines.Add(Line(policy.Refuses(prices) is { } noPrices
            ? $"price list: {prices.Host} (never contacted: {noPrices})."
            : $"price list: {prices.Host}, only when the dashboard looks up cloud models' prices (Look up prices, Discover models); nothing but the request for the list is sent."));

        // The desktop app follows the machine policy only, not this host's own Network:Offline.
        lines.Add(Line(Updates(services.GetService<MachinePolicy>() ?? MachinePolicy.None)));

        try
        {
            OutboundTrust trust = services.GetRequiredService<OutboundTrust>();
            if (trust.Roots is { } roots)
            {
                lines.Add(new RouterCheck("network", RouterCheckStatus.Ok,
                    $"HTTPS also trusts the {roots.Count} certificate(s) in {trust.Source} (SemanticRouter:Network:CaBundle)."));
            }
        }
        catch (InvalidOperationException ex)
        {
            lines.Add(new RouterCheck("network", RouterCheckStatus.Failed, ex.Message));
        }

        return lines;
    }

    private static RouterCheck Line(string message) => new(Subject, RouterCheckStatus.Ok, message);

    /// <summary>Whether ONNX Runtime's own telemetry is off, from <c>ORT_DISABLE_TELEMETRY</c> as ONNX Runtime will read it.</summary>
    internal static RouterCheck OnnxTelemetry(string? setting) => OnnxRuntimeEnvironment.TurnsTelemetryOff(setting)
        ? Line($"ONNX Runtime telemetry: off ({OnnxRuntimeEnvironment.TelemetryVariable}=1, set before ONNX Runtime loads): " +
               $"{OnnxRuntimeEnvironment.TelemetryHost} is never contacted and no device id is written.")
        : new RouterCheck(Subject, RouterCheckStatus.Warning,
            $"ONNX Runtime telemetry: {OnnxRuntimeEnvironment.TelemetryVariable} is {(setting is null ? "not set" : $"'{setting}'")} in this environment, " +
            $"so on Linux and macOS ONNX Runtime may send usage data to {OnnxRuntimeEnvironment.TelemetryHost} and keep a device id in " +
            $"~/.cache/Microsoft/DeveloperTools/.onnxruntime. Set it to 1, or unset it and the router sets it.");

    /// <summary>The desktop app's updates line: where they come from, and what the machine policy says about them.</summary>
    internal static string Updates(MachinePolicy policy)
    {
        UpdatePolicy updates = policy.Updates;
        if (policy.Offline || updates.Mode == UpdateMode.Off)
        {
            return "desktop app updates: off (your organization's policy).";
        }

        string from = updates.Mode == UpdateMode.Feed && Uri.TryCreate(updates.Feed, UriKind.Absolute, out Uri? feed)
            ? (feed.IsFile ? $"the folder {feed.LocalPath}" : Host(feed)!) + " (your organization's feed)"
            : UpdateHosts;
        string applied = updates.Apply == UpdateApply.Notify ? "; applied only when the person chooses to" : string.Empty;
        string signed = updates.Publisher is { } publisher ? $"; applied only when signed by {publisher}"
            : updates.Mode == UpdateMode.Feed ? string.Empty
            : "; applied only when the release workflow attested it, or GitHub cannot be asked";
        string custom = updates.AllowCustomFeed && updates.Mode != UpdateMode.Feed ? "; SEMANTICROUTER_UPDATE_SOURCE may name another feed" : string.Empty;
        return $"desktop app updates: {from} (only the installed desktop app{applied}{signed}{custom}; untick Check for updates automatically in its tray menu to turn this off).";
    }

    private static string Vendor(CliPreset preset) => preset switch
    {
        CliPreset.ClaudeCode => "Anthropic",
        CliPreset.Codex => "OpenAI",
        CliPreset.GitHubCopilot => "GitHub",
        _ => "whatever that program contacts",
    };

    /// <summary>Host and port only: never the user information, path or query, which can carry credentials.</summary>
    private static string? Host(Uri? address) => address is { IsAbsoluteUri: true }
        ? address.Authority + (address.IsLoopback ? " (this machine)" : string.Empty)
        : null;

    private static bool IsOnnx(EmbeddingProviderOptions embeddings) =>
        string.Equals(embeddings.Provider, OnnxEmbeddings.Provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where embedding requests go, and whether that needs attention (a model that is missing and may not be downloaded).</summary>
    private static (string Text, bool Problem)? Embeddings(IServiceProvider services, EmbeddingProviderOptions embeddings, string configurationDirectory, MachinePolicy policy)
    {
        bool onnxRuns = services.GetServices<IEmbeddingProviderFactory>()
            .FirstOrDefault(f => string.Equals(f.Kind, OnnxEmbeddings.Provider, StringComparison.OrdinalIgnoreCase))?.Unavailable is null;
        switch (IsOnnx(embeddings) ? OnnxEmbeddings.Provider : embeddings.Provider)
        {
            case EmbeddingProviderKinds.OpenAICompatible:
                return (Host(embeddings.Endpoint) ?? "no Endpoint", false);
            case OnnxEmbeddings.Provider when !onnxRuns:
                // The embeddings check says why and what to use instead.
                return ("none: ONNX Runtime cannot run on this machine, so no ONNX model is downloaded or used", true);
            case OnnxEmbeddings.Provider when !string.IsNullOrWhiteSpace(embeddings.ModelPath):
                return ($"ONNX model at {embeddings.ModelPath}, on this machine; nothing is downloaded", false);
            case OnnxEmbeddings.Provider when OnnxEmbeddingModels.Find(embeddings.Model ?? OnnxEmbeddingModels.DefaultModel) is { } model:
                // OnnxEmbeddingProviderFactory's default: a models directory next to the configuration file.
                string models = services.GetService<IOptions<OnnxEmbeddingOptions>>()?.Value.ModelsDirectory ?? Path.Combine(configurationDirectory, "models");
                string directory = Path.Combine(Path.GetFullPath(models), model.Name);
                if (File.Exists(Path.Combine(directory, "model.onnx")) && File.Exists(Path.Combine(directory, "vocab.txt")))
                {
                    return ($"ONNX model {model.Name} is in {directory}; nothing is downloaded", false);
                }

                return policy.Refuses(model.ModelUrl) is { } refused
                    ? ($"ONNX model {model.Name} is not in {directory} and is not downloaded ({refused}): place model.onnx and vocab.txt there", true)
                    : ($"{OnnxEmbeddingModels.DownloadHosts}, once, to download ONNX model {model.Name} into {directory} (copy it there to skip the download)", false);
            case EmbeddingProviderKinds.Hashing:
                return ("Hashing, on this machine; nothing is sent", false);
            default:
                return null;
        }
    }
}
