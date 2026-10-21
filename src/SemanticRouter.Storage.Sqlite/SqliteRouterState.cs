using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Storage.Sqlite;

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

/// <summary>
/// The shared database: schema, connections and failure handling. Every operation degrades to a fallback value when
/// the database is unavailable, so routing never fails because of state storage. What requests write (usage, activity,
/// feedback, warm caches, tool-call affinity, cooldowns) goes through <see cref="Write"/>: one background writer saves
/// it in batches, one transaction each, so no request waits for the file or for another router holding it.
/// </summary>
internal sealed partial class SqliteRouterDatabase : IStateRetention, IStateStorageHealth, IDisposable
{
    /// <summary>Writes waiting at most; beyond, a write is made by its caller (nothing is dropped for lack of room).</summary>
    private const int QueueCapacity = 10_000;

    /// <summary>Writes saved in one transaction at most.</summary>
    private const int BatchSize = 256;

    private static readonly Meter Meter = new(SemanticRouterDiagnostics.MeterName);

    private static readonly Counter<long> DroppedWrites = Meter.CreateCounter<long>(
        "semantic_router.state.dropped_writes", "{write}", "State writes the database refused or could not take in time: they are lost.");

    // The physical connections (pooled) the pragmas already ran on.
    private static readonly ConditionalWeakTable<object, object> Prepared = new();

    /// <summary>How often everything past its retention is deleted, besides at start-up and when retention changes.</summary>
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

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
    private readonly int _busyTimeoutSeconds;
    private readonly ILogger _logger;
    private readonly ITimer? _pruning;
    private long _lastWarningTicks;

    // A checkpoint that could not empty the write-ahead log (another connection was reading): tried again after the next
    // operation, so what was deleted does not wait for the next prune to leave the log.
    private volatile bool _logOwed;

    public SqliteRouterDatabase(IOptions<SqliteStateOptions> options, ILogger<SqliteRouterDatabase> logger, TimeProvider? time = null)
    {
        Options = options.Value;
        Time = time ?? TimeProvider.System;
        _logger = logger;
        string path = System.IO.Path.GetFullPath(Options.Path);
        Location = path;
        _connectionString = ConnectionStringFor(path);
        _busyTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(Options.BusyTimeout.TotalSeconds));

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

            // Retention must not depend on traffic: a router that serves a few requests a day, or is restarted every
            // evening, still loses what is past it.
            Prune();
            _pruning = Time.CreateTimer(_ => Prune(), null, PruneInterval, PruneInterval);
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

    /// <summary>Takes the retention of the configuration just applied, and deletes what is past it.</summary>
    public void Apply(StateRetentionOptions retention)
    {
        _retention = retention ?? new StateRetentionOptions();
        Prune();
    }

    /// <summary>
    /// Deletes every row past its retention (activity by age and count, feedback, usage and the audit log by age,
    /// configuration versions by count, and expired affinity, warm caches and cooldowns), then empties the write-ahead
    /// log so the deleted prompt previews are in neither file. Runs at start-up, when retention changes and every
    /// <see cref="PruneInterval"/>, whatever the traffic. Returns how many rows it deleted.
    /// </summary>
    public int Prune() => Run("deleting state past its retention", 0, connection =>
    {
        DateTimeOffset now = Time.GetUtcNow();
        long before = TotalChanges(connection);
        using (SqliteCommand command = Command(
            connection,
            "DELETE FROM activity WHERE timestamp_ticks < $activity;" +
            "DELETE FROM activity WHERE id < (SELECT id FROM activity ORDER BY id DESC LIMIT 1 OFFSET $entries - 1);" +
            "DELETE FROM feedback WHERE timestamp_ticks < $activity;" +
            "DELETE FROM usage WHERE timestamp_ticks < $usage;" +
            "DELETE FROM audit WHERE timestamp_ticks < $audit;" +
            "DELETE FROM config_history WHERE id NOT IN (SELECT id FROM config_history ORDER BY id DESC LIMIT $versions);" +
            "DELETE FROM affinity WHERE created_ticks < $affinity;" +
            "DELETE FROM warm_caches WHERE expires_ticks <= $now;" +
            "DELETE FROM cooldowns WHERE until_ticks <= $now;",
            ("$activity", (now - ActivityMaxAge).UtcTicks), ("$entries", ActivityMaxEntries), ("$usage", (now - UsageRetention).UtcTicks),
            ("$audit", (now - AuditRetention).UtcTicks), ("$versions", HistoryMaxEntries),
            ("$affinity", (now - Options.AffinityWindow).UtcTicks), ("$now", now.UtcTicks)))
        {
            command.ExecuteNonQuery();
        }

        int deleted = (int)(TotalChanges(connection) - before);
        if (deleted > 0)
        {
            EmptyLog(connection);
        }

        return deleted;
    });

