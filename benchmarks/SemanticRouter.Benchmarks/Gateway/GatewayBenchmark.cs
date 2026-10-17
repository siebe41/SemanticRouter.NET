// The gateway's own cost and behaviour, with no model behind it: the router as a separate process (the semantic-router
// tool with SQLite state, its default) in front of a stub backend that answers at once.
//
//   dotnet build -c Release
//   dotnet run -c Release --no-build --project benchmarks/SemanticRouter.Benchmarks -- gateway [options]
//
//   (no options)          the latency table: time to first byte and total time, p50/p95/p99, at c = 1, 8 and 32
//   --latency             the latency table as well, with --soak or --faults
//   --quick               shorter runs (CI on every pull request)
//   --soak <seconds>      unique prompts at c = 8 for that long, in windows; fails when p50 or memory drift
//   --faults              500, 429, hang, stall, drop and client cancel, and two routers on one database; fails on
//                         client errors, missed failover or cooldown, or activity and usage that do not add up
//   --json <file>         write the results, for a baseline
//   --baseline <file>     compare with an earlier --json; regressions are reported (as GitHub warnings in Actions)
//   --host <dll>          the semantic-router.dll to run (default: the one the solution build produced)
//   --memory-state        the latency table with the router's state in memory (the database cannot be opened), to see
//                         what the database costs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SemanticRouter.Benchmarks.Gateway;

internal static class GatewayBenchmark
{
    private static readonly int[] Concurrency = [1, 8, 32];

