using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Benchmarks.Gateway;

/// <summary>
/// The <c>semantic-router</c> tool, run as its own process on a home directory with SQLite state on (the tool's
/// default), with two models on the stub backend: <c>primary</c> (cheapest, so tried first) and <c>backup</c>.
/// </summary>
internal sealed class RouterProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _output = new();

    private RouterProcess(Process process, Uri address, string home)
    {
        _process = process;
        Address = address;
        Home = home;
    }

    public Uri Address { get; }

    public string Home { get; }

    /// <summary>How long the route index took to embed after the router first answered, and the state it then reported.</summary>
    public (TimeSpan Waited, string State) WarmUp { get; private set; }

    /// <summary>The router's resident memory now, in MB.</summary>
    public double ResidentMegabytes
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64 / 1024d / 1024d;
        }
    }

    /// <summary>Writes the configuration for a scenario: <paramref name="primary"/> is what the stub does for the first model.</summary>
    /// <param name="embeddings">What the router classifies with; the template's Hashing provider when null.</param>
    /// <param name="keepAll">Keep every activity entry, so a scenario can count them (the default keeps 10,000).</param>
    public static string Prepare(string repository, Uri stub, string primary, RouterEmbeddings? embeddings, bool memoryState = false, bool keepAll = false)
    {
        string home = Directory.CreateTempSubdirectory("gateway-bench").FullName;
        if (memoryState)
        {
            // A directory where the database would be: the router cannot open it and keeps its state in memory.
            Directory.CreateDirectory(Path.Combine(home, "semantic-router.db"));
        }

        string template = File.ReadAllText(Path.Combine(repository, "src", "SemanticRouter.Hosting", "Templates", "semantic-router.template.json"));
        JsonObject root = JsonNode.Parse(template, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        root["Connectors"] = new JsonObject
        {
            ["stub"] = new JsonObject
            {
                ["Type"] = "OpenAICompatible",
                ["BaseAddress"] = stub.ToString(),
                ["Timeout"] = "00:00:02",
                ["StallTimeout"] = "00:00:01",
            },
        };
        root["Models"] = new JsonArray(Model("primary", primary, 0.1m), Model("backup", "ok", 1m));

        // The saved file sits above environment variables and the command line, so the embeddings are set here.
        if (embeddings is not null)
        {
            root["Embeddings"] = embeddings.Section.DeepClone();
            root["DefaultSimilarityThreshold"] = embeddings.Threshold;
        }

        if (keepAll)
        {
            root["State"] = new JsonObject { ["Retention"] = new JsonObject { ["ActivityEntries"] = 10_000_000 } };
        }

        File.WriteAllText(Path.Combine(home, "semantic-router.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return home;
    }

    private static JsonObject Model(string name, string backend, decimal price) => new()
    {
        ["Name"] = name,
        ["Connector"] = "stub",
        ["Model"] = backend,
        ["Tier"] = 5,
        ["InputCostPerMillionTokens"] = price,
        ["OutputCostPerMillionTokens"] = price,

        // What the template's routes can ask for, so every prompt has a model.
        ["Capabilities"] = new JsonArray("code", "tools"),
    };

    public static async Task<RouterProcess> StartAsync(string hostAssembly, string home)
    {
        int port = FreePort();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { hostAssembly, "--config-dir", home, "--urls", $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}" })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the router.");
        var router = new RouterProcess(process, new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}"), home);
        process.OutputDataReceived += (_, e) => router.Keep(e.Data);
        process.ErrorDataReceived += (_, e) => router.Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(90))
        {
            if (process.HasExited)
            {
                break;
            }

            try
            {
                if ((await http.GetAsync(new Uri(router.Address, "/router/api/status"))).IsSuccessStatusCode)
                {
                    router.WarmUp = await router.WarmedUpAsync();
                    return router;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // not listening yet
            }

            await Task.Delay(200);
        }

        string output = router.Output();
        await router.DisposeAsync();
        throw new InvalidOperationException("The router did not start:\n" + output);
    }

    /// <summary>
    /// Waits until the router's status is no longer <c>starting</c>: it answers before its route index is embedded, which
    /// with an embedding model can take longer than a scenario. Returns how long it waited, and the state it ended in.
    /// </summary>
    private async Task<(TimeSpan Waited, string State)> WarmedUpAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        long started = Stopwatch.GetTimestamp();
        string state = "?";
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(5) && !_process.HasExited)
        {
            try
            {
                using JsonDocument status = JsonDocument.Parse(await http.GetStringAsync(new Uri(Address, "/router/api/status")));
                state = status.RootElement.GetProperty("service").GetProperty("state").GetString() ?? "?";
                if (state != "starting")
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // busy
            }

            await Task.Delay(100);
        }

        return (Stopwatch.GetElapsedTime(started), state);
    }

    /// <summary>Stops the router the way a service manager does, so it finishes its writes.</summary>
    public async Task StopAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            using Process? kill = Process.Start("kill", ["-TERM", _process.Id.ToString(CultureInfo.InvariantCulture)]);
            if (kill is not null)
            {
                await kill.WaitForExitAsync();
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    /// <summary>The last lines the router wrote, for a failure message.</summary>
    public string Output()
    {
        lock (_output)
        {
            return string.Join('\n', _output);
        }
    }

    /// <summary>What the stopped routers on <paramref name="home"/> stored: activity entries by outcome, and usage rows.</summary>
    public static (Dictionary<string, int> Outcomes, int Entries, int DistinctIds, int UsageRows) Stored(string home)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(home, "semantic-router.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        int entries = 0;
        var ids = new HashSet<long>();
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, entry FROM activity;";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                entries++;
                ids.Add(reader.GetInt64(0));
                using JsonDocument entry = JsonDocument.Parse(reader.GetString(1));
                string outcome = entry.RootElement.TryGetProperty("outcome", out JsonElement o) ? o.GetString() ?? "?" : "?";
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }
        }

        using SqliteCommand usage = connection.CreateCommand();
        usage.CommandText = "SELECT COUNT(*) FROM usage;";
        return (outcomes, entries, ids.Count, Convert.ToInt32(usage.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    private void Keep(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > 40)
            {
                _output.Dequeue();
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>An <c>Embeddings</c> section for the router's configuration, with the similarity threshold that suits it.</summary>
internal sealed record RouterEmbeddings(string Name, JsonObject Section, float Threshold)
{
    /// <summary>
    /// An ONNX model loaded from the files <see cref="OnnxModelDownloader"/> keeps in <paramref name="models"/> (fetched now
    /// when missing), so the router finds them through <c>ModelPath</c> and does not download during the run.
    /// </summary>
    public static async Task<RouterEmbeddings> OnnxAsync(OnnxModelInfo model, string models)
    {
        using var http = new HttpClient();
        (string modelFile, _) = await OnnxModelDownloader.EnsureAsync(model, models, http);
        return new($"ONNX {model.Name}", new JsonObject
        {
            ["Provider"] = "Onnx",
            ["Model"] = model.Name,
            ["ModelPath"] = Path.GetDirectoryName(modelFile),
            ["Pooling"] = model.Pooling.ToString(),
            ["WarmUpOnStartup"] = true,
        }, model.SuggestedThreshold);
    }
}