    private static long TotalChanges(SqliteConnection connection)
    {
        using SqliteCommand command = Command(connection, "SELECT total_changes();");
        return (long)command.ExecuteScalar()!;
    }


    public TimeProvider Time { get; }

    public string Location { get; }

    /// <summary>Whether the database opened; when not, every store keeps its state in memory only.</summary>
    public bool Available { get; private set; }

    private string? _problem;

    /// <inheritdoc />
    public string? Problem
    {
        get
        {
            if (_problem is not null)
            {
                return _problem;
            }

            long dropped = Interlocked.Read(ref _dropped);
            return dropped == 0 ? null : string.Create(CultureInfo.InvariantCulture,
                $"{dropped} write(s) to the router's state database {Location} failed since the router started and were lost ({Volatile.Read(ref _lastDrop)}). Usage, activity and feedback may be missing them; see the router's log.");
        }

        private set => _problem = value;
    }

    private readonly BlockingCollection<PendingWrite> _queue = new(QueueCapacity);
    private readonly object _progress = new();
    private readonly Lock _start = new();
    private Thread? _writer;
    private long _enqueued;
    private long _written;
    private long _dropped;
    private string? _lastDrop;

    /// <summary>Writes that failed and were lost since the router started.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>A write waiting for the background writer.</summary>
    /// <param name="What">What it is, for the log.</param>
    /// <param name="Operation">The write.</param>
    /// <param name="Done">Told, once the batch is committed or failed, whether the write is in the file.</param>
    /// <param name="Alone">Runs outside a transaction (a checkpoint).</param>
    private sealed record PendingWrite(string What, Action<SqliteConnection> Operation, Action<bool>? Done, bool Alone);

    /// <summary>
    /// Saves <paramref name="operation"/> in the background: it joins the next batch, and <paramref name="done"/> hears
    /// whether it was saved. Writes are saved in the order they were made. With <paramref name="alone"/>, it runs on its
    /// own, outside a transaction (a checkpoint cannot run inside one). When the database is unavailable, or the queue
    /// full, nothing waits: it is reported unsaved, or made right here.
    /// </summary>
    public void Write(string what, Action<SqliteConnection> operation, Action<bool>? done = null, bool alone = false)
    {
        if (!Available)
        {
            done?.Invoke(false);
            return;
        }

        StartWriter();
        var write = new PendingWrite(what, operation, done, alone);
        Interlocked.Increment(ref _enqueued);
        bool queued;
        try
        {
            queued = _queue.TryAdd(write);
        }
        catch (InvalidOperationException)
        {
            queued = false; // the router is stopping
        }

        if (!queued)
        {
            bool saved = Execute("saving state", false, connection =>
            {
                write.Operation(connection);
                return true;
            });
            if (!saved)
            {
                Drop(write.What, "it failed");
            }

            write.Done?.Invoke(saved);
            Progress(1);
        }
    }