    public static async Task<int> RunAsync(string[] args)
    {
        bool quick = args.Contains("--quick");
        bool faults = args.Contains("--faults");
        int soak = Option(args, "--soak") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        string? jsonOut = Option(args, "--json");
        string? baseline = Option(args, "--baseline");
        string repository = FindRepository();
        string host = Option(args, "--host") ?? HostAssembly(repository);
        if (!File.Exists(host))
        {
            Console.Error.WriteLine($"{host} is missing: build the solution first (dotnet build -c Release), or pass --host <semantic-router.dll>.");
            return 2;
        }

        Console.WriteLine($"Gateway benchmark on {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}. " +
                          "The load generator, the stub backend and the router share the machine: compare the columns, not production throughput.");
        Console.WriteLine();

        await using StubBackend stub = await StubBackend.StartAsync();
        var results = new JsonObject();
        bool passed = true;
        if (args.Contains("--latency") || (soak == 0 && !faults))
        {
            results["latency"] = await LatencyAsync(repository, host, stub, quick ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(10), args.Contains("--memory-state"));
        }

        if (soak > 0)
        {
            (JsonObject report, bool ok) = await SoakAsync(repository, host, stub, TimeSpan.FromSeconds(soak));
            results["soak"] = report;
            passed &= ok;
        }

        if (faults)
        {
            (JsonObject report, bool ok) = await FaultsAsync(repository, host, stub);
            results["faults"] = report;
            passed &= ok;
        }

        if (jsonOut is not null)
        {
            await File.WriteAllTextAsync(jsonOut, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        if (baseline is not null && File.Exists(baseline) && results["latency"] is JsonArray latency)
        {
            Compare(latency, JsonNode.Parse(await File.ReadAllTextAsync(baseline))?["latency"] as JsonArray);
        }

        Console.WriteLine(passed ? "All checks passed." : "Some checks FAILED (see above).");
        return passed ? 0 : 1;
    }

    private static async Task<JsonArray> LatencyAsync(string repository, string host, StubBackend stub, TimeSpan duration, bool memoryState)
    {
        string home = RouterProcess.Prepare(repository, stub.Address, "ok", memoryState);
        await using RouterProcess router = await RouterProcess.StartAsync(host, home);
        using HttpClient http = LoadGenerator.Client();
        var load = new LoadGenerator(http);
        var rows = new JsonArray();
        var failures = new List<string>();
        Console.WriteLine($"Short prompt, not streamed, every prompt different, {duration.TotalSeconds:0} s per row after 2 s of warm-up, state {(memoryState ? "in memory" : "in SQLite")}. Times in ms.");
        Console.WriteLine();
        Console.WriteLine("| c | Stub direct, total p50 / p95 / p99 | Router, first byte p50 / p95 / p99 | Router, total p50 / p95 / p99 | Router requests/s | Errors | Router RSS |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (int c in Concurrency)
        {
            List<Sample> direct = await load.RunAsync(stub.Address, c, duration, TimeSpan.FromSeconds(2));
            List<Sample> routed = await load.RunAsync(router.Address, c, duration, TimeSpan.FromSeconds(2));
            int errors = routed.Count(r => r.Status != 200);
            failures.AddRange(routed.Where(r => r.Status != 200).Select(r => $"c={c}: {(r.Status == 0 ? r.Body : $"HTTP {r.Status}: {r.Body?[..Math.Min(r.Body.Length, 300)]}")}"));
            double rps = routed.Count / duration.TotalSeconds;
            double rss = router.ResidentMegabytes;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {c} | {Triple(direct.Select(r => r.Total))} | {Triple(routed.Select(r => r.FirstByte))} | {Triple(routed.Select(r => r.Total))} | {rps:0} | {errors} | {rss:0} MB |"));
            rows.Add(new JsonObject
            {
                ["concurrency"] = c,
                ["requestsPerSecond"] = Math.Round(rps, 1),
                ["firstByteP50"] = Round(LoadGenerator.Percentile(routed.Select(r => r.FirstByte), 0.50)),
                ["totalP50"] = Round(LoadGenerator.Percentile(routed.Select(r => r.Total), 0.50)),
                ["totalP95"] = Round(LoadGenerator.Percentile(routed.Select(r => r.Total), 0.95)),
                ["totalP99"] = Round(LoadGenerator.Percentile(routed.Select(r => r.Total), 0.99)),
                ["directTotalP50"] = Round(LoadGenerator.Percentile(direct.Select(r => r.Total), 0.50)),
                ["errors"] = errors,
                ["residentMegabytes"] = Math.Round(rss),
            });
        }

        Console.WriteLine();
        foreach (string failure in failures.Distinct().Take(5))
        {
            Console.WriteLine("Error: " + failure);
        }

        await router.StopAsync();
        return rows;
    }

    private static async Task<(JsonObject Report, bool Passed)> SoakAsync(string repository, string host, StubBackend stub, TimeSpan duration)
    {
        const int C = 8;
        TimeSpan window = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds / 8, 5, 30));
        string home = RouterProcess.Prepare(repository, stub.Address, "ok");
        await using RouterProcess router = await RouterProcess.StartAsync(host, home);
        using HttpClient http = LoadGenerator.Client();
        var load = new LoadGenerator(http);

        // Memory is sampled while the load runs.
        var memory = new List<(TimeSpan At, double Megabytes)>();
        using var sampling = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                memory.Add((clock.Elapsed - TimeSpan.FromSeconds(2), router.ResidentMegabytes));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), sampling.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        List<Sample> samples = await load.RunAsync(router.Address, C, duration, TimeSpan.FromSeconds(2));
        await sampling.CancelAsync();
        await sampler;

        Console.WriteLine($"Soak: unique prompts at c = {C} for {duration.TotalSeconds:0} s, in windows of {window.TotalSeconds:0} s. Times in ms.");
        Console.WriteLine();
        Console.WriteLine("| From | Requests/s | Total p50 | Total p95 | Errors | Router RSS |");
        Console.WriteLine("|---|---|---|---|---|---|");
        var windows = new JsonArray();
        var stats = new List<(double Rps, double P50, double Rss)>();
        TimeSpan warmUp = TimeSpan.FromSeconds(2);
        for (TimeSpan from = warmUp; from + window <= warmUp + duration + TimeSpan.FromMilliseconds(1); from += window)
        {
            TimeSpan to = from + window;
            Sample[] inWindow = [.. samples.Where(r => r.StartedAt >= from && r.StartedAt < to)];
            double[] rssInWindow = [.. memory.Where(m => m.At >= from - warmUp && m.At < to - warmUp).Select(m => m.Megabytes)];
            double rss = rssInWindow.Length > 0 ? rssInWindow.Max() : router.ResidentMegabytes;
            double rps = inWindow.Length / window.TotalSeconds;
            double p50 = LoadGenerator.Percentile(inWindow.Select(r => r.Total), 0.50);
            stats.Add((rps, p50, rss));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {(from - warmUp).TotalSeconds:0} s | {rps:0} | {p50:0.0} | {LoadGenerator.Percentile(inWindow.Select(r => r.Total), 0.95):0.0} | {inWindow.Count(r => r.Status != 200)} | {rss:0} MB |"));
            windows.Add(new JsonObject { ["from"] = (from - warmUp).TotalSeconds, ["requestsPerSecond"] = Math.Round(rps, 1), ["totalP50"] = Round(p50), ["residentMegabytes"] = Math.Round(rss) });
        }

        await router.StopAsync();
        Console.WriteLine();

        // The first window still warms up; the last one is compared with the second.
        bool passed = true;
        if (stats.Count >= 3)
        {
            (double rps, double p50, double rss) reference = stats[1], last = stats[^1];
            passed &= Check(last.p50 <= (reference.p50 * 1.25) + 0.5, $"soak: p50 went from {reference.p50:0.0} ms to {last.p50:0.0} ms (at most +25%)");
            passed &= Check(last.rps >= reference.rps * 0.75, $"soak: throughput went from {reference.rps:0} to {last.rps:0} requests/s (at most -25%)");
            passed &= Check(last.rss <= (reference.rss * 1.5) + 50, $"soak: router memory went from {reference.rss:0} MB to {last.rss:0} MB (at most +50% + 50 MB)");
        }

        passed &= Check(samples.All(r => r.Status == 200), $"soak: {samples.Count(r => r.Status != 200)} of {samples.Count} requests failed");
        Console.WriteLine();
        return (new JsonObject { ["windows"] = windows }, passed);
    }

