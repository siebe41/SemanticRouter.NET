using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Time series and exports over routing activity, for the dashboard and for analysis elsewhere.</summary>
internal static class ActivityReports
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A window the dashboard offers, with its bucket size.</summary>
    public static (TimeSpan Window, TimeSpan Bucket) Window(string? name) => name switch
    {
        "1h" => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
        "7d" => (TimeSpan.FromDays(7), TimeSpan.FromHours(2)),
        "30d" => (TimeSpan.FromDays(30), TimeSpan.FromHours(12)),
        _ => (TimeSpan.FromHours(24), TimeSpan.FromMinutes(30)),
    };

    /// <summary>Nearest-rank percentile of <paramref name="sorted"/> (ascending), or <see langword="null"/> when empty.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double percent)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            return null;
        }

        int rank = (int)Math.Ceiling(percent / 100 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>Request counts, tier and model mix, and latency percentiles per bucket.</summary>
    public static ActivitySeries Series(IReadOnlyList<RoutingActivityEntry> entries, DateTimeOffset since, DateTimeOffset until, TimeSpan bucket)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucket, TimeSpan.Zero);
        var buckets = new List<ActivityBucket>();
        for (DateTimeOffset start = since; start < until; start += bucket)
        {
            DateTimeOffset end = start + bucket;
            RoutingActivityEntry[] inBucket = entries.Where(e => e.Timestamp >= start && e.Timestamp < end).ToArray();
            double[] latencies = Latencies(inBucket);
            buckets.Add(new ActivityBucket
            {
                Start = start,
                Requests = inBucket.Length,
                Served = inBucket.Count(e => e.Outcome == "served"),
                Failed = inBucket.Count(e => e.Outcome == "failed"),
                ByTier = inBucket.GroupBy(e => e.Tier).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
                ByModel = inBucket.Where(e => e.ServedBy is not null).GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                P50Milliseconds = Percentile(latencies, 50),
                P95Milliseconds = Percentile(latencies, 95),
            });
        }

        double[] all = Latencies(entries);
        return new ActivitySeries
        {
            Since = since,
            Until = until,
            Bucket = bucket,
            Requests = entries.Count,
            P50Milliseconds = Percentile(all, 50),
            P95Milliseconds = Percentile(all, 95),
            P99Milliseconds = Percentile(all, 99),
            Buckets = buckets,
        };
    }

    /// <summary>Served requests' total times in milliseconds, ascending.</summary>
    public static double[] Latencies(IEnumerable<RoutingActivityEntry> entries) =>
        [.. entries.Where(e => e.Outcome == "served" && e.TotalMilliseconds is not null).Select(e => e.TotalMilliseconds!.Value).Order()];

    /// <summary>
    /// Writes entries as CSV (RFC 4180), with or without prompt text: the prompt previews and the <c>detail</c> column
    /// (failover notes, which can quote a failed CLI run's output), both kept only as <see cref="StateOptions.StorePrompts"/>
    /// allows.
    /// </summary>
    public static string ToCsv(IEnumerable<RoutingActivityEntry> entries, bool includePrompts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        string[] header =
        [
            "id", "timestamp", "protocol", "path", "client", "requested_model", "tier", "reason", "route", "similarity", "complexity",
            "has_tools", "tool_continuation", "local_only", "estimated_input_tokens", "planned_model", "served_by", "connector", "attempt",
            "degraded", "outcome", "budget", "dropped", "classification_ms", "time_to_first_byte_ms", "total_ms", "input_tokens", "output_tokens",
            "cache_read_tokens", "cache_creation_tokens", "cost_usd", "trace_id", "detail", "prompted_tool_calls",
            "attempt_time_to_first_byte_ms", "attempt_ms",
        ];
        int detail = Array.IndexOf(header, "detail");
        string[] columns = includePrompts ? [.. header, "prompt_preview"] : [.. header[..detail], .. header[(detail + 1)..]];
        builder.Append(string.Join(',', columns)).Append("\r\n");
        foreach (RoutingActivityEntry e in entries)
        {
            object?[] row =
            [
                e.Id, e.Timestamp.ToString("o", CultureInfo.InvariantCulture), e.Protocol, e.Path, e.Client, e.RequestedModel, e.Tier, e.Reason, e.Route,
                e.Similarity, e.Complexity, e.HasTools, e.ToolContinuation, e.LocalOnly, e.EstimatedInputTokens, e.PlannedModel, e.ServedBy,
                e.Connector, e.Attempt, e.Degraded, e.Outcome, e.Budget, e.Dropped, e.ClassificationMilliseconds, e.TimeToFirstByteMilliseconds,
                e.TotalMilliseconds, e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheCreationTokens, e.CostUsd, e.TraceId, e.Detail,
                e.PromptedToolCalls, e.AttemptTimeToFirstByteMilliseconds, e.AttemptMilliseconds,
            ];
            object?[] cells = includePrompts ? [.. row, e.PromptPreview] : [.. row[..detail], .. row[(detail + 1)..]];
            builder.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes entries as JSON Lines, with or without prompt text: <c>promptPreview</c>, <c>detail</c> (which can quote a
    /// failed CLI run's output) and <c>gradeNote</c> (the judge's reason about the answer).
    /// </summary>
    public static string ToJsonLines(IEnumerable<RoutingActivityEntry> entries, bool includePrompts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        foreach (RoutingActivityEntry entry in entries)
        {
            var node = JsonSerializer.SerializeToNode(entry, Json)!.AsObject();
            if (!includePrompts)
            {
                node.Remove("promptPreview");
                node.Remove("detail");
                node.Remove("gradeNote");
            }

            builder.Append(node.ToJsonString()).Append('\n');
        }

        return builder.ToString();
    }

    private static string Cell(object? value)
    {
        string text = value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}

/// <summary>Activity over a window.</summary>
internal sealed record ActivitySeries
{
    /// <summary>Start of the window (inclusive).</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>End of the window (exclusive).</summary>
    public required DateTimeOffset Until { get; init; }

    /// <summary>Length of each bucket.</summary>
    public required TimeSpan Bucket { get; init; }

    /// <summary>Requests in the window.</summary>
    public required int Requests { get; init; }

    /// <summary>Median total time of the window's served requests, if any.</summary>
    public double? P50Milliseconds { get; init; }

    /// <summary>95th percentile total time of the served requests, if any.</summary>
    public double? P95Milliseconds { get; init; }

    /// <summary>99th percentile total time of the served requests, if any.</summary>
    public double? P99Milliseconds { get; init; }

    /// <summary>The window split into buckets of <see cref="Bucket"/>, oldest first.</summary>
    public required IReadOnlyList<ActivityBucket> Buckets { get; init; }
}

/// <summary>One bucket of an <see cref="ActivitySeries"/>.</summary>
internal sealed record ActivityBucket
{
    /// <summary>Start of the bucket.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>Requests in the bucket.</summary>
    public required int Requests { get; init; }

    /// <summary>Requests a model served.</summary>
    public required int Served { get; init; }

    /// <summary>Requests no model could serve.</summary>
    public required int Failed { get; init; }

    /// <summary>Requests per required tier.</summary>
    public required IReadOnlyDictionary<string, int> ByTier { get; init; }

    /// <summary>Served requests per catalog model.</summary>
    public required IReadOnlyDictionary<string, int> ByModel { get; init; }

    /// <summary>Median total time of the bucket's served requests, if any.</summary>
    public double? P50Milliseconds { get; init; }

    /// <summary>95th percentile total time of the served requests, if any.</summary>
    public double? P95Milliseconds { get; init; }
}
