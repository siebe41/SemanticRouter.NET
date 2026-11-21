using System.Globalization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The quota forecast (#374): for each subscription connector (one that runs a program, such as Claude Code, Copilot or
/// Codex) and each connector with a usage limit set by hand (<see cref="UsageOptions.Quotas"/>), when the current pace
/// runs into the limit and how that compares with the next reset; and for each budget, when the pace spends it. Running
/// hot, it names the routes and clients driving the burn and where they could go instead; running cold, the routes that
/// could move up to the subscription. It reads the usage ledger and the limit episodes
/// (<see cref="ConnectorIncidents.LimitEpisodes"/>), and changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// A subscription does not publish its limit, so it is learned: a <em>cycle</em> runs from a reset to the next time the
/// connector ran out, and the limit is the median of what the router sent through the connector in its recent cycles. A
/// cycle starts at the previous episode's reset (<see cref="LimitEpisode.Reset"/>); with a known period it starts a
/// whole period before its own reset. Usage the router never saw (the same account used directly) is not in the ledger,
/// so the learned limit is the router's share of it.
/// </para>
/// <para>
/// The pace is the usage per hour over the last cycle length (at most 7 days, in seven buckets, each weighing half as
/// much as the one two buckets newer), not counting the time the connector was out of its limit. The error band comes
/// from a back-test over the stored cycles: each is replayed from its midpoint with the limit learned from the others.
/// </para>
/// </remarks>
internal sealed class QuotaForecaster(IUsageLedger ledger, ConnectorIncidents incidents, BudgetGate budgets, TimeProvider? time = null)
{
    /// <summary>The most recent cycles a limit is learned from.</summary>
    public const int MaxCycles = 8;

    /// <summary>The longest cycle without a known period: a reset further back is not taken as the start of this one.</summary>
    public static readonly TimeSpan MaxCycle = ModelHealthTracker.MaximumResetCooldown;

    /// <summary>Share of the limit left unused at the reset from which a connector is running cold.</summary>
    public const double ColdShare = 0.25;

    /// <summary>The most routes and clients listed as driving the burn, or as able to move up.</summary>
    public const int MaxListed = 5;

    private const int Buckets = 7;
    private static readonly TimeSpan PaceWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinPeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(366);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The forecast for every subscription connector, every connector with a quota and every budget, as of now.</summary>
    public QuotaForecast Forecast(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        DateTimeOffset now = _time.GetUtcNow();
        TimeZoneInfo zone = BudgetGate.ZoneOf(configuration.Usage);
        var connectors = new List<ConnectorForecast>();
        IReadOnlyList<UsageRecord>? recent = null;
        foreach ((string name, ConnectorOptions options) in configuration.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            UsageQuota? quota = configuration.Usage.Quotas.TryGetValue(name, out UsageQuota? set) ? set : null;
            if (!options.RunsProgram && quota is null)
            {
                continue;
            }

            ConnectorForecast forecast = ForecastConnector(name, options.RunsProgram, quota, configuration, now, zone);
            if (forecast.Status == QuotaStatus.Cold)
            {
                recent ??= ledger.Records(now - PaceWindow, now) ?? [];
                forecast = forecast with { Upgrades = Upgrades(forecast, recent, configuration, now) };
            }

            connectors.Add(forecast);
        }

        return new QuotaForecast
        {
            GeneratedAt = now,
            TimeZone = zone.Id,
            Connectors = connectors,
            Budgets = [.. ForecastBudgets(configuration, now)],
        };
    }

    private ConnectorForecast ForecastConnector(string name, bool subscription, UsageQuota? quota, RouterConfiguration configuration, DateTimeOffset now, TimeZoneInfo zone)
    {
        IReadOnlyList<LimitEpisode> episodes = incidents.LimitEpisodes(name);
        TimeSpan? inferred = quota?.Period is null ? InferPeriod(episodes, now) : null;
        TimeSpan? period = quota?.Period ?? inferred;
        string? periodSource = quota?.Period is not null ? "configured" : inferred is not null ? "learned" : null;
        // A few more than are learned from, for the ones the ledger has nothing for: they bound how far back records are read.
        List<Cycle> cycles = [.. Cycles(episodes, period, now).TakeLast(MaxCycles + 4)];
        DateTimeOffset since = cycles.Count > 0 ? Min(cycles.Min(c => c.Start), now - PaceWindow - PaceWindow) : now - PaceWindow - PaceWindow;
        IReadOnlyList<UsageRecord>? records = ledger.Records(since, now, connector: name);
        var notes = new List<string>();
        if (records is null)
        {
            return new ConnectorForecast
            {
                Connector = name,
                Subscription = subscription,
                Status = QuotaStatus.Unknown,
                Unit = QuotaUnit.Tokens,
                Limit = new QuotaLimit { Confidence = "none" },
                LimitHits = episodes.Count,
                Summary = $"{name}: the usage ledger keeps only totals, and the forecast needs its records.",
                Notes = ["The usage ledger keeps only totals, and the forecast needs its records."],
            };
        }

        QuotaUnit unit = quota?.MaxRequests is not null ? QuotaUnit.Requests
            : quota?.MaxTokens is not null || records.Any(r => r.InputTokens + r.OutputTokens + r.CacheReadTokens + r.CacheWriteTokens > 0) ? QuotaUnit.Tokens
            : QuotaUnit.Requests;
        var curve = new UsageCurve(records, unit);
        var outages = episodes.Select(e => (e.Hit, End: e.Reset ?? now)).ToArray();
        TimeSpan window = PaceWindowFor(period, cycles);

        foreach (Cycle cycle in cycles)
        {
            cycle.Used = curve.Between(cycle.Start, cycle.Hit);
        }

        // A cycle with nothing recorded is older than the ledger keeps (or the account was used only elsewhere).
        cycles = [.. cycles.Where(c => c.Used > 0).TakeLast(MaxCycles)];
        double? learned = cycles.Count > 0 ? Median(cycles.Select(c => c.Used)) : null;
        double? configured = unit == QuotaUnit.Requests ? quota?.MaxRequests : quota?.MaxTokens;
        double? limit = configured ?? learned;
        var limitInfo = new QuotaLimit
        {
            Value = limit,
            Source = configured is not null ? "configured" : learned is not null ? "learned" : null,
            Learned = learned,
            Cycles = cycles.Count,
            LearnedMin = cycles.Count > 0 ? cycles.Min(c => c.Used) : null,
            LearnedMax = cycles.Count > 0 ? cycles.Max(c => c.Used) : null,
            Confidence = configured is not null ? "configured" : cycles.Count switch { 0 => "none", 1 => "low", < 4 => "medium", _ => "high" },
        };

        QuotaBacktest? backtest = Backtest(cycles, configured, curve, outages, window);
        double pace = Pace(curve, outages, now, window);

        // Where the current cycle stands.
        LimitEpisode? last = episodes.Count > 0 ? episodes[^1] : null;
        DateTimeOffset? limitedUntil = last is { ServedAgain: null } open
            ? open.ResetsAt is { } resets && resets > now ? resets
            : open.ResetsAt is null && now - open.LastHit < configuration.Selection.QuotaCooldown ? open.LastHit + configuration.Selection.QuotaCooldown
            : null
            : null;
        DateTimeOffset? lastReset = episodes.Select(e => e.Reset).Where(r => r <= now).Max();
        DateTimeOffset? start = null, nextReset = null;
        if (limitedUntil is not null)
        {
            nextReset = last!.ResetsAt;
        }
        else if (period is { } p)
        {
            DateTimeOffset? anchor = quota?.PeriodStart ?? lastReset;
            if (anchor is { } a)
            {
                start = a + (p * Math.Floor((now - a) / p));
                nextReset = start + p;
            }
        }
        else if (lastReset is { } r)
        {
            // Without a period, the cycle started at the last reset, unless that is longer ago than a cycle lasts.
            TimeSpan longest = cycles.Count > 0 ? cycles.Max(c => c.Hit - c.Start) * 1.5 : MaxCycle;
            if (now - r <= Min(longest < MinPeriod ? MinPeriod : longest, MaxCycle))
            {
                start = r;
            }
        }

        double? used = start is { } s ? curve.Between(s, now) : null;
        DateTimeOffset? runsOut = null, earliest = null, latest = null;
        double? shareAtReset = null;
        QuotaStatus status;
        if (limitedUntil is not null)
        {
            status = QuotaStatus.Limited;
        }
        else if (limit is not { } l || used is not { } u)
        {
            status = QuotaStatus.Unknown;
            notes.Add(limit is null
                ? episodes.Count == 0
                    ? "No usage limit hit recorded yet, and none set by hand (Usage:Quotas), so there is no limit to forecast against."
                    : "The limit hits recorded so far do not show a whole cycle (from a reset to the next hit) with usage in the ledger."
                : "No reset seen yet to count the current cycle from; set Usage:Quotas:" + name + ":Period and PeriodStart.");
        }
        else
        {
            double remaining = l - u;
            runsOut = remaining <= 0 ? now : pace > 0 && remaining / pace < Horizon.TotalHours ? now + TimeSpan.FromHours(remaining / pace) : null;
            if (runsOut is { } at && backtest is not null && at > now)
            {
                TimeSpan half = (at - now) * backtest.MeanRelativeError;
                earliest = at - half;
                latest = at + half;
            }

            if (nextReset is { } next)
            {
                shareAtReset = (u + (pace * (next - now).TotalHours)) / l;
                status = runsOut is { } when && when < next ? QuotaStatus.Hot : 1 - shareAtReset >= ColdShare ? QuotaStatus.Cold : QuotaStatus.Ok;
            }
            else
            {
                status = runsOut is { } when && when - now <= TimeSpan.FromDays(1) ? QuotaStatus.Hot : QuotaStatus.Ok;
                notes.Add("The next reset is not known (the resets seen do not repeat at a fixed interval): set Usage:Quotas:" + name + ":Period to compare against it.");
            }
        }

        var forecast = new ConnectorForecast
        {
            Connector = name,
            Subscription = subscription,
            Status = status,
            Unit = unit,
            Limit = limitInfo,
            Period = period,
            PeriodSource = periodSource,
            CycleStart = start,
            NextReset = nextReset,
            LimitedUntil = limitedUntil,
            Used = used,
            UsedShare = used is { } usedNow && limit is > 0 ? usedNow / limit.Value : null,
            PacePerHour = pace,
            RunsOutAt = runsOut,
            RunsOutEarliest = earliest,
            RunsOutLatest = latest,
            ShareAtReset = shareAtReset,
            Backtest = backtest,
            LimitHits = episodes.Count,
            Notes = notes,
            Summary = string.Empty,
        };

        // What drives the burn: the current cycle, or the last week without one.
        DateTimeOffset from = start ?? now - PaceWindow;
        UsageRecord[] driving = [.. records.Where(r => r.Timestamp >= from)];
        forecast = forecast with
        {
            Routes = Drivers(driving, unit, r => r.Route ?? UsageSummaries.NoRoute, route => MoveFor(route, name, configuration)),
            Clients = Drivers(driving, unit, r => r.Client ?? UsageSummaries.NoClient, _ => null),
        };
        return forecast with { Summary = Summarize(forecast, zone) };
    }

    /// <summary>
    /// The period of resets reported at a fixed interval: the shortest gap between consecutive reported resets, when every
    /// gap is a whole number of it (within 2% or 15 minutes) and there are at least two gaps.
    /// </summary>
    internal static TimeSpan? InferPeriod(IReadOnlyList<LimitEpisode> episodes, DateTimeOffset now)
    {
        DateTimeOffset[] resets = [.. episodes.Select(e => e.ResetsAt).OfType<DateTimeOffset>().Where(r => r <= now).Distinct().Order()];
        if (resets.Length < 3)
        {
            return null;
        }

        TimeSpan[] gaps = [.. resets.Zip(resets.Skip(1), (a, b) => b - a)];
        TimeSpan shortest = gaps.Min();
        if (shortest < MinPeriod || shortest > TimeSpan.FromDays(31))
        {
            return null;
        }

        TimeSpan tolerance = Max(TimeSpan.FromMinutes(15), shortest * 0.02);
        foreach (TimeSpan gap in gaps)
        {
            double periods = Math.Round(gap / shortest);
            if ((gap - (shortest * periods)).Duration() > tolerance)
            {
                return null;
            }
        }

        // The mean of the gaps of one period, to the minute: a reset reported as "resets 9am" is to the minute.
        TimeSpan mean = TimeSpan.FromTicks((long)gaps.Where(g => Math.Round(g / shortest) == 1).Average(g => g.Ticks));
        return TimeSpan.FromMinutes(Math.Round(mean.TotalMinutes));
    }

    /// <summary>Each episode whose cycle start is known: from the reset before it to when it ran out.</summary>
    private static List<Cycle> Cycles(IReadOnlyList<LimitEpisode> episodes, TimeSpan? period, DateTimeOffset now)
    {
        var cycles = new List<Cycle>();
        for (int k = 0; k < episodes.Count; k++)
        {
            LimitEpisode episode = episodes[k];
            DateTimeOffset? previous = k > 0 && episodes[k - 1].Reset is { } reset && reset < episode.Hit ? reset : null;
            DateTimeOffset? start = null;
            if (period is { } p)
            {
                if (episode.Reset is { } own && own > episode.Hit)
                {
                    start = own - p;
                }
                else if (previous is { } prior)
                {
                    start = prior + (p * Math.Floor((episode.Hit - prior) / p));
                }

                if (start is { } s && previous is { } after && after > s)
                {
                    start = after;
                }
            }
            else if (previous is { } prior && episode.Hit - prior <= MaxCycle)
            {
                start = prior;
            }

            if (start is { } begin && begin < episode.Hit && episode.Hit <= now)
            {
                cycles.Add(new Cycle { Start = begin, Hit = episode.Hit });
            }
        }

        return cycles;
    }

    /// <summary>The cycle length (or the known period), within an hour and 7 days: how far back the pace looks.</summary>
    private static TimeSpan PaceWindowFor(TimeSpan? period, List<Cycle> cycles)
    {
        TimeSpan length = period ?? (cycles.Count > 0 ? TimeSpan.FromTicks((long)Median(cycles.Select(c => (double)(c.Hit - c.Start).Ticks))) : PaceWindow);
        return length < MinPeriod ? MinPeriod : length > PaceWindow ? PaceWindow : length;
    }

    /// <summary>
    /// The buckets a pace is measured in: whole days for a window of a day or more, so a daily rhythm (nights, office
    /// hours) weighs the same in each; seven for a shorter one.
    /// </summary>
    private static (TimeSpan Bucket, int Count) BucketsOf(TimeSpan window) =>
        window >= TimeSpan.FromDays(1) ? (TimeSpan.FromDays(1), (int)Math.Clamp(Math.Round(window.TotalDays), 1, Buckets)) : (window / Buckets, Buckets);

    /// <summary>
    /// Usage per hour up to <paramref name="at"/> over <paramref name="window"/>, in buckets (<see cref="BucketsOf"/>)
    /// that each weigh half as much as the one two newer, without the time the connector was out of its limit (when it
    /// could not be used) or before its first record.
    /// </summary>
    private static double Pace(UsageCurve curve, (DateTimeOffset Hit, DateTimeOffset End)[] outages, DateTimeOffset at, TimeSpan window)
    {
        (TimeSpan bucket, int count) = BucketsOf(window);
        double used = 0, hours = 0;
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset end = at - (bucket * i);
            DateTimeOffset start = end - bucket;
            DateTimeOffset from = curve.First is { } first && first > start ? Min(first, end) : start;
            TimeSpan open = (end - from) - outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + Overlap(from, end, o.Hit, o.End));
            double weight = Math.Pow(0.5, i / 2.0);
            used += weight * curve.Between(start, end);
            hours += weight * Math.Max(0, open.TotalHours);
        }

        return hours > 0 ? used / hours : 0;
    }

    /// <summary>
    /// Replays each stored cycle from its midpoint: the limit learned from the other cycles (or the one set by hand) and
    /// the pace at that moment give a run-out time, compared with when the connector really ran out.
    /// </summary>
    private static QuotaBacktest? Backtest(List<Cycle> cycles, double? configured, UsageCurve curve, (DateTimeOffset Hit, DateTimeOffset End)[] outages, TimeSpan window)
    {
        if (cycles.Count < (configured is null ? 2 : 1))
        {
            return null;
        }

        var absolute = new List<double>();
        var relative = new List<double>();
        for (int k = 0; k < cycles.Count; k++)
        {
            Cycle cycle = cycles[k];
            double limit = configured ?? Median(cycles.Where((_, j) => j != k).Select(c => c.Used));
            DateTimeOffset middle = cycle.Start + ((cycle.Hit - cycle.Start) / 2);
            double used = curve.Between(cycle.Start, middle);
            double pace = Pace(curve, outages, middle, window);
            if (pace <= 0)
            {
                continue;
            }

            DateTimeOffset predicted = used >= limit ? middle : middle + TimeSpan.FromHours(Math.Min((limit - used) / pace, Horizon.TotalHours));
            TimeSpan error = (predicted - cycle.Hit).Duration();
            absolute.Add(error.TotalHours);
            relative.Add(error / (cycle.Hit - middle));
        }

        return absolute.Count == 0 ? null : new QuotaBacktest
        {
            Cycles = absolute.Count,
            MeanAbsoluteErrorHours = Math.Round(absolute.Average(), 2),
            MeanRelativeError = Math.Round(relative.Average(), 3),
        };
    }

    /// <summary>The groups using the most, largest first, with their share of the whole.</summary>
    private static IReadOnlyList<QuotaDriver> Drivers(UsageRecord[] records, QuotaUnit unit, Func<UsageRecord, string> key, Func<string, QuotaMove?> move)
    {
        double total = records.Sum(r => UsageCurve.Amount(r, unit));
        return total <= 0 ? [] : [.. records
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Requests: g.Count(), Amount: g.Sum(r => UsageCurve.Amount(r, unit))))
            .OrderByDescending(g => g.Amount)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxListed)
            .Select(g => new QuotaDriver { Key = g.Key, Requests = g.Requests, Amount = g.Amount, Share = Math.Round(g.Amount / total, 3), MoveTo = move(g.Key) })];
    }

    /// <summary>
    /// Where a route burning a connector's limit could go instead: a local model that already serves its tier, else the
    /// highest-tier local model below it (lower the route's tier), else a model that costs nothing on another connector.
    /// </summary>
    private static QuotaMove? MoveFor(string route, string connector, RouterConfiguration configuration)
    {
        RouteDefinition? definition = configuration.Routes.FirstOrDefault(r => string.Equals(r.Name, route, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return null;
        }

        ModelDefinition[] others = [.. configuration.Models.Where(m => m.Enabled && !string.Equals(m.Connector, connector, StringComparison.OrdinalIgnoreCase))];
        ModelDefinition? local = others.Where(m => m.IsLocal && m.Tier >= definition.Tier).OrderBy(m => m.Tier).FirstOrDefault()
            ?? others.Where(m => m.IsLocal).OrderByDescending(m => m.Tier).FirstOrDefault();
        return local is not null
            ? new QuotaMove { Route = definition.Name, FromTier = definition.Tier, Tier = Math.Min(local.Tier, definition.Tier), Model = local.Name, Local = true }
            : null;
    }

    /// <summary>Routes served elsewhere last week (not by a subscription) whose tier is below what this connector serves.</summary>
    private static IReadOnlyList<QuotaUpgrade> Upgrades(ConnectorForecast forecast, IReadOnlyList<UsageRecord> recent, RouterConfiguration configuration, DateTimeOffset now)
    {
        ModelDefinition? top = configuration.Models
            .Where(m => m.Enabled && string.Equals(m.Connector, forecast.Connector, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Tier).ThenByDescending(m => m.Priority).FirstOrDefault();
        if (top is null || forecast.Limit.Value is not { } limit || forecast.ShareAtReset is not { } share || forecast.NextReset is not { } reset)
        {
            return [];
        }

        double spare = Math.Max(0, (1 - share) * limit);
        double hours = Math.Max(0, (reset - now).TotalHours);
        HashSet<string> subscriptions = new(configuration.Connectors.Where(c => c.Value.RunsProgram).Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        var upgrades = new List<QuotaUpgrade>();
        foreach (IGrouping<string, UsageRecord> group in recent
                     .Where(r => r.Route is not null && !subscriptions.Contains(r.Connector) && !string.Equals(r.Connector, forecast.Connector, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(r => r.Route!, StringComparer.OrdinalIgnoreCase))
        {
            RouteDefinition? route = configuration.Routes.FirstOrDefault(r => string.Equals(r.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (route is null || route.Tier >= top.Tier)
            {
                continue;
            }

            double amount = group.Sum(r => UsageCurve.Amount(r, forecast.Unit));
            double untilReset = amount / PaceWindow.TotalHours * hours;
            upgrades.Add(new QuotaUpgrade
            {
                Route = route.Name,
                Tier = route.Tier,
                Requests = group.Count(),
                Amount = amount,
                AmountUntilReset = Math.Round(untilReset),
                Fits = untilReset <= spare,
                ServedBy = [.. group.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)],
                MoveTo = new QuotaMove { Route = route.Name, FromTier = route.Tier, Tier = top.Tier, Model = top.Name, Local = top.IsLocal },
            });
        }

        return [.. upgrades.OrderByDescending(u => u.Fits).ThenByDescending(u => u.Requests).ThenBy(u => u.Route, StringComparer.Ordinal).Take(MaxListed)];
    }

    /// <summary>Each budget: when its pace spends it, against the end of its period.</summary>
    private IEnumerable<BudgetForecast> ForecastBudgets(RouterConfiguration configuration, DateTimeOffset now)
    {
        UsageOptions usage = configuration.Usage;
        Dictionary<string, BudgetStatus> states = budgets.Status(usage).GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (BudgetDefinition budget in usage.Budgets)
        {
            if (!states.TryGetValue(budget.Name, out BudgetStatus? state))
            {
                continue;
            }

            // A day's budget looks back a day; a week's or a month's, a week.
            TimeSpan window = budget.Period == BudgetPeriod.Day ? TimeSpan.FromDays(1) : PaceWindow;
            TimeSpan bucket = window / Buckets;
            double tokens = 0, cost = 0, hours = 0;
            for (int i = 0; i < Buckets; i++)
            {
                DateTimeOffset end = now - (bucket * i);
                UsageTotals spent = ledger.TotalAtCurrentPrices(configuration, end - bucket, end, NullIfEmpty(budget.Model), NullIfEmpty(budget.Connector), NullIfEmpty(budget.Profile));
                double weight = Math.Pow(0.5, i / 2.0);
                tokens += weight * spent.TotalTokens;
                cost += weight * (double)spent.CostUsd;
                hours += weight * bucket.TotalHours;
            }

            var measures = new List<(QuotaUnit Unit, double Used, double Limit, double Pace)>();
            if (budget.MaxTokens is > 0 and { } maxTokens)
            {
                measures.Add((QuotaUnit.Tokens, state.TokensUsed, maxTokens, tokens / hours));
            }

            if (budget.MaxCostUsd is > 0 and { } maxCost)
            {
                measures.Add((QuotaUnit.Usd, (double)state.CostUsedUsd, (double)maxCost, cost / hours));
            }

            if (measures.Count == 0)
            {
                continue;
            }

            (QuotaUnit unit, double used, double limit, double pace) = measures.MinBy(m => m.Used >= m.Limit ? 0 : m.Pace > 0 ? (m.Limit - m.Used) / m.Pace : double.MaxValue);
            DateTimeOffset? runsOut = used >= limit ? now : pace > 0 && (limit - used) / pace < Horizon.TotalHours ? now + TimeSpan.FromHours((limit - used) / pace) : null;
            yield return new BudgetForecast
            {
                Name = budget.Name,
                Covers = state.Covers,
                Period = state.Period.ToString(),
                PeriodEnd = state.PeriodEnd,
                Unit = unit,
                Used = used,
                Limit = limit,
                PacePerHour = pace,
                RunsOutAt = runsOut,
                ShareAtEnd = (used + (pace * Math.Max(0, (state.PeriodEnd - now).TotalHours))) / limit,
                Status = state.Exceeded ? QuotaStatus.Spent : runsOut is { } at && at < state.PeriodEnd ? QuotaStatus.Hot : QuotaStatus.Ok,
            };
        }
    }

    /// <summary>One line for a person or a script, with times in the usage time zone.</summary>
    private static string Summarize(ConnectorForecast f, TimeZoneInfo zone)
    {
        string When(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        string reset = f.NextReset is { } next ? $"; it resets {When(next)}" : string.Empty;
        string band = f.RunsOutEarliest is { } early && f.RunsOutLatest is { } late ? $" (between {When(early)} and {When(late)})" : string.Empty;
        return f.Status switch
        {
            QuotaStatus.Limited => $"{f.Connector} is out of its limit{(f.LimitedUntil is { } until ? $" until about {When(until)}" : string.Empty)}.",
            QuotaStatus.Unknown => $"{f.Connector}: {(f.Notes.Count > 0 ? f.Notes[0] : "no forecast")}",
            QuotaStatus.Cold => $"At this pace {f.Connector} leaves about {Math.Round((1 - f.ShareAtReset!.Value) * 100)}% of its limit unused{reset}.",
            _ when f.RunsOutAt is { } at => $"At this pace {f.Connector} runs out {When(at)}{band}{reset}.",
            _ => $"At this pace {f.Connector} does not run out{reset}.",
        };
    }

    private static TimeSpan Overlap(DateTimeOffset from, DateTimeOffset to, DateTimeOffset start, DateTimeOffset end)
    {
        DateTimeOffset a = Max(from, start), b = Min(to, end);
        return b > a ? b - a : TimeSpan.Zero;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>From a reset to the time the connector ran out, and what the router sent through it meanwhile.</summary>
    private sealed class Cycle
    {
        public required DateTimeOffset Start { get; init; }

        public required DateTimeOffset Hit { get; init; }

        public double Used { get; set; }
    }

    /// <summary>A connector's usage added up over time, to read any interval's in a binary search.</summary>
    private sealed class UsageCurve
    {
        private readonly long[] _ticks;
        private readonly double[] _sums;

        public UsageCurve(IReadOnlyList<UsageRecord> records, QuotaUnit unit)
        {
            UsageRecord[] sorted = [.. records.OrderBy(r => r.Timestamp)];
            _ticks = [.. sorted.Select(r => r.Timestamp.UtcTicks)];
            _sums = new double[sorted.Length + 1];
            for (int i = 0; i < sorted.Length; i++)
            {
                _sums[i + 1] = _sums[i] + Amount(sorted[i], unit);
            }

            First = sorted.Length > 0 ? sorted[0].Timestamp : null;
        }

        /// <summary>The first record, before which nothing was recorded.</summary>
        public DateTimeOffset? First { get; }

        public static double Amount(UsageRecord record, QuotaUnit unit) =>
            unit == QuotaUnit.Requests ? 1 : record.InputTokens + record.OutputTokens + record.CacheReadTokens + record.CacheWriteTokens;

        /// <summary>Usage from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive).</summary>
        public double Between(DateTimeOffset from, DateTimeOffset to) => to <= from ? 0 : Before(to) - Before(from);

        private double Before(DateTimeOffset at)
        {
            int index = Array.BinarySearch(_ticks, at.UtcTicks);
            if (index < 0)
            {
                return _sums[~index];
            }

            while (index > 0 && _ticks[index - 1] == at.UtcTicks)
            {
                index--;
            }

            return _sums[index];
        }
    }
}

/// <summary>How a connector or budget stands against its limit.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<QuotaStatus>))]
internal enum QuotaStatus
{
    /// <summary>No limit to forecast against (none learned or set), or no current cycle to count from.</summary>
    Unknown = 0,

    /// <summary>On pace to stay within the limit, with less than <see cref="QuotaForecaster.ColdShare"/> left over.</summary>
    Ok = 1,

    /// <summary>On pace to run out before the next reset.</summary>
    Hot = 2,

    /// <summary>On pace to leave at least <see cref="QuotaForecaster.ColdShare"/> of the limit unused at the reset.</summary>
    Cold = 3,

    /// <summary>Out of its limit now.</summary>
    Limited = 4,

    /// <summary>A budget that is spent for this period.</summary>
    Spent = 5,
}

/// <summary>What a limit counts.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<QuotaUnit>))]
internal enum QuotaUnit
{
    /// <summary>Input, output and cache tokens.</summary>
    Tokens = 0,

    /// <summary>Requests: for a connector that reports no tokens, or a limit set in requests.</summary>
    Requests = 1,

    /// <summary>Dollars, for a budget's spend.</summary>
    Usd = 2,
}

/// <summary>The forecast (<c>GET api/forecast</c>).</summary>
internal sealed record QuotaForecast
{
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The time zone the summaries give times in (<see cref="UsageOptions.TimeZone"/>).</summary>
    public required string TimeZone { get; init; }

    public required IReadOnlyList<ConnectorForecast> Connectors { get; init; }

    public required IReadOnlyList<BudgetForecast> Budgets { get; init; }

    /// <summary>The dashboard page that shows what moving routes would do.</summary>
    public string WhatIf { get; init; } = "#/whatif";
}

/// <summary>One connector's forecast. Amounts are in <see cref="Unit"/>.</summary>
internal sealed record ConnectorForecast
{
    public required string Connector { get; init; }

    /// <summary>The connector runs a program (a subscription CLI).</summary>
    public required bool Subscription { get; init; }

    public required QuotaStatus Status { get; init; }

    public required QuotaUnit Unit { get; init; }

    /// <summary>One line, with times in the usage time zone.</summary>
    public required string Summary { get; init; }

    public required QuotaLimit Limit { get; init; }

    /// <summary>How long the limit lasts before it resets, if known.</summary>
    public TimeSpan? Period { get; init; }

    /// <summary><c>configured</c> or <c>learned</c> (from resets at a fixed interval); <see langword="null"/> when unknown.</summary>
    public string? PeriodSource { get; init; }

    /// <summary>When the current cycle started (the last reset), if known.</summary>
    public DateTimeOffset? CycleStart { get; init; }

    public DateTimeOffset? NextReset { get; init; }

    /// <summary>While it is out of its limit: until when.</summary>
    public DateTimeOffset? LimitedUntil { get; init; }

    /// <summary>Usage in the current cycle.</summary>
    public double? Used { get; init; }

    public double? UsedShare { get; init; }

    /// <summary>Usage per hour, weighted to recent hours.</summary>
    public double PacePerHour { get; init; }

    /// <summary>When the pace reaches the limit; <see langword="null"/> when it does not within a year (or there is no limit).</summary>
    public DateTimeOffset? RunsOutAt { get; init; }

    /// <summary>The back-test's error band around <see cref="RunsOutAt"/>.</summary>
    public DateTimeOffset? RunsOutEarliest { get; init; }

    public DateTimeOffset? RunsOutLatest { get; init; }

    /// <summary>The share of the limit used by the next reset at this pace (above 1: it runs out first).</summary>
    public double? ShareAtReset { get; init; }

    public QuotaBacktest? Backtest { get; init; }

    /// <summary>Limit episodes stored for the connector.</summary>
    public int LimitHits { get; init; }

    /// <summary>The routes using the most of it in the current cycle (or the last 7 days), with a local model that could take each.</summary>
    public IReadOnlyList<QuotaDriver> Routes { get; init; } = [];

    /// <summary>The gateway clients using the most of it.</summary>
    public IReadOnlyList<QuotaDriver> Clients { get; init; } = [];

    /// <summary>Running cold: routes served elsewhere last week that could move up to it.</summary>
    public IReadOnlyList<QuotaUpgrade> Upgrades { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The limit a forecast runs against.</summary>
internal sealed record QuotaLimit
{
    /// <summary>The limit used: the one set by hand, else the learned one.</summary>
    public double? Value { get; init; }

    /// <summary><c>configured</c> or <c>learned</c>.</summary>
    public string? Source { get; init; }

    /// <summary>The median usage of the learned cycles.</summary>
    public double? Learned { get; init; }

    /// <summary>Whole cycles (from a reset to the next hit) it is learned from.</summary>
    public int Cycles { get; init; }

    public double? LearnedMin { get; init; }

    public double? LearnedMax { get; init; }

    /// <summary><c>none</c> (no cycle), <c>low</c> (1), <c>medium</c> (2–3), <c>high</c> (4 or more), or <c>configured</c>.</summary>
    public required string Confidence { get; init; }
}

/// <summary>The forecast replayed over the stored cycles.</summary>
internal sealed record QuotaBacktest
{
    public required int Cycles { get; init; }

    public required double MeanAbsoluteErrorHours { get; init; }

    /// <summary>The error as a share of the time from the replay point (each cycle's midpoint) to the real run-out.</summary>
    public required double MeanRelativeError { get; init; }
}

/// <summary>A route or client and its share of a connector's usage.</summary>
internal sealed record QuotaDriver
{
    public required string Key { get; init; }

    public required int Requests { get; init; }

    public required double Amount { get; init; }

    public required double Share { get; init; }

    /// <summary>For a route: a model on another connector that could take it.</summary>
    public QuotaMove? MoveTo { get; init; }
}

/// <summary>A route's tier change that would send it to <see cref="Model"/>; check it on the What if page first.</summary>
internal sealed record QuotaMove
{
    public required string Route { get; init; }

    public required int FromTier { get; init; }

    public required int Tier { get; init; }

    public required string Model { get; init; }

    public required bool Local { get; init; }
}

/// <summary>A route served elsewhere that a cold subscription could take.</summary>
internal sealed record QuotaUpgrade
{
    public required string Route { get; init; }

    public required int Tier { get; init; }

    /// <summary>Requests in the last 7 days.</summary>
    public required int Requests { get; init; }

    /// <summary>Its usage in the last 7 days, in the connector's unit.</summary>
    public required double Amount { get; init; }

    /// <summary>Its usage from now to the reset, at last week's rate.</summary>
    public required double AmountUntilReset { get; init; }

    /// <summary>Whether that fits in what would be left unused.</summary>
    public required bool Fits { get; init; }

    public required IReadOnlyList<string> ServedBy { get; init; }

    public required QuotaMove MoveTo { get; init; }
}

/// <summary>One budget's forecast.</summary>
internal sealed record BudgetForecast
{
    public required string Name { get; init; }

    public required string Covers { get; init; }

    public required string Period { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>The measure that runs out first: <c>Tokens</c> or <c>Usd</c>.</summary>
    public required QuotaUnit Unit { get; init; }

    public required double Used { get; init; }

    public required double Limit { get; init; }

    public required double PacePerHour { get; init; }

    public DateTimeOffset? RunsOutAt { get; init; }

    /// <summary>The share used by the end of the period at this pace.</summary>
    public required double ShareAtEnd { get; init; }

    public required QuotaStatus Status { get; init; }
}
