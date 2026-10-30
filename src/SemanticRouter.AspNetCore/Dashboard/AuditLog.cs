using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// One action taken through the dashboard or admin API. Never holds a request body, a token or prompt text.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>Increasing id, assigned by the log.</summary>
    public required long Id { get; init; }

    /// <summary>When the request finished.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Who: the user a trusted proxy named, <c>admin token</c>, <c>read-only token</c>, <c>this machine</c>, …</summary>
    public required string Actor { get; init; }

    /// <summary>The client's address (after a known proxy's <c>X-Forwarded-For</c>); <see langword="null"/> in-process.</summary>
    public string? Client { get; init; }

    /// <summary>The HTTP method.</summary>
    public required string Method { get; init; }

    /// <summary>The request path, without the query string.</summary>
    public required string Path { get; init; }

    /// <summary>The endpoint's route template, e.g. <c>/router/api/config/history/{id:long}/rollback</c>.</summary>
    public string? Route { get; init; }

    /// <summary>The status code answered.</summary>
    public required int Status { get; init; }

    /// <summary>What was done, e.g. <c>applied the configuration: version 12, saved</c>.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// An append-only record of changes made through the dashboard and admin API, sign-ins and sign-outs. Entries are
/// never changed; a store may drop the oldest past its retention.
/// </summary>
public interface IAuditLog
{
    /// <summary>Appends <paramref name="entry"/> (its <see cref="AuditEntry.Id"/> is ignored) and returns it as stored.</summary>
    AuditEntry Append(AuditEntry entry);

    /// <summary>Entries from <paramref name="since"/> (inclusive) to <paramref name="until"/> (exclusive), newest first.</summary>
    IReadOnlyList<AuditEntry> Query(DateTimeOffset? since, DateTimeOffset? until, int limit);
}

/// <summary>The default <see cref="IAuditLog"/>: the latest entries of this process, in memory.</summary>
internal sealed class InMemoryAuditLog : IAuditLog
{
    private readonly LinkedList<AuditEntry> _entries = new();
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private long _next = 1;

    /// <summary>Keeps the latest 5000 entries.</summary>
    public InMemoryAuditLog()
        : this(5000)
    {
    }

    /// <summary>Keeps the latest <paramref name="capacity"/> entries.</summary>
    public InMemoryAuditLog(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <inheritdoc />
    public AuditEntry Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            AuditEntry stored = entry with { Id = _next++ };
            _entries.AddFirst(stored);
            while (_entries.Count > _capacity)
            {
                _entries.RemoveLast();
            }

            return stored;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Query(DateTimeOffset? since, DateTimeOffset? until, int limit)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => (since is null || e.Timestamp >= since) && (until is null || e.Timestamp < until))
                .Take(Math.Max(0, limit))
                .ToArray();
        }
    }
}

/// <summary>Names what an audited endpoint does, for its <see cref="AuditEntry.Summary"/>.</summary>
/// <param name="Summary">E.g. <c>cleared cooldowns</c>.</param>
internal sealed record DashboardAuditMetadata(string Summary);

/// <summary>
/// The dashboard group's endpoint filter that records each audited request once it is answered: every request other
/// than <c>GET</c> and <c>HEAD</c>, and endpoints marked <see cref="DashboardWriteMetadata"/> or
/// <see cref="DashboardAuditMetadata"/>, when the caller is known (signed in, or refused for its role), plus wrong tokens.
/// Requests refused before anyone signed in are not recorded, so a stranger cannot fill the log; wrong tokens are,
/// and are throttled per address.
/// </summary>
internal sealed partial class DashboardAudit(IAuditLog log, TimeProvider time, ILogger logger)
{
    /// <summary>The log category of audit entries.</summary>
    public const string LogCategory = "SemanticRouter.Audit";

    /// <summary>The event id of an audit entry in the logs.</summary>
    public const int AuditEventId = 1;

    /// <summary>The event name of an audit entry in the logs.</summary>
    public const string AuditEventName = "AuditEntry";

    private const string SummaryKey = "SemanticRouter.AuditSummary";
    private const string DetailKey = "SemanticRouter.AuditDetail";
    private const int MaxText = 300;

