using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>Oldest audit log entries kept. Default 365 days.</summary>
    public TimeSpan AuditRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How long a write waits for another writer (another router instance) before giving up. Default 5 s.</summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Registers the SQLite-backed state.</summary>
public static class SqliteStateBuilderExtensions
{
    /// <summary>
    /// Keeps model cooldowns, tool-call affinity, conversations' warm prompt caches, the activity log, feedback, usage,
    /// learned changes, connector incidents, the configuration history and the audit log in one SQLite file, so they survive restarts. Several router instances on one machine may share the file (cooldowns and affinity
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
        services.TryAddSingleton<IStateRetention>(provider => provider.GetRequiredService<SqliteRouterDatabase>());
        services.Replace(ServiceDescriptor.Singleton<ICooldownStore, SqliteCooldownStore>());
        services.Replace(ServiceDescriptor.Singleton<IToolCallAffinity, SqliteToolCallAffinity>());
        services.Replace(ServiceDescriptor.Singleton<IWarmCacheStore, SqliteWarmCaches>());
        services.Replace(ServiceDescriptor.Singleton<IRoutingActivityStore, SqliteActivityStore>());
        services.Replace(ServiceDescriptor.Singleton<IRoutingFeedbackStore, SqliteFeedbackStore>());
        services.Replace(ServiceDescriptor.Singleton<IRouterConfigurationHistory, SqliteConfigurationHistory>());
        services.Replace(ServiceDescriptor.Singleton<ITuningStore, SqliteTuningStore>());
        services.Replace(ServiceDescriptor.Singleton<IUsageLedger, SqliteUsageLedger>());
        services.Replace(ServiceDescriptor.Singleton<IStateValueStore, SqliteStateValues>());
        services.Replace(ServiceDescriptor.Singleton<IAuditLog, SqliteAuditLog>());
        return builder;
    }
}

/// <summary>
/// The shared database: schema, connections and failure handling. Every operation degrades to a fallback value when
/// the database is unavailable, so routing never fails because of state storage.
/// </summary>
internal sealed partial class SqliteRouterDatabase : IStateRetention
{
    // 2: usage.profile (routing profiles). 3: usage.subscription_value (subscriptions at API prices). Older files are
    // migrated in place.
    private const int SchemaVersion = 3;

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