    private static async Task<(JsonObject Report, bool Passed)> FaultsAsync(string repository, string host, StubBackend stub)
    {
        const int C = 4, N = 40;
        bool passed = true;
        var report = new JsonObject();
        using HttpClient http = LoadGenerator.Client();
        var load = new LoadGenerator(http);
        Console.WriteLine($"Faults: {N} requests from {C} clients per scenario; the cheapest model fails as described, the other answers.");
        Console.WriteLine();

        foreach ((string fault, bool stream) in new[] { ("500", false), ("429", false), ("hang", false), ("stall", true), ("drop", true) })
        {
            string home = RouterProcess.Prepare(repository, stub.Address, fault, keepAll: true);
            List<Sample> samples;
            await using (RouterProcess router = await RouterProcess.StartAsync(host, home))
            {
                samples = await load.SendAsync(router.Address, C, N, stream);
                await router.StopAsync();
            }

            (Dictionary<string, int> outcomes, int entries, _, int usage) = RouterProcess.Stored(home);
            int failedOver = samples.Count(r => r.Attempt is not (null or "1"));
            int broken = samples.Count(r => r.Body?.Contains("stopped mid-reply", StringComparison.Ordinal) == true);
            string name = $"{fault}{(stream ? ", streamed" : string.Empty)}";
            Console.WriteLine($"- {name}: {samples.Count(r => r.Status == 200)}/{N} answered, {failedOver} failed over, {broken} ended with an error event; " +
                              $"stored {entries} entries ({string.Join(", ", outcomes.Select(o => $"{o.Value} {o.Key}"))}), {usage} usage rows");
            passed &= Check(samples.All(r => r.Status == 200), $"{name}: every client gets an answer");
            passed &= Check(entries == N && usage == N, $"{name}: one activity entry and one usage row per request");
            if (stream)
            {
                // Output had started: too late to fail over. The client hears so, and the model cools down.
                passed &= Check(broken is > 0 and <= 2 * C, $"{name}: only the requests in flight before the cooldown end with an error event ({broken})");
                passed &= Check(outcomes.GetValueOrDefault("failed") == broken, $"{name}: those are recorded as failed");
            }
            else
            {
                passed &= Check(failedOver is > 0 and <= 2 * C, $"{name}: only the requests in flight before the cooldown fail over ({failedOver})");
                passed &= Check(outcomes.GetValueOrDefault("served") == N, $"{name}: all recorded as served");
            }

            report[name] = new JsonObject { ["failedOver"] = failedOver, ["errorEvents"] = broken, ["entries"] = entries, ["usageRows"] = usage };
        }

        // Clients that go away mid-reply.
        {
            string home = RouterProcess.Prepare(repository, stub.Address, "slowstream", keepAll: true);
            await using (RouterProcess router = await RouterProcess.StartAsync(host, home))
            {
                await load.SendAsync(router.Address, C, N, stream: true, cancelAfterFirstByte: TimeSpan.FromMilliseconds(300));
                await Task.Delay(TimeSpan.FromSeconds(1));
                await router.StopAsync();
            }

            (Dictionary<string, int> outcomes, int entries, _, int usage) = RouterProcess.Stored(home);
            Console.WriteLine($"- client cancel: stored {entries} entries ({string.Join(", ", outcomes.Select(o => $"{o.Value} {o.Key}"))}), {usage} usage rows");
            passed &= Check(entries == N && outcomes.GetValueOrDefault("cancelled") == N, "client cancel: every request recorded as cancelled");
            passed &= Check(usage == N, "client cancel: one usage row per request");
            report["client cancel"] = new JsonObject { ["entries"] = entries, ["cancelled"] = outcomes.GetValueOrDefault("cancelled"), ["usageRows"] = usage };
        }

        // Two routers on one database (the service and the desktop app, or two hosts on a shared home).
        {
            string home = RouterProcess.Prepare(repository, stub.Address, "ok", keepAll: true);
            List<Sample>[] runs;
            await using (RouterProcess first = await RouterProcess.StartAsync(host, home))
            await using (RouterProcess second = await RouterProcess.StartAsync(host, home))
            {
                runs = await Task.WhenAll(
                    load.RunAsync(first.Address, 8, TimeSpan.FromSeconds(10), TimeSpan.Zero),
                    load.RunAsync(second.Address, 8, TimeSpan.FromSeconds(10), TimeSpan.Zero));
                await Task.WhenAll(first.StopAsync(), second.StopAsync());
            }

            int sent = runs.Sum(r => r.Count), answered = runs.Sum(r => r.Count(s => s.Status == 200));
            (_, int entries, int distinct, int usage) = RouterProcess.Stored(home);
            Console.WriteLine($"- two routers on one database: {answered}/{sent} answered; stored {entries} entries ({distinct} distinct ids), {usage} usage rows");
            passed &= Check(answered == sent, "two routers: every client gets an answer");
            passed &= Check(entries == sent && distinct == entries, "two routers: every request keeps its own activity entry");
            passed &= Check(usage == sent, "two routers: one usage row per request");
            report["two routers"] = new JsonObject { ["requests"] = sent, ["entries"] = entries, ["distinctIds"] = distinct, ["usageRows"] = usage };
        }

        Console.WriteLine();
        return (report, passed);
    }

