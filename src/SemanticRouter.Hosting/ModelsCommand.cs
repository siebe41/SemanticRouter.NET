using System.Globalization;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// <c>semantic-router models [connector]</c>: asks every connector (or the one named) which models it offers and prints
/// them with their load state, context and size, which ones the catalog already has, and a suggested tier and
/// capabilities for the rest. Local servers also say whether they load models on demand. Nothing is changed.
/// </summary>
internal static class ModelsCommand
{
    public static async Task<int> RunAsync(HostSettings settings, TextWriter output, Action<WebApplicationBuilder>? configure = null)
    {
        await using WebApplication app = RouterHost.Build(settings, builder =>
        {
            builder.Logging.SetMinimumLevel(LogLevel.Error);
            configure?.Invoke(builder);
        });

        IReadOnlyList<ModelLineupReport> reports;
        try
        {
            reports = settings.Connector is { } only
                ? [await ModelLineup.DiscoverAsync(app.Services, only).ConfigureAwait(false)]
                : await ModelLineup.DiscoverAllAsync(app.Services).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            await output.WriteLineAsync(ex.Message + " `semantic-router models` lists every connector.").ConfigureAwait(false);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"The configuration does not validate: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (reports.Count == 0)
        {
            await output.WriteLineAsync("No connectors are configured. Run `semantic-router init`, or add one on the dashboard (Connectors).").ConfigureAwait(false);
            return 0;
        }

        foreach (ModelLineupReport report in reports)
        {
            await WriteAsync(output, report).ConfigureAwait(false);
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        if (reports[0].Families is { } families)
        {
            await output.WriteLineAsync($"Tiers come from the model-family table ({families}) where a family is known, else from the size or name.").ConfigureAwait(false);
        }

        await output.WriteLineAsync("Add models on the dashboard: Models → Discover models fills in these suggestions, and every field can be changed before adding.").ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteAsync(TextWriter output, ModelLineupReport report)
    {
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"{report.Connector} ({report.Type}{(report.Server is null ? string.Empty : ", " + report.Server)})").ConfigureAwait(false);
        if (report.Note is not null)
        {
            await output.WriteLineAsync("  " + report.Note).ConfigureAwait(false);
        }

        if (report.LoadingNote is not null)
        {
            await output.WriteLineAsync("  " + report.LoadingNote).ConfigureAwait(false);
        }

        if (report.FamiliesWarning is not null)
        {
            await output.WriteLineAsync("  " + report.FamiliesWarning).ConfigureAwait(false);
        }

        if (report.Entries.Count == 0)
        {
            return;
        }

        int width = Math.Clamp(report.Entries.Max(e => e.Model.Id.Length), 10, 48);
        await output.WriteLineAsync($"  {"MODEL".PadRight(width)}  {"STATE",-11} {"CONTEXT",9} {"SIZE",6}  CATALOG").ConfigureAwait(false);
        foreach (ModelLineupEntry entry in report.Entries)
        {
            DiscoveredModel model = entry.Model;
            string state = model.Kind == DiscoveredModelKind.Embedding ? "embedding"
                : !model.Listed ? "not served"
                : model.Loaded switch { true => "loaded", false => "not loaded", _ => string.Empty };
            int? context = model.LoadedContextLength ?? model.MaxContextLength;
            string catalog = entry.InCatalog is { } existing
                ? $"{existing} (T{entry.InCatalogTier?.ToString(CultureInfo.InvariantCulture)})"
                : entry.Suggestion is { } s
                    ? $"+ {s.Name} T{s.Tier.ToString(CultureInfo.InvariantCulture)} ({entry.TierFrom}) {string.Join(",", s.Capabilities)}{(s.Enabled ? string.Empty : " (disabled)")}"
                    : entry.Reason;
            string id = model.Id.Length > width ? model.Id[..(width - 1)] + "…" : model.Id;
            await output.WriteLineAsync(
                $"  {id.PadRight(width)}  {state,-11} {(context is { } c ? c.ToString("N0", CultureInfo.InvariantCulture) : "-"),9} " +
                $"{(model.ParametersBillions is { } b ? b.ToString("0.#", CultureInfo.InvariantCulture) + "B" : "-"),6}  {catalog}").ConfigureAwait(false);
            if (entry.InCatalog is null && entry.Suggestion is not null)
            {
                await output.WriteLineAsync($"  {string.Empty.PadRight(width)}  ↳ {entry.Reason}").ConfigureAwait(false);
            }
        }
    }
}
