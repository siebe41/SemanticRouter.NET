using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Storage.Sqlite;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Durable state in SQLite: cooldowns, tool-call affinity, activity and configuration history.</summary>
public sealed class SqliteStateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-state").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<SqliteRouterDatabase> _opened = [];

    public void Dispose()
    {
        foreach (SqliteRouterDatabase database in _opened)
        {
            database.Dispose();
        }

        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private string DbPath => Path.Combine(_directory, "state.db");

    /// <summary>
    /// Another router on the file, or this one after a restart. What the earlier ones wrote is saved first: each saves
    /// in the background, and a restart (stopping saves what waits) or another router a moment later would find it.
    /// </summary>
    private SqliteRouterDatabase Open(Action<SqliteStateOptions>? configure = null)
    {
        Settle();
        var options = new SqliteStateOptions { Path = DbPath };
        configure?.Invoke(options);
        using var factory = LoggerFactory.Create(b => b.AddProvider(_logs));
        var database = new SqliteRouterDatabase(Microsoft.Extensions.Options.Options.Create(options), factory.CreateLogger<SqliteRouterDatabase>(), _time);
        _opened.Add(database);
        return database;
    }

    /// <summary>Waits until every database opened here has saved what it was given.</summary>
    private void Settle()
    {
        foreach (SqliteRouterDatabase database in _opened)
        {
            database.WaitForWrites();
        }
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
        ledger.Record(new UsageRecord
        {
            Timestamp = _time.GetUtcNow(),
            Model = "new",
            Connector = "c",
            IsLocal = false,
            InputTokens = 20,
            OutputTokens = 10,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.2m,
            ReferenceCostUsd = 0.2m,
            Profile = "work",
            SubscriptionValueUsd = 0.5m,
        });

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
        _logs.All.Should().Contain($"migrated from schema 1 to {SqliteRouterDatabase.SchemaVersion}").And.Contain(backup);
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
            .Contain($"written by a newer SemanticRouter (state schema {SqliteRouterDatabase.SchemaVersion + 1}; this version reads schema {SqliteRouterDatabase.SchemaVersion} and older)")
            .And.Contain("No copy from before the upgrade was found");
        SqliteRouterDatabase database = Open();

        database.Available.Should().BeFalse();
        database.Problem.Should().Contain($"state schema {SqliteRouterDatabase.SchemaVersion + 1}").And.Contain("budgets count from zero").And.Contain("audit log");
        SqliteConnection.ClearAllPools();
        File.ReadAllBytes(DbPath).Should().Equal(before, "nothing is written to a file this release does not understand");
        Directory.GetFiles(_directory).Should().ContainSingle("no copy is made and no WAL is started");

        File.WriteAllBytes(SqliteRouterDatabase.BackupFile(DbPath, SqliteRouterDatabase.SchemaVersion), before);
        SqliteRouterDatabase.NewerSchemaProblem(DbPath).Should().Contain($"move {SqliteRouterDatabase.BackupFile(DbPath, SqliteRouterDatabase.SchemaVersion)} (the copy made before the upgrade) to {DbPath}");
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

        SqliteFiles.Release(_directory);
        var reopened = new SqliteConfigurationHistory(Open());

        reopened.List().Should().HaveCount(2);
        reopened.List().Select(v => reopened.GetVersion(v.Id)!.Connectors["openai"].ApiKey).Should().AllBe("***");
        SqliteFiles.Release(_directory);
        File.ReadAllText(DbPath).Should().NotContain("sk-inherited-from-env", "the old pages are rewritten");
    }

    [Fact]
    public void A_restart_during_a_quota_cooldown_keeps_the_original_expiry()
    {
        ModelDefinition claude = Model("claude", 4);
        var before = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(Open()) });
        before.ReportFailure(claude, ModelFailureKind.QuotaExceeded);
        DateTimeOffset expiry = before.GetCooldowns()["claude"];

        _time.Advance(TimeSpan.FromMinutes(5));
        var after = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(Open()) });

        after.IsAvailable(claude).Should().BeFalse("the cooldown was stored with its absolute expiry");
        after.GetCooldowns()["claude"].Should().Be(expiry);
        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        after.IsAvailable(claude).Should().BeTrue();
    }

    [Fact]
    public void Instances_sharing_the_file_see_each_others_cooldowns_after_a_refresh()
    {
        ModelDefinition claude = Model("claude", 4);
        var a = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(Open()) });
        var b = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(Open()) });

        a.ReportFailure(claude, ModelFailureKind.QuotaExceeded);
        Settle();
        b.IsAvailable(claude).Should().BeTrue("b has not refreshed yet");
        _time.Advance(TimeSpan.FromSeconds(6));
        b.IsAvailable(claude).Should().BeFalse();

        a.ReportSuccess(claude);
        Settle();
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
        var opus = new WarmCache { Model = "opus", CachedTokens = 80_000, ExpiresAt = _time.GetUtcNow().AddMinutes(5) };
        var before = new SqliteWarmCaches(Open());
        before.Remember("conversation", opus);
        before.Remember("conversation", new WarmCache { Model = "sonnet", CachedTokens = 20_000, ExpiresAt = _time.GetUtcNow().AddMinutes(5) });
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
        next.Id.Should().BeGreaterThan(first.Id, "the restarted log takes a new block of ids, past the one the first run reserved");
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
    public void Activity_past_the_count_cap_on_a_running_router_leaves_the_files_too()
    {
        // Under the cap the running router prunes every 100th entry, without a restart or the hourly prune.
        var store = new SqliteActivityStore(Open(o => o.ActivityMaxEntries = 5));
        for (int i = 1; i <= 100; i++)
        {
            store.Append(new RoutingActivityEntry { Id = i, Timestamp = _time.GetUtcNow(), PromptPreview = i <= 5 ? "capped-out 1d3e " + i : "kept " + i });
        }

        Settle();

        store.Recent(1000).Should().HaveCount(5);
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().NotContain("capped-out 1d3e");
    }

    [Fact]
    public async Task A_purge_empties_the_log_once_a_reader_that_held_it_has_finished()
    {
        var store = new SqliteActivityStore(Open());
        store.Append(new RoutingActivityEntry { Id = 1, Timestamp = _time.GetUtcNow(), PromptPreview = "purge-me 3f9d: the board minutes" });
        Settle();

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

        Task<ActivityPurgeResult> purge = Task.Run(() => store.PurgeWithResult(_time.GetUtcNow().AddHours(-1), _time.GetUtcNow().AddHours(1)));
        await Task.Delay(300);
        read.Commit();

        (await purge).Should().Be(new ActivityPurgeResult { Removed = 1, Complete = true });
        string files = DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db");
        files.Should().NotContain("purge-me 3f9d", "the purge waits for the reader, then empties the log");
    }

    [Fact]
    public void A_purge_says_so_when_a_reader_keeps_the_log_past_the_busy_timeout_and_empties_it_after_the_next_operation()
    {
        var store = new SqliteActivityStore(Open(o => o.BusyTimeout = TimeSpan.FromMilliseconds(200)));
        store.Append(new RoutingActivityEntry { Id = 1, Timestamp = _time.GetUtcNow(), PromptPreview = "kept by a reader 81c2" });
        using (var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            reader.Open();
            using SqliteTransaction read = reader.BeginTransaction(deferred: true);
            using (SqliteCommand count = reader.CreateCommand())
            {
                count.Transaction = read;
                count.CommandText = "SELECT count(*) FROM activity;";
                count.ExecuteScalar();
            }

            store.PurgeWithResult(_time.GetUtcNow().AddHours(-1), _time.GetUtcNow().AddHours(1))
                .Should().Be(new ActivityPurgeResult { Removed = 1, Complete = false }, "the rows are gone, but the log still holds them");
            _logs.Messages.Should().Contain(m => m.Contains("could not be emptied", StringComparison.Ordinal));
            read.Commit();
        }

        store.Recent(1).Should().BeEmpty();
        string files = DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db");
        files.Should().NotContain("kept by a reader 81c2", "the checkpoint owed is made after the next operation");
    }

    [Fact]
    public void Retention_is_enforced_without_traffic_at_start_up_and_when_it_changes()
    {
        SqliteRouterDatabase database = Open(o => o.ActivityMaxAge = TimeSpan.FromDays(1));
        var activity = new SqliteActivityStore(database);
        var feedback = new SqliteFeedbackStore(database);
        var usage = new SqliteUsageLedger(database);
        var audit = new SqliteAuditLog(database);
        for (int i = 1; i <= 5; i++)
        {
            activity.Append(new RoutingActivityEntry { Id = i, Timestamp = _time.GetUtcNow(), PromptPreview = "aged-out 5e7a " + i });
            feedback.Add(new RoutingFeedback { Id = i, Timestamp = _time.GetUtcNow(), EntryId = i, Kind = FeedbackKind.Judged, Detail = "graded 1/5 by judge: aged-out 5e7a" });
        }

        usage.Record(new UsageRecord
        {
            Timestamp = _time.GetUtcNow(),
            Model = "aged-out-usage-5e7a",
            Connector = "c",
            IsLocal = false,
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0m,
            ReferenceCostUsd = 0m,
        });
        audit.Append(new AuditEntry
        {
            Id = 0,
            Timestamp = _time.GetUtcNow(),
            Actor = "aged-out-auditor-5e7a",
            Method = "PUT",
            Path = "/router/api/config",
            Status = 200,
            Summary = "changed",
        });

        // Past retention but not pruned yet: already left out of every read, the export's included.
        _time.Advance(TimeSpan.FromDays(40));
        activity.Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 1000).Should().BeEmpty();
        activity.Recent(1000).Should().BeEmpty();
        feedback.Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 1000).Should().BeEmpty();
        feedback.Recent(1000).Should().BeEmpty();
        Files().Should().Contain("aged-out 5e7a");

        // Fewer than 100 writes since: a restart deletes it all the same.
        database.Dispose();
        SqliteRouterDatabase restarted = Open(o =>
        {
            o.ActivityMaxAge = TimeSpan.FromDays(1);
            o.UsageRetention = TimeSpan.FromDays(30);
            o.AuditRetention = TimeSpan.FromDays(30);
        });
        new SqliteActivityStore(restarted).Append(new RoutingActivityEntry { Id = 6, Timestamp = _time.GetUtcNow(), PromptPreview = "fresh" });

        string files = Files();
        files.Should().NotContain("aged-out 5e7a").And.NotContain("aged-out-usage-5e7a").And.NotContain("aged-out-auditor-5e7a");

        // A shorter retention applied to a running router takes effect at once.
        _time.Advance(TimeSpan.FromDays(3));
        restarted.Apply(new StateRetentionOptions { ActivityDays = 2 });
        Files().Should().NotContain("fresh");
        restarted.Dispose();

        string Files() => DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db");
    }

    [Fact]
    public void Feedback_about_purged_entries_is_deleted_from_the_store()
    {
        var feedback = new SqliteFeedbackStore(Open());
        feedback.Add(new RoutingFeedback { Id = 1, Timestamp = _time.GetUtcNow(), EntryId = 10, Kind = FeedbackKind.Judged });
        feedback.Add(new RoutingFeedback { Id = 2, Timestamp = _time.GetUtcNow(), EntryId = 11, Kind = FeedbackKind.Retried });
        feedback.Add(new RoutingFeedback { Id = 3, Timestamp = _time.GetUtcNow(), EntryId = 12, Kind = FeedbackKind.Retried });

        feedback.Remove([10, 12]);

        feedback.Recent(10).Select(f => f.EntryId).Should().Equal(11);
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
    public void A_configuration_version_past_the_cap_leaves_the_files_too()
    {
        var history = new SqliteConfigurationHistory(Open(o => o.HistoryMaxEntries = 2));
        history.Record(new RouterConfiguration { FallbackTier = 1 }, "admin API", "capped-actor 0b5e", "capped-note 0b5e");
        history.Record(new RouterConfiguration { FallbackTier = 2 }, "admin API");
        history.Record(new RouterConfiguration { FallbackTier = 3 }, "admin API");
        Settle();

        history.List().Should().HaveCount(2);
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().NotContain("0b5e", "the write-ahead log is emptied after the cap deletes a version");
    }

    [Fact]
    public void A_capped_version_does_not_wait_for_a_reader_and_leaves_the_files_once_it_has_finished()
    {
        // The default busy timeout (5 s): a checkpoint that waited for the reader would take it, or twice it.
        var history = new SqliteConfigurationHistory(Open(o => o.HistoryMaxEntries = 2));
        history.Record(new RouterConfiguration { FallbackTier = 1 }, "admin API", "read-held-actor 7c4a");
        history.Record(new RouterConfiguration { FallbackTier = 2 }, "admin API");
        using (var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            reader.Open();
            using SqliteTransaction read = reader.BeginTransaction(deferred: true);
            using (SqliteCommand count = reader.CreateCommand())
            {
                count.Transaction = read;
                count.CommandText = "SELECT count(*) FROM config_history;";
                count.ExecuteScalar();
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long id = history.Record(new RouterConfiguration { FallbackTier = 3 }, "admin API");
            history.List().Should().HaveCount(2);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5), "the configuration is applied under the runtime's lock");
            history.GetVersion(id).Should().NotBeNull("the version is saved even though the log could not be emptied");
            DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().Contain("7c4a", "the reader must actually pin the deleted version in the log");
            read.Commit();
        }

        history.List().Should().HaveCount(2);
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().NotContain("7c4a", "the checkpoint owed is made after the next operation");
    }

    [Fact]
    public async Task The_hourly_prune_waits_for_a_short_reader_to_release_deleted_audit_data()
    {
        SqliteRouterDatabase database = Open(o => o.AuditRetention = TimeSpan.FromDays(30));
        var audit = new SqliteAuditLog(database);
        audit.Append(new AuditEntry
        {
            Id = 0,
            Timestamp = _time.GetUtcNow(),
            Actor = "short-reader 3a8f",
            Method = "PUT",
            Path = "/router/api/config",
            Status = 200,
            Summary = "changed",
        });
        _time.Advance(TimeSpan.FromDays(40));
        using var reader = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        reader.Open();
        using SqliteTransaction read = reader.BeginTransaction(deferred: true);
        using (SqliteCommand count = reader.CreateCommand())
        {
            count.Transaction = read;
            count.CommandText = "SELECT count(*) FROM audit;";
            count.ExecuteScalar().Should().Be(1L);
        }

        Task<int> prune = Task.Run(database.Prune);
        // Wait until the delete has reached the file while this snapshot still pins it.
        using (var observer = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            observer.Open();
            using SqliteCommand count = observer.CreateCommand();
            count.CommandText = "SELECT count(*) FROM audit;";
            for (int attempt = 0; (long)count.ExecuteScalar()! != 0 && attempt < 100; attempt++)
            {
                await Task.Delay(10);
            }

            ((long)count.ExecuteScalar()!).Should().Be(0);
        }

        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().Contain("3a8f");
        read.Commit();
        (await prune.WaitAsync(TimeSpan.FromSeconds(6))).Should().BeGreaterThan(0);
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().NotContain("3a8f");
    }

    [Fact]
    public void An_audit_entry_past_retention_leaves_the_files_when_the_next_one_is_written()
    {
        SqliteRouterDatabase database = Open(o => o.AuditRetention = TimeSpan.FromDays(30));
        var audit = new SqliteAuditLog(database);
        audit.Append(new AuditEntry
        {
            Id = 0,
            Timestamp = _time.GetUtcNow(),
            Actor = "aged-auditor 9d1f",
            Method = "PUT",
            Path = "/router/api/config",
            Status = 200,
            Summary = "changed",
        });
        database.Run("emptying the log", connection => SqliteRouterDatabase.TruncateLog(connection));
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().Contain("aged-auditor 9d1f", "the entry is in state.db now");

        // Past retention, then another entry: its insert deletes the aged one, so a later prune finds nothing to delete.
        _time.Advance(TimeSpan.FromDays(40));
        audit.Append(new AuditEntry
        {
            Id = 0,
            Timestamp = _time.GetUtcNow(),
            Actor = "fresh-auditor",
            Method = "PUT",
            Path = "/router/api/config",
            Status = 200,
            Summary = "changed",
        });

        audit.Query(null, null, 10).Should().ContainSingle(e => e.Actor == "fresh-auditor");
        DurableStateHostTests.ReadDatabaseFiles(_directory, "state.db").Should().NotContain("aged-auditor 9d1f", "the write-ahead log is emptied after the age cap deletes an entry");
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
        reopened.Recent()[0].Id.Should().BeGreaterThan(reopened.Recent()[1].Id, "ids continue after a restart, never reused");
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
        var tracker = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(database) });
        var history = new SqliteConfigurationHistory(database);
        tracker.ReportFailure(Model("claude", 4), ModelFailureKind.QuotaExceeded);
        long id = history.Record(new RouterConfiguration { FallbackTier = 2 }, "startup");

        database.Available.Should().BeFalse();
        _logs.All.Should().Contain("cannot be used").And.Contain("stay in memory");
        tracker.IsAvailable(Model("claude", 4)).Should().BeFalse("cooldowns still work in memory");
        history.GetVersion(id)!.FallbackTier.Should().Be(2, "history falls back to memory");
        new SqliteToolCallAffinity(database).Lookup(["none"]).Should().BeNull();

        // Nothing is left holding the broken file, so it can be replaced (Windows refuses to delete an open file).
        SqliteFiles.Release(_directory);
        if (OperatingSystem.IsLinux())
        {
            Directory.GetFiles("/proc/self/fd")
                .Select(fd => new FileInfo(fd).LinkTarget)
                .Should().NotContain(DbPath, "no connection to the corrupt file may stay open");
        }

        File.Delete(DbPath);
    }

    [Fact]
    public void Writes_do_not_wait_for_a_file_another_router_holds_and_are_saved_once_it_lets_go()
    {
        SqliteRouterDatabase database = Open(o => o.BusyTimeout = TimeSpan.FromSeconds(10));
        var ledger = new SqliteUsageLedger(database);
        var activity = new SqliteActivityStore(database);
        var holder = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        holder.Open();
        SqliteTransaction held = holder.BeginTransaction(); // another router in the middle of a write

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            ledger.Record(new UsageRecord
            {
                Timestamp = _time.GetUtcNow(),
                Model = "cloud",
                Connector = "c",
                IsLocal = false,
                InputTokens = 10,
                OutputTokens = 10,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = 0.25m,
                ReferenceCostUsd = 0.25m,
            });
            activity.Append(new RoutingActivityEntry { Id = i + 1, Timestamp = _time.GetUtcNow(), PromptPreview = "p" + i });
        }

        timer.Stop();
        held.Commit();
        holder.Dispose();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "requests used to wait up to the busy timeout for each write");
        activity.Recent(1000).Should().HaveCount(100, "reading waits for this router's writes");
        ledger.Total(Since, Until).Requests.Should().Be(100);
        database.Dropped.Should().Be(0);
    }

    [Fact]
    public void A_locked_database_degrades_to_memory_without_throwing()
    {
        SqliteRouterDatabase database = Open(o => o.BusyTimeout = TimeSpan.FromSeconds(1));
        var tracker = new ModelHealthTracker(Selection, new ModelHealthTrackerDependencies { Time = _time, Store = new SqliteCooldownStore(database) });
        using var holder = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        holder.Open();
        using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        tracker.ReportFailure(Model("claude", 4), ModelFailureKind.QuotaExceeded);

        tracker.IsAvailable(Model("claude", 4)).Should().BeFalse();
        database.WaitForWrites();
        _logs.All.Should().Contain("saving a cooldown failed").And.Contain("continuing in memory");
        database.Dropped.Should().Be(1, "the cooldown could not be saved");
        database.Problem.Should().Contain("1 write(s)").And.Contain("lost");
    }

    [Fact]
    public void A_version_3_state_file_gets_row_ids_and_costs_in_picodollars()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta (key, value) VALUES ('schema_version', '3');
                CREATE TABLE usage (
                    timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
                    is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                    cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL, profile TEXT,
                    subscription_value TEXT NOT NULL DEFAULT '0');
                CREATE INDEX usage_timestamp ON usage (timestamp_ticks);
                INSERT INTO usage VALUES ($ts, 'a', 'c', NULL, NULL, 0, 10, 5, 0, 0, '0.1', '0.2', NULL, '0');
                INSERT INTO usage VALUES ($ts, 'b', 'c', 'r', NULL, 0, 1, 1, 0, 0, '0.0000000000001', '0', 'work', '0.5');
                """;
            create.Parameters.AddWithValue("$ts", _time.GetUtcNow().UtcTicks);
            create.ExecuteNonQuery();
        }

        var ledger = new SqliteUsageLedger(Open());
        ledger.Record(new UsageRecord
        {
            Timestamp = _time.GetUtcNow(),
            Model = "a",
            Connector = "c",
            IsLocal = false,
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.3m,
            ReferenceCostUsd = 0.3m,
        });
        new SqliteUsageLedger(Open()).Total(Since, Until).Requests.Should().Be(3, "opening the migrated file again changes nothing");

        UsageTotals total = ledger.Total(Since, Until);
        total.Should().Be(new UsageTotals
        {
            Requests = 3,
            InputTokens = 12,
            OutputTokens = 7,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.4000000000001m,
            ReferenceCostUsd = 0.5m,
            SubscriptionValueUsd = 0.5m,
        });
        ledger.Total(Since, Until, model: "A").CostUsd.Should().Be(0.4m);
        ledger.Summarize(Since, Until, UsageGrouping.Profile).Select(g => g.Key).Should().BeEquivalentTo("work", "(no profile)");
        using var check = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        check.Open();
        using SqliteCommand rows = check.CreateCommand();
        rows.CommandText = "SELECT id, model, cost_pico FROM usage ORDER BY id; SELECT value FROM meta WHERE key = 'schema_version';";
        using SqliteDataReader reader = rows.ExecuteReader();
        var migrated = new List<(long, string, long?)>();
        while (reader.Read())
        {
            migrated.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        migrated.Should().Equal((1L, "a", 100_000_000_000L), (2L, "b", null), (3L, "a", 300_000_000_000L));
        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("4");
    }

    [Fact]
    public void Usage_costs_add_up_exactly()
    {
        var ledger = new SqliteUsageLedger(Open());
        foreach (decimal cost in new[] { 0.1m, 0.1m, 0.1m, 0.0000000000001m, 1234567.000000000001m })
        {
            ledger.Record(new UsageRecord
            {
                Timestamp = _time.GetUtcNow(),
                Model = "m",
                Connector = "c",
                IsLocal = false,
                InputTokens = 1,
                OutputTokens = 1,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = cost,
                ReferenceCostUsd = cost * 2,
                SubscriptionValueUsd = cost,
            });
        }

        UsageTotals total = ledger.Total(Since, Until);
        total.CostUsd.Should().Be(1234567.300000000001m + 0.0000000000001m);
        total.ReferenceCostUsd.Should().Be(2 * total.CostUsd);
        total.SubscriptionValueUsd.Should().Be(total.CostUsd);
        ledger.Summarize(Since, Until, UsageGrouping.Connector).Single().Totals.Should().Be(total);
        new SqliteUsageLedger(Open()).Total(Since, Until, connector: "c").CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("1234567.3000000000011", "no trailing zeros are added");
    }

    [Fact]
    public void Usage_summaries_from_the_database_match_the_records_in_every_grouping()
    {
        // Days in a zone with daylight saving time: the last Sunday of October 2026 has 25 hours.
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(1), "test", "test", "test",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);
        var ledger = new SqliteUsageLedger(Open());
        var records = new List<UsageRecord>();
        DateTimeOffset start = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 300; i++)
        {
            var record = new UsageRecord
            {
                Timestamp = start.AddMinutes(17 * i),
                Model = i % 3 == 0 ? "Cloud" : i % 3 == 1 ? "cloud" : "local",
                Connector = i % 2 == 0 ? "c" : "d",
                Route = i % 4 == 0 ? null : "r" + (i % 2),
                Client = i % 5 == 0 ? null : "k",
                IsLocal = i % 3 == 2,
                InputTokens = i,
                OutputTokens = 2 * i,
                CacheReadTokens = i % 7,
                CacheWriteTokens = i % 11,
                CostUsd = i * 0.001m,
                ReferenceCostUsd = i * 0.002m,
                Profile = i % 6 == 0 ? null : "p",
                SubscriptionValueUsd = i * 0.0005m,
            };
            records.Add(record);
            ledger.Record(record);
        }

        var prices = new RouterConfiguration();
        prices.Models.Add(new ModelDefinition { Name = "cloud", Connector = "c", InputCostPerMillionTokens = 3, OutputCostPerMillionTokens = 15, CacheReadCostPerMillionTokens = 0.3m });
        prices.Usage.ReferenceModel = "cloud";
        DateTimeOffset since = start.AddHours(5), until = start.AddDays(3);
        Func<UsageRecord, UsageRecord> price = UsagePricing.Pricer(prices);
        UsageRecord[] inRange = [.. records.Where(r => r.Timestamp >= since && r.Timestamp < until)];
        foreach (UsageGrouping by in Enum.GetValues<UsageGrouping>())
        {
            ledger.Summarize(since, until, by, zone).Should().BeEquivalentTo(UsageSummaries.Group(inRange, by, zone), o => o.WithStrictOrdering(), by.ToString());
            ledger.SummarizeAtCurrentPrices(prices, since, until, by, zone)
                .Should().BeEquivalentTo(UsageSummaries.Group(inRange.Select(price), by, zone), o => o.WithStrictOrdering(), by + " at current prices");
        }

        ledger.Summarize(since, until, UsageGrouping.Day, zone).Select(g => g.Key).Should().Equal("2026-10-24", "2026-10-25", "2026-10-26");
        ledger.TotalAtCurrentPrices(prices, since, until, connector: "c").Should().Be(
            inRange.Where(r => r.Connector == "c").Select(price).Aggregate(UsageTotals.Zero, (t, r) => t.Add(r)));
    }

    [Fact]
    public void A_budget_check_reads_the_usage_table_once_however_many_rows_it_has()
    {
        var ledger = new SqliteUsageLedger(Open());
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO usage (timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile, subscription_value, cost_pico, reference_pico, value_pico) " +
                "VALUES ($ts, $model, $connector, NULL, NULL, 0, 1000, 100, 0, 0, '0.001', '0.001', NULL, '0', 1000000000, 1000000000, 0);";
            SqliteParameter ts = insert.Parameters.Add("$ts", SqliteType.Integer);
            SqliteParameter model = insert.Parameters.Add("$model", SqliteType.Text);
            SqliteParameter connector = insert.Parameters.Add("$connector", SqliteType.Text);
            for (int i = 0; i < 100_000; i++)
            {
                ts.Value = _time.GetUtcNow().AddSeconds(-i).UtcTicks;
                model.Value = i % 2 == 0 ? "cloud" : "other";
                connector.Value = i % 2 == 0 ? "c" : "d";
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var configuration = new RouterConfiguration();
        ModelDefinition cloud = new() { Name = "cloud", Connector = "c", InputCostPerMillionTokens = 2, OutputCostPerMillionTokens = 10 };
        configuration.Models.Add(cloud);
        configuration.Usage.TimeZone = "UTC";
        configuration.Usage.Budgets.Add(new BudgetDefinition { Name = "monthly", Connector = "c", Period = BudgetPeriod.Month, MaxCostUsd = 1_000_000 });
        var gate = new BudgetGate(ledger, _time, Microsoft.Extensions.Logging.Abstractions.NullLogger<BudgetGate>.Instance, () => configuration);

        gate.Blocking(cloud, configuration.Usage).Should().BeNull();
        int reads = ledger.Reads;
        for (int i = 0; i < 50; i++)
        {
            ledger.Record(new UsageRecord
            {
                Timestamp = _time.GetUtcNow(),
                Model = "cloud",
                Connector = "c",
                IsLocal = false,
                InputTokens = 1000,
                OutputTokens = 100,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = 0m,
                ReferenceCostUsd = 0m,
            });
            gate.Blocking(cloud, configuration.Usage).Should().BeNull();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            gate.Blocking(cloud, configuration.Usage);
        }

        watch.Stop();
        ledger.Reads.Should().Be(reads, "what this instance records is added to the running totals; nothing is read again");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "1,000 checks against 100,000 rows; reading them took most of a second per check");
        gate.Status(configuration.Usage).Single().CostUsedUsd.Should().Be(50_050 * 0.003m, "the period's 50,050 requests on the connector, at its current prices");
    }

    [Fact]
    public void Instances_sharing_the_file_see_each_others_usage_within_the_refresh_interval()
    {
        var a = new SqliteUsageLedger(Open());
        var b = new SqliteUsageLedger(Open());
        UsageRecord Request() => new UsageRecord
        {
            Timestamp = _time.GetUtcNow(),
            Model = "cloud",
            Connector = "c",
            IsLocal = false,
            InputTokens = 10,
            OutputTokens = 10,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.25m,
            ReferenceCostUsd = 0.25m,
        };
        a.Total(Since, Until, connector: "c").Requests.Should().Be(0);

        b.Record(Request());
        Settle();
        a.Total(Since, Until, connector: "c").Requests.Should().Be(0, "within the refresh interval");
        _time.Advance(SqliteUsageLedger.RefreshInterval);
        a.Total(Since, Until, connector: "c").Should().Match<UsageTotals>(t => t.Requests == 1 && t.CostUsd == 0.25m);

        b.Record(Request());
        Settle();
        a.Record(Request());
        a.Total(Since, Until, connector: "c").Requests.Should().Be(3, "a row it did not write came before its own, so it reads the rows it missed");
        b.Total(Since, Until, connector: "c").CostUsd.Should().Be(0.75m);
    }

    [Fact]
    public async Task Two_activity_logs_sharing_the_file_keep_every_entry_under_concurrent_writes()
    {
        RoutingActivityLog[] logs = [new(store: new SqliteActivityStore(Open())), new(store: new SqliteActivityStore(Open()))];

        RoutingActivityEntry[] written = await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(() =>
        {
            RoutingActivityLog log = logs[i % 2];
            RoutingActivityEntry entry = log.Add(new RoutingActivityEntry { Id = log.Reserve(), Timestamp = _time.GetUtcNow(), PromptPreview = "p" + i });
            entry.Outcome = "served";
            log.Complete(entry);
            return entry;
        })));
        RoutingActivityEntry graded = written[7];
        graded.Grade = 2;
        logs[1].Revise(graded);

        IReadOnlyList<RoutingActivityEntry> stored = new SqliteActivityStore(Open()).Recent(1000);
        written.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        stored.Should().HaveCount(400, "no entry replaced another");
        stored.Select(e => e.PromptPreview).Should().BeEquivalentTo(Enumerable.Range(0, 400).Select(i => "p" + i));
        stored.Single(e => e.Id == graded.Id).Should().Match<RoutingActivityEntry>(e => e.Grade == 2 && e.PromptPreview == graded.PromptPreview);
        _logs.All.Should().NotContain("failed");
    }

    [Fact]
    public async Task Two_feedback_logs_sharing_the_file_keep_every_item_under_concurrent_writes()
    {
        RoutingFeedbackLog[] logs = [new(store: new SqliteFeedbackStore(Open())), new(store: new SqliteFeedbackStore(Open()))];

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
            logs[i % 2].Add(new RoutingFeedback { Timestamp = _time.GetUtcNow(), EntryId = i, Kind = FeedbackKind.Retried }).Should().BeTrue())));

        IReadOnlyList<RoutingFeedback> stored = new SqliteFeedbackStore(Open()).Recent(1000);
        stored.Select(f => f.EntryId).Should().BeEquivalentTo(Enumerable.Range(0, 200).Select(i => (long)i));
        stored.Select(f => f.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id > 0);
        _logs.All.Should().NotContain("failed");
    }

    [Fact]
    public void An_activity_id_that_is_already_stored_is_not_overwritten()
    {
        var store = new SqliteActivityStore(Open());
        store.Append(new RoutingActivityEntry { Id = 5, Timestamp = _time.GetUtcNow(), PromptPreview = "first" });
        store.Append(new RoutingActivityEntry { Id = 5, Timestamp = _time.GetUtcNow(), PromptPreview = "second" });

        store.Recent(10).Should().ContainSingle().Which.PromptPreview.Should().Be("first");
        _logs.All.Should().Contain("saving activity failed");
    }

    private DateTimeOffset Since => _time.GetUtcNow().Date.AddDays(-1);

    private DateTimeOffset Until => _time.GetUtcNow().Date.AddDays(2);
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
    private string DatabaseFiles() => ReadDatabaseFiles(_directory, "state.db");

    /// <summary>
    /// The text of a database and its -wal and -shm files, while the router may be using them. The log is read before
    /// the database: a checkpoint moves pages from the log into the database (and closing the last connection deletes
    /// the log), so what leaves the log in between is still found in the database.
    /// </summary>
    internal static string ReadDatabaseFiles(string directory, string name)
    {
        string database = Path.Combine(directory, name);
        return ReadWhileOpen(database + "-wal") + ReadWhileOpen(database) + ReadWhileOpen(database + "-shm");
    }

    /// <summary>
    /// Reads a file the router may be writing; empty when there is no such file (any more). On macOS the shared lock
    /// .NET takes when opening a file conflicts with the lock SQLite holds while it writes, so a read that lands during
    /// a write is tried again.
    /// </summary>
    private static string ReadWhileOpen(string file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return string.Empty;
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
        await WaitUntilAsync(() => store.Recent(count).Count >= count);
        store.Recent(count).Should().HaveCount(count);
    }

    /// <summary>Waits until <paramref name="done"/> holds, for up to 10 seconds: a slow runner gets the time it needs, a fast one does not wait for it.</summary>
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!done() && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }
    }

    private static async Task<string> LastPreviewAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("/router/api/activity"))!["entries"]!.AsArray()[0]!["promptPreview"]!.GetValue<string>();

    [Fact]
    public async Task Concurrent_requests_get_distinct_ids_and_each_its_own_stored_entry()
    {
        const int Requests = 48;
        HttpClient client = await StartDurableAsync();

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, Requests).Select(i => client.PostAsync("/v1/chat/completions", Chat("hello " + i))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        long[] ids = [.. responses.Select(r => long.Parse(r.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture))];
        ids.Should().OnlyHaveUniqueItems();
        await WaitForStoredActivityAsync(Requests);
        IReadOnlyList<RoutingActivityEntry> stored = Services.GetRequiredService<IRoutingActivityStore>().Recent(1000);
        stored.Select(e => e.Id).Should().BeEquivalentTo(ids);
        stored.Select(e => e.PromptPreview).Should().BeEquivalentTo(Enumerable.Range(0, Requests).Select(i => "hello " + i));
    }

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

        await WaitUntilAsync(() => Events().Contains("exit code 1", StringComparison.Ordinal));

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
        foreach (string format in new[] { "jsonl", "csv" })
        {
            (await client.GetStringAsync($"/router/api/activity/export?format={format}&prompts=true&since=2000-01-01T00:00:00Z"))
                .Should().NotContain("purge-me 7a1c", $"the {format} export reads the purged store");
        }
    }

    [Fact]
    public async Task The_routers_log_holds_no_prompt_text_even_when_previews_are_stored()
    {
        // The local backend fails with an error that quotes the prompt, as some servers do; the cloud model answers.
        LocalHttp = new StubHttpMessageHandler((_, body) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["message"] = "could not continue: " + body } }.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        var logs = new CapturingLoggerProvider();
        HttpClient client = await StartDurableAsync(
            o => o.StorePrompts = PromptStorage.On,
            services: s =>
            {
                s.AddSingleton<ILoggerProvider>(logs);
                s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
            });

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat("logmarker 6d2a: the severance terms for the Leeds office"));
        await client.PostAsync("/semantic-router/classify", JsonBody(new JsonObject { ["prompt"] = "logmarker 6d2a: and the second office" }));
        await WaitForStoredActivityAsync(1);

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        LocalHttp.Requests.Should().ContainSingle().Which.Body.Should().Contain("logmarker 6d2a");
        DatabaseFiles().Should().Contain("logmarker 6d2a", "StorePrompts: On keeps the preview");
        logs.Messages.Should().NotBeEmpty();
        logs.All.Should().NotContain("logmarker 6d2a", "the log names decisions and errors, never the prompt (docs/host.md)");
    }

    [Fact]
    public async Task The_export_leaves_out_activity_past_the_retention_before_it_is_pruned()
    {
        HttpClient client = await StartDurableAsync(o =>
        {
            o.StorePrompts = PromptStorage.On;
            o.Retention.ActivityDays = 7;
        });
        _ = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        Services.GetRequiredService<IRoutingActivityStore>().Append(
            new RoutingActivityEntry { Id = 900, Timestamp = DateTimeOffset.UtcNow.AddDays(-40), PromptPreview = "aged-out 2b8e", Outcome = "served" });

        string export = await client.GetStringAsync("/router/api/activity/export?format=jsonl&prompts=true&since=2000-01-01T00:00:00Z");

        export.Should().NotContain("aged-out 2b8e");
    }

    [Fact]
    public async Task Deleting_activity_deletes_the_judges_reasons_learned_examples_and_connector_errors_from_its_prompts()
    {
        LocalHttp = new StubHttpMessageHandler((_, body) => OpenAIReply(
            body.Contains("\"model\":\"judge-32b\"", StringComparison.Ordinal)
                ? """{"grade": 2, "reason": "the purge-marker 4c1d figures are wrong"}"""
                : "Revenue rose."));
        Cli = FakeCliProcessRunner.Failing(1, stdout: ClaudeStream.Error("I cannot share the purge-marker 4c1d numbers"), stderr: "aborted");
        HttpClient client = await StartDurableAsync(
            o => o.StorePrompts = PromptStorage.On,
            middleware: o => (o.Judge.Enabled, o.Judge.Model, o.Judge.SampleRate) = (true, "judge", 1),
            routing: o =>
            {
                ModelDefinition judge = Model("judge", 3, local: true, connector: "lmstudio");
                judge.Model = "judge-32b";
                o.Models.Add(judge);
            },
            withCli: true,
            services: s => s.AddSingleton<IModelResidency>(new NotLoaded("judge-32b")));

        await client.PostAsync("/v1/chat/completions", Chat("the purge-marker 4c1d quarterly numbers"));
        await Services.GetRequiredService<AnswerJudge>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        await client.PostAsync("/v1/chat/completions", JsonBody(new JsonObject
        {
            ["model"] = "claude-cli",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "the numbers again" }),
        }));
        await WaitForStoredActivityAsync(2);

        // A learned change made from that feedback, quoting the prompt as its evidence.
        ITuningStore tuning = Services.GetRequiredService<ITuningStore>();
        tuning.Save(new TuningChange
        {
            Id = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Kind = TuningChangeKind.DefaultThreshold,
            OldValue = 0.75f,
            NewValue = 0.73f,
            Why = "At 0.73, 1 labelled prompt routes to the tier it needs that does not now (1 from feedback: judged), e.g. \"the purge-marker 4c1d quarterly numbers\"; none would route lower than they need.",
            Examples = ["the purge-marker 4c1d quarterly numbers"],
            State = TuningChangeState.Suggested,
        });
        Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().ContainSingle().Which.Detail.Should().Contain("purge-marker 4c1d");
        Services.GetRequiredService<ConnectorIncidents>().Failing().Should().ContainSingle().Which.Detail.Should().Contain("purge-marker 4c1d");
        DatabaseFiles().Should().Contain("purge-marker 4c1d");

        JsonNode purge = JsonNode.Parse(await (await client.DeleteAsync("/router/api/activity")).Content.ReadAsStringAsync())!;

        purge["removed"]!.GetValue<int>().Should().Be(2);
        purge["complete"]!.GetValue<bool>().Should().BeTrue();
        Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().BeEmpty();
        TuningChange scrubbed = tuning.Changes().Single();
        scrubbed.Examples.Should().BeEmpty();
        scrubbed.Why.Should().Be("At 0.73, 1 labelled prompt routes to the tier it needs that does not now (1 from feedback: judged); none would route lower than they need.");
        Services.GetRequiredService<ConnectorIncidents>().Failing().Should().ContainSingle().Which.Detail.Should().BeNull("it still fails, without the message");
        DatabaseFiles().Should().NotContain("purge-marker 4c1d");
    }

    [Fact]
    public async Task A_request_still_running_or_waiting_for_the_judge_when_activity_is_deleted_is_not_stored_again()
    {
        using var answer = new ManualResetEventSlim();
        using var grade = new ManualResetEventSlim();
        LocalHttp = new StubHttpMessageHandler((_, body) =>
        {
            bool judging = body.Contains("\"model\":\"judge-32b\"", StringComparison.Ordinal);
            (judging ? grade : answer).Wait(TimeSpan.FromSeconds(20));
            return OpenAIReply(judging ? """{"grade": 1, "reason": "inflight-marker 9e0f is wrong"}""" : "Done.");
        });
        HttpClient client = await StartDurableAsync(
            o => o.StorePrompts = PromptStorage.On,
            middleware: o => (o.Judge.Enabled, o.Judge.Model, o.Judge.SampleRate) = (true, "judge", 1),
            routing: o =>
            {
                ModelDefinition judge = Model("judge", 3, local: true, connector: "lmstudio");
                judge.Model = "judge-32b";
                o.Models.Add(judge);
            },
            services: s => s.AddSingleton<IModelResidency>(new NotLoaded("judge-32b")));
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        IRoutingActivityStore store = Services.GetRequiredService<IRoutingActivityStore>();

        // The first request is answered and waits for the judge; the second is still waiting for its answer.
        answer.Set();
        await client.PostAsync("/v1/chat/completions", Chat("judged inflight-marker 9e0f the layoffs list"));
        await WaitForStoredActivityAsync(1);
        answer.Reset();
        Task<HttpResponseMessage> running = client.PostAsync("/v1/chat/completions", Chat("running inflight-marker 9e0f the layoffs list"));
        for (int i = 0; i < 500 && activity.Recent(10).Count < 2; i++)
        {
            await Task.Delay(10);
        }

        activity.Recent(10).Should().HaveCount(2);
        (await client.DeleteAsync("/router/api/activity")).StatusCode.Should().Be(HttpStatusCode.OK);
        answer.Set();
        grade.Set();
        (await running).StatusCode.Should().Be(HttpStatusCode.OK);
        await Services.GetRequiredService<AnswerJudge>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        store.Recent(10).Should().BeEmpty();
        activity.Recent(10).Should().BeEmpty();
        Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().BeEmpty();
        DatabaseFiles().Should().NotContain("inflight-marker 9e0f");
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
        SqliteFiles.Release(_directory);
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
        SqliteFiles.Release(_directory);
        Directory.Delete(_directory, recursive: true);
    }
}
