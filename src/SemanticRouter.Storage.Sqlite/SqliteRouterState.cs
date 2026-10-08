using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>Options for <see cref="SqliteStateBuilderExtensions.UseSqliteState"/>.</summary>
public sealed class SqliteStateOptions
{
    /// <summary>The database file. Relative paths resolve against the current directory. Default <c>semantic-router.db</c>.</summary>
    public string Path { get; set; } = "semantic-router.db";

    /// <summary>How long a tool call stays pinned to the model that issued it. Default 24 hours.</summary>
    public TimeSpan AffinityWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Most activity entries kept. Default 10,000.</summary>
    public int ActivityMaxEntries { get; set; } = 10_000;

    /// <summary>Oldest activity kept. Default 30 days.</summary>
    public TimeSpan ActivityMaxAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Oldest usage records kept (budgets need the current month; the dashboard shows longer). Default 400 days.</summary>
    public TimeSpan UsageRetention { get; set; } = TimeSpan.FromDays(400);

    /// <summary>Most configuration versions kept. Default 200.</summary>
    public int HistoryMaxEntries { get; set; } = 200;

    /// <summary>How long a write waits for another writer (another router instance) before giving up. Default 5 s.</summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Registers the SQLite-backed state.</summary>
public static class SqliteStateBuilderExtensions
{
    /// <summary>
    /// Keeps model cooldowns, tool-call affinity, the activity log and the configuration history in one SQLite file,
    /// so they survive restarts. Several router instances on one machine may share the file (cooldowns and affinity
    /// are then shared); a database that cannot be opened or written falls back to memory with a warning.
    /// </summary>
    public static SemanticRouterBuilder UseSqliteState(this SemanticRouterBuilder builder, string path, Action<SqliteStateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IServiceCollection services = builder.Services;
        services.Configure<SqliteStateOptions>(o =>
        {
            o.Path = path;
            configure?.Invoke(o);
        });
        services.TryAddSingleton<SqliteRouterDatabase>();
        services.Replace(ServiceDescriptor.Singleton<ICooldownStore, SqliteCooldownStore>());
        services.Replace(ServiceDescriptor.Singleton<IToolCallAffinity, SqliteToolCallAffinity>());
        services.Replace(ServiceDescriptor.Singleton<IRoutingActivityStore, SqliteActivityStore>());
        services.Replace(ServiceDescriptor.Singleton<IRouterConfigurationHistory, SqliteConfigurationHistory>());
        services.Replace(ServiceDescriptor.Singleton<IUsageLedger, SqliteUsageLedger>());
        return builder;
    }
}

/// <summary>
/// The shared database: schema, connections and failure handling. Every operation degrades to a fallback value when
/// the database is unavailable, so routing never fails because of state storage.
/// </summary>
internal sealed partial class SqliteRouterDatabase
{
    // 2: usage.profile (routing profiles). Version 1 files are migrated in place.
    private const int SchemaVersion = 2;

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private long _lastWarningTicks;

