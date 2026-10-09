using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Durable state in SQLite: cooldowns, tool-call affinity, activity and configuration history.</summary>
public sealed class SqliteStateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-state").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private string DbPath => Path.Combine(_directory, "state.db");

    private SqliteRouterDatabase Open(Action<SqliteStateOptions>? configure = null)
    {
        var options = new SqliteStateOptions { Path = DbPath };
        configure?.Invoke(options);
        using var factory = LoggerFactory.Create(b => b.AddProvider(_logs));
        return new SqliteRouterDatabase(Microsoft.Extensions.Options.Options.Create(options), factory.CreateLogger<SqliteRouterDatabase>(), _time);
    }

    private static readonly ModelSelectionOptions Selection = new() { QuotaCooldown = TimeSpan.FromMinutes(15), FailureCooldown = TimeSpan.FromSeconds(30) };

    [Fact]
    public void A_version_1_state_file_is_migrated_and_keeps_its_usage()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta (key, value) VALUES ('schema_version', '1');
                CREATE TABLE usage (
                    timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
                    is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                    cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO usage VALUES ($ts, 'old', 'c', NULL, NULL, 0, 10, 5, 0, 0, '0.1', '0.1');";
            insert.Parameters.AddWithValue("$ts", _time.GetUtcNow().UtcTicks);
            insert.ExecuteNonQuery();
        }

        var ledger = new SqliteUsageLedger(Open());
        ledger.Record(new UsageRecord(_time.GetUtcNow(), "new", "c", null, null, false, 20, 10, 0, 0, 0.2m, 0.2m, "work"));

        DateTimeOffset since = _time.GetUtcNow().AddHours(-1), until = _time.GetUtcNow().AddHours(1);
        ledger.Total(since, until).Requests.Should().Be(2, "the old row survives the migration");
        ledger.Total(since, until, profile: "work").InputTokens.Should().Be(20);
        ledger.Summarize(since, until, UsageGrouping.Profile).Select(g => g.Key).Should().BeEquivalentTo("work", "(no profile)");
        _logs.Messages.Should().NotContain(m => m.Contains("cannot be used", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restart_during_a_quota_cooldown_keeps_the_original_expiry()
    {
        ModelDefinition claude = Model("claude", 4);
        var before = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(Open()));
        before.ReportFailure(claude, ModelFailureKind.QuotaExceeded);
        DateTimeOffset expiry = before.GetCooldowns()["claude"];

        _time.Advance(TimeSpan.FromMinutes(5));
        var after = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(Open()));

        after.IsAvailable(claude).Should().BeFalse("the cooldown was stored with its absolute expiry");
        after.GetCooldowns()["claude"].Should().Be(expiry);
        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        after.IsAvailable(claude).Should().BeTrue();
    }

    [Fact]
    public void Instances_sharing_the_file_see_each_others_cooldowns_after_a_refresh()
    {
        ModelDefinition claude = Model("claude", 4);
        var a = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(Open()));
        var b = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(Open()));

        a.ReportFailure(claude, ModelFailureKind.QuotaExceeded);
        b.IsAvailable(claude).Should().BeTrue("b has not refreshed yet");
        _time.Advance(TimeSpan.FromSeconds(6));
        b.IsAvailable(claude).Should().BeFalse();

        a.ReportSuccess(claude);
        _time.Advance(TimeSpan.FromSeconds(6));
        b.IsAvailable(claude).Should().BeTrue("a success elsewhere ends the cooldown here too");
    }

    [Fact]
    public void Tool_calls_stay_pinned_across_a_restart_until_the_window_ends()
    {
        new SqliteToolCallAffinity(Open()).Remember("toolu_01", "claude-cli");

        var restarted = new SqliteToolCallAffinity(Open(o => o.AffinityWindow = TimeSpan.FromHours(1)));

        restarted.Lookup(["toolu_unknown", "toolu_01"]).Should().Be("claude-cli");
        _time.Advance(TimeSpan.FromHours(2));
        new SqliteToolCallAffinity(Open(o => o.AffinityWindow = TimeSpan.FromHours(1))).Lookup(["toolu_01"]).Should().BeNull("expired");
    }

    [Fact]
    public void The_activity_log_is_seeded_from_disk_and_continues_its_ids()
    {
        var store = new SqliteActivityStore(Open());
        var log = new RoutingActivityLog(store: store);
        RoutingActivityEntry first = log.Add(new RoutingActivityEntry
        {
            Id = log.Reserve(), Timestamp = _time.GetUtcNow(), PromptPreview = "hello there", Tier = 2, Reason = RoutingReason.SemanticMatch,
            Route = "smalltalk", Protocol = ChatProtocol.Anthropic, Client = "laptop",
        });
        first.ServedBy = "local";
        first.Outcome = "served";
        first.TotalMilliseconds = 12.5;
        log.Complete(first);

        var restarted = new RoutingActivityLog(store: new SqliteActivityStore(Open()));
        RoutingActivityEntry seeded = restarted.Recent().Single();
        RoutingActivityEntry next = restarted.Add(new RoutingActivityEntry { Id = restarted.Reserve(), PromptPreview = "again" });

        seeded.Should().BeEquivalentTo(first);
        next.Id.Should().Be(first.Id + 1);
        restarted.Recent(10, after: first.Id).Should().ContainSingle().Which.PromptPreview.Should().Be("again");
    }

    [Fact]
    public void Activity_is_capped_by_count_and_age()
    {
        SqliteRouterDatabase database = Open(o =>
        {
            o.ActivityMaxEntries = 5;
            o.ActivityMaxAge = TimeSpan.FromDays(1);
        });
        var store = new SqliteActivityStore(database);
        store.Append(new RoutingActivityEntry { Id = 0, Timestamp = _time.GetUtcNow(), PromptPreview = "old" });
        _time.Advance(TimeSpan.FromDays(2));
        for (int i = 1; i <= 120; i++)
        {
            store.Append(new RoutingActivityEntry { Id = i, Timestamp = _time.GetUtcNow(), PromptPreview = "p" + i });
        }

        database.Run("prune", store.Prune);
        IReadOnlyList<RoutingActivityEntry> recent = store.Recent(1000);

        recent.Select(e => e.Id).Should().Equal(120, 119, 118, 117, 116);
    }

    [Fact]
    public void History_keeps_exact_documents_newest_first_and_is_capped()
    {
        var history = new SqliteConfigurationHistory(Open(o => o.HistoryMaxEntries = 2));
        var one = new RouterConfiguration { FallbackTier = 1 };
        var two = new RouterConfiguration { FallbackTier = 2 };
        two.Connectors["x"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1"), ApiKey = "${env:X_KEY}" };

        long first = history.Record(one, "startup");
        long second = history.Record(two, "admin API", "this machine", "added x");
        long third = history.Record(one, "rollback", "this machine", $"to version {first}");

        history.List().Select(v => (v.Id, v.Source)).Should().Equal((third, "rollback"), (second, "admin API"));
        history.GetVersion(second)!.ToJson().Should().Be(two.ToJson());
        history.GetVersion(first).Should().BeNull("capped at two versions");
        history.List()[1].Should().Match<RouterConfigurationVersion>(v => v.Actor == "this machine" && v.Note == "added x");
    }

    [Fact]
    public async Task Concurrent_writers_on_one_file_lose_nothing()
    {
        var a = new SqliteToolCallAffinity(Open());
        var b = new SqliteToolCallAffinity(Open());
        var cooldownsA = new SqliteCooldownStore(Open());
        var cooldownsB = new SqliteCooldownStore(Open());

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            (i % 2 == 0 ? a : b).Remember("toolu_" + i, "model-" + i);
            (i % 2 == 0 ? cooldownsA : cooldownsB).SaveCooldown("m" + i, _time.GetUtcNow().AddMinutes(5));
        })));

        var reader = new SqliteToolCallAffinity(Open());
        Enumerable.Range(0, 200).Select(i => reader.Lookup(["toolu_" + i])).Should().Equal(Enumerable.Range(0, 200).Select(i => "model-" + i));
        new SqliteCooldownStore(Open()).LoadCooldowns().Should().HaveCount(200);
        _logs.All.Should().NotContain("failed");
    }

    [Fact]
    public void Feedback_survives_a_restart()
    {
        var log = new RoutingFeedbackLog(store: new SqliteFeedbackStore(Open()));
        DateTimeOffset now = _time.GetUtcNow();
        log.Add(new RoutingFeedback { Timestamp = now, EntryId = 7, Kind = FeedbackKind.Retried, Route = "smalltalk", Model = "local", Tier = 1, NeededTier = 2 }).Should().BeTrue();
        log.Add(new RoutingFeedback { Timestamp = now, EntryId = 7, Kind = FeedbackKind.Retried }).Should().BeFalse("one per decision and kind");
        log.Add(new RoutingFeedback { Timestamp = now, EntryId = 8, Kind = FeedbackKind.Corrected, Tier = 2, NeededTier = 3 });

        var reopened = new RoutingFeedbackLog(store: new SqliteFeedbackStore(Open()));

        reopened.Recent().Select(f => (f.Id, f.EntryId, f.Kind)).Should().Equal((2L, 8L, FeedbackKind.Corrected), (1L, 7L, FeedbackKind.Retried));
        reopened.Recent()[1].Should().Match<RoutingFeedback>(f => f.Route == "smalltalk" && f.NeededTier == 2);
        reopened.Between(now.AddMinutes(-1), now.AddMinutes(1)).Should().HaveCount(2);
        reopened.Add(new RoutingFeedback { Timestamp = now, EntryId = 8, Kind = FeedbackKind.Corrected }).Should().BeFalse("the reopened log knows what was recorded");
        reopened.Add(new RoutingFeedback { Timestamp = now, EntryId = 9, Kind = FeedbackKind.Overridden }).Should().BeTrue();
        reopened.Recent()[0].Id.Should().Be(3, "ids continue after a restart");
    }

    [Fact]
    public void A_corrupt_database_falls_back_to_memory_with_a_warning()
    {
        File.WriteAllBytes(DbPath, Encoding.UTF8.GetBytes(new string('x', 4096)));

        SqliteRouterDatabase database = Open();
        var tracker = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(database));
        var history = new SqliteConfigurationHistory(database);
        tracker.ReportFailure(Model("claude", 4), ModelFailureKind.QuotaExceeded);
        long id = history.Record(new RouterConfiguration { FallbackTier = 2 }, "startup");

        database.Available.Should().BeFalse();
        _logs.All.Should().Contain("cannot be used").And.Contain("stay in memory");
        tracker.IsAvailable(Model("claude", 4)).Should().BeFalse("cooldowns still work in memory");
        history.GetVersion(id)!.FallbackTier.Should().Be(2, "history falls back to memory");
        new SqliteToolCallAffinity(database).Lookup(["none"]).Should().BeNull();

        // Nothing is left holding the broken file, so it can be replaced (Windows refuses to delete an open file).
        SqliteConnection.ClearAllPools();
        if (OperatingSystem.IsLinux())
        {
            Directory.GetFiles("/proc/self/fd")
                .Select(fd => new FileInfo(fd).LinkTarget)
                .Should().NotContain(DbPath, "no connection to the corrupt file may stay open");
        }

        File.Delete(DbPath);
    }

    [Fact]
    public void A_locked_database_degrades_to_memory_without_throwing()
    {
        SqliteRouterDatabase database = Open(o => o.BusyTimeout = TimeSpan.FromSeconds(1));
        var tracker = new ModelHealthTracker(Selection, _time, new SqliteCooldownStore(database));
        using var holder = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        holder.Open();
        using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        tracker.ReportFailure(Model("claude", 4), ModelFailureKind.QuotaExceeded);

        tracker.IsAvailable(Model("claude", 4)).Should().BeFalse();
        _logs.All.Should().Contain("saving a cooldown failed").And.Contain("continuing in memory");
    }
}