            // The activity log holds prompt previews. SQLite gives the -wal and -shm files the database's mode.
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            {
                OwnerOnly.Restrict(file);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Available = false;
            LogUnavailable(_logger, path, ex.Message);
        }
    }

    public SqliteStateOptions Options { get; }

    private volatile StateRetentionOptions _retention = new();

    /// <summary>Oldest activity and feedback kept: <c>State:Retention:ActivityDays</c>, or <see cref="SqliteStateOptions.ActivityMaxAge"/>.</summary>
    public TimeSpan ActivityMaxAge => _retention.ActivityDays is int days ? TimeSpan.FromDays(days) : Options.ActivityMaxAge;

    /// <summary>Most activity entries kept: <c>State:Retention:ActivityEntries</c>, or <see cref="SqliteStateOptions.ActivityMaxEntries"/>.</summary>
    public int ActivityMaxEntries => _retention.ActivityEntries ?? Options.ActivityMaxEntries;

    /// <summary>Oldest usage kept: <c>State:Retention:UsageDays</c>, or <see cref="SqliteStateOptions.UsageRetention"/>.</summary>
    public TimeSpan UsageRetention => _retention.UsageDays is int days ? TimeSpan.FromDays(days) : Options.UsageRetention;

    /// <summary>Most configuration versions kept: <c>State:Retention:ConfigurationVersions</c>, or <see cref="SqliteStateOptions.HistoryMaxEntries"/>.</summary>
    public int HistoryMaxEntries => _retention.ConfigurationVersions ?? Options.HistoryMaxEntries;

    /// <summary>Oldest audit log entries kept: <c>State:Retention:AuditDays</c>, or <see cref="SqliteStateOptions.AuditRetention"/>.</summary>
    public TimeSpan AuditRetention => _retention.AuditDays is int days ? TimeSpan.FromDays(days) : Options.AuditRetention;

    /// <summary>Takes the retention of the configuration just applied; records past it go at the next prune.</summary>
    public void Apply(StateRetentionOptions retention) => _retention = retention ?? new StateRetentionOptions();

    public TimeProvider Time { get; }

    public string Location { get; }

    /// <summary>Whether the database opened; when not, every store keeps its state in memory only.</summary>
    public bool Available { get; private set; }

    private long _rewrites;

    /// <summary>
    /// How often this process rewrote the file (VACUUM): that can renumber the rows of tables without an integer
    /// primary key, so anything that remembers row ids must read again.
    /// </summary>
    public long Rewrites => Interlocked.Read(ref _rewrites);

    /// <summary>Notes a VACUUM (<see cref="Rewrites"/>).</summary>
    public void Rewritten() => Interlocked.Increment(ref _rewrites);

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

    /// <summary>
    /// Checkpoints the write-ahead log into the database and truncates it. While another connection is still reading,
    /// its snapshot pins the log and SQLite gives up at once (it reports busy rather than waiting): with
    /// <paramref name="wait"/>, the checkpoint is tried again until the log is empty or that time has passed. Returns
    /// whether the log was emptied.
    /// </summary>
    public static bool TruncateLog(SqliteConnection connection, TimeSpan wait = default)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            using (SqliteCommand checkpoint = Command(connection, "PRAGMA wal_checkpoint(TRUNCATE);"))
            using (SqliteDataReader result = checkpoint.ExecuteReader())
            {
                // One row: busy (1 when a reader or writer kept it from completing), then the log's frame counts.
                if (!result.Read() || result.GetInt64(0) == 0)
                {
                    return true;
                }
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= wait)
            {
                return false;
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Empties the write-ahead log after a purge, so what was deleted is in neither file: waits up to the busy timeout
    /// for readers to finish, and says so in the log when one did not.
    /// </summary>
    public void EmptyLogAfterPurge(SqliteConnection connection, string what)
    {
        if (!TruncateLog(connection, Options.BusyTimeout))
        {
            LogLogNotEmptied(_logger, what, Location, Options.BusyTimeout.TotalSeconds);
        }
    }

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
        try
        {
            connection.Open();

            // With WAL, NORMAL still never corrupts the file and loses nothing when the process crashes, but it does
            // not flush to disk on every commit. FULL did, holding the write lock for each flush: on Windows, where
            // flushes are slow, a burst of writes (two routers sharing the file) queued past the busy timeout and was
            // dropped.
            // secure_delete: a deleted row (a pruned or purged prompt preview) is overwritten with zeros, not left in a
            // free page where a copy of the file would still hold it.
            using (SqliteCommand pragmas = Command(connection, "PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON;"))
            {
                pragmas.ExecuteNonQuery();
            }

            return connection;
        }
        catch
        {
            // A corrupt file fails here, before the caller owns the connection: close it, or it keeps the file open.
            connection.Dispose();
            throw;
        }
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
            CREATE TABLE IF NOT EXISTS warm_caches (
                conversation TEXT NOT NULL, model TEXT NOT NULL COLLATE NOCASE, tokens INTEGER NOT NULL, expires_ticks INTEGER NOT NULL,
                PRIMARY KEY (conversation, model));
            CREATE INDEX IF NOT EXISTS warm_caches_expires ON warm_caches (expires_ticks);
            CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY, timestamp_ticks INTEGER NOT NULL, entry TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_timestamp ON activity (timestamp_ticks);
            CREATE TABLE IF NOT EXISTS feedback (id INTEGER PRIMARY KEY, timestamp_ticks INTEGER NOT NULL, entry_id INTEGER NOT NULL, item TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS feedback_timestamp ON feedback (timestamp_ticks);
            CREATE TABLE IF NOT EXISTS tuning_changes (id INTEGER PRIMARY KEY, item TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS usage (
                timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
                is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL, profile TEXT,
                subscription_value TEXT NOT NULL DEFAULT '0');
            CREATE INDEX IF NOT EXISTS usage_timestamp ON usage (timestamp_ticks);
            CREATE TABLE IF NOT EXISTS config_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_ticks INTEGER NOT NULL,
                source TEXT NOT NULL, actor TEXT, note TEXT, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_ticks INTEGER NOT NULL, actor TEXT NOT NULL, client TEXT,
                method TEXT NOT NULL, path TEXT NOT NULL, route TEXT, status INTEGER NOT NULL, summary TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS audit_timestamp ON audit (timestamp_ticks);
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
            if (version is "1" or "2")
            {
                // Only the columns the file lacks: a file with no usage table yet just got the current one above.
                string add = (HasColumn(connection, transaction, "usage", "profile") ? string.Empty : "ALTER TABLE usage ADD COLUMN profile TEXT;")
                    + (HasColumn(connection, transaction, "usage", "subscription_value") ? string.Empty : "ALTER TABLE usage ADD COLUMN subscription_value TEXT NOT NULL DEFAULT '0';");
                using SqliteCommand migrate = Command(connection, add + "UPDATE meta SET value = '3' WHERE key = 'schema_version';");
                migrate.Transaction = transaction;
                migrate.ExecuteNonQuery();
            }
            else if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException($"Unsupported state schema version {version}.");
            }
        }

        bool scrubbed = MaskHistory(connection, transaction);
        transaction.Commit();

        if (scrubbed)
        {
            // The plain-text documents are still in free pages and the WAL until the file is rewritten.
            using SqliteCommand vacuum = Command(connection, "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
            vacuum.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Masks the secrets in configuration history written before the history was masked: those documents kept keys
    /// inherited from environment variables and user-secrets in plain text. Runs once; returns whether anything changed.
    /// </summary>
    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand command = Command(connection, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;", ("$table", table), ("$column", column));
        command.Transaction = transaction;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool MaskHistory(SqliteConnection connection, SqliteTransaction transaction)
    {
        const string Done = "history_masked";
        using (SqliteCommand check = Command(connection, "SELECT value FROM meta WHERE key = $key;", ("$key", Done)))
        {
            check.Transaction = transaction;
            if (check.ExecuteScalar() is not null)
            {
                return false;
            }
        }

        var rows = new List<(long Id, string Document)>();
        using (SqliteCommand read = Command(connection, "SELECT id, document FROM config_history;"))
        {
            read.Transaction = transaction;
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        bool changed = false;
        foreach ((long id, string document) in rows)
        {
            string? masked;
            try
            {
                masked = RouterConfiguration.FromJson(document).Masked().ToJson();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                masked = null; // unreadable: it cannot be masked, so it goes
            }

            if (masked == document)
            {
                continue;
            }

            using SqliteCommand write = masked is null
                ? Command(connection, "DELETE FROM config_history WHERE id = $id;", ("$id", id))
                : Command(connection, "UPDATE config_history SET document = $document WHERE id = $id;", ("$id", id), ("$document", masked));
            write.Transaction = transaction;
            write.ExecuteNonQuery();
            changed = true;
        }

        using (SqliteCommand mark = Command(connection, "INSERT INTO meta (key, value) VALUES ($key, '1');", ("$key", Done)))
        {
            mark.Transaction = transaction;
            mark.ExecuteNonQuery();
        }

        return changed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database {Path} cannot be used ({Error}); cooldowns, tool-call affinity, activity and configuration history stay in memory and are lost on restart.")]
    private static partial void LogUnavailable(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database: {What} failed for {Path} ({Error}); continuing in memory.")]
    private static partial void LogOperationFailed(ILogger logger, string what, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database: after {What}, {Path}-wal could not be emptied within {Seconds} s because another connection kept reading; what was deleted stays in that file until the next checkpoint.")]
    private static partial void LogLogNotEmptied(ILogger logger, string what, string path, double seconds);
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

/// <summary>
/// Conversations' warm prompt caches that survive restarts: an in-memory copy in front of the database, so a
/// conversation that spans a restart (or moves to another instance sharing the file) still weighs its warm cache.
/// Expired entries are pruned as new ones are written.
/// </summary>
internal sealed class SqliteWarmCaches(SqliteRouterDatabase database) : IWarmCacheStore
{
    private readonly WarmCacheStore _cache = new(database.Time);
    private int _writes;

    public IReadOnlyList<WarmCache> Lookup(string conversation)
    {
        IReadOnlyList<WarmCache> cached = _cache.Lookup(conversation);
        if (cached.Count > 0)
        {
            return cached;
        }

        IReadOnlyList<WarmCache> stored = database.Run<IReadOnlyList<WarmCache>>("reading warm prompt caches", [], connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "SELECT model, tokens, expires_ticks FROM warm_caches WHERE conversation = $conversation AND expires_ticks > $now;",
                ("$conversation", conversation), ("$now", database.Time.GetUtcNow().UtcTicks));
            using SqliteDataReader reader = command.ExecuteReader();
            var caches = new List<WarmCache>();
            while (reader.Read())
            {
                caches.Add(new WarmCache(reader.GetString(0), reader.GetInt32(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
            }

            return caches;
        });

        foreach (WarmCache cache in stored)
        {
            _cache.Remember(conversation, cache);
        }

        return stored;
    }

    public void Remember(string conversation, WarmCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache.Remember(conversation, cache);
        bool prune = Interlocked.Increment(ref _writes) % 200 == 0;
        database.Run("saving a warm prompt cache", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO warm_caches (conversation, model, tokens, expires_ticks) VALUES ($conversation, $model, $tokens, $expires) " +
                "ON CONFLICT(conversation, model) DO UPDATE SET model = excluded.model, tokens = excluded.tokens, expires_ticks = excluded.expires_ticks;" +
                (prune ? "DELETE FROM warm_caches WHERE expires_ticks <= $now;" : string.Empty),
                ("$conversation", conversation), ("$model", cache.Model), ("$tokens", cache.CachedTokens),
                ("$expires", cache.ExpiresAt.UtcTicks), ("$now", database.Time.GetUtcNow().UtcTicks));
            command.ExecuteNonQuery();
        });
    }

    public void Forget(string conversation, string? model = null)
    {
        _cache.Forget(conversation, model);
        database.Run("forgetting a warm prompt cache", connection =>
        {
            using SqliteCommand command = model is null
                ? SqliteRouterDatabase.Command(connection, "DELETE FROM warm_caches WHERE conversation = $conversation;", ("$conversation", conversation))
                : SqliteRouterDatabase.Command(
                    connection, "DELETE FROM warm_caches WHERE conversation = $conversation AND model = $model;", ("$conversation", conversation), ("$model", model));
            command.ExecuteNonQuery();
        });
    }
}

/// <summary>Feedback about routing decisions on disk, kept as long as the activity it is about.</summary>
internal sealed class SqliteFeedbackStore(SqliteRouterDatabase database) : IRoutingFeedbackStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _writes;

    public void Add(RoutingFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        bool prune = Interlocked.Increment(ref _writes) % 100 == 0;
        database.Run("saving feedback", connection =>
        {
            // The row id is SQLite's: routers sharing the file number their feedback independently.
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO feedback (timestamp_ticks, entry_id, item) VALUES ($ts, $entry, $item);",
                ("$ts", feedback.Timestamp.UtcTicks), ("$entry", feedback.EntryId), ("$item", JsonSerializer.Serialize(feedback, Json)));
            command.ExecuteNonQuery();
            if (prune)
            {
                using SqliteCommand old = SqliteRouterDatabase.Command(
                    connection, "DELETE FROM feedback WHERE timestamp_ticks < $oldest;",
                    ("$oldest", (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks));
                old.ExecuteNonQuery();
            }
        });
    }

    public IReadOnlyList<RoutingFeedback> Recent(int limit) => Read(
        "SELECT item FROM feedback ORDER BY id DESC LIMIT $limit;", ("$limit", Math.Max(0, limit)));

    public IReadOnlyList<RoutingFeedback> Between(DateTimeOffset since, DateTimeOffset until, int limit) => Read(
        "SELECT item FROM feedback WHERE timestamp_ticks >= $since AND timestamp_ticks < $until ORDER BY id LIMIT $limit;",
        ("$since", since.UtcTicks), ("$until", until.UtcTicks), ("$limit", Math.Max(0, limit)));

    private IReadOnlyList<RoutingFeedback> Read(string sql, params (string Name, object? Value)[] parameters) =>
        database.Run<IReadOnlyList<RoutingFeedback>>("reading feedback", [], connection =>
        {
            var items = new List<RoutingFeedback>();
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, sql, parameters);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<RoutingFeedback>(reader.GetString(0), Json) is { } item)
                {
                    items.Add(item);
                }
            }

            return items;
        });
}

/// <summary>
/// What automatic tuning learned, and its state (paused, last run), on disk. Read from memory; a database that fails
/// leaves the changes in memory for this run.
/// </summary>
internal sealed class SqliteTuningStore : ITuningStore
{
    private const string StateKey = "tuning_state";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteRouterDatabase _database;
    private readonly InMemoryTuningStore _memory = new();

    public SqliteTuningStore(SqliteRouterDatabase database)
    {
        _database = database;
        IReadOnlyList<TuningChange> changes = database.Run<IReadOnlyList<TuningChange>>("reading learned changes", [], connection =>
        {
            var items = new List<TuningChange>();
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "SELECT item FROM tuning_changes ORDER BY id;");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<TuningChange>(reader.GetString(0), Json) is { } item)
                {
                    items.Add(item);
                }
            }

            return items;
        });
        foreach (TuningChange change in changes)
        {
            _memory.Save(change);
        }

        string? state = database.Run<string?>("reading the tuning state", null, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "SELECT value FROM meta WHERE key = $key;", ("$key", StateKey));
            return command.ExecuteScalar() as string;
        });
        if (state is not null && JsonSerializer.Deserialize<TuningState>(state, Json) is { } loaded)
        {
            _memory.SaveState(loaded);
        }
    }

    public IReadOnlyList<TuningChange> Changes() => _memory.Changes();

    public void Save(TuningChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _memory.Save(change);
        _database.Run("saving a learned change", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "INSERT OR REPLACE INTO tuning_changes (id, item) VALUES ($id, $item);", ("$id", change.Id), ("$item", JsonSerializer.Serialize(change, Json)));
            command.ExecuteNonQuery();
        });
    }

    public TuningState LoadState() => _memory.LoadState();

    public void SaveState(TuningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _memory.SaveState(state);
        _database.Run("saving the tuning state", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);", ("$key", StateKey), ("$value", JsonSerializer.Serialize(state, Json)));
            command.ExecuteNonQuery();
        });
    }
}

