using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Storage.Sqlite;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Stopping the router: work it does in the background on the state store (the trackers' refreshes, the judge's count)
/// is waited for, and the state database is not touched once it is closed, so its files can be moved or deleted.
/// </summary>
public sealed class StoppingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-stopping").FullName;

    public void Dispose()
    {
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Stopping_waits_for_the_background_reads_of_the_state_store()
    {
        using var store = new SlowActivityStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(new ConfigurationBuilder().Build().GetSection("SemanticRouter"));
        services.AddSingleton<IRoutingActivityStore>(store);
        ServiceProvider provider = services.BuildServiceProvider();

        // A configuration applied starts the trackers' refreshes; the judge reads today's count when it is created.
        provider.GetRequiredService<ModelLatencyTracker>().Configure(new ModelSelectionOptions());
        provider.GetRequiredService<ModelCacheTracker>().Configure(new ModelSelectionOptions());
        provider.GetRequiredService<AnswerJudge>();
        SpinWait.SpinUntil(() => store.Reading == 3, TimeSpan.FromSeconds(10)).Should().BeTrue();

        // Longer than anything else stopping waits for (the judge's worker: 2 s).
        Task stopping = Task.Run(provider.Dispose);
        bool stoppedFirst = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(3))) == stopping;
        store.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        stoppedFirst.Should().BeFalse("the reads under way are waited for");
        store.Reading.Should().Be(0, "nothing reads the store once the router stopped");
    }

    [Fact]
    public void A_closed_state_database_is_not_opened_again()
    {
        string path = Path.Combine(_directory, "state.db");
        var database = new SqliteRouterDatabase(
            Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = path }), NullLogger<SqliteRouterDatabase>.Instance);
        database.Peek("reading", 0, _ => 1).Should().Be(1);

        database.Dispose();
        SqliteFiles.Release(_directory);
        foreach (string file in Directory.GetFiles(_directory))
        {
            File.Delete(file);
        }

        database.Peek("reading", 0, _ => 1).Should().Be(0, "background work that outlived the router gets the fallback");
        Directory.GetFiles(_directory).Should().BeEmpty("the file is not created again");
    }

    /// <summary>An activity store whose reads of a time range wait until released (another router holds the file).</summary>
    private sealed class SlowActivityStore : IRoutingActivityStore, IDisposable
    {
        private readonly ManualResetEventSlim _open = new(false);
        private int _reading;

        public int Reading => Volatile.Read(ref _reading);

        public void Release() => _open.Set();

        public void Append(RoutingActivityEntry entry)
        {
        }

        public IReadOnlyList<RoutingActivityEntry> Recent(int limit) => [];

        public IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit)
        {
            Interlocked.Increment(ref _reading);
            try
            {
                _open.Wait(TimeSpan.FromSeconds(30));
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _reading);
            }
        }

        public void Dispose()
        {
            _open.Set();
            _open.Dispose();
        }
    }
}