/// <summary>Durable state through the running router: activity after a restart, history and rollback over the admin API.</summary>
public sealed class DurableStateHostTests : RouterHostTest
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-durable").FullName;

    private Task<HttpClient> StartDurableAsync() =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")));
                new SemanticRouterBuilder(s).UseSqliteState(Path.Combine(_directory, "state.db"));
            });

    private static StringContent JsonBody(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Activity_from_before_a_restart_is_still_on_the_dashboard()
    {
        HttpClient client = await StartDurableAsync();
        (await client.PostAsync("/v1/chat/completions", Json(ChatJson))).StatusCode.Should().Be(HttpStatusCode.OK);
        await DisposeAsync();

        HttpClient restarted = await StartDurableAsync();
        JsonNode activity = JsonNode.Parse(await restarted.GetStringAsync("/router/api/activity"))!;

        JsonNode entry = activity["entries"]!.AsArray().Single()!;
        entry["promptPreview"]!.GetValue<string>().Should().Be("hello there");
        entry["outcome"]!.GetValue<string>().Should().Be("served");
        entry["servedBy"]!.GetValue<string>().Should().Be("local");
        await DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Rolling_back_restores_the_exact_previous_document_and_records_the_rollback()
    {
        string file = Path.Combine(_directory, "semantic-router.json");
        HttpClient client = await StartDurableAsync();
        JsonNode config = JsonNode.Parse(await client.GetStringAsync("/router/api/config"))!["config"]!;
        config["FallbackTier"] = 2;
        (await client.PutAsync("/router/api/config?note=first", JsonBody(config))).StatusCode.Should().Be(HttpStatusCode.OK);
        string firstFile = await File.ReadAllTextAsync(file);
        config["FallbackTier"] = 4;
        config["Selection"]!["Strategy"] = "MostCapable";
        (await client.PutAsync("/router/api/config", JsonBody(config))).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonNode history = JsonNode.Parse(await client.GetStringAsync("/router/api/config/history"))!;
        JsonNode[] versions = [.. history["versions"]!.AsArray().Select(v => v!)];
        versions.Select(v => v["source"]!.GetValue<string>()).Should().Equal("admin API", "admin API", "startup");
        versions[1]["note"]!.GetValue<string>().Should().Be("first");
        versions[1]["actor"]!.GetValue<string>().Should().Be("this machine");
        long firstId = versions[1]["id"]!.GetValue<long>();
        JsonNode old = JsonNode.Parse(await client.GetStringAsync($"/router/api/config/history/{firstId}"))!;
        old["config"]!["Connectors"]!["anthropic"]!["ApiKey"]!.GetValue<string>().Should().Be("***", "history documents are masked like the live one");

        HttpResponseMessage rollback = await client.PostAsync($"/router/api/config/history/{firstId}/rollback", null);

        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await File.ReadAllTextAsync(file)).Should().Be(firstFile);
        JsonNode latest = JsonNode.Parse(await client.GetStringAsync("/router/api/config/history?limit=1"))!["versions"]![0]!;
        latest["source"]!.GetValue<string>().Should().Be("rollback");
        latest["note"]!.GetValue<string>().Should().Be($"to version {firstId}");
        (await client.PostAsync("/router/api/config/history/999/rollback", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