/// <summary>Named values in the <c>meta</c> table (keys prefixed with <c>value:</c>).</summary>
internal sealed class SqliteStateValues(SqliteRouterDatabase database) : IStateValueStore
{
    private const string Prefix = "value:";

    public string? Load(string key) => database.Run<string?>("reading a saved value", null, connection =>
    {
        using SqliteCommand command = SqliteRouterDatabase.Command(connection, "SELECT value FROM meta WHERE key = $key;", ("$key", Prefix + key));
        return command.ExecuteScalar() as string;
    });

    public void Save(string key, string value) => database.Run("saving a value", connection =>
    {
        using SqliteCommand command = SqliteRouterDatabase.Command(
            connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);", ("$key", Prefix + key), ("$value", value));
        command.ExecuteNonQuery();
    });
}

/// <summary>
/// The activity log on disk, capped by count and age. Entry ids come from a counter in the file, in blocks, so routers
/// sharing it never hand out the same id.
/// </summary>
internal sealed class SqliteActivityStore(SqliteRouterDatabase database) : IRoutingActivityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _writes;

    // A plain INSERT: an id stored twice is a bug, and fails (with a warning) rather than replacing another request's entry.
    public void Append(RoutingActivityEntry entry) => Store(entry, "INSERT INTO activity (id, timestamp_ticks, entry) VALUES ($id, $ts, $entry);");

    public void Replace(RoutingActivityEntry entry) => Store(entry, "INSERT OR REPLACE INTO activity (id, timestamp_ticks, entry) VALUES ($id, $ts, $entry);");

    public long? ReserveIds(int count, long floor) =>
        database.Run<long?>("reserving activity ids", null, connection =>
        {
            // One statement, so atomic among routers sharing the file. Never below the newest stored id: a file
            // written before the counter existed, or by a router that could not reserve.
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO meta (key, value) VALUES ('activity_next_id', max($floor, (SELECT COALESCE(MAX(id), -1) + 1 FROM activity)) + $count) " +
                "ON CONFLICT(key) DO UPDATE SET value = max(CAST(value AS INTEGER), $floor, (SELECT COALESCE(MAX(id), -1) + 1 FROM activity)) + $count " +
                "RETURNING CAST(value AS INTEGER) - $count;",
                ("$floor", floor), ("$count", count));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    private void Store(RoutingActivityEntry entry, string sql)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool prune = Interlocked.Increment(ref _writes) % 100 == 0;
        database.Run("saving activity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, sql, ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", JsonSerializer.Serialize(entry, Json)));
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
                ("$oldest", (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks), ("$limit", Math.Max(0, limit)));
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
        using (SqliteCommand command = SqliteRouterDatabase.Command(
            connection,
            "DELETE FROM activity WHERE timestamp_ticks < $oldest;" +
            "DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT $max);",
            ("$oldest", (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks), ("$max", database.ActivityMaxEntries)))
        {
            command.ExecuteNonQuery();
        }

        // The write-ahead log still holds the pages as they were before; move them into the file and empty it.
        SqliteRouterDatabase.TruncateLog(connection);
    }

    public int Purge(DateTimeOffset since, DateTimeOffset until) =>
        database.Run("purging activity", 0, connection =>
        {
            int removed;
            using (SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "DELETE FROM activity WHERE timestamp_ticks >= $since AND timestamp_ticks < $until;",
                ("$since", since.UtcTicks), ("$until", until.UtcTicks)))
            {
                removed = command.ExecuteNonQuery();
            }

            // secure_delete zeroes what this delete freed; VACUUM rewrites the file without free pages left by deletes
            // from before it was on, and the log is emptied last, once no reader still holds it.
            using (SqliteCommand vacuum = SqliteRouterDatabase.Command(connection, "VACUUM;"))
            {
                vacuum.ExecuteNonQuery();
            }

            database.Rewritten();

            database.EmptyLogAfterPurge(connection, "purging activity");
            return removed;
        });
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
                ("$document", document.ToJson()), ("$max", database.HistoryMaxEntries));
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

