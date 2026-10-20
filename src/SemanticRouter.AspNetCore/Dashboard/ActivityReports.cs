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
        var buckets = new List<ActivityBucket>();
        for (DateTimeOffset start = since; start < until; start += bucket)
        {
            DateTimeOffset end = start + bucket;
            RoutingActivityEntry[] inBucket = entries.Where(e => e.Timestamp >= start && e.Timestamp < end).ToArray();
            double[] latencies = Latencies(inBucket);
            buckets.Add(new ActivityBucket(
                start,
                inBucket.Length,
                inBucket.Count(e => e.Outcome == "served"),
                inBucket.Count(e => e.Outcome == "failed"),
                inBucket.GroupBy(e => e.Tier).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
                inBucket.Where(e => e.ServedBy is not null).GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                Percentile(latencies, 50),
                Percentile(latencies, 95)));
        }

        double[] all = Latencies(entries);
        return new ActivitySeries(since, until, bucket, entries.Count, Percentile(all, 50), Percentile(all, 95), Percentile(all, 99), buckets);
    }

    /// <summary>Served requests' total times in milliseconds, ascending.</summary>
    public static double[] Latencies(IEnumerable<RoutingActivityEntry> entries) =>
        [.. entries.Where(e => e.Outcome == "served" && e.TotalMilliseconds is not null).Select(e => e.TotalMilliseconds!.Value).Order()];

    /// <summary>Writes entries as CSV (RFC 4180), with or without the prompt previews.</summary>
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
        builder.Append(string.Join(',', includePrompts ? [.. header, "prompt_preview"] : header)).Append("\r\n");
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
            builder.Append(string.Join(',', (includePrompts ? [.. row, e.PromptPreview] : row).Select(Cell))).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>Writes entries as JSON Lines, with or without the prompt previews.</summary>
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
public sealed record ActivitySeries(
    DateTimeOffset Since, DateTimeOffset Until, TimeSpan Bucket, int Requests, double? P50Milliseconds, double? P95Milliseconds, double? P99Milliseconds,
    IReadOnlyList<ActivityBucket> Buckets);

/// <summary>One bucket of an <see cref="ActivitySeries"/>.</summary>
public sealed record ActivityBucket(
    DateTimeOffset Start, int Requests, int Served, int Failed, IReadOnlyDictionary<string, int> ByTier, IReadOnlyDictionary<string, int> ByModel,
    double? P50Milliseconds, double? P95Milliseconds);
