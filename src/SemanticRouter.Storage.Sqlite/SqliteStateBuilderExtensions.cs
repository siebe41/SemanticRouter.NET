using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticRouter;
using SemanticRouter.AspNetCore;
using SemanticRouter.Storage.Sqlite;

namespace Microsoft.Extensions.DependencyInjection;

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