    /// <summary>Replaces the request's audit summary (the sign-in endpoint).</summary>
    public static void Summary(HttpContext context, string summary) => context.Items[SummaryKey] = summary;

    /// <summary>Adds detail to the endpoint's audit summary, e.g. the version a change produced. No prompt text or secrets.</summary>
    public static void Detail(HttpContext context, string detail) => context.Items[DetailKey] = detail;

    public async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;
        object? result;
        try
        {
            result = await next(invocation).ConfigureAwait(false);
        }
        catch
        {
            Record(context, StatusCodes.Status500InternalServerError);
            throw;
        }

        Record(context, result is IStatusCodeHttpResult { StatusCode: int status } ? status : context.Response.StatusCode);
        return result;
    }

    private void Record(HttpContext context, int status)
    {
        Endpoint? endpoint = context.GetEndpoint();
        string method = context.Request.Method;
        bool audited = (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            || endpoint?.Metadata.GetMetadata<DashboardWriteMetadata>() is not null
            || endpoint?.Metadata.GetMetadata<DashboardAuditMetadata>() is not null;
        bool wrongToken = DashboardAccess.PresentedWrongToken(context);
        string? actor = DashboardAccess.ActorOf(context);
        if (!wrongToken && (!audited || actor is null))
        {
            return;
        }

        string summary = context.Items.TryGetValue(SummaryKey, out object? set) && set is string text
            ? text
            : wrongToken && actor is null
                ? "refused a wrong token"
                : endpoint?.Metadata.GetMetadata<DashboardAuditMetadata>()?.Summary ?? $"{method} {RouteOf(endpoint) ?? context.Request.Path.Value}";
        if (context.Items.TryGetValue(DetailKey, out object? detail) && detail is string more && status < 400)
        {
            summary += ": " + more;
        }

        var entry = new AuditEntry
        {
            Id = 0,
            Timestamp = time.GetUtcNow(),
            Actor = Clip(actor ?? "anonymous"),
            Client = context.Connection.RemoteIpAddress?.ToString(),
            Method = method,
            Path = Clip((context.Request.PathBase + context.Request.Path).Value ?? "/"),
            Route = RouteOf(endpoint) is { } route ? Clip(route) : null,
            Status = status,
            Summary = Clip(summary),
        };
        AuditEntry stored = log.Append(entry);
        LogEntry(logger, stored.Actor, stored.Client ?? "this machine", stored.Method, stored.Path, stored.Status, stored.Summary);
    }

    private static string? RouteOf(Endpoint? endpoint) => (endpoint as RouteEndpoint)?.RoutePattern.RawText;

    private static string Clip(string text)
    {
        var clean = new StringBuilder(Math.Min(text.Length, MaxText));
        foreach (char c in text)
        {
            if (clean.Length == MaxText)
            {
                break;
            }

            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        return clean.ToString();
    }

    /// <summary>Entries as JSON Lines, one object per line, for a SIEM.</summary>
    public static string ToJsonLines(IEnumerable<AuditEntry> entries, JsonSerializerOptions json)
    {
        var lines = new StringBuilder();
        foreach (AuditEntry entry in entries)
        {
            lines.Append(JsonSerializer.Serialize(entry, json)).Append('\n');
        }

        return lines.ToString();
    }

    // SIEM rules match on the category, this id and name, and the field names (docs/dashboard.md#audit-log): keep them.
    [LoggerMessage(EventId = AuditEventId, EventName = AuditEventName, Level = LogLevel.Information, Message = "Audit: {Actor} from {Client}: {Method} {Path} -> {Status}: {Summary}")]
    private static partial void LogEntry(ILogger logger, string actor, string client, string method, string path, int status, string summary);
}

/// <summary>Marks endpoints for the audit log.</summary>
internal static class DashboardAuditExtensions
{
    /// <summary>Records calls to the endpoint in the audit log, summarised as <paramref name="summary"/>.</summary>
    public static TBuilder Audited<TBuilder>(this TBuilder builder, string summary)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new DashboardAuditMetadata(summary));
}

/// <summary>Audit entries, newest first.</summary>
internal sealed record AuditResponse(IReadOnlyList<AuditEntry> Entries);
