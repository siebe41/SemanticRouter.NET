namespace SemanticRouter.AspNetCore;

/// <summary>
/// Which local models are loaded, asked of each LM Studio or Ollama connector the catalog's enabled local models use
/// (<see cref="ConnectorProbe.LoadedModelsAsync"/>). Routing never waits for it: a request reads the last answer and,
/// when that is older than a few seconds, starts a new round in the background.
/// </summary>
internal sealed class ModelResidencyTracker(IHttpClientFactory http, TimeProvider? time = null) : IModelResidency
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Dictionary<string, ConnectorOptions> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, IReadOnlySet<string>?> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;
    private bool _refreshing;

    /// <summary>
    /// Applies a new configuration, with secrets resolved: the connectors its enabled free local models use, asked right
    /// away. With fewer than two such models there is never a choice between them, so nothing is asked.
    /// </summary>
    public void Configure(RouterConfiguration resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ModelDefinition[] local = resolved.Models.Where(m => m.Enabled && m.IsLocal && m.EstimateCost(1_000, 1_000) == 0).ToArray();
        var connectors = (local.Length > 1 ? local : [])
            .Select(m => m.Connector).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(resolved.Connectors.ContainsKey)
            .ToDictionary(name => name, name => resolved.Connectors[name], StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _connectors = connectors;
            _refreshedAt = DateTimeOffset.MinValue;
        }

        Refresh();
    }

    /// <inheritdoc />
    public bool? IsLoaded(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsLocal || model.Model is not { Length: > 0 } id)
        {
            return null;
        }

        Refresh();
        return _loaded.TryGetValue(model.Connector, out IReadOnlySet<string>? loaded) && loaded is not null ? loaded.Contains(id) : null;
    }

    /// <summary>Starts a round of questions when the last one is old and none is running.</summary>
    private void Refresh()
    {
        Dictionary<string, ConnectorOptions> connectors;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_refreshing || now - _refreshedAt < RefreshEvery || _connectors.Count == 0)
            {
                return;
            }

            _refreshing = true;
            _refreshedAt = now;
            connectors = _connectors;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                (string Name, IReadOnlySet<string>? Loaded)[] answers = await Task.WhenAll(connectors.Select(async c => (c.Key, await AskAsync(c.Key, c.Value).ConfigureAwait(false)))).ConfigureAwait(false);
                _loaded = answers.ToDictionary(a => a.Name, a => a.Loaded, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                lock (_gate)
                {
                    _refreshing = false;
                }
            }
        });
    }

    private async Task<IReadOnlySet<string>?> AskAsync(string name, ConnectorOptions connector)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            return await ConnectorProbe.LoadedModelsAsync(connector, http.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + name), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
