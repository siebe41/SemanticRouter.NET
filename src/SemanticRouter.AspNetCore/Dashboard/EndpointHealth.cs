using System.Collections.Concurrent;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Where each connector sends requests and how it has been doing, for the dashboard: the host (never the full address,
/// which may carry a key), a state, and figures from the activity log, the models' cooldowns, the connector incidents
/// and the last on-demand check.
/// </summary>
internal static class EndpointHealth
{
    private const int LastErrorLength = 300;

    public static IReadOnlyList<EndpointStatus> Build(
        RouterConfiguration config, IReadOnlyDictionary<string, DateTimeOffset> cooldowns, IReadOnlyList<RoutingActivityEntry> recent,
        ConnectorIncidents? incidents, EndpointChecks? checks)
    {
        Dictionary<string, string> connectorOf = config.Models
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Connector, StringComparer.OrdinalIgnoreCase);
        var attempts = new Dictionary<string, List<Attempt>>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingActivityEntry entry in recent)
        {
            foreach (Attempt attempt in Attempts(entry))
            {
                if (connectorOf.TryGetValue(attempt.Model, out string? connector))
                {
                    if (!attempts.TryGetValue(connector, out List<Attempt>? list))
                    {
                        attempts[connector] = list = [];
                    }

                    list.Add(attempt);
                }
            }
        }

        Dictionary<string, DateTimeOffset> failingSince = (incidents?.Failing() ?? [])
            .ToDictionary(f => f.Connector, f => f.Since, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> limitDays = (incidents?.LimitDays() ?? [])
            .ToDictionary(f => f.Connector, f => f.Days, StringComparer.OrdinalIgnoreCase);

        return [.. config.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(kv =>
        {
            (string name, ConnectorOptions options) = (kv.Key, kv.Value);
            ModelDefinition[] models = EnabledModels(config, name);
            DateTimeOffset[] cooling = models
                .Select(m => cooldowns.TryGetValue(m.Name, out DateTimeOffset until) ? until : (DateTimeOffset?)null)
                .OfType<DateTimeOffset>()
                .ToArray();
            List<Attempt> tried = attempts.TryGetValue(name, out List<Attempt>? found) ? found : [];
            Attempt[] served = [.. tried.Where(a => a.Succeeded)];
            Attempt? lastFailure = tried.Where(a => !a.Succeeded).MaxBy(a => a.At);
            EndpointTarget target = Target(options, models);
            EndpointCheck? check = checks?.Last(name, target.Key);

            var status = new EndpointStatus
            {
                Connector = name,
                Kind = options.Type == ConnectorTypes.Cli ? $"Cli/{options.Preset}" : options.Type.ToString(),
                Host = target.Host,
                Scheme = target.Scheme,
                Program = target.Program,
                Via = target.Via,
                Local = target.Local,
                Encrypted = target.Encrypted,
                State = "ok",
                Models = models.Length,
                ModelsCoolingDown = cooling.Length,
                CooldownUntil = cooling.Length > 0 ? cooling.Min() : null,
                Attempts = tried.Count,
                Failures = tried.Count - served.Length,
                SuccessRate = tried.Count > 0 ? Math.Round((double)served.Length / tried.Count, 3) : null,
                LastUsed = tried.Count > 0 ? tried.Max(a => a.At) : null,
                LastSuccess = served.Length > 0 ? served.Max(a => a.At) : null,
                LastFailure = lastFailure?.At,
                LastError = lastFailure?.Error,
                FailingSince = failingSince.TryGetValue(name, out DateTimeOffset since) ? since : null,
                LimitDays = limitDays.GetValueOrDefault(name),
                FirstByteP50 = Percentile(served.Select(a => a.FirstByte), 0.5),
                FirstByteP95 = Percentile(served.Select(a => a.FirstByte), 0.95),
                TotalP50 = Percentile(served.Select(a => a.Total), 0.5),
                TotalP95 = Percentile(served.Select(a => a.Total), 0.95),
                TokensPerSecond = Percentile(served.Select(a => a.TokensPerSecond), 0.5),
                Check = check,
            };
            (string state, string? note) = State(status);
            return status with { State = state, StateNote = note };
        })];
    }

    /// <summary>
    /// Ok, degraded (some models cooling down, or many recent failures), failing (the latest request or check failed),
    /// down (every model cooling down), idle (nothing sent yet) or unused (no enabled model).
    /// </summary>
    private static (string State, string? Note) State(EndpointStatus s)
    {
        DateTimeOffset? lastTraffic = s.LastUsed;
        if (s.Models == 0)
        {
            return ("unused", "No enabled model uses it.");
        }

        if (s.ModelsCoolingDown == s.Models)
        {
            return ("down", s.Models == 1 ? "Its model is cooling down after a failure or a usage limit." : $"All {s.Models} of its models are cooling down after failures or a usage limit.");
        }

        if (s.Check is { } check && (lastTraffic is null || check.At > lastTraffic))
        {
            if (check.Status == nameof(RouterCheckStatus.Failed))
            {
                return ("failing", "The last check failed.");
            }

            if (s.FailingSince is null && s.ModelsCoolingDown == 0)
            {
                return (check.Status == nameof(RouterCheckStatus.Ok) ? "ok" : "degraded", check.Status == nameof(RouterCheckStatus.Ok) ? "The last check passed." : "The last check found something to look at.");
            }
        }

        if (s.FailingSince is not null)
        {
            return ("failing", "Every request since it first failed has failed.");
        }

        if (s.ModelsCoolingDown > 0)
        {
            return ("degraded", $"{s.ModelsCoolingDown} of its {s.Models} models are cooling down.");
        }

        if (s.Attempts >= 4 && s.SuccessRate < 0.75)
        {
            return ("degraded", $"{s.Failures} of the last {s.Attempts} requests to it failed.");
        }

        return s.Attempts == 0 ? ("idle", "No requests sent to it yet.") : ("ok", null);
    }

    /// <summary>Each model the entry was sent to, in order, and whether it answered.</summary>
    private static IEnumerable<Attempt> Attempts(RoutingActivityEntry entry)
    {
        if (entry.Outcome is not ("served" or "failed") || entry.SentTo.Count == 0)
        {
            yield break;
        }

        int answered = entry.Outcome == "served" && entry.ServedBy is { } servedBy
            ? LastIndexOf(entry.SentTo, servedBy)
            : -1;
        for (int i = 0; i < entry.SentTo.Count; i++)
        {
            string model = entry.SentTo[i];
            if (i == answered)
            {
                // The serving attempt's own times: the failed attempts before it belong to their endpoints, not this one.
                (double? firstByte, double? totalTime) = entry.ServingTimes();
                double? rate = entry.OutputTokens is > 0 && totalTime is { } total && firstByte is { } first && total - first >= 50
                    ? Math.Round(entry.OutputTokens.Value / ((total - first) / 1000), 1)
                    : null;
                yield return new Attempt(model, entry.Timestamp, true, firstByte, totalTime, rate, null);
            }
            else
            {
                yield return new Attempt(model, entry.Timestamp, false, null, null, null, ErrorOf(entry.Detail, model));
            }
        }
    }

    private static int LastIndexOf(IList<string> models, string model)
    {
        for (int i = models.Count - 1; i >= 0; i--)
        {
            if (string.Equals(models[i], model, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The model's part of the dispatcher's failover notes (<c>a: reason; b: reason</c>), already redacted.</summary>
    private static string? ErrorOf(string? detail, string model)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        const string AllFailed = "Every candidate model failed: ";
        string notes = detail.StartsWith(AllFailed, StringComparison.Ordinal) ? detail[AllFailed.Length..] : detail;
        string prefix = model + ": ";
        string? mine = notes.Split("; ").LastOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        string error = mine is null ? detail : mine[prefix.Length..];
        return error.Length <= LastErrorLength ? error : string.Concat(error.AsSpan(0, LastErrorLength - 1), "…");
    }

    /// <summary>What identifies where <paramref name="connector"/> sends requests: its type and program, or its scheme, host and port.</summary>
    public static string TargetKey(RouterConfiguration config, string connector) => Target(config.Connectors[connector], EnabledModels(config, connector)).Key;

    private static ModelDefinition[] EnabledModels(RouterConfiguration config, string connector) => config.Models
        .Where(m => m.Enabled && string.Equals(m.Connector, connector, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>Where requests go. Only the scheme, host and port of an address: its path or query may carry a key.</summary>
    private static EndpointTarget Target(ConnectorOptions options, ModelDefinition[] models)
    {
        if (options.RunsProgram)
        {
            bool cli = options.Type == ConnectorTypes.Cli;
            string program = !string.IsNullOrWhiteSpace(options.Executable) ? options.Executable
                : cli ? CliAdapter.For(options.Preset).DefaultExecutable
                : options.Type;
            string name = Path.GetFileName(program);
            string? via = !cli ? options.Type : options.Preset switch
            {
                CliPreset.ClaudeCode => "Anthropic",
                CliPreset.Codex => "OpenAI",
                CliPreset.GitHubCopilot => "GitHub Copilot",
                _ => null,
            };

            // A preset CLI talks to its vendor, and a packaged type (Copilot) to its own; what a custom command does is
            // only known from its models' IsLocal.
            bool local = via is null && models.Length > 0 && models.All(m => m.IsLocal);
            return new EndpointTarget(null, null, name, via, local, local || via is not null, $"{options.Type}|{options.Preset}|{program}");
        }

        if (options.BaseAddress is not { IsAbsoluteUri: true } address)
        {
            return new EndpointTarget(null, null, null, null, false, false, string.Empty);
        }

        bool loopback = address.IsLoopback || LocalHosts.IsLocalName(address.Host);
        string host = LocalHosts.Authority(address);
        return new EndpointTarget(
            host, address.Scheme, null, null, loopback, loopback || address.Scheme == Uri.UriSchemeHttps, $"{options.Type}|{address.Scheme}://{host}");
    }

    private static double? Percentile(IEnumerable<double?> values, double quantile)
    {
        double[] sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)], 0);
    }

    private sealed record Attempt(string Model, DateTimeOffset At, bool Succeeded, double? FirstByte, double? Total, double? TokensPerSecond, string? Error);

    private sealed record EndpointTarget(string? Host, string? Scheme, string? Program, string? Via, bool Local, bool Encrypted, string Key);
}

/// <summary>
/// The latest on-demand check of each connector (<c>POST api/connectors/{name}/check</c>). A check of a connector that
/// is already being checked joins it rather than starting another, so the button cannot pile up probes or processes.
/// </summary>
internal sealed class EndpointChecks(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Target, EndpointCheck Check)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<EndpointCheck>>> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The latest check of <paramref name="connector"/>, unless it has been pointed somewhere else since.</summary>
    public EndpointCheck? Last(string connector, string target) =>
        _last.TryGetValue(connector, out var last) && last.Target == target ? last.Check : null;

    /// <summary>Runs <paramref name="check"/> for <paramref name="connector"/>, or joins the run already under way.</summary>
    public Task<EndpointCheck> RunAsync(string connector, string target, Func<Task<IReadOnlyList<RouterCheck>>> check)
    {
        Lazy<Task<EndpointCheck>> run = _running.GetOrAdd(connector, key => new Lazy<Task<EndpointCheck>>(async () =>
        {
            try
            {
                long started = _time.GetTimestamp();
                IReadOnlyList<RouterCheck> results = await check().ConfigureAwait(false);
                RouterCheckStatus worst = results.Count == 0 ? RouterCheckStatus.Ok : results.Max(r => r.Status);
                var outcome = new EndpointCheck
                {
                    At = _time.GetUtcNow(),
                    Status = worst.ToString(),
                    Milliseconds = Math.Round(_time.GetElapsedTime(started).TotalMilliseconds, 0),
                    Messages = [.. results.Select(r => r.Subject.StartsWith("connector ", StringComparison.Ordinal) ? r.Message : $"{r.Subject}: {r.Message}")],
                };
                _last[connector] = (target, outcome);
                return outcome;
            }
            finally
            {
                _running.TryRemove(connector, out _);
            }
        }));
        return run.Value;
    }
}

/// <summary>A connector's endpoint and its health, as <c>GET api/status</c> reports it under <c>endpoints</c>.</summary>
public sealed record EndpointStatus
{
    /// <summary>The connector's name.</summary>
    public required string Connector { get; init; }

    /// <summary>Its type (<c>OpenAICompatible</c>, <c>Anthropic</c>, <c>Cli/ClaudeCode</c>, …).</summary>
    public required string Kind { get; init; }

    /// <summary>The host (and port, when not the default) requests go to; <see langword="null"/> for a program.</summary>
    public string? Host { get; init; }

    /// <summary><c>https</c> or <c>http</c>, for an address.</summary>
    public string? Scheme { get; init; }

    /// <summary>The program a CLI or Copilot connector runs on this machine.</summary>
    public string? Program { get; init; }

    /// <summary>Whom that program sends prompts to (<c>Anthropic</c>, <c>OpenAI</c>, <c>GitHub Copilot</c>), when its preset says.</summary>
    public string? Via { get; init; }

    /// <summary>Whether prompts stay on this machine (a loopback address, or a custom command whose models are all local).</summary>
    public required bool Local { get; init; }

    /// <summary>Whether requests leave this machine only over TLS (HTTPS, or a vendor CLI).</summary>
    public required bool Encrypted { get; init; }

    /// <summary><c>ok</c>, <c>degraded</c>, <c>failing</c>, <c>down</c>, <c>idle</c> or <c>unused</c>.</summary>
    public required string State { get; init; }

    /// <summary>Why the state is what it is.</summary>
    public string? StateNote { get; init; }

    /// <summary>Enabled models served through it.</summary>
    public required int Models { get; init; }

    /// <summary>How many of them are cooling down.</summary>
    public required int ModelsCoolingDown { get; init; }

    /// <summary>When the first of those becomes available again.</summary>
    public DateTimeOffset? CooldownUntil { get; init; }

    /// <summary>Requests sent to it in the activity log (failed attempts that failed over included).</summary>
    public required int Attempts { get; init; }

    /// <summary>How many of those failed.</summary>
    public required int Failures { get; init; }

    /// <summary>The share that was answered.</summary>
    public double? SuccessRate { get; init; }

    /// <summary>The latest request sent to it.</summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>The latest it answered.</summary>
    public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>The latest that failed.</summary>
    public DateTimeOffset? LastFailure { get; init; }

    /// <summary>Why that one failed (redacted).</summary>
    public string? LastError { get; init; }

    /// <summary>Since when every request to it has failed, when they all have.</summary>
    public DateTimeOffset? FailingSince { get; init; }

    /// <summary>Days in the last 7 on which it hit a usage limit.</summary>
    public required int LimitDays { get; init; }

    /// <summary>Median time to the first byte of a reply, in milliseconds, from the start of the attempt that served it.</summary>
    public double? FirstByteP50 { get; init; }

    /// <summary>95th percentile time to the first byte.</summary>
    public double? FirstByteP95 { get; init; }

    /// <summary>Median time to the end of a reply.</summary>
    public double? TotalP50 { get; init; }

    /// <summary>95th percentile time to the end of a reply.</summary>
    public double? TotalP95 { get; init; }

    /// <summary>Median output tokens per second after the first byte, when the backend reported usage.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>The latest on-demand check, if any since it was last pointed somewhere else.</summary>
    public EndpointCheck? Check { get; init; }
}

/// <summary>The outcome of checking a connector on demand: reachable, credentials accepted, installed, signed in.</summary>
public sealed record EndpointCheck
{
    /// <summary>When the check finished.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary><c>Ok</c>, <c>Warning</c> or <c>Failed</c>: the worst of its findings.</summary>
    public required string Status { get; init; }

    /// <summary>How long it took.</summary>
    public required double Milliseconds { get; init; }

    /// <summary>What it found. Never contains a secret.</summary>
    public required IReadOnlyList<string> Messages { get; init; }
}