    public SqliteRouterDatabase(IOptions<SqliteStateOptions> options, ILogger<SqliteRouterDatabase> logger, TimeProvider? time = null)
    {
        Options = options.Value;
        Time = time ?? TimeProvider.System;
        _logger = logger;
        string path = System.IO.Path.GetFullPath(Options.Path);
        Location = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(Options.BusyTimeout.TotalSeconds)),
            Pooling = true,
        }.ToString();

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using SqliteConnection connection = Open();
            Migrate(connection);
            Available = true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Available = false;
            LogUnavailable(_logger, path, ex.Message);
        }
    }

    public SqliteStateOptions Options { get; }

    public TimeProvider Time { get; }

    public string Location { get; }

    /// <summary>Whether the database opened; when not, every store keeps its state in memory only.</summary>
    public bool Available { get; private set; }

    /// <summary>Runs <paramref name="operation"/>, or returns <paramref name="fallback"/> when the database fails.</summary>
    public T Run<T>(string what, T fallback, Func<SqliteConnection, T> operation)
    {
        if (!Available)
        {
            return fallback;
        }

        try
        {
            using SqliteConnection connection = Open();
            return operation(connection);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or JsonException)
        {
            // At most one warning a minute: a locked or broken database must not flood the log.
            long now = Time.GetUtcNow().UtcTicks;
            long last = Interlocked.Read(ref _lastWarningTicks);
            if (now - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
            {
                LogOperationFailed(_logger, what, Location, ex.Message);
            }

            return fallback;
        }
    }

    public void Run(string what, Action<SqliteConnection> operation) =>
        Run<bool>(what, false, connection =>
        {
            operation(connection);
            return true;
        });

    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // With WAL, NORMAL still never corrupts the file and loses nothing when the process crashes, but it does not
        // flush to disk on every commit. FULL did, holding the write lock for each flush: on Windows, where flushes are
        // slow, a burst of writes (two routers sharing the file) queued past the busy timeout and was dropped.
        using (SqliteCommand synchronous = Command(connection, "PRAGMA synchronous=NORMAL;"))
        {
            synchronous.ExecuteNonQuery();
        }

        return connection;
    }

    private static void Migrate(SqliteConnection connection)
    {
        // WAL lets readers and one writer (possibly another instance) work at the same time.
        using (SqliteCommand wal = Command(connection, "PRAGMA journal_mode=WAL;"))
        {
            wal.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand create = Command(connection, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cooldowns (model TEXT PRIMARY KEY COLLATE NOCASE, until_ticks INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS affinity (tool_use_id TEXT PRIMARY KEY, model TEXT NOT NULL, created_ticks INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS affinity_created ON affinity (created_ticks);
            CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY, timestamp_ticks INTEGER NOT NULL, entry TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_timestamp ON activity (timestamp_ticks);
            CREATE TABLE IF NOT EXISTS usage (
                timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
                is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL, profile TEXT);
            CREATE INDEX IF NOT EXISTS usage_timestamp ON usage (timestamp_ticks);
            CREATE TABLE IF NOT EXISTS config_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_ticks INTEGER NOT NULL,
                source TEXT NOT NULL, actor TEXT, note TEXT, document TEXT NOT NULL);
            INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', $version);
            """, ("$version", SchemaVersion.ToString(CultureInfo.InvariantCulture))))
        {
            create.Transaction = transaction;
            create.ExecuteNonQuery();
        }

        using (SqliteCommand check = Command(connection, "SELECT value FROM meta WHERE key = 'schema_version';"))
        {
            check.Transaction = transaction;
            string? version = check.ExecuteScalar() as string;
            if (version == "1")
            {
                using SqliteCommand migrate = Command(connection, """
                    ALTER TABLE usage ADD COLUMN profile TEXT;
                    UPDATE meta SET value = '2' WHERE key = 'schema_version';
                    """);
                migrate.Transaction = transaction;
                migrate.ExecuteNonQuery();
            }
            else if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException($"Unsupported state schema version {version}.");
            }
        }

        transaction.Commit();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database {Path} cannot be used ({Error}); cooldowns, tool-call affinity, activity and configuration history stay in memory and are lost on restart.")]
    private static partial void LogUnavailable(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database: {What} failed for {Path} ({Error}); continuing in memory.")]
    private static partial void LogOperationFailed(ILogger logger, string what, string path, string error);
}

/// <summary>Cooldowns with absolute expiry, so a restart during a quota cooldown keeps the model cooling down.</summary>
internal sealed class SqliteCooldownStore(SqliteRouterDatabase database) : ICooldownStore
{
    public IReadOnlyDictionary<string, DateTimeOffset> LoadCooldowns() =>
        database.Run<IReadOnlyDictionary<string, DateTimeOffset>>("reading cooldowns", new Dictionary<string, DateTimeOffset>(), connection =>
        {
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT model, until_ticks FROM cooldowns WHERE until_ticks > $now;", ("$now", database.Time.GetUtcNow().UtcTicks));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero);
            }

            return result;
        });

    public void SaveCooldown(string model, DateTimeOffset until) =>
        database.Run("saving a cooldown", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO cooldowns (model, until_ticks) VALUES ($model, $until) ON CONFLICT(model) DO UPDATE SET until_ticks = excluded.until_ticks;" +
                "DELETE FROM cooldowns WHERE until_ticks <= $now;",
                ("$model", model), ("$until", until.UtcTicks), ("$now", database.Time.GetUtcNow().UtcTicks));
            command.ExecuteNonQuery();
        });

    public void RemoveCooldown(string model) =>
        database.Run("removing a cooldown", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "DELETE FROM cooldowns WHERE model = $model;", ("$model", model));
            command.ExecuteNonQuery();
        });

    public void ClearCooldowns() =>
        database.Run("clearing cooldowns", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "DELETE FROM cooldowns;");
            command.ExecuteNonQuery();
        });
}

/// <summary>
/// Tool-call affinity that survives restarts: an in-memory cache in front of the database, so a tool turn that spans
/// a restart (or lands on another instance sharing the file) still goes back to the model that issued the call.
/// </summary>
internal sealed class SqliteToolCallAffinity(SqliteRouterDatabase database) : IToolCallAffinity
{
    private readonly ToolCallAffinity _cache = new();
    private int _writes;

    public void Remember(string toolUseId, string modelName)
    {
        if (string.IsNullOrEmpty(toolUseId))
        {
            return;
        }

        _cache.Remember(toolUseId, modelName);
        bool prune = Interlocked.Increment(ref _writes) % 200 == 0;
        long now = database.Time.GetUtcNow().UtcTicks;
        database.Run("saving tool-call affinity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO affinity (tool_use_id, model, created_ticks) VALUES ($id, $model, $now) " +
                "ON CONFLICT(tool_use_id) DO UPDATE SET model = excluded.model, created_ticks = excluded.created_ticks;" +
                (prune ? "DELETE FROM affinity WHERE created_ticks < $oldest;" : string.Empty),
                ("$id", toolUseId), ("$model", modelName), ("$now", now), ("$oldest", now - database.Options.AffinityWindow.Ticks));
            command.ExecuteNonQuery();
        });
    }

    public string? Lookup(IEnumerable<string> toolUseIds)
    {
        ArgumentNullException.ThrowIfNull(toolUseIds);
        string[] ids = [.. toolUseIds.Where(id => !string.IsNullOrEmpty(id))];
        if (ids.Length == 0)
        {
            return null;
        }

        if (_cache.Lookup(ids) is { } cached)
        {
            return cached;
        }

        return database.Run<string?>("reading tool-call affinity", null, connection =>
        {
            long oldest = database.Time.GetUtcNow().UtcTicks - database.Options.AffinityWindow.Ticks;
            foreach (string id in ids)
            {
                using SqliteCommand command = SqliteRouterDatabase.Command(
                    connection, "SELECT model FROM affinity WHERE tool_use_id = $id AND created_ticks >= $oldest;", ("$id", id), ("$oldest", oldest));
                if (command.ExecuteScalar() is string model)
                {
                    _cache.Remember(id, model);
                    return model;
                }
            }

            return null;
        });
    }
}

/// <summary>The activity log on disk, capped by count and age.</summary>
internal sealed class SqliteActivityStore(SqliteRouterDatabase database) : IRoutingActivityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _writes;

    public void Append(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool prune = Interlocked.Increment(ref _writes) % 100 == 0;
        database.Run("saving activity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT OR REPLACE INTO activity (id, timestamp_ticks, entry) VALUES ($id, $ts, $entry);",
                ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", JsonSerializer.Serialize(entry, Json)));
            command.ExecuteNonQuery();
            if (prune)
            {
                Prune(connection);
            }
        });
    }

    public IReadOnlyList<RoutingActivityEntry> Recent(int limit) =>
        database.Run<IReadOnlyList<RoutingActivityEntry>>("reading activity", [], connection =>
        {
            var entries = new List<RoutingActivityEntry>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT entry FROM activity WHERE timestamp_ticks >= $oldest ORDER BY id DESC LIMIT $limit;",
                ("$oldest", (database.Time.GetUtcNow() - database.Options.ActivityMaxAge).UtcTicks), ("$limit", Math.Max(0, limit)));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<RoutingActivityEntry>(reader.GetString(0), Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        });

    public IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit) =>
        database.Run<IReadOnlyList<RoutingActivityEntry>>("reading activity", [], connection =>
        {
            var entries = new List<RoutingActivityEntry>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT entry FROM activity WHERE timestamp_ticks >= $since AND timestamp_ticks < $until ORDER BY id LIMIT $limit;",
                ("$since", since.UtcTicks), ("$until", until.UtcTicks), ("$limit", Math.Max(0, limit)));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<RoutingActivityEntry>(reader.GetString(0), Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        });

    internal void Prune(SqliteConnection connection)
    {
        using SqliteCommand command = SqliteRouterDatabase.Command(
            connection,
            "DELETE FROM activity WHERE timestamp_ticks < $oldest;" +
            "DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT $max);",
            ("$oldest", (database.Time.GetUtcNow() - database.Options.ActivityMaxAge).UtcTicks), ("$max", database.Options.ActivityMaxEntries));
        command.ExecuteNonQuery();
    }
}

/// <summary>Every applied configuration document, for the dashboard's history, diff and rollback.</summary>
internal sealed class SqliteConfigurationHistory(SqliteRouterDatabase database) : IRouterConfigurationHistory
{
    private readonly InMemoryRouterConfigurationHistory _fallback = new();

    public long Record(RouterConfiguration document, string source, string? actor = null, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        long id = database.Run<long>("recording configuration history", -1, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO config_history (timestamp_ticks, source, actor, note, document) VALUES ($ts, $source, $actor, $note, $document);" +
                "DELETE FROM config_history WHERE id NOT IN (SELECT id FROM config_history ORDER BY id DESC LIMIT $max);" +
                "SELECT last_insert_rowid();",
                ("$ts", database.Time.GetUtcNow().UtcTicks), ("$source", source), ("$actor", actor), ("$note", note),
                ("$document", document.ToJson()), ("$max", database.Options.HistoryMaxEntries));
            return (long)command.ExecuteScalar()!;
        });

        return id >= 0 ? id : _fallback.Record(document, source, actor, note);
    }

    public IReadOnlyList<RouterConfigurationVersion> List(int limit = 50)
    {
        if (!database.Available)
        {
            return _fallback.List(limit);
        }

        return database.Run<IReadOnlyList<RouterConfigurationVersion>>("reading configuration history", _fallback.List(limit), connection =>
        {
            var versions = new List<RouterConfigurationVersion>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT id, timestamp_ticks, source, actor, note FROM config_history ORDER BY id DESC LIMIT $limit;", ("$limit", Math.Max(0, limit)));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                versions.Add(new RouterConfigurationVersion(
                    reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return versions;
        });
    }

    public RouterConfiguration? GetVersion(long id)
    {
        if (!database.Available)
        {
            return _fallback.GetVersion(id);
        }

        string? json = database.Run<string?>("reading configuration history", null, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "SELECT document FROM config_history WHERE id = $id;", ("$id", id));
            return command.ExecuteScalar() as string;
        });
        return json is null ? _fallback.GetVersion(id) : RouterConfiguration.FromJson(json);
    }
}

/// <summary>Usage records on disk, for the dashboard's usage views and for budgets across restarts.</summary>
internal sealed class SqliteUsageLedger(SqliteRouterDatabase database) : IUsageLedger
{
    private readonly InMemoryUsageLedger _fallback = new();
    private int _writes;

    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool prune = Interlocked.Increment(ref _writes) % 500 == 0;
        bool stored = database.Run("recording usage", false, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO usage (timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile) " +
                "VALUES ($ts, $model, $connector, $route, $client, $local, $input, $output, $cacheRead, $cacheWrite, $cost, $reference, $profile);" +
                (prune ? "DELETE FROM usage WHERE timestamp_ticks < $oldest;" : string.Empty),
                ("$ts", record.Timestamp.UtcTicks), ("$model", record.Model), ("$connector", record.Connector), ("$route", record.Route),
                ("$client", record.Client), ("$local", record.IsLocal ? 1 : 0), ("$input", record.InputTokens), ("$output", record.OutputTokens),
                ("$cacheRead", record.CacheReadTokens), ("$cacheWrite", record.CacheWriteTokens),
                ("$cost", record.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$reference", record.ReferenceCostUsd.ToString(CultureInfo.InvariantCulture)),
                ("$oldest", (record.Timestamp - database.Options.UsageRetention).UtcTicks), ("$profile", record.Profile));
            command.ExecuteNonQuery();
            return true;
        });

        if (!stored)
        {
            _fallback.Record(record);
        }
    }

    public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        Read(since, until, model, connector, profile).Aggregate(_fallback.Total(since, until, model, connector, profile), (total, r) => total.Add(r));

    public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null) =>
        UsageSummaries.Group(Read(since, until, null, null, null), by, zone ?? TimeZoneInfo.Local)
            .Concat(_fallback.Summarize(since, until, by, zone))
            .GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First() : new UsageGroup(g.Key, Merge(g.Select(x => x.Totals))))
            .ToArray();

    private static UsageTotals Merge(IEnumerable<UsageTotals> totals) => totals.Aggregate(UsageTotals.Zero, (a, b) => new UsageTotals(
        a.Requests + b.Requests, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens, a.CostUsd + b.CostUsd, a.ReferenceCostUsd + b.ReferenceCostUsd));

    private List<UsageRecord> Read(DateTimeOffset since, DateTimeOffset until, string? model, string? connector, string? profile) =>
        database.Run("reading usage", new List<UsageRecord>(), connection =>
        {
            var records = new List<UsageRecord>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "SELECT timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile FROM usage " +
                "WHERE timestamp_ticks >= $since AND timestamp_ticks < $until AND ($model IS NULL OR model = $model COLLATE NOCASE) " +
                "AND ($connector IS NULL OR connector = $connector COLLATE NOCASE) AND ($profile IS NULL OR profile = $profile COLLATE NOCASE);",
                ("$since", since.UtcTicks), ("$until", until.UtcTicks), ("$model", model), ("$connector", connector), ("$profile", profile));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new UsageRecord(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) != 0,
                    reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                    decimal.Parse(reader.GetString(10), CultureInfo.InvariantCulture), decimal.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                    reader.IsDBNull(12) ? null : reader.GetString(12)));
            }

            return records;
        });
}
