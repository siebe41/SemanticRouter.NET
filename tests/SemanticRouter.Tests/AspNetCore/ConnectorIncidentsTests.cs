using System.Text.Json;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Connector incidents (#392): a failed request changes memory only, the state is saved in the background outside the
/// lock, and a connector that hits its limit on every request keeps one hit per day.
/// </summary>
public sealed class ConnectorIncidentsTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void A_failed_request_does_not_wait_for_the_store()
    {
        using var store = new SlowStore();
        using var incidents = new ConnectorIncidents(_time, store);
        store.Block();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit reached");
            incidents.Failed("lmstudio", ModelFailureKind.Unavailable, "connection refused");
            incidents.Failing().Should().ContainSingle();
        }

        timer.Stop();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the store is saving (blocked) in the background");
        store.Saves.Should().BeLessThanOrEqualTo(1, "only the first change started a save, which is still under way");

        store.Unblock();
        incidents.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        store.Saves.Should().BeLessThanOrEqualTo(2, "the changes made meanwhile are saved together");

        using var restarted = new ConnectorIncidents(_time, store);
        restarted.Failing().Should().Equal(("lmstudio", _time.GetUtcNow(), "connection refused"));
        restarted.LimitDays().Should().Equal(("claude", 1, "limit reached"));
    }

    [Fact]
    public void A_connector_that_hits_its_limit_on_every_request_keeps_one_hit_per_day()
    {
        var store = new MemoryStore();
        using var incidents = new ConnectorIncidents(_time, store);

        for (int day = 0; day < 10; day++)
        {
            for (int i = 0; i < 100; i++)
            {
                incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit reached");
                _time.Advance(TimeSpan.FromMinutes(1));
            }

            _time.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(100));
        }

        incidents.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        incidents.LimitDays().Should().Equal(("claude", 7, "limit reached"));
        JsonDocument.Parse(store.Values[ConnectorIncidents.StoreKey]).RootElement.GetProperty("claude").GetProperty("limitHits").GetArrayLength()
            .Should().BeLessThanOrEqualTo(8, "one hit per day in the last week, not one per request");
        store.Saves.Should().BeLessThan(100, "a hit on a day that already has one changes nothing a restart would miss");
    }

    [Fact]
    public void State_saved_by_an_earlier_version_with_every_hit_is_read_and_compacted()
    {
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset[] hits = [.. Enumerable.Range(0, 3000).Select(i => now.AddMinutes(-5 * i))]; // about 10 days, one every 5 minutes
        var store = new MemoryStore();
        store.Values[ConnectorIncidents.StoreKey] = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["claude"] = new { failingSince = (DateTimeOffset?)null, limitHits = hits, limitDetail = "limit reached" },
            ["lmstudio"] = new { failingSince = now.AddHours(-2), detail = "connection refused", limitHits = Array.Empty<DateTimeOffset>() },
        });

        using var incidents = new ConnectorIncidents(_time, store);

        incidents.LimitDays().Should().Equal(("claude", 8, "limit reached"));
        incidents.Failing().Should().Equal(("lmstudio", now.AddHours(-2), "connection refused"));

        incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit reached once more");
        incidents.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        JsonDocument.Parse(store.Values[ConnectorIncidents.StoreKey]).RootElement.GetProperty("claude").GetProperty("limitHits").GetArrayLength()
            .Should().BeLessThanOrEqualTo(8);
    }

    [Fact]
    public void Forgetting_the_details_saves_before_it_returns()
    {
        var store = new MemoryStore();
        using var incidents = new ConnectorIncidents(_time, store);
        incidents.Failed("claude-cli", ModelFailureKind.Unavailable, "the CLI said something private");

        incidents.ForgetDetails();

        store.Values[ConnectorIncidents.StoreKey].Should().NotContain("something private");
        incidents.Failing().Should().ContainSingle().Which.Detail.Should().BeNull();
    }

    private class MemoryStore : IStateValueStore
    {
        private int _saves;

        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Values { get; } = new();

        public int Saves => Volatile.Read(ref _saves);

        public string? Load(string key) => Values.GetValueOrDefault(key);

        public virtual void Save(string key, string value)
        {
            Interlocked.Increment(ref _saves);
            Values[key] = value;
        }
    }

    /// <summary>A store whose saves wait while it is blocked, like a database file another router holds.</summary>
    private sealed class SlowStore : MemoryStore, IDisposable
    {
        private readonly ManualResetEventSlim _open = new(true);

        public void Block() => _open.Reset();

        public void Unblock() => _open.Set();

        public override void Save(string key, string value)
        {
            _open.Wait(TimeSpan.FromSeconds(30));
            base.Save(key, value);
        }

        public void Dispose()
        {
            _open.Set();
            _open.Dispose();
        }
    }
}