    /// <summary>
    /// Waits until every write made before this call is in the file, or failed: a read that must see this router's own
    /// writes calls it first (<see cref="Run{T}"/> does). Returns at once when nothing is waiting.
    /// </summary>
    public void WaitForWrites()
    {
        long target = Interlocked.Read(ref _enqueued);
        if (Interlocked.Read(ref _written) >= target || Thread.CurrentThread == _writer)
        {
            return;
        }

        long deadline = Environment.TickCount64 + (long)((Options.BusyTimeout.TotalMilliseconds * 2) + 5000);
        lock (_progress)
        {
            while (Interlocked.Read(ref _written) < target)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || (_writer is { IsAlive: false }))
                {
                    return;
                }

                Monitor.Wait(_progress, TimeSpan.FromMilliseconds(left));
            }
        }
    }

    /// <summary>Stops the retention timer, saves what is still waiting, then stops the writer.</summary>
    public void Dispose()
    {
        _pruning?.Dispose();
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(30));
    }

    private void StartWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        lock (_start)
        {
            if (_writer is null)
            {
                var writer = new Thread(WriteAll) { IsBackground = true, Name = "SemanticRouter state writer" };
                writer.Start();
                _writer = writer;
            }
        }
    }

    private void WriteAll()
    {
        var batch = new List<PendingWrite>(BatchSize);
        foreach (PendingWrite first in _queue.GetConsumingEnumerable())
        {
            batch.Add(first);
            while (batch.Count < BatchSize && !batch[^1].Alone && _queue.TryTake(out PendingWrite? next))
            {
                batch.Add(next);
            }

            // A write to run on its own goes after the batch it would have ended.
            PendingWrite? alone = batch[^1].Alone ? batch[^1] : null;
            if (alone is not null)
            {
                batch.RemoveAt(batch.Count - 1);
            }

            if (batch.Count > 0)
            {
                Save(batch);
            }

            if (alone is not null)
            {
                Save([alone]);
            }

            batch.Clear();
        }
    }

    private void Save(List<PendingWrite> batch)
    {
        bool[] saved = new bool[batch.Count];
        bool[] failed = new bool[batch.Count];
        try
        {
            using SqliteConnection connection = Open();
            SqliteTransaction? transaction = batch is [{ Alone: true }] ? null : connection.BeginTransaction();
            using var statements = new Batch(connection, transaction);
            t_batch = statements;
            try
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    try
                    {
                        batch[i].Operation(connection);
                        saved[i] = true;
                    }
                    catch (Exception ex) when (ex is SqliteException or InvalidOperationException or JsonException)
                    {
                        // One bad write (a constraint) does not undo the batch: SQLite rolls back only its statement.
                        Warn(batch[i].What, ex.Message);
                        Drop(batch[i].What, ex.Message);
                        failed[i] = true;
                    }
                }

                transaction?.Commit();
            }
            finally
            {
                t_batch = null;
                transaction?.Dispose();
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // The batch could not be saved (the file is locked past the busy timeout, or broken).
            Warn(batch[0].What, ex.Message);
            for (int i = 0; i < batch.Count; i++)
            {
                if (!failed[i])
                {
                    saved[i] = false;
                    Drop(batch[i].What, ex.Message);
                }
            }
        }

        // The callbacks first (the usage ledger adds the rows to its running totals), then readers waiting for these
        // writes go on. So a callback must never wait for writes: the ledger reads under its locks with Peek.
        for (int i = 0; i < batch.Count; i++)
        {
            try
            {
                batch[i].Done?.Invoke(saved[i]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Warn(batch[i].What, ex.Message);
            }
        }

        Progress(batch.Count);
    }

    private void Progress(int count)
    {
        lock (_progress)
        {
            Interlocked.Add(ref _written, count);
            Monitor.PulseAll(_progress);
        }
    }

    private void Drop(string what, string error)
    {
        Interlocked.Increment(ref _dropped);
        Volatile.Write(ref _lastDrop, $"last: {what}, {error}");
        DroppedWrites.Add(1);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> once this router's waiting writes are saved (<see cref="WaitForWrites"/>), so
    /// it sees them; or returns <paramref name="fallback"/> when the database fails.
    /// </summary>
    public T Run<T>(string what, T fallback, Func<SqliteConnection, T> operation)
    {
        WaitForWrites();
        return Execute(what, fallback, operation, settleLog: true);
    }

    /// <summary>
    /// Like <see cref="Run{T}"/>, without waiting for this router's writes: for reads on the request path that this
    /// router's own writes reach another way (an in-memory copy), and for reads under a lock a write's callback takes.
    /// </summary>
    public T Peek<T>(string what, T fallback, Func<SqliteConnection, T> operation) => Execute(what, fallback, operation, settleLog: false);

    // settleLog: also empty a log a purge or prune could not empty earlier (not for a request-path read).
    private T Execute<T>(string what, T fallback, Func<SqliteConnection, T> operation, bool settleLog = false)
    {
        if (!Available)
        {
            return fallback;
        }

        try
        {
            using SqliteConnection connection = Open();
            T result = operation(connection);
            if (settleLog && _logOwed)
            {
                try
                {
                    EmptyLog(connection);
                }
                catch (SqliteException)
                {
                    // Tried again after the next operation; this one succeeded.
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or JsonException)
        {
            Warn(what, ex.Message);
            return fallback;
        }
    }

    /// <summary>At most one warning a minute: a locked or broken database must not flood the log.</summary>
    private void Warn(string what, string error)
    {
        long now = Time.GetUtcNow().UtcTicks;
        long last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
        {
            LogOperationFailed(_logger, what, Location, error);
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
    /// Empties the write-ahead log after rows were deleted, without waiting. When another connection keeps it, it is
    /// tried again after the next operation. Returns whether the log was emptied.
    /// </summary>
    public bool EmptyLog(SqliteConnection connection, TimeSpan wait = default)
    {
        bool emptied = TruncateLog(connection, wait);
        _logOwed = !emptied;
        return emptied;
    }

    /// <summary>
    /// Empties the write-ahead log after a purge, so what was deleted is in neither file: waits up to the busy timeout
    /// for readers to finish, and says so in the log when one did not (it is then tried again after the next
    /// operation). Returns whether the log was emptied.
    /// </summary>
    public bool EmptyLogAfterPurge(SqliteConnection connection, string what)
    {
        if (EmptyLog(connection, Options.BusyTimeout))
        {
            return true;
        }

        LogLogNotEmptied(_logger, what, Location, Options.BusyTimeout.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Reserves <paramref name="count"/> ids for rows of <paramref name="table"/> (<c>activity</c> or <c>feedback</c>),
    /// past every id reserved before and every id in the table, and returns the first; -1 when the database failed.
    /// The counter is in <c>meta</c>, so routers sharing the file never hand out the same id.
    /// </summary>
    public long ReserveIds(string table, int count) =>
        Peek("reserving ids", -1L, connection =>
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

    /// <summary>
    /// The connection string for a database file. It depends on the path alone, so the pool of pooled connections to
    /// one file can be found again (<see cref="ClearPool"/>); the busy timeout is set on each connection instead.
    /// </summary>
    internal static string ConnectionStringFor(string fullPath) => new SqliteConnectionStringBuilder
    {
        DataSource = fullPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    /// <summary>
    /// Closes the pooled connections to one database file, so it can be deleted or replaced. Unlike
    /// <see cref="SqliteConnection.ClearAllPools"/> it leaves every other database in the process alone.
    /// </summary>
    internal static void ClearPool(string path)
    {
        using var connection = new SqliteConnection(ConnectionStringFor(System.IO.Path.GetFullPath(path)));
        SqliteConnection.ClearPool(connection);
    }

    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command;
        if (t_batch is { } batch && batch.Connection == connection)
        {
            // In the writer's batch the same few statements run hundreds of times: each is prepared once.
            if (!batch.Statements.TryGetValue(sql, out BatchCommand? kept))
            {
                kept = new BatchCommand { Connection = connection, CommandText = sql, Transaction = batch.Transaction };
                batch.Statements[sql] = kept;
            }

            kept.Parameters.Clear();
            command = kept;
        }
        else
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
        }

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    // The batch the writer thread is saving, for Command.
    [ThreadStatic]
    private static Batch? t_batch;

    private sealed class Batch(SqliteConnection connection, SqliteTransaction? transaction) : IDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public SqliteTransaction? Transaction { get; } = transaction;

        public Dictionary<string, BatchCommand> Statements { get; } = new(StringComparer.Ordinal);

        public void Dispose()
        {
            foreach (BatchCommand command in Statements.Values)
            {
                command.Release();
            }
        }
    }

    /// <summary>A statement kept for the rest of a batch: disposing it (the caller's <c>using</c>) keeps it prepared.</summary>
    private sealed class BatchCommand : SqliteCommand
    {
        private bool _released;

        public void Release()
        {
            _released = true;
            Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (_released)
            {
                base.Dispose(disposing);
            }
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString) { DefaultTimeout = _busyTimeoutSeconds };
        try
        {
            connection.Open();

            // Once per physical connection: the pool hands the same one out again.
            // With WAL, NORMAL still never corrupts the file and loses nothing when the process crashes, but it does
            // not flush to disk on every commit. FULL did, holding the write lock for each flush: on Windows, where
            // flushes are slow, a burst of writes (two routers sharing the file) queued past the busy timeout and was
            // dropped.
            // secure_delete: a deleted row (a pruned or purged prompt preview) is overwritten with zeros, not left in a
            // free page where a copy of the file would still hold it.
            // busy_timeout: a write that finds the file locked (another router) waits in SQLite, which retries within
            // milliseconds, rather than in Microsoft.Data.Sqlite's retry loop, which sleeps 150 ms at a time.
            if (connection.Handle is { } handle && !Prepared.TryGetValue(handle, out _))
            {
                using (SqliteCommand pragmas = Command(
                    connection,
                    "PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON; PRAGMA busy_timeout=" +
                    ((long)Options.BusyTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ";"))
                {
                    pragmas.ExecuteNonQuery();
                }

                Prepared.AddOrUpdate(handle, handle);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router state database: after {What}, {Path}-wal could not be emptied within {Seconds} s because another connection kept reading; what was deleted stays in that file until the next checkpoint, after the next write or read.")]
    private static partial void LogLogNotEmptied(ILogger logger, string what, string path, double seconds);
}

/// <summary>Cooldowns with absolute expiry, so a restart during a quota cooldown keeps the model cooling down.</summary>
internal sealed class SqliteCooldownStore(SqliteRouterDatabase database) : ICooldownStore
{
    // Peek: the health tracker reloads these on a request every few seconds, for other routers' cooldowns; its own it
    // keeps in memory, so it need not wait for them to be saved.
    public IReadOnlyDictionary<string, DateTimeOffset> LoadCooldowns() =>
        database.Peek<IReadOnlyDictionary<string, DateTimeOffset>>("reading cooldowns", new Dictionary<string, DateTimeOffset>(), connection =>
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
        database.Write("saving a cooldown", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO cooldowns (model, until_ticks) VALUES ($model, $until) ON CONFLICT(model) DO UPDATE SET until_ticks = excluded.until_ticks;" +
                "DELETE FROM cooldowns WHERE until_ticks <= $now;",
                ("$model", model), ("$until", until.UtcTicks), ("$now", database.Time.GetUtcNow().UtcTicks));
            command.ExecuteNonQuery();
        });

    public void RemoveCooldown(string model) =>
        database.Write("removing a cooldown", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(connection, "DELETE FROM cooldowns WHERE model = $model;", ("$model", model));
            command.ExecuteNonQuery();
        });

    public void ClearCooldowns() =>
        database.Write("clearing cooldowns", connection =>
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
        database.Write("saving tool-call affinity", connection =>
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

        return database.Peek<string?>("reading tool-call affinity", null, connection =>
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

        IReadOnlyList<WarmCache> stored = database.Peek<IReadOnlyList<WarmCache>>("reading warm prompt caches", [], connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "SELECT model, tokens, expires_ticks FROM warm_caches WHERE conversation = $conversation AND expires_ticks > $now;",
                ("$conversation", conversation), ("$now", database.Time.GetUtcNow().UtcTicks));
            using SqliteDataReader reader = command.ExecuteReader();
            var caches = new List<WarmCache>();
            while (reader.Read())
            {
                caches.Add(new WarmCache
                {
                    Model = reader.GetString(0),
                    CachedTokens = reader.GetInt32(1),
                    ExpiresAt = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                });
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
        database.Write("saving a warm prompt cache", connection =>
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
        // Nothing to delete when this router holds no cache for the conversation: Lookup brings what the file has into
        // memory first. Most turns of a backend without prompt caching come here, and the delete found nothing.
        if (!_cache.Remove(conversation, model))
        {
            return;
        }

        database.Write("forgetting a warm prompt cache", connection =>
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
        database.Write("saving feedback", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO feedback (id, timestamp_ticks, entry_id, item) VALUES ($id, $ts, $entry, $item);",
                ("$id", feedback.Id), ("$ts", feedback.Timestamp.UtcTicks), ("$entry", feedback.EntryId), ("$item", JsonSerializer.Serialize(feedback, Json)));
            command.ExecuteNonQuery();
            if (prune)
            {
                using SqliteCommand old = SqliteRouterDatabase.Command(
                    connection, "DELETE FROM feedback WHERE timestamp_ticks < $oldest;", ("$oldest", Oldest()));
                if (old.ExecuteNonQuery() > 0)
                {
                    // Not here, inside the writer's transaction: on its own, next.
                    database.Write("emptying the log", c => database.EmptyLog(c), alone: true);
                }
            }
        });
    }

    public long ReserveIds(int count) => database.ReserveIds("feedback", count);

    public IReadOnlyList<RoutingFeedback> Recent(int limit) => Read(
        "SELECT item FROM feedback WHERE timestamp_ticks >= $oldest ORDER BY id DESC LIMIT $limit;", ("$oldest", Oldest()), ("$limit", Math.Max(0, limit)));

    // Past the retention is as good as deleted, even before the next prune deletes it.
    public IReadOnlyList<RoutingFeedback> Between(DateTimeOffset since, DateTimeOffset until, int limit) => Read(
        "SELECT item FROM feedback WHERE timestamp_ticks >= max($since, $oldest) AND timestamp_ticks < $until ORDER BY id LIMIT $limit;",
        ("$since", since.UtcTicks), ("$oldest", Oldest()), ("$until", until.UtcTicks), ("$limit", Math.Max(0, limit)));

    public void Remove(IReadOnlyCollection<long> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return;
        }

        database.Run("deleting feedback about deleted activity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "DELETE FROM feedback WHERE entry_id IN (SELECT value FROM json_each($ids));", ("$ids", JsonSerializer.Serialize(entryIds)));
            command.ExecuteNonQuery();
        });
    }

    private long Oldest() => (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks;

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

        // Every tenth of the cap (at least every 100 entries): the cap holds within 10%, and the checkpoint that follows
        // a prune (it writes the whole log into the file) does not run dozens of times a second on a busy router.
        bool prune = Interlocked.Increment(ref _writes) % Math.Max(100, database.ActivityMaxEntries / 10) == 0;
        string json = JsonSerializer.Serialize(entry, Json); // as it is now: the entry may still change
        database.Write("saving activity", connection =>
        {
            // A plain insert: an id that is already there is a bug to hear about, not an entry to overwrite.
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection,
                "INSERT INTO activity (id, timestamp_ticks, entry) VALUES ($id, $ts, $entry);",
                ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", json));
            command.ExecuteNonQuery();
            if (prune)
            {
                Prune(connection);
            }
        });

        if (prune)
        {
            // The write-ahead log still holds the pages as they were before; move them into the file and empty it.
            database.Write("emptying the log", connection => database.EmptyLog(connection), alone: true);
        }
    }

    public void Replace(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string json = JsonSerializer.Serialize(entry, Json);
        database.Write("saving activity", connection =>
        {
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "UPDATE activity SET timestamp_ticks = $ts, entry = $entry WHERE id = $id;",
                ("$id", entry.Id), ("$ts", entry.Timestamp.UtcTicks), ("$entry", json));
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

    // Past the retention is as good as deleted (and Recent hides it), even before the next prune deletes it: the
    // export, the feedback summary and tuning never see it.
    public IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit) =>
        database.Run<IReadOnlyList<RoutingActivityEntry>>("reading activity", [], connection =>
        {
            var entries = new List<RoutingActivityEntry>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT entry FROM activity WHERE timestamp_ticks >= max($since, $oldest) AND timestamp_ticks < $until ORDER BY id LIMIT $limit;",
                ("$since", since.UtcTicks), ("$oldest", (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks), ("$until", until.UtcTicks),
                ("$limit", Math.Max(0, limit)));
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
            "DELETE FROM activity WHERE id < (SELECT id FROM activity ORDER BY id DESC LIMIT 1 OFFSET $max - 1);",
            ("$oldest", (database.Time.GetUtcNow() - database.ActivityMaxAge).UtcTicks), ("$max", database.ActivityMaxEntries)))
        {
            command.ExecuteNonQuery();
        }

        // The write-ahead log still holds the pages as they were before: the store empties it next, on its own (a
        // checkpoint cannot run inside the writer's transaction).
    }

    public IReadOnlyList<long> IdsBetween(DateTimeOffset since, DateTimeOffset until) =>
        database.Run<IReadOnlyList<long>>("reading activity", [], connection =>
        {
            var ids = new List<long>();
            using SqliteCommand command = SqliteRouterDatabase.Command(
                connection, "SELECT id FROM activity WHERE timestamp_ticks >= $since AND timestamp_ticks < $until;",
                ("$since", since.UtcTicks), ("$until", until.UtcTicks));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids;
        });

    public int Purge(DateTimeOffset since, DateTimeOffset until) => PurgeWithResult(since, until).Removed;

    // A database that is not in use holds nothing to purge; one that fails during the purge may still hold it.
    public ActivityPurgeResult PurgeWithResult(DateTimeOffset since, DateTimeOffset until) => !database.Available ? new ActivityPurgeResult { Removed = 0 } :
        database.Run("purging activity", new ActivityPurgeResult { Removed = 0, Complete = false }, connection =>
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

            return new ActivityPurgeResult { Removed = removed, Complete = database.EmptyLogAfterPurge(connection, "purging activity") };
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
                versions.Add(new RouterConfigurationVersion
                {
                    Id = reader.GetInt64(0),
                    Timestamp = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    Source = reader.GetString(2),
                    Actor = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Note = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
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
        long id = -1;
        database.Write("recording usage", connection =>
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
            id = (long)command.ExecuteScalar()!;
        }, done: saved => Saved(saved ? id : -1, record, prune, oldest));
    }

    /// <summary>Once the background writer saved <paramref name="record"/> as row <paramref name="id"/> (-1: it could not).</summary>
    private void Saved(long id, UsageRecord record, bool prune, DateTimeOffset oldest)
    {
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
        // Before any lock is taken: this router's own rows are counted once they are in the file.
        database.WaitForWrites();
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
        // Peek, not Run: the running totals read under their lock, which the writer's callback (Saved) takes.
        database.Peek<(IReadOnlyList<UsageModelTotals>, long)?>("reading usage", null, connection =>
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
                    totals.Add(KeyOf(reader, by, days), reader.GetString(1), new UsageTotals
                    {
                        Requests = reader.GetInt64(2),
                        InputTokens = reader.GetInt64(3),
                        OutputTokens = reader.GetInt64(4),
                        CacheReadTokens = reader.GetInt64(5),
                        CacheWriteTokens = reader.GetInt64(6),
                        CostUsd = FromPico(reader, 7),
                        ReferenceCostUsd = FromPico(reader, 9),
                        SubscriptionValueUsd = FromPico(reader, 11),
                    });
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
            records.Add(new UsageRecord
            {
                Timestamp = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                Model = reader.GetString(1),
                Connector = reader.GetString(2),
                Route = reader.IsDBNull(3) ? null : reader.GetString(3),
                Client = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsLocal = reader.GetInt64(5) != 0,
                InputTokens = reader.GetInt64(6),
                OutputTokens = reader.GetInt64(7),
                CacheReadTokens = reader.GetInt64(8),
                CacheWriteTokens = reader.GetInt64(9),
                CostUsd = decimal.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                ReferenceCostUsd = decimal.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                Profile = reader.IsDBNull(12) ? null : reader.GetString(12),
                SubscriptionValueUsd = decimal.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            });
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
                entries.Add(new AuditEntry
                {
                    Id = reader.GetInt64(0),
                    Timestamp = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    Actor = reader.GetString(2),
                    Client = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Method = reader.GetString(4),
                    Path = reader.GetString(5),
                    Route = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Status = reader.GetInt32(7),
                    Summary = reader.GetString(8),
                });
            }

            return entries;
        });
    }
}
