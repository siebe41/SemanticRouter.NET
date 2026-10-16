using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// What <c>semantic-router init</c> would find on this machine, for the dashboard's first-run setup: LM Studio and
/// Ollama on their default ports with their chat models, and the <c>claude</c>, <c>codex</c> and <c>copilot</c> CLIs on
/// PATH with whether they are signed in. It runs init's own detection on a copy of the template, so both always agree,
/// and changes nothing: the dashboard asks before it adds anything.
/// </summary>
internal sealed class SetupDetector(IServiceProvider services) : IRouterSetupDetector
{
    // Claude Code sends its own model names; these aliases from the template route them (the dashboard's Claude Code defaults).
    private static readonly string[] ClaudeNames = ["opus", "sonnet", "haiku", "claude"];

    public Task<IReadOnlyList<SetupDetection>> DetectAsync(CancellationToken cancellationToken = default)
    {
        // The router's own client settings (Network:CaBundle, a proxy), as for a connector.
        HttpClient http = services.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + "setup");
        return DetectAsync(services, new InitProbes(http, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), services.GetService<MachinePolicy>()), cancellationToken);
    }

    /// <summary>Detects with <paramref name="probes"/>, which say where to look (replaceable in tests).</summary>
    internal static async Task<IReadOnlyList<SetupDetection>> DetectAsync(IServiceProvider services, InitProbes probes, CancellationToken cancellationToken)
    {
        RouterConfiguration template = RouterConfiguration.FromJson(RouterHost.Template());
        RouterConfiguration found = template.Clone();
        var notes = new List<string>();
        string? file = (services.GetService<IRouterConfigurationStore>() as JsonFileRouterConfigurationStore)?.Location;
        ModelFamilyTable families = ModelFamilyTable.Load(file is null ? null : ModelFamilyTable.FileNextTo(file));

        // As `init --yes`: every CLI is described, and the dashboard shows its consent text before enabling it.
        await InitCommand.DetectAsync(found, probes, notes, new CliConsent(Yes: true, Input: null, TextWriter.Null), cancellationToken, families).ConfigureAwait(false);

        ICliProcessRunner runner = services.GetRequiredService<ICliProcessRunner>();
        var detections = new List<Task<SetupDetection>>();
        foreach ((string name, ConnectorOptions connector) in found.Connectors.Where(c => !template.Connectors.ContainsKey(c.Key)))
        {
            ModelDefinition[] models = [.. found.Models.Where(m => string.Equals(m.Connector, name, StringComparison.OrdinalIgnoreCase))];
            detections.Add(connector.Type == ConnectorType.Cli
                ? CliAsync(name, connector, models, template, probes, runner, cancellationToken)
                : Task.FromResult(Local(name, connector, models, notes)));
        }

        return await Task.WhenAll(detections).ConfigureAwait(false);
    }

    private static SetupDetection Local(string name, ConnectorOptions connector, ModelDefinition[] models, List<string> notes)
    {
        string title = name switch
        {
            "lmstudio" => "LM Studio",
            "ollama" => "Ollama",
            _ => name,
        };
        string where = connector.BaseAddress?.Authority ?? name;
        string detail = models.Length > 0
            ? $"Running at {where} with {models.Length} chat model{(models.Length == 1 ? string.Empty : "s")}: " +
              string.Join(", ", models.Take(4).Select(m => m.Model ?? m.Name)) + (models.Length > 4 ? ", …" : string.Empty)
            : notes.FirstOrDefault(n => n.StartsWith(name + ": ", StringComparison.Ordinal)) is { } note ? note[(name.Length + 2)..] : $"Running at {where}";
        return new SetupDetection(name, title, "local", name, connector) { Detail = detail, Models = models };
    }

    private static async Task<SetupDetection> CliAsync(
        string name, ConnectorOptions connector, ModelDefinition[] models, RouterConfiguration template, InitProbes probes, ICliProcessRunner runner,
        CancellationToken cancellationToken)
    {
        string executable = CliAdapter.For(connector.Preset).DefaultExecutable;
        string? path = CliExecutableLocator.Find(executable, probes.SearchPath, probes.Windows).Path;
        string detail = path is null ? executable : await SetupApi.DescribeCliAsync(executable, path, connector, runner, cancellationToken).ConfigureAwait(false);
        (string id, string title) = connector.Preset switch
        {
            CliPreset.ClaudeCode => ("claude-code", "Claude Code"),
            CliPreset.Codex => ("codex", "Codex"),
            CliPreset.GitHubCopilot => ("copilot-cli", "GitHub Copilot CLI"),
            _ => (name, name),
        };

        // init adds Copilot switched off because it spends premium requests; here the person enables it after reading
        // the consent text, which says so.
        if (connector.Preset == CliPreset.GitHubCopilot)
        {
            foreach (ModelDefinition model in models)
            {
                model.Enabled = true;
            }
        }

        Dictionary<string, string> aliases = connector.Preset == CliPreset.ClaudeCode
            ? template.Middleware.ModelAliases
                .Where(a => ClaudeNames.Any(n => a.Key.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
            : [];
        return new SetupDetection(id, title, "cli", name, connector)
        {
            Detail = detail,
            Consent = SetupApi.Consent(connector.Preset),
            Models = models,
            Aliases = aliases,
        };
    }
}
