using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class ModelHealthTrackerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ModelDefinition _model = Model("claude-cli", 4);

    private ModelHealthTracker Create() => new(
        new ModelSelectionOptions { QuotaCooldown = TimeSpan.FromMinutes(15), FailureCooldown = TimeSpan.FromSeconds(30) },
        new ModelHealthTrackerDependencies { Time = _time });

    [Fact]
    public void Models_start_available()
    {
        Create().IsAvailable(_model).Should().BeTrue();
    }

    [Fact]
    public void Quota_failures_cool_down_longer_than_transient_failures()
    {
        ModelHealthTracker health = Create();
        ModelDefinition other = Model("other", 1);

        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        health.ReportFailure(other, ModelFailureKind.Unavailable);
        _time.Advance(TimeSpan.FromMinutes(1));

        health.IsAvailable(other).Should().BeTrue();
        health.IsAvailable(_model).Should().BeFalse();
        health.GetCooldowns().Should().ContainKey("claude-cli");

        _time.Advance(TimeSpan.FromMinutes(14));
        health.IsAvailable(_model).Should().BeTrue();
        health.GetCooldowns().Should().BeEmpty();
    }

    [Fact]
    public void Success_clears_a_cooldown()
    {
        ModelHealthTracker health = Create();
        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);

        health.ReportSuccess(_model);

        health.IsAvailable(_model).Should().BeTrue();
    }

    [Fact]
    public void Zero_cooldown_disables_tracking()
    {
        var health = new ModelHealthTracker(new ModelSelectionOptions { FailureCooldown = TimeSpan.Zero }, new ModelHealthTrackerDependencies { Time = _time });

        health.ReportFailure(_model, ModelFailureKind.Unavailable);

        health.IsAvailable(_model).Should().BeTrue();
    }

    private ModelHealthTracker Create(ICooldownStore store) => new(
        new ModelSelectionOptions { QuotaCooldown = TimeSpan.FromMinutes(15), FailureCooldown = TimeSpan.FromSeconds(30) },
        new ModelHealthTrackerDependencies { Time = _time, Store = store });

    /// <summary>
    /// Starts the periodic refresh from the store on another thread and returns once it has read the store, which then
    /// waits until <see cref="PausingStore.Resume"/>: what happens meanwhile happens while the refresh is under way.
    /// </summary>
    private Task RefreshUnderWay(ModelHealthTracker health, PausingStore store)
    {
        store.PauseNextLoad();
        _time.Advance(health.RefreshInterval);
        Task refresh = Task.Run(() => health.IsAvailable(Model("someone-else", 1)));
        store.Loading.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the refresh reads the store");
        return refresh;
    }

    [Fact]
    public async Task A_cooldown_set_while_a_refresh_reads_the_store_stays()
    {
        // Issue #394: the refresh read the store before the cooldown was in it, and ended the cooldown.
        using var store = new PausingStore();
        ModelHealthTracker health = Create(store);
        Task refresh = RefreshUnderWay(health, store);

        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        store.Resume();
        await refresh;

        health.IsAvailable(_model).Should().BeFalse("the cooldown is newer than what the refresh read");
        health.GetCooldowns().Should().ContainKey("claude-cli");
    }

    [Fact]
    public async Task A_cooldown_cleared_while_a_refresh_reads_the_store_does_not_come_back()
    {
        using var store = new PausingStore();
        ModelHealthTracker health = Create(store);
        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        Task refresh = RefreshUnderWay(health, store);

        health.ReportSuccess(_model);
        store.Resume();
        await refresh;

        health.IsAvailable(_model).Should().BeTrue("the refresh read the cooldown before it was cleared");
    }

    [Fact]
    public async Task Clearing_every_cooldown_while_a_refresh_reads_the_store_holds()
    {
        using var store = new PausingStore();
        ModelHealthTracker health = Create(store);
        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        Task refresh = RefreshUnderWay(health, store);

        health.Clear();
        store.Resume();
        await refresh;

        health.GetCooldowns().Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_still_takes_other_instances_changes_to_models_this_one_did_not_touch()
    {
        using var store = new PausingStore();
        ModelHealthTracker health = Create(store);
        ModelDefinition other = Model("other", 2);
        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        store.SaveCooldown("other", _time.GetUtcNow().AddMinutes(10)); // another instance
        store.RemoveCooldown("claude-cli"); // another instance saw it answer
        Task refresh = RefreshUnderWay(health, store);

        store.Resume();
        await refresh;

        health.IsAvailable(other).Should().BeFalse();
        health.IsAvailable(_model).Should().BeTrue();
    }

    /// <summary>A store whose next load reads its cooldowns, then waits until it is told to go on.</summary>
    private sealed class PausingStore : ICooldownStore, IDisposable
    {
        private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
        private readonly ManualResetEventSlim _resume = new(true);
        private readonly Lock _gate = new();
        private bool _pause;

        public ManualResetEventSlim Loading { get; } = new();

        public void PauseNextLoad()
        {
            _resume.Reset();
            Loading.Reset();
            _pause = true;
        }

        public void Resume() => _resume.Set();

        public void Dispose()
        {
            _resume.Dispose();
            Loading.Dispose();
        }

        public IReadOnlyDictionary<string, DateTimeOffset> LoadCooldowns()
        {
            Dictionary<string, DateTimeOffset> read;
            lock (_gate)
            {
                read = new Dictionary<string, DateTimeOffset>(_cooldowns, StringComparer.OrdinalIgnoreCase);
            }

            if (_pause)
            {
                _pause = false;
                Loading.Set();
                _resume.Wait(TimeSpan.FromSeconds(10));
            }

            return read;
        }

        public void SaveCooldown(string model, DateTimeOffset until)
        {
            lock (_gate)
            {
                _cooldowns[model] = until;
            }
        }

        public void RemoveCooldown(string model)
        {
            lock (_gate)
            {
                _cooldowns.Remove(model);
            }
        }

        public void ClearCooldowns()
        {
            lock (_gate)
            {
                _cooldowns.Clear();
            }
        }
    }
}
