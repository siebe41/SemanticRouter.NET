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
    /// are then shared); a database that cannot be opened or written falls back to memory, with a warning in the log and
    /// in the runtime's warnings. Before an older file is migrated, a copy is kept next to it
    /// (<c>semantic-router.db.v2.bak</c>), so the release that wrote it can be started on it again.
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
        services.TryAddSingleton<IStateStorageHealth>(provider => provider.GetRequiredService<SqliteRouterDatabase>());
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
internal sealed partial class SqliteRouterDatabase : IStateRetention, IStateStorageHealth
{
    // 2: usage.profile (routing profiles). 3: usage.subscription_value (subscriptions at API prices). 4: usage.id, so a
    // row keeps its number through VACUUM and the running budget totals read only the rows added since, and the costs
    // also in whole picodollars, so SQL adds them up exactly. Older files are migrated in place, after a copy of the
    // file is kept (BackupFile).
    public const int SchemaVersion = 4;

    // The usage table as schema version 4 has it.
    private const string UsageTable = """
        usage (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL, connector TEXT NOT NULL, route TEXT, client TEXT,
            is_local INTEGER NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL,
            cache_write INTEGER NOT NULL, cost TEXT NOT NULL, reference_cost TEXT NOT NULL, profile TEXT,
            subscription_value TEXT NOT NULL DEFAULT '0', cost_pico INTEGER, reference_pico INTEGER, value_pico INTEGER)
        """;

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
            int? from = Migrate(connection, path);
            Available = true;

            // The activity log holds prompt previews. SQLite gives the -wal and -shm files the database's mode.
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            {
                OwnerOnly.Restrict(file);
            }

