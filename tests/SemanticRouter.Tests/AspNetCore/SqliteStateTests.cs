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
        ledger.Record(new UsageRecord(_time.GetUtcNow(), "new", "c", null, null, false, 20, 10, 0, 0, 0.2m, 0.2m, "work", SubscriptionValueUsd: 0.5m));

        DateTimeOffset since = _time.GetUtcNow().AddHours(-1), until = _time.GetUtcNow().AddHours(1);
        ledger.Total(since, until).Requests.Should().Be(2, "the old row survives the migration");
        ledger.Total(since, until).SubscriptionValueUsd.Should().Be(0.5m, "the old row has no subscription value");
        ledger.Total(since, until, profile: "work").InputTokens.Should().Be(20);
        ledger.Summarize(since, until, UsageGrouping.Profile).Select(g => g.Key).Should().BeEquivalentTo("work", "(no profile)");
        _logs.Messages.Should().NotContain(m => m.Contains("cannot be used", StringComparison.Ordinal));
    }

    [Fact]
    public void A_migration_keeps_a_copy_of_the_file_the_older_release_can_open()
    {
        CreateVersion1File();

        SqliteRouterDatabase database = Open();

        database.Available.Should().BeTrue();
        database.Problem.Should().BeNull();
        string backup = SqliteRouterDatabase.BackupFile(DbPath, 1);
        File.Exists(backup).Should().BeTrue();
        SqliteRouterDatabase.StoredSchemaVersion(backup).Should().Be(1, "the copy is the file as it was before the migration");
        SqliteRouterDatabase.StoredSchemaVersion(DbPath).Should().Be(SqliteRouterDatabase.SchemaVersion);
        using (var connection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM usage;";
            Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('usage') WHERE name = 'profile';";
            Convert.ToInt64(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0, "the copy keeps the old schema");
        }

        File.Exists(backup + ".tmp").Should().BeFalse();
        _logs.All.Should().Contain("migrated from schema 1 to 3").And.Contain(backup);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(backup).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "the copy holds prompt previews too");
        }
    }

    [Fact]
    public void A_new_or_current_database_is_not_copied()
    {
        Open().Available.Should().BeTrue();
        SqliteConnection.ClearAllPools();
        Open().Available.Should().BeTrue();

        Directory.GetFiles(_directory, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public void A_database_from_a_newer_release_is_left_as_it_is_and_reported()
    {
        CreateVersion1File(version: SqliteRouterDatabase.SchemaVersion + 1);
        byte[] before = File.ReadAllBytes(DbPath);

        SqliteRouterDatabase.NewerSchemaProblem(DbPath).Should()
            .Contain("written by a newer SemanticRouter (state schema 4; this version reads schema 3 and older)")
            .And.Contain("No copy from before the upgrade was found");
        SqliteRouterDatabase database = Open();

        database.Available.Should().BeFalse();
        database.Problem.Should().Contain("state schema 4").And.Contain("budgets count from zero").And.Contain("audit log");
        SqliteConnection.ClearAllPools();
        File.ReadAllBytes(DbPath).Should().Equal(before, "nothing is written to a file this release does not understand");
        Directory.GetFiles(_directory).Should().ContainSingle("no copy is made and no WAL is started");

        File.WriteAllBytes(SqliteRouterDatabase.BackupFile(DbPath, SqliteRouterDatabase.SchemaVersion), before);
        SqliteRouterDatabase.NewerSchemaProblem(DbPath).Should().Contain($"move {SqliteRouterDatabase.BackupFile(DbPath, 3)} (the copy made before the upgrade) to {DbPath}");
    }

    [Fact]
    public void A_database_that_cannot_be_used_says_so_and_what_it_means()
    {
        File.WriteAllBytes(DbPath, Encoding.UTF8.GetBytes(new string('x', 4096)));

        SqliteRouterDatabase database = Open();

        database.Problem.Should().StartWith($"The router's state database {DbPath} cannot be used (").And.Contain("budgets count from zero");
        SqliteRouterDatabase.NewerSchemaProblem(DbPath).Should().BeNull("a broken file is not a newer one");
        SqliteRouterDatabase.NewerSchemaProblem(Path.Combine(_directory, "none.db")).Should().BeNull();
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
    }

    private void CreateVersion1File(int version = 1)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta (key, value) VALUES ('schema_version', '{version}');
            CREATE TABLE usage (
                timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
                is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL);
            INSERT INTO usage VALUES (1, 'old', 'c', NULL, NULL, 0, 10, 5, 0, 0, '0.1', '0.1');
            """;
        create.ExecuteNonQuery();
    }

    [Fact]
    public void Configuration_history_written_with_keys_in_plain_text_is_masked_once()
    {
        var history = new SqliteConfigurationHistory(Open());
        var document = new RouterConfiguration();
        document.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com"), ApiKey = "sk-inherited-from-env" };
        history.Record(document.Masked(), "startup");
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            // What a router from before masking left behind.
            connection.Open();
            using SqliteCommand legacy = connection.CreateCommand();
            legacy.CommandText = "INSERT INTO config_history (timestamp_ticks, source, document) VALUES (0, 'startup', $doc); DELETE FROM meta WHERE key = 'history_masked';";
            legacy.Parameters.AddWithValue("$doc", document.ToJson());
            legacy.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new SqliteConfigurationHistory(Open());

        reopened.List().Should().HaveCount(2);
        reopened.List().Select(v => reopened.GetVersion(v.Id)!.Connectors["openai"].ApiKey).Should().AllBe("***");
        SqliteConnection.ClearAllPools();
        File.ReadAllText(DbPath).Should().NotContain("sk-inherited-from-env", "the old pages are rewritten");
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
    public void Warm_prompt_caches_survive_a_restart_until_they_expire()
    {
        var opus = new WarmCache("opus", 80_000, _time.GetUtcNow().AddMinutes(5));
        var before = new SqliteWarmCaches(Open());
        before.Remember("conversation", opus);
        before.Remember("conversation", new WarmCache("sonnet", 20_000, _time.GetUtcNow().AddMinutes(5)));
        before.Forget("conversation", "SONNET");

        new SqliteWarmCaches(Open()).Lookup("conversation").Should().Equal(opus);
        new SqliteWarmCaches(Open()).Lookup("another").Should().BeEmpty();
        _time.Advance(TimeSpan.FromMinutes(6));
        new SqliteWarmCaches(Open()).Lookup("conversation").Should().BeEmpty("expired");
    }

    [Fact]
    public void The_activity_log_is_seeded_from_disk_and_continues_its_ids()
    {
        var store = new SqliteActivityStore(Open());
        var log = new RoutingActivityLog(store: store);
        RoutingActivityEntry first = log.Add(new RoutingActivityEntry
        {
            Id = log.Reserve(),
            Timestamp = _time.GetUtcNow(),
            PromptPreview = "hello there",
            Tier = 2,
            Reason = RoutingReason.SemanticMatch,
            Route = "smalltalk",
            Protocol = ChatProtocol.Anthropic,
            Client = "laptop",
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
    public async Task A_purge_empties_the_log_once_a_reader_that_held_it_has_finished()
    {
        var store = new SqliteActivityStore(Open());
        store.Append(new RoutingActivityEntry { Id = 1, Timestamp = _time.GetUtcNow(), PromptPreview = "purge-me 3f9d: the board minutes" });

        // Another reader of the file (the dashboard, a second router) in the middle of a read: its snapshot pins the
        // write-ahead log, which still holds the page with the preview, so the log cannot be emptied until it is done.
        using var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        reader.Open();
        using SqliteTransaction read = reader.BeginTransaction(deferred: true);
        using (SqliteCommand count = reader.CreateCommand())
        {
            count.Transaction = read;
            count.CommandText = "SELECT count(*) FROM activity;";
            ((long)count.ExecuteScalar()!).Should().Be(1);
        }

        Task<int> purge = Task.Run(() => store.Purge(_time.GetUtcNow().AddHours(-1), _time.GetUtcNow().AddHours(1)));
        await Task.Delay(300);
        read.Commit();

        (await purge).Should().Be(1);
        string files = string.Concat(Directory.GetFiles(_directory, "state.db*").Select(DurableStateHostTests.ReadWhileOpen));
        files.Should().NotContain("purge-me 3f9d", "the purge waits for the reader, then empties the log");
    }

    [Fact]
    public void A_purge_says_so_when_a_reader_keeps_the_log_past_the_busy_timeout()
    {
        var store = new SqliteActivityStore(Open(o => o.BusyTimeout = TimeSpan.FromMilliseconds(200)));
        store.Append(new RoutingActivityEntry { Id = 1, Timestamp = _time.GetUtcNow(), PromptPreview = "kept by a reader" });
        using var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        reader.Open();
        using SqliteTransaction read = reader.BeginTransaction(deferred: true);
        using (SqliteCommand count = reader.CreateCommand())
        {
            count.Transaction = read;
            count.CommandText = "SELECT count(*) FROM activity;";
            count.ExecuteScalar();
        }

        store.Purge(_time.GetUtcNow().AddHours(-1), _time.GetUtcNow().AddHours(1)).Should().Be(1, "the rows are gone even when the log is not emptied yet");
        _logs.Messages.Should().Contain(m => m.Contains("could not be emptied", StringComparison.Ordinal));
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
    public void Connector_incidents_survive_a_restart()
    {
        var first = new ConnectorIncidents(_time, new SqliteStateValues(Open()));
        first.Failed("lmstudio", ModelFailureKind.Unavailable, "connection refused");
        DateTimeOffset since = _time.GetUtcNow();
        first.Failed("claude", ModelFailureKind.QuotaExceeded, "limit reached");
        _time.Advance(TimeSpan.FromDays(1));
        first.Failed("claude", ModelFailureKind.QuotaExceeded, "limit reached again");

        var restarted = new ConnectorIncidents(_time, new SqliteStateValues(Open()));

        restarted.Failing().Should().Equal(("lmstudio", since, "connection refused"));
        restarted.LimitDays().Should().Equal(("claude", 2, "limit reached again"));

        restarted.Served("lmstudio");
        new ConnectorIncidents(_time, new SqliteStateValues(Open())).Failing().Should().BeEmpty("serving again clears it, on disk too");
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
    public void Learned_changes_and_the_tuning_state_survive_a_restart()
    {
        var store = new SqliteTuningStore(Open());
        DateTimeOffset now = _time.GetUtcNow();
        store.Save(new TuningChange { Id = 1, Timestamp = now, Kind = TuningChangeKind.DefaultThreshold, OldValue = 0.8f, NewValue = 0.78f, Why = "because", State = TuningChangeState.Applied, Version = 4 });
        store.Save(new TuningChange { Id = 2, Timestamp = now, Kind = TuningChangeKind.Utterance, Route = "code", Utterance = "fix the build", State = TuningChangeState.Suggested });
        store.Save(new TuningChange { Id = 1, Timestamp = now, Kind = TuningChangeKind.DefaultThreshold, OldValue = 0.8f, NewValue = 0.78f, Why = "because", State = TuningChangeState.Undone, UndoneAt = now });
        store.SaveState(new TuningState { Paused = true, LastRun = now, LastLabelCount = 12, LastResult = "1 change applied" });

        var reopened = new SqliteTuningStore(Open());

        reopened.Changes().Select(c => (c.Id, c.Kind, c.State)).Should().Equal(
            (1L, TuningChangeKind.DefaultThreshold, TuningChangeState.Undone), (2L, TuningChangeKind.Utterance, TuningChangeState.Suggested));
        reopened.Changes()[0].Should().Match<TuningChange>(c => c.NewValue == 0.78f && c.UndoneAt == now && c.Why == "because");
        reopened.LoadState().Should().Be(new TuningState { Paused = true, LastRun = now, LastLabelCount = 12, LastResult = "1 change applied" });
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

    private Task<HttpClient> StartDurableAsync(
        Action<StateOptions>? state = null,
        Action<PromptRouterMiddlewareOptions>? middleware = null,
        Action<SemanticRouterOptions>? routing = null,
        bool withCli = false,
        Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                middleware?.Invoke(o);
            },
            routing: routing,
            withCli: withCli,
            services: s =>
            {
                s.AddSingleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")));
                new SemanticRouterBuilder(s).UseSqliteState(Path.Combine(_directory, "state.db"));
                if (state is not null)
                {
                    s.Configure(state);
                }

                services?.Invoke(s);
            });

    private static StringContent JsonBody(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static StringContent Chat(string prompt) =>
        JsonBody(new JsonObject { ["model"] = "auto", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }) });

    /// <summary>Everything the state database's files hold, as text, read while the router still has them open.</summary>
    private string DatabaseFiles()
    {
        var text = new StringBuilder();
        foreach (string file in Directory.GetFiles(_directory, "state.db*"))
        {
            text.Append(ReadWhileOpen(file));
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a file the router may be writing. On macOS the shared lock .NET takes when opening a file conflicts with
    /// the lock SQLite holds while it writes, so a read that lands during a write is tried again.
    /// </summary>
    internal static string ReadWhileOpen(string file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 50 && File.Exists(file))
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Waits until <paramref name="count"/> entries are in the store: an entry is stored once its request finishes, which can be just after the client has the reply.</summary>
    private async Task WaitForStoredActivityAsync(int count)
    {
        IRoutingActivityStore store = Services.GetRequiredService<IRoutingActivityStore>();
        for (int i = 0; i < 200 && store.Recent(count).Count < count; i++)
        {
            await Task.Delay(10);
        }

        store.Recent(count).Should().HaveCount(count);
    }

    private static async Task<string> LastPreviewAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("/router/api/activity"))!["entries"]!.AsArray()[0]!["promptPreview"]!.GetValue<string>();

    [Fact]
    public async Task Prompt_previews_mask_credentials_by_default_and_can_be_turned_off()
    {
        const string key = "sk-ant-api03-q1w2e3r4t5y6u7i8o9p0a1s2d3f4";
        HttpClient client = await StartDurableAsync();
        await client.PostAsync("/v1/chat/completions", Chat($"why does {key} get a 401?"));
        await WaitForStoredActivityAsync(1);

        (await LastPreviewAsync(client)).Should().Be("why does *** get a 401?");
        DatabaseFiles().Should().NotContain(key);
        await DisposeAsync();

        // Off: no prompt text, and none of what models wrote about it either: a failed CLI run's reply and errors, and
        // the judge's reason for its grade.
        LocalHttp = new StubHttpMessageHandler((_, body) => OpenAIReply(
            body.Contains("\"model\":\"judge-32b\"", StringComparison.Ordinal)
                ? """{"grade": 2, "reason": "the nightingale figures are wrong"}"""
                : "Revenue rose."));
        Cli = FakeCliProcessRunner.Failing(1, stdout: ClaudeStream.Error("I cannot share the nightingale numbers"), stderr: "error: nightingale run aborted");
        HttpClient off = await StartDurableAsync(
            o => o.StorePrompts = PromptStorage.Off,
            middleware: o => (o.Judge.Enabled, o.Judge.Model, o.Judge.SampleRate) = (true, "judge", 1),
            routing: o =>
            {
                ModelDefinition judge = Model("judge", 3, local: true, connector: "lmstudio");
                judge.Model = "judge-32b";
                o.Models.Add(judge);
            },
            withCli: true,
            // The 32B judge is not loaded, so the small model answers and the judge loads only to grade.
            services: s => s.AddSingleton<IModelResidency>(new NotLoaded("judge-32b")));
        var events = new StringBuilder();
        using var listening = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using HttpResponseMessage stream = await off.GetAsync("/router/api/events", HttpCompletionOption.ResponseHeadersRead, listening.Token);
        Task listener = ListenAsync(stream, events, listening.Token);

        await off.PostAsync("/v1/chat/completions", Chat("the quarterly numbers for project nightingale"));
        await Services.GetRequiredService<AnswerJudge>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        HttpResponseMessage failed = await off.PostAsync("/v1/chat/completions", JsonBody(new JsonObject
        {
            ["model"] = "claude-cli",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "the nightingale numbers again" }),
        }));
        await WaitForStoredActivityAsync(3);

        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        IReadOnlyList<RoutingActivityEntry> entries = Services.GetRequiredService<IRoutingActivityStore>().Recent(2);
        entries.Should().Contain(e => e.Grade == 2 && e.GradeNote == "judge", "the grade is kept, the reason is not");
        entries.Should().Contain(e => e.Detail != null && e.Detail.Contains("claude-cli: exit code 1: output omitted (StorePrompts: Off)", StringComparison.Ordinal));
        (await LastPreviewAsync(off)).Should().BeEmpty();
        string export = await off.GetStringAsync("/router/api/activity/export?format=jsonl&prompts=true");
        export.Should().Contain("exit code 1").And.NotContain("nightingale");
        DatabaseFiles().Should().NotContain("nightingale");
        Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().ContainSingle().Which.Detail.Should().Be("graded 2/5 by judge");
        string Events()
        {
            lock (events)
            {
                return events.ToString();
            }
        }

        for (int i = 0; i < 200 && !Events().Contains("exit code 1", StringComparison.Ordinal); i++)
        {
            await Task.Delay(10);
        }

        await listening.CancelAsync();
        await listener;
        Events().Should().Contain("\"grade\":2").And.Contain("exit code 1").And.NotContain("nightingale");
    }

    private sealed class NotLoaded(string model) : IModelResidency
    {
        public bool? IsLoaded(ModelDefinition candidate) => candidate.IsLocal ? candidate.Model != model : null;
    }

    private static HttpResponseMessage OpenAIReply(string content) => new(HttpStatusCode.OK)
    {
        Content = JsonBody(new JsonObject
        {
            ["id"] = "x",
            ["object"] = "chat.completion",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        }),
    };

    /// <summary>Appends what the event stream sends to <paramref name="events"/> until <paramref name="cancellationToken"/> stops it.</summary>
    private static async Task ListenAsync(HttpResponseMessage stream, StringBuilder events, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(cancellationToken));
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (events)
                {
                    events.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped listening.
        }
    }

    [Fact]
    public async Task Retention_comes_from_the_configuration_document()
    {
        await StartDurableAsync(o =>
        {
            o.Retention.ActivityDays = 7;
            o.Retention.ConfigurationVersions = 3;
        });
        _ = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        SqliteRouterDatabase database = Services.GetRequiredService<SqliteRouterDatabase>();

        database.ActivityMaxAge.Should().Be(TimeSpan.FromDays(7));
        database.HistoryMaxEntries.Should().Be(3);
        database.UsageRetention.Should().Be(TimeSpan.FromDays(400), "unset values keep the store's default");

        RouterConfiguration document = RouterConfiguration.FromJson("""{ "State": { "StorePrompts": "Off", "Retention": { "UsageDays": 90, "ActivityEntries": 0 } } }""");
        document.State.StorePrompts.Should().Be(PromptStorage.Off);
        document.State.Retention.UsageDays.Should().Be(90);
        document.GetDocumentValidationErrors().Should().Contain(e => e.StartsWith("State:Retention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purged_activity_cannot_be_recovered_from_the_database_files()
    {
        HttpClient client = await StartDurableAsync(o => o.StorePrompts = PromptStorage.On);
        await client.PostAsync("/v1/chat/completions", Chat("purge-me 7a1c: the draft of the merger memo"));
        await client.PostAsync("/v1/chat/completions", Chat("purge-me 7a1c: and the second draft"));
        await WaitForStoredActivityAsync(2);
        DatabaseFiles().Should().Contain("purge-me 7a1c", "the previews were stored");

        HttpResponseMessage purge = await client.DeleteAsync("/router/api/activity");

        purge.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await purge.Content.ReadAsStringAsync())!["removed"]!.GetValue<int>().Should().Be(2);
        JsonNode.Parse(await client.GetStringAsync("/router/api/activity"))!["entries"]!.AsArray().Should().BeEmpty();
        DatabaseFiles().Should().NotContain("purge-me 7a1c");
    }

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