/// <summary>
/// Usage records on disk, for the dashboard's usage views and for budgets across restarts. Totals are added up in SQL.
/// A budget checks its period's totals on every request, so the totals of a range that is still open (it ends in the
/// future) are kept in memory and brought up to date from the rows added since: those this router wrote, and those of
/// other routers sharing the file.
/// </summary>
internal sealed class SqliteUsageLedger(SqliteRouterDatabase database) : IUsageLedger
{
    private const string Columns =
        "SELECT timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile, subscription_value FROM usage ";

    private const string Filters =
        "WHERE timestamp_ticks >= $since AND timestamp_ticks < $until AND ($model IS NULL OR model = $model COLLATE NOCASE) " +
        "AND ($connector IS NULL OR connector = $connector COLLATE NOCASE) AND ($profile IS NULL OR profile = $profile COLLATE NOCASE)";

    /// <summary>Most open ranges kept: a budget's period is one, and it changes only when the period does.</summary>
    private const int MaxOpenRanges = 64;

    /// <summary>How long an open range goes without being read again when this router wrote nothing: other routers' rows wait this long.</summary>
    private static readonly TimeSpan OthersWait = TimeSpan.FromSeconds(1);

    /// <summary>How often an open range is added up again from scratch, in case another router pruned or rewrote the file.</summary>
    private static readonly TimeSpan Recount = TimeSpan.FromHours(1);

