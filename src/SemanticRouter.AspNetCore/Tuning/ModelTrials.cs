namespace SemanticRouter.AspNetCore;

/// <summary>
/// The cheaper models on trial for their routes (<see cref="TuningChangeKind.RouteModel"/> changes that are
/// <see cref="TuningChangeState.Trying"/>): picks the turns that try them, a share of each route's plain matches
/// (<see cref="TuningOptions.ModelTrialRate"/>) up to <see cref="TuningOptions.ModelTrialTurns"/> turns. Automatic tuning
/// starts and ends the trials (<see cref="RouterTuner"/>); trials run only while it applies changes and is not paused.
/// </summary>
internal sealed class ModelTrials(ITuningStore store, RouterRuntimeProvider provider, RoutingActivityLog activity)
{
    private readonly Lock _gate = new();
    private Dictionary<string, Trial>? _trials;
    private bool _paused;

    /// <summary>Draws whether a turn tries the model on trial; replaceable for tests.</summary>
    internal Random Random { get; set; } = Random.Shared;

    /// <summary>The trial to run on this plain match of <paramref name="route"/>, if this turn is one that tries it.</summary>
    public (long Id, string Model)? Pick(string route)
    {
        TuningOptions options = provider.Current.Configuration.Tuning;
        if (options.Mode != TuningMode.Apply || options.ModelTrialRate <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            Dictionary<string, Trial> trials = Load();
            return !_paused && trials.TryGetValue(route, out Trial? trial) && trial.Turns < options.ModelTrialTurns && Random.NextDouble() < options.ModelTrialRate
                ? (trial.Id, trial.Model)
                : null;
        }
    }

    /// <summary>A turn of trial <paramref name="id"/> was routed to its model.</summary>
    public void Counted(long id)
    {
        lock (_gate)
        {
            if (Load().Values.FirstOrDefault(t => t.Id == id) is { } trial)
            {
                trial.Turns++;
            }
        }
    }

    /// <summary>Reads the trials again after automatic tuning started, ended or stopped one, or was paused.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _trials = null;
        }
    }

    /// <summary>The running trials by route, with the turns each had so far (counted from activity after a restart).</summary>
    private Dictionary<string, Trial> Load()
    {
        if (_trials is not null)
        {
            return _trials;
        }

        _paused = store.LoadState().Paused;
        TuningChange[] trying = [.. store.Changes().Where(c => c.Kind == TuningChangeKind.RouteModel && c.State == TuningChangeState.Trying && c.Route is not null && c.NewModel is not null)];
        Dictionary<long, int> turns = trying.Length == 0
            ? []
            : activity.Between(trying.Min(c => c.Timestamp) - TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddMinutes(1))
                .Where(e => e.Trial is not null).GroupBy(e => e.Trial!.Value).ToDictionary(g => g.Key, g => g.Count());
        _trials = trying.GroupBy(c => c.Route!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(c => c.Id)!)
            .ToDictionary(c => c.Route!, c => new Trial(c.Id, c.NewModel!) { Turns = turns.GetValueOrDefault(c.Id) }, StringComparer.OrdinalIgnoreCase);
        return _trials;
    }

    private sealed record Trial(long Id, string Model)
    {
        public int Turns { get; set; }
    }
}
