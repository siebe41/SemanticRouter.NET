using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Host;

/// <summary>
/// For <c>doctor</c>: every host the router will contact with the current configuration, one <c>egress</c> line each,
/// for a firewall allowlist (docs/network.md). Webhooks show their host only: their URLs carry tokens.
/// </summary>
internal static class EgressList
{
    public const string Subject = "egress";

    /// <summary>The hosts GitHub Releases updates come from.</summary>
    public const string UpdateHosts = "api.github.com, github.com and objects.githubusercontent.com";

    /// <summary>
    /// Lists the destinations. Call it before the embeddings check, which may download the ONNX model.
    /// <paramref name="configurationDirectory"/> is where the model is downloaded to unless <c>ModelsDirectory</c> says otherwise.
    /// </summary>
    public static IReadOnlyList<RouterCheck> For(IServiceProvider services, string configurationDirectory)
    {
        RouterConfiguration resolved = services.GetRequiredService<RouterRuntimeProvider>().Current.Resolved;
        var lines = new List<RouterCheck>();

        foreach ((string name, ConnectorOptions connector) in resolved.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool used = resolved.Models.Any(m => m.Enabled && string.Equals(m.Connector, name, StringComparison.OrdinalIgnoreCase));
            string where = connector.Type switch
            {
                ConnectorType.Cli => $"CLI: {(string.IsNullOrWhiteSpace(connector.Executable) ? CliAdapter.For(connector.Preset).DefaultExecutable : connector.Executable)}, which contacts {Vendor(connector.Preset)}",
                ConnectorType.Copilot => $"CLI: {(string.IsNullOrWhiteSpace(connector.Executable) ? "copilot" : connector.Executable)} (the Copilot SDK's runtime), which contacts GitHub",
                _ => Host(connector.BaseAddress) ?? "no BaseAddress",
            };
            lines.Add(Line($"connector {name}: {where}{(used ? string.Empty : " (no enabled model uses it yet)")}."));
        }

        if (Embeddings(services, resolved.Embeddings, configurationDirectory) is { } embeddings)
        {
            lines.Add(Line($"embeddings: {embeddings}."));
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

        lines.Add(Line($"desktop app updates: {UpdateHosts} (only the installed desktop app; untick Check for updates in its tray menu to turn this off)."));

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

    private static string? Embeddings(IServiceProvider services, EmbeddingProviderOptions embeddings, string configurationDirectory)
    {
        switch (embeddings.Provider)
        {
            case EmbeddingProviderKind.OpenAICompatible:
                return Host(embeddings.Endpoint) ?? "no Endpoint";
            case EmbeddingProviderKind.Onnx when !string.IsNullOrWhiteSpace(embeddings.ModelPath):
                return $"ONNX model at {embeddings.ModelPath}, on this machine; nothing is downloaded";
            case EmbeddingProviderKind.Onnx when OnnxEmbeddingModels.Find(embeddings.Model ?? OnnxEmbeddingModels.DefaultModel) is { } model:
                // OnnxEmbeddingProviderFactory's default: a models directory next to the configuration file.
                string models = services.GetService<IOptions<OnnxEmbeddingOptions>>()?.Value.ModelsDirectory ?? Path.Combine(configurationDirectory, "models");
                string directory = Path.Combine(Path.GetFullPath(models), model.Name);
                return File.Exists(Path.Combine(directory, "model.onnx")) && File.Exists(Path.Combine(directory, "vocab.txt"))
                    ? $"ONNX model {model.Name} is in {directory}; nothing is downloaded"
                    : $"{model.ModelUrl.Host}, once, to download ONNX model {model.Name} into {directory} (copy it there to skip the download)";
            case EmbeddingProviderKind.Hashing:
                return "Hashing, on this machine; nothing is sent";
            default:
                return null;
        }
    }
}