    private readonly InMemoryUsageLedger _fallback = new();
    private readonly ConcurrentDictionary<(long Since, long Until, string? Model, string? Connector, string? Profile), OpenRange> _open = new();
    private int _writes;
    private long _written;

    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool prune = Interlocked.Increment(ref _writes) % 500 == 0;
        bool stored = database.Run("recording usage", false, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO usage (timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile, subscription_value) " +
                "VALUES ($ts, $model, $connector, $route, $client, $local, $input, $output, $cacheRead, $cacheWrite, $cost, $reference, $profile, $value);" +
                (prune ? "DELETE FROM usage WHERE timestamp_ticks < $oldest;" : string.Empty),
                ("$ts", record.Timestamp.UtcTicks), ("$model", record.Model), ("$connector", record.Connector), ("$route", record.Route),
                ("$client", record.Client), ("$local", record.IsLocal ? 1 : 0), ("$input", record.InputTokens), ("$output", record.OutputTokens),
                ("$cacheRead", record.CacheReadTokens), ("$cacheWrite", record.CacheWriteTokens),
                ("$cost", record.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$reference", record.ReferenceCostUsd.ToString(CultureInfo.InvariantCulture)),
                ("$oldest", (record.Timestamp - database.UsageRetention).UtcTicks), ("$profile", record.Profile),
                ("$value", record.SubscriptionValueUsd.ToString(CultureInfo.InvariantCulture)));
            command.ExecuteNonQuery();
            return true;
        });

        if (!stored)
        {
            _fallback.Record(record);
        }

        if (prune)
        {
            // A retention shorter than a budget's period deletes rows the open totals counted.
            _open.Clear();
        }

        Interlocked.Increment(ref _written);
    }

    public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        TotalsByModel(since, until, model, connector, profile).Aggregate(UsageTotals.Zero, (total, g) => total.Add(g.Totals));

    public IReadOnlyList<UsageGroup> TotalsByModel(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null)
    {
        IEnumerable<UsageGroup> stored = until > database.Time.GetUtcNow()
            ? Open(since, until, model, connector, profile)
            : database.Run<IReadOnlyList<UsageGroup>>("reading usage", [], connection => AddUp(connection, since, until, model, connector, profile, after: null, through: null));
        return Combine(stored.Concat(((IUsageLedger)_fallback).TotalsByModel(since, until, model, connector, profile) ?? []));
    }

    public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null) =>
        UsageSummaries.Group(Read(since, until, null, null, null), by, zone ?? TimeZoneInfo.Local)
            .Concat(_fallback.Summarize(since, until, by, zone))
            .GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First() : new UsageGroup(g.Key, g.Aggregate(UsageTotals.Zero, (total, x) => total.Add(x.Totals))))
            .ToArray();

    public IReadOnlyList<UsageRecord> Records(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        [.. Read(since, until, model, connector, profile), .. _fallback.Records(since, until, model, connector, profile)];

    private static UsageGroup[] Combine(IEnumerable<UsageGroup> groups) =>
        [.. groups.GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new UsageGroup(g.First().Key, g.Aggregate(UsageTotals.Zero, (total, x) => total.Add(x.Totals))))];

    /// <summary>The totals of a range that is still open: kept, and brought up to date from the rows added since.</summary>
    private UsageGroup[] Open(DateTimeOffset since, DateTimeOffset until, string? model, string? connector, string? profile)
    {
        if (_open.Count >= MaxOpenRanges)
        {
            _open.Clear();
        }

        OpenRange range = _open.GetOrAdd((since.UtcTicks, until.UtcTicks, model?.ToUpperInvariant(), connector?.ToUpperInvariant(), profile?.ToUpperInvariant()), _ => new OpenRange());
        lock (range)
        {
            long written = Interlocked.Read(ref _written);
            long rewrites = database.Rewrites;
            if (range.Through >= 0 && written == range.Written && rewrites == range.Rewrites && Stopwatch.GetElapsedTime(range.ReadAt) < OthersWait)
            {
                return range.Snapshot();
            }

            bool read = database.Run("reading usage", false, connection =>
            {
                long newest;
                using (SqliteCommand max = SqliteRouterDatabase.Command(connection, "SELECT COALESCE(MAX(rowid), 0) FROM usage;"))
                {
                    newest = Convert.ToInt64(max.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // Rows are only ever added at the end; a lower newest row id means rows were deleted or renumbered.
                bool recount = range.Through < 0 || newest < range.Through || rewrites != range.Rewrites || Stopwatch.GetElapsedTime(range.CountedAt) > Recount;
                if (recount)
                {
                    range.Totals.Clear();
                    range.CountedAt = Stopwatch.GetTimestamp();
                }

                foreach (UsageGroup added in AddUp(connection, since, until, model, connector, profile, after: recount ? null : range.Through, through: newest))
                {
                    range.Totals[added.Key] = range.Totals.TryGetValue(added.Key, out UsageTotals? before) ? before.Add(added.Totals) : added.Totals;
                }

                range.Through = newest;
                return true;
            });

            if (!read)
            {
                range.Through = -1;
                range.Totals.Clear();
                return [];
            }

            (range.Written, range.Rewrites, range.ReadAt) = (written, rewrites, Stopwatch.GetTimestamp());
            return range.Snapshot();
        }
    }

    /// <summary>Totals per model of the rows in the range, optionally only those with <paramref name="after"/> &lt; row id ≤ <paramref name="through"/>.</summary>
    private static List<UsageGroup> AddUp(
        SqliteConnection connection, DateTimeOffset since, DateTimeOffset until, string? model, string? connector, string? profile, long? after, long? through)
    {
        // Costs are stored as text, exact decimals; SUM would add them up as floating point.
        connection.CreateAggregate<string, decimal, string>(
            "decimal_sum", 0m, (sum, value) => sum + decimal.Parse(value, CultureInfo.InvariantCulture), sum => sum.ToString(CultureInfo.InvariantCulture), isDeterministic: true);
        using SqliteCommand command = SqliteRouterDatabase.Command(
            connection,
            "SELECT model, COUNT(*), SUM(input), SUM(output), SUM(cache_read), SUM(cache_write), decimal_sum(cost), decimal_sum(reference_cost), decimal_sum(subscription_value) " +
            "FROM usage " + Filters + " AND ($after IS NULL OR rowid > $after) AND ($through IS NULL OR rowid <= $through) GROUP BY model COLLATE NOCASE;",
            ("$since", since.UtcTicks), ("$until", until.UtcTicks), ("$model", model), ("$connector", connector), ("$profile", profile),
            ("$after", after), ("$through", through));
        using SqliteDataReader reader = command.ExecuteReader();
        var groups = new List<UsageGroup>();
        while (reader.Read())
        {
            groups.Add(new UsageGroup(reader.GetString(0), new UsageTotals(
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture), decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture))));
        }

        return groups;
    }

    private List<UsageRecord> Read(DateTimeOffset since, DateTimeOffset until, string? model, string? connector, string? profile) =>
        database.Run("reading usage", new List<UsageRecord>(), connection =>
        {
            var records = new List<UsageRecord>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, Columns + Filters + ";",
                ("$since", since.UtcTicks), ("$until", until.UtcTicks), ("$model", model), ("$connector", connector), ("$profile", profile));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new UsageRecord(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) != 0,
                    reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                    decimal.Parse(reader.GetString(10), CultureInfo.InvariantCulture), decimal.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                    reader.IsDBNull(12) ? null : reader.GetString(12), decimal.Parse(reader.GetString(13), CultureInfo.InvariantCulture)));
            }

            return records;
        });

    /// <summary>An open range's totals per model, counted up to row <see cref="Through"/> (-1: not counted).</summary>
    private sealed class OpenRange
    {
        public Dictionary<string, UsageTotals> Totals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long Through { get; set; } = -1;

        public long Written { get; set; }

        public long Rewrites { get; set; }

        public long ReadAt { get; set; }

        public long CountedAt { get; set; }

        public UsageGroup[] Snapshot() => [.. Totals.Select(t => new UsageGroup(t.Key, t.Value))];
    }
}