            if (from is int version)
            {
                string backup = BackupFile(path, version);
                LogMigrated(_logger, path, version, SchemaVersion, backup);
            }
        }
        catch (NewerSchemaException ex)
        {
            Available = false;
            Problem = $"{ex.Message} {InMemoryConsequence}";
            LogUnavailable(_logger, path, ex.Message);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Available = false;
            Problem = $"The router's state database {path} cannot be used ({ex.Message}). {InMemoryConsequence}";
            LogUnavailable(_logger, path, ex.Message);
        }
    }

    /// <summary>What running without the database means, for <see cref="Problem"/>.</summary>
    private const string InMemoryConsequence =
        "Until it can, budgets count from zero, and usage, activity, feedback, cooldowns, learned changes, the configuration " +
        "history and the audit log are kept in memory only and lost on restart.";

    /// <summary>
    /// The copy of the database kept before it is migrated from <paramref name="version"/>: the release that wrote it can
    /// be started on it again. One per schema version; a later migration from the same version replaces it.
    /// </summary>
    public static string BackupFile(string path, int version) => $"{path}.v{version.ToString(CultureInfo.InvariantCulture)}.bak";

    /// <summary>
    /// The schema version recorded in the database at <paramref name="path"/>, read without changing the file;
    /// <see langword="null"/> when there is no file, or it cannot be read (opening it for use says why).
    /// </summary>
    public static int? StoredSchemaVersion(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using SqliteCommand read = Command(connection, "SELECT value FROM meta WHERE key = 'schema_version';");
            return read.ExecuteScalar() is string value && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ? version : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why this build cannot use the database at <paramref name="path"/> because a newer release wrote it (usually after
    /// a downgrade), with the way back; <see langword="null"/> when that is not the case. Read without changing the file.
    /// </summary>
    public static string? NewerSchemaProblem(string path) =>
        StoredSchemaVersion(path) is int version && version > SchemaVersion ? NewerSchema(System.IO.Path.GetFullPath(path), version) : null;

    private static string NewerSchema(string path, int version)
    {
        string backup = BackupFile(path, SchemaVersion);
        string wayBack = File.Exists(backup)
            ? $"Start the newer release again, or, to go back to this one, move {backup} (the copy made before the upgrade) to {path}; what was recorded since the upgrade is then lost."
            : $"Start the newer release again. No copy from before the upgrade was found ({backup}); to start this release with empty state, move {path} aside.";
        return $"The router's state database {path} was written by a newer SemanticRouter (state schema {version.ToString(CultureInfo.InvariantCulture)}; " +
               $"this version reads schema {SchemaVersion.ToString(CultureInfo.InvariantCulture)} and older). {wayBack}";
    }

    /// <summary>A database written by a newer release.</summary>
    private sealed class NewerSchemaException(string message) : Exception(message);

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

    /// <inheritdoc />
    public string? Problem { get; private set; }

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

    /// <summary>
    /// Reserves <paramref name="count"/> ids for rows of <paramref name="table"/> (<c>activity</c> or <c>feedback</c>),
    /// past every id reserved before and every id in the table, and returns the first; -1 when the database failed.
    /// The counter is in <c>meta</c>, so routers sharing the file never hand out the same id.
    /// </summary>
    public long ReserveIds(string table, int count) =>
        Run("reserving ids", -1L, connection =>
        {
            // One statement, so it is atomic. The table's newest id counts too: rows written before the counter existed.
            using SqliteCommand command = Command(
                connection,
                $"INSERT INTO meta (key, value) VALUES ($key, (SELECT COALESCE(MAX(id), 0) + 1 FROM {table}) + $count) " +
                $"ON CONFLICT(key) DO UPDATE SET value = MAX(CAST(value AS INTEGER), (SELECT COALESCE(MAX(id), 0) + 1 FROM {table})) + $count " +
                "RETURNING CAST(value AS INTEGER) - $count;",
                ("$key", table + "_next_id"), ("$count", count));
            return (long)command.ExecuteScalar()!;
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

    /// <summary>
    /// Creates the tables, or brings an older file up to <see cref="SchemaVersion"/> after keeping a copy of it
    /// (<see cref="BackupFile"/>). Returns the version it migrated from, or <see langword="null"/> when nothing was migrated.
    /// </summary>
    private static int? Migrate(SqliteConnection connection, string path)
    {
        // Checked before anything is written: a file from a newer release is left exactly as it is.
        int? stored = StoredVersion(connection);
        if (stored > SchemaVersion)
        {
            throw new NewerSchemaException(NewerSchema(path, stored.Value));
        }

        if (stored < SchemaVersion)
        {
            Backup(connection, BackupFile(path, stored.Value));
        }

        // WAL lets readers and one writer (possibly another instance) work at the same time.
        using (SqliteCommand wal = Command(connection, "PRAGMA journal_mode=WAL;"))
        {
            wal.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand create = Command(connection, $$"""
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
            CREATE TABLE IF NOT EXISTS {{UsageTable}};
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
            if (stored is 1 or 2 or 3)
            {
                // Only the columns the file lacks: a file with no usage table yet just got the current one above.
                string add = (HasColumn(connection, transaction, "usage", "profile") ? string.Empty : "ALTER TABLE usage ADD COLUMN profile TEXT;")
                    + (HasColumn(connection, transaction, "usage", "subscription_value") ? string.Empty : "ALTER TABLE usage ADD COLUMN subscription_value TEXT NOT NULL DEFAULT '0';");
                using SqliteCommand migrate = Command(connection, add + "UPDATE meta SET value = '4' WHERE key = 'schema_version';");
                migrate.Transaction = transaction;
                migrate.ExecuteNonQuery();
                if (!HasColumn(connection, transaction, "usage", "id"))
                {
                    RebuildUsage(connection, transaction);
                }
            }
            else if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException($"Unsupported state schema version {version}.");
            }
        }

        // After the migration, which rebuilds the usage table: budgets and the Usage page filter by model or connector.
        using (SqliteCommand indexes = Command(connection, """
            CREATE INDEX IF NOT EXISTS usage_model ON usage (model COLLATE NOCASE, timestamp_ticks);
            CREATE INDEX IF NOT EXISTS usage_connector ON usage (connector COLLATE NOCASE, timestamp_ticks);
            """))
        {
            indexes.Transaction = transaction;
            indexes.ExecuteNonQuery();
        }

        bool scrubbed = MaskHistory(connection, transaction);
        transaction.Commit();

        if (scrubbed)
        {
            // The plain-text documents are still in free pages and the WAL until the file is rewritten.
            using SqliteCommand vacuum = Command(connection, "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
            vacuum.ExecuteNonQuery();
        }

        return stored < SchemaVersion ? stored : null;
    }

    /// <summary>The schema version the file records; <see langword="null"/> for a new file, which has no meta table yet.</summary>
    private static int? StoredVersion(SqliteConnection connection)
    {
        using SqliteCommand exists = Command(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';");
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return null;
        }

        using SqliteCommand read = Command(connection, "SELECT value FROM meta WHERE key = 'schema_version';");
        string? value = read.ExecuteScalar() as string;
        return value is null ? null
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ? version
            : throw new InvalidDataException($"Unsupported state schema version {value}.");
    }

    /// <summary>
    /// Copies the database, as it is before the migration, to <paramref name="backup"/>: written next to it and then
    /// moved into place, so a copy that was cut short never replaces a good one. It holds prompt previews, so only the
    /// owner can read it.
    /// </summary>
    private static void Backup(SqliteConnection connection, string backup)
    {
        string temp = backup + ".tmp";
        File.Delete(temp);
        using (SqliteCommand copy = Command(connection, "VACUUM INTO $file;", ("$file", temp)))
        {
            copy.ExecuteNonQuery();
        }

        OwnerOnly.Restrict(temp);
        File.Move(temp, backup, overwrite: true);
    }

    /// <summary>
    /// Copies the usage rows into the version 4 table, in their order, and fills in their costs in picodollars. Part of
    /// the migration's transaction, so a failure leaves the file as it was.
    /// </summary>
    private static void RebuildUsage(SqliteConnection connection, SqliteTransaction transaction)
    {
        const string Columns = "timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile, subscription_value";
        using (SqliteCommand copy = Command(connection, $"""
            CREATE TABLE {UsageTable.Replace("usage (", "usage_v4 (", StringComparison.Ordinal)};
            INSERT INTO usage_v4 ({Columns}) SELECT {Columns} FROM usage ORDER BY rowid;
            DROP TABLE usage;
            ALTER TABLE usage_v4 RENAME TO usage;
            CREATE INDEX usage_timestamp ON usage (timestamp_ticks);
            """))
        {
            copy.Transaction = transaction;
            copy.ExecuteNonQuery();
        }

        var rows = new List<(long Id, long Cost, long Reference, long Value)>();
        using (SqliteCommand read = Command(connection, "SELECT id, cost, reference_cost, subscription_value FROM usage;"))
        {
            read.Transaction = transaction;
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                // A cost that is not a whole number of picodollars (or not a number) stays text only, and is added up from there.
                if (decimal.TryParse(reader.GetString(1), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal cost)
                    && decimal.TryParse(reader.GetString(2), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal reference)
                    && decimal.TryParse(reader.GetString(3), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
                    && SqliteUsageLedger.Picodollars(cost, reference, value) is ({ } c, { } r, { } v))
                {
                    rows.Add((reader.GetInt64(0), c, r, v));
                }
            }
        }

        using SqliteCommand update = Command(
            connection, "UPDATE usage SET cost_pico = $cost, reference_pico = $reference, value_pico = $value WHERE id = $id;",
            ("$cost", 0L), ("$reference", 0L), ("$value", 0L), ("$id", 0L));
        update.Transaction = transaction;
        foreach ((long id, long cost, long reference, long value) in rows)
        {
            update.Parameters["$cost"].Value = cost;
            update.Parameters["$reference"].Value = reference;
            update.Parameters["$value"].Value = value;
            update.Parameters["$id"].Value = id;
            update.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand command = Command(connection, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;", ("$table", table), ("$column", column));
        command.Transaction = transaction;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Masks the secrets in configuration history written before the history was masked: those documents kept keys
    /// inherited from environment variables and user-secrets in plain text. Runs once; returns whether anything changed.
    /// </summary>
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Router state database {Path} was migrated from schema {From} to {To}; the file as it was is kept as {Backup}.")]
    private static partial void LogMigrated(ILogger logger, string path, int from, int to, string backup);

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
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO feedback (id, timestamp_ticks, entry_id, item) VALUES ($id, $ts, $entry, $item);",
                ("$id", feedback.Id), ("$ts", feedback.Timestamp.UtcTicks), ("$entry", feedback.EntryId), ("$item", JsonSerializer.Serialize(feedback, Json)));
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

    public long ReserveIds(int count) => database.ReserveIds("feedback", count);

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
            // A plain insert: an id that is already there is a bug to hear about, not an entry to overwrite.
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO activity (id, timestamp_ticks, entry) VALUES ($id, $ts, $entry);",
                ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", JsonSerializer.Serialize(entry, Json)));
            command.ExecuteNonQuery();
            if (prune)
            {
                Prune(connection);
            }
        });
    }

    public void Replace(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        database.Run("saving activity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "UPDATE activity SET timestamp_ticks = $ts, entry = $entry WHERE id = $id;",
                ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", JsonSerializer.Serialize(entry, Json)));
            command.ExecuteNonQuery();
        });
    }

    public long ReserveIds(int count) => database.ReserveIds("activity", count);

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
/// The totals of the ranges asked for most recently (a budget's current period) are also kept in memory: what this
/// instance records is added to them, and when one is asked for and <see cref="RefreshInterval"/> has passed (or a row
/// it does not know of was written meanwhile), only the rows added since are read, so instances sharing the file agree
/// within that time and a budget check does not read the period's usage on every request.
/// </summary>
internal sealed class SqliteUsageLedger(SqliteRouterDatabase database) : IUsageLedger
{
    /// <summary>How long running totals may go without reading rows other instances added.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    // Costs are kept as text, exactly, and as whole picodollars too, so SQL can add them up exactly.
    private const decimal Pico = 0.000000000001m;
    private const decimal MaxUsd = 9_000_000m; // in picodollars, still within a long
    private readonly InMemoryUsageLedger _fallback = new();
    private readonly RunningUsageTotals<Running> _running = new();
    private readonly Lock _gate = new();
    private int _writes;
    private int _reads;

    /// <summary>How many times totals were read from the database, for tests.</summary>
    internal int Reads => Volatile.Read(ref _reads);

    private SqliteRouterDatabase Database => database;

    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool prune = Interlocked.Increment(ref _writes) % 500 == 0;
        DateTimeOffset oldest = record.Timestamp - database.UsageRetention;
        (long? cost, long? reference, long? value) = Picodollars(record.CostUsd, record.ReferenceCostUsd, record.SubscriptionValueUsd);
        long id = database.Run<long>("recording usage", -1, connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO usage (timestamp_ticks, model, connector, route, client, is_local, input, output, cache_read, cache_write, cost, reference_cost, profile, subscription_value, cost_pico, reference_pico, value_pico) " +
                "VALUES ($ts, $model, $connector, $route, $client, $local, $input, $output, $cacheRead, $cacheWrite, $cost, $reference, $profile, $value, $costPico, $referencePico, $valuePico);" +
                (prune ? "DELETE FROM usage WHERE timestamp_ticks < $oldest;" : string.Empty) +
                "SELECT last_insert_rowid();",
                ("$ts", record.Timestamp.UtcTicks), ("$model", record.Model), ("$connector", record.Connector), ("$route", record.Route),
                ("$client", record.Client), ("$local", record.IsLocal ? 1 : 0), ("$input", record.InputTokens), ("$output", record.OutputTokens),
                ("$cacheRead", record.CacheReadTokens), ("$cacheWrite", record.CacheWriteTokens),
                ("$cost", record.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$reference", record.ReferenceCostUsd.ToString(CultureInfo.InvariantCulture)),
                ("$oldest", oldest.UtcTicks), ("$profile", record.Profile),
                ("$value", record.SubscriptionValueUsd.ToString(CultureInfo.InvariantCulture)),
                ("$costPico", cost), ("$referencePico", reference), ("$valuePico", value));
            return (long)command.ExecuteScalar()!;
        });

        if (id < 0)
        {
            _fallback.Record(record);
            return;
        }

        KeyValuePair<UsageRange, Running>[] running;
        lock (_gate)
        {
            running = [.. _running.Entries];
        }

        foreach ((UsageRange range, Running totals) in running)
        {
            totals.Recorded(id, range.Covers(record) ? record : null, prune && range.Since < oldest);
        }
    }

    public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        ModelTotals(since, until, UsageGrouping.Model, null, model, connector, profile).Aggregate(UsageTotals.Zero, (total, t) => total.Add(t.Totals));

    public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null) =>
        UsageSummaries.Group(ModelTotals(since, until, by, zone), by);

    public IReadOnlyList<UsageModelTotals> ModelTotals(
        DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null, string? model = null, string? connector = null, string? profile = null)
    {
        var range = new UsageRange(since, until, model, connector, profile);
        IReadOnlyList<UsageModelTotals> stored;
        if (by == UsageGrouping.Model && database.Available)
        {
            Running running;
            lock (_gate)
            {
                running = _running.GetOrAdd(range, _ => new Running());
            }

            stored = running.Current(this, range);
        }
        else
        {
            stored = Read(range, by, zone ?? TimeZoneInfo.Local, after: null)?.Totals ?? [];
        }

        IReadOnlyList<UsageModelTotals> unstored = _fallback.ModelTotals(since, until, by, zone, model, connector, profile);
        if (unstored.Count == 0)
        {
            return stored;
        }

        var merged = new UsageModelTotalsBuilder();
        merged.Add(stored);
        merged.Add(unstored);
        return merged.ToList();
    }

    public IReadOnlyList<UsageRecord> Records(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        [.. ReadRecords(new UsageRange(since, until, model, connector, profile)), .. _fallback.Records(since, until, model, connector, profile)];

    /// <summary>Costs in whole picodollars, or nothing when one of them is not a whole number of them (or too large).</summary>
    internal static (long? Cost, long? Reference, long? Value) Picodollars(decimal cost, decimal reference, decimal value) =>
        ToPico(cost) is { } c && ToPico(reference) is { } r && ToPico(value) is { } v ? (c, r, v) : (null, null, null);

    private static long? ToPico(decimal usd)
    {
        if (usd is > MaxUsd or < -MaxUsd)
        {
            return null;
        }

        decimal pico = usd / Pico;
        return pico == decimal.Truncate(pico) ? (long)pico : null;
    }

    // Added up in two parts (micro and picodollars) so that the sums cannot overflow.
    private static decimal FromPico(SqliteDataReader reader, int micro) =>
        Normalize(((reader.IsDBNull(micro) ? 0 : reader.GetInt64(micro)) * 0.000001m) + ((reader.IsDBNull(micro + 1) ? 0 : reader.GetInt64(micro + 1)) * Pico));

    // Without trailing zeros: the sum of 0.25 and 0.25 is 0.5, not 0.500000000000.
    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>
    /// Totals per group and model in <paramref name="range"/>, and the newest row id they include, from one snapshot of
    /// the file; with <paramref name="after"/>, only of the rows after that id. <see langword="null"/> when the database failed.
    /// </summary>
    private (IReadOnlyList<UsageModelTotals> Totals, long Newest)? Read(UsageRange range, UsageGrouping by, TimeZoneInfo zone, long? after) =>
        database.Run<(IReadOnlyList<UsageModelTotals>, long)?>("reading usage", null, connection =>
        {
            Interlocked.Increment(ref _reads);
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            long newest;
            using (SqliteCommand max = SqliteRouterDatabase.Command(connection, "SELECT COALESCE(MAX(id), 0) FROM usage;"))
            {
                max.Transaction = snapshot;
                newest = (long)max.ExecuteScalar()!;
            }

            string[]? days = null;
            string from = "usage u";
            if (by == UsageGrouping.Day)
            {
                (string Key, long Start, long End)[]? spans = Days(connection, snapshot, range, zone);
                if (spans is null)
                {
                    // A day of this time zone does not start where it should (or there are too many): add them up here.
                    var builder = UsageModelTotalsBuilder.Of(ReadRecords(range, connection, snapshot), by, zone);
                    return (builder.ToList(), newest);
                }

                if (spans.Length == 0)
                {
                    return ([], newest);
                }

                days = [.. spans.Select(d => d.Key)];
                from = "(VALUES " + string.Join(", ", spans.Select((d, i) => string.Create(CultureInfo.InvariantCulture, $"({i}, {d.Start}, {d.End})"))) + ") d " +
                    "JOIN usage u ON u.timestamp_ticks >= d.column2 AND u.timestamp_ticks < d.column3";
            }

            string key = by switch
            {
                UsageGrouping.Model => "u.model",
                UsageGrouping.Connector => "u.connector",
                UsageGrouping.Route => "u.route",
                UsageGrouping.Client => "u.client",
                UsageGrouping.Profile => "u.profile",
                UsageGrouping.Location => "u.is_local",
                _ => "d.column1",
            };
            string where = Where(range, after);
            var totals = new UsageModelTotalsBuilder();
            bool inexact = false;
            using (SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                $"SELECT {key}, u.model, COUNT(*), SUM(u.input), SUM(u.output), SUM(u.cache_read), SUM(u.cache_write), " +
                "SUM(u.cost_pico / 1000000), SUM(u.cost_pico % 1000000), SUM(u.reference_pico / 1000000), SUM(u.reference_pico % 1000000), " +
                "SUM(u.value_pico / 1000000), SUM(u.value_pico % 1000000), COUNT(*) - COUNT(u.cost_pico) " +
                $"FROM {from} WHERE {where} GROUP BY {key}, u.model;",
                Parameters(range, after)))
            {
                command.Transaction = snapshot;
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    totals.Add(KeyOf(reader, by, days), reader.GetString(1), new UsageTotals(
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                        FromPico(reader, 7), FromPico(reader, 9), FromPico(reader, 11)));
                    inexact |= reader.GetInt64(13) > 0;
                }
            }

            if (inexact)
            {
                // The few rows whose costs are not whole picodollars: their tokens are counted above, their costs here.
                using SqliteCommand command = SqliteRouterDatabase.Command(
                    connection, $"SELECT {key}, u.model, u.cost, u.reference_cost, u.subscription_value FROM {from} WHERE {where} AND u.cost_pico IS NULL;",
                    Parameters(range, after));
                command.Transaction = snapshot;
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    totals.Add(KeyOf(reader, by, days), reader.GetString(1), UsageTotals.Zero with
                    {
                        CostUsd = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        ReferenceCostUsd = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        SubscriptionValueUsd = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    });
                }
            }

            snapshot.Commit();
            return (totals.ToList(), newest);
        });

    private static string KeyOf(SqliteDataReader reader, UsageGrouping by, string[]? days) => by switch
    {
        UsageGrouping.Location => reader.GetInt64(0) != 0 ? "local" : "cloud",
        UsageGrouping.Day => days![reader.GetInt32(0)],
        _ when !reader.IsDBNull(0) => reader.GetString(0),
        UsageGrouping.Route => UsageSummaries.NoRoute,
        UsageGrouping.Client => UsageSummaries.NoClient,
        _ => UsageSummaries.NoProfile,
    };

    // Only the filters that apply, so SQLite can use the model and connector indexes. After a row id, the other terms
    // are kept from using an index (the unary +): the rows after it are few, and the id finds them.
    private static string Where(UsageRange range, long? after)
    {
        string plain = after is null ? string.Empty : "+";
        var terms = new List<string> { $"{plain}u.timestamp_ticks >= $since", $"{plain}u.timestamp_ticks < $until" };
        if (after is not null)
        {
            terms.Insert(0, "u.id > $after");
        }

        if (range.Model is not null)
        {
            terms.Add($"{plain}u.model = $model COLLATE NOCASE");
        }

        if (range.Connector is not null)
        {
            terms.Add($"{plain}u.connector = $connector COLLATE NOCASE");
        }

        if (range.Profile is not null)
        {
            terms.Add($"{plain}u.profile = $profile COLLATE NOCASE");
        }

        return string.Join(" AND ", terms);
    }

    private static (string Name, object? Value)[] Parameters(UsageRange range, long? after) =>
    [
        ("$since", range.Since.UtcTicks), ("$until", range.Until.UtcTicks), ("$after", after ?? 0L),
        ("$model", range.Model), ("$connector", range.Connector), ("$profile", range.Profile),
    ];

    /// <summary>
    /// The days of <paramref name="zone"/> that have usage in <paramref name="range"/>, as tick ranges; <see langword="null"/>
    /// when a day does not start at the instant its local midnight converts to, or there are too many to list.
    /// </summary>
    private static (string Key, long Start, long End)[]? Days(SqliteConnection connection, SqliteTransaction snapshot, UsageRange range, TimeZoneInfo zone)
    {
        using SqliteCommand command = SqliteRouterDatabase.Command(
            connection,
            "SELECT (SELECT MIN(timestamp_ticks) FROM usage WHERE timestamp_ticks >= $since AND timestamp_ticks < $until), " +
            "(SELECT MAX(timestamp_ticks) FROM usage WHERE timestamp_ticks >= $since AND timestamp_ticks < $until);",
            ("$since", range.Since.UtcTicks), ("$until", range.Until.UtcTicks));
        command.Transaction = snapshot;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return [];
        }

        var first = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
        var last = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero);
        var days = new List<(string Key, long Start, long End)>();
        DateTime day = TimeZoneInfo.ConvertTime(first, zone).Date;
        DateTimeOffset start = StartOf(day, zone);
        while (start <= last)
        {
            DateTime next = day.AddDays(1);
            DateTimeOffset end = StartOf(next, zone);
            string key = UsageSummaries.DayKey(start, zone);
            if (days.Count >= 5000 || end <= start || key != UsageSummaries.DayKey(end.AddTicks(-1), zone)
                || key != day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || UsageSummaries.DayKey(start.AddTicks(-1), zone) == key)
            {
                return null;
            }

            days.Add((key, start.UtcTicks, end.UtcTicks));
            (day, start) = (next, end);
        }

        return [.. days];
    }

    private static DateTimeOffset StartOf(DateTime day, TimeZoneInfo zone) =>
        new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), zone.GetUtcOffset(DateTime.SpecifyKind(day, DateTimeKind.Unspecified))).ToUniversalTime();

    private List<UsageRecord> ReadRecords(UsageRange range) =>
        database.Run("reading usage", new List<UsageRecord>(), connection => ReadRecords(range, connection, null));

    private static List<UsageRecord> ReadRecords(UsageRange range, SqliteConnection connection, SqliteTransaction? transaction)
    {
        var records = new List<UsageRecord>();
        using SqliteCommand command = SqliteRouterDatabase.Command(
            connection,
            "SELECT u.timestamp_ticks, u.model, u.connector, u.route, u.client, u.is_local, u.input, u.output, u.cache_read, u.cache_write, u.cost, u.reference_cost, u.profile, u.subscription_value " +
            $"FROM usage u WHERE {Where(range, after: null)};",
            Parameters(range, after: null));
        command.Transaction = transaction;
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
    }

    /// <summary>The running per-model totals of one range, and the newest row they include.</summary>
    private sealed class Running
    {
        private readonly Lock _lock = new();
        private UsageModelTotalsBuilder? _totals;
        private long _newest;
        private bool _stale;
        private DateTimeOffset _readAt;

        public IReadOnlyList<UsageModelTotals> Current(SqliteUsageLedger ledger, UsageRange range)
        {
            lock (_lock)
            {
                DateTimeOffset now = ledger.Database.Time.GetUtcNow();
                if (_totals is not null && !_stale && now - _readAt < RefreshInterval)
                {
                    return _totals.ToList();
                }

                // From scratch the first time, and while the range reaches back past the retention: pruning there
                // (here or by another instance) deletes rows these totals hold.
                bool all = _totals is null || range.Since < now - ledger.Database.UsageRetention;
                (IReadOnlyList<UsageModelTotals> Totals, long Newest)? read = ledger.Read(range, UsageGrouping.Model, TimeZoneInfo.Utc, all ? null : _newest);
                if (!all && read is { Newest: var newest } && newest < _newest)
                {
                    all = true; // the table was replaced
                    read = ledger.Read(range, UsageGrouping.Model, TimeZoneInfo.Utc, after: null);
                }

                if (read is { } fresh)
                {
                    if (all)
                    {
                        _totals = new UsageModelTotalsBuilder();
                    }

                    _totals!.Add(fresh.Totals);
                    _newest = fresh.Newest;
                    _stale = false;
                    _readAt = now;
                }

                return _totals?.ToList() ?? [];
            }
        }

        /// <summary>
        /// Row <paramref name="id"/> was written by this instance: <paramref name="record"/> when it is in the range. The
        /// next row adds to the totals; after a gap (rows from another instance, or a concurrent write), the rows after
        /// the newest known are read on the next request.
        /// </summary>
        public void Recorded(long id, UsageRecord? record, bool pruned)
        {
            lock (_lock)
            {
                if (_totals is null)
                {
                    return;
                }

                if (pruned)
                {
                    _totals = null; // rows in the range were deleted: read it again
                }
                else if (id == _newest + 1)
                {
                    if (record is not null)
                    {
                        _totals.Add(record.Model, record);
                    }

                    _newest = id;
                }
                else if (id > _newest)
                {
                    _stale = true;
                }
            }
        }
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