    /// <summary>Reports where this run is worse than <paramref name="baseline"/>: as GitHub warnings in Actions.</summary>
    private static void Compare(JsonArray latency, JsonArray? baseline)
    {
        if (baseline is null)
        {
            return;
        }

        bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        foreach (JsonNode? row in latency)
        {
            int c = row!["concurrency"]!.GetValue<int>();
            if (baseline.FirstOrDefault(b => b?["concurrency"]?.GetValue<int>() == c) is not { } before)
            {
                continue;
            }

            foreach ((string field, bool higherIsWorse) in new[] { ("totalP50", true), ("totalP99", true), ("requestsPerSecond", false) })
            {
                double now = row[field]!.GetValue<double>(), then = before[field]!.GetValue<double>();
                bool worse = higherIsWorse ? now > (then * 2) + 1 : now < then / 2;
                if (worse)
                {
                    string message = string.Create(CultureInfo.InvariantCulture, $"Gateway at c={c}: {field} is {now:0.##}, against {then:0.##} in the baseline.");
                    Console.WriteLine(actions ? $"::warning title=Gateway benchmark::{message}" : "Regression: " + message);
                }
            }
        }
    }

    private static bool Check(bool condition, string what)
    {
        if (!condition)
        {
            Console.WriteLine("  FAILED: " + what);
        }

        return condition;
    }

    private static string Triple(IEnumerable<TimeSpan> values)
    {
        TimeSpan[] all = [.. values];
        return string.Create(CultureInfo.InvariantCulture,
            $"{LoadGenerator.Percentile(all, 0.50):0.0} / {LoadGenerator.Percentile(all, 0.95):0.0} / {LoadGenerator.Percentile(all, 0.99):0.0}");
    }

    private static double Round(double value) => double.IsNaN(value) ? 0 : Math.Round(value, 2);

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The semantic-router.dll the solution build produced, in the same configuration as this benchmark.</summary>
    private static string HostAssembly(string repository)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(repository, "src", "SemanticRouter.Host", "bin", configuration, "net10.0", "semantic-router.dll");
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SemanticRouter.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Run from inside the repository.");
    }
}