/// <summary>
/// The audit log on disk. Rows are only ever inserted; the only delete is of rows older than <c>State:Retention:AuditDays</c>
/// (365 days by default), as each new one is written. A database that fails keeps entries in memory for this run.
/// </summary>
internal sealed class SqliteAuditLog(SqliteRouterDatabase database) : IAuditLog
{
    private readonly InMemoryAuditLog _fallback = new();

    public AuditEntry Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        long id = database.Run<long>("recording the audit log", -1, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO audit (timestamp_ticks, actor, client, method, path, route, status, summary) " +
                "VALUES ($ts, $actor, $client, $method, $path, $route, $status, $summary);" +
                "DELETE FROM audit WHERE timestamp_ticks < $oldest;" +
                "SELECT last_insert_rowid();",
                ("$ts", entry.Timestamp.UtcTicks), ("$actor", entry.Actor), ("$client", entry.Client), ("$method", entry.Method),
                ("$path", entry.Path), ("$route", entry.Route), ("$status", entry.Status), ("$summary", entry.Summary),
                ("$oldest", (database.Time.GetUtcNow() - database.AuditRetention).UtcTicks));
            return (long)command.ExecuteScalar()!;
        });

        return id >= 0 ? entry with { Id = id } : _fallback.Append(entry);
    }

    public IReadOnlyList<AuditEntry> Query(DateTimeOffset? since, DateTimeOffset? until, int limit)
    {
        if (!database.Available)
        {
            return _fallback.Query(since, until, limit);
        }

        return database.Run<IReadOnlyList<AuditEntry>>("reading the audit log", _fallback.Query(since, until, limit), connection =>
        {
            var entries = new List<AuditEntry>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "SELECT id, timestamp_ticks, actor, client, method, path, route, status, summary FROM audit " +
                "WHERE timestamp_ticks >= $since AND timestamp_ticks < $until ORDER BY id DESC LIMIT $limit;",
                ("$since", since?.UtcTicks ?? 0L), ("$until", until?.UtcTicks ?? long.MaxValue), ("$limit", Math.Max(0, limit)));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new AuditEntry(
                    reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7), reader.GetString(8)));
            }

            return entries;
        });
    }
}
