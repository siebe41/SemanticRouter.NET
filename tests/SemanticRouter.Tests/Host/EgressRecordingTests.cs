using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// <c>doctor</c>'s egress list (docs/network.md) is meant to be a firewall allowlist (#185). The tool runs as a child
/// process with a recording proxy in <c>HTTPS_PROXY</c> and <c>HTTP_PROXY</c>, a configuration whose connectors and judge
/// are on made-up hosts, and Hashing embeddings. The proxy answers every request itself and forwards nothing. After
/// start-up, <c>doctor</c>, a chat call (which the judge grades), a classify call and a dashboard page load, every host
/// the proxy saw must be named on one of the <c>egress</c> lines <c>doctor</c> printed (the child's own list);
/// offline, the proxy must see nothing.
/// </summary>
public sealed class EgressRecordingTests : IDisposable
{
    private const string Llm = "llm.egress-test.example";
    private const string Judge = "judge.egress-test.example";
    private const string Backup = "backup.egress-test.example";
    private const string Hooks = "hooks.egress-test.example";

    /// <summary>
    /// How long one test may drive the tool, all steps together. Generous: the full suite runs this beside other
    /// child-process tests, and a loaded runner starts a Release build of the tool slowly. It costs nothing when the tool
    /// is quick, and a hung tool fails after this rather than after every step's own limit.
    /// </summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(8);

    private readonly string _directory = Directory.CreateTempSubdirectory("router-egress").FullName;

    public void Dispose()
    {
        for (int attempt = 1; ; attempt++)
        {
            SqliteFiles.Release(_directory);
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                // The child process may hold its files for a moment after it is killed (Windows).
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public async Task Every_host_the_router_contacts_is_on_the_egress_list()
    {
        using var proxy = new RecordingProxy(Reply);
        HostSettings settings = Configure();

        // Doctor's connector check reaches the judge's host first (its models), so wait for the grading call itself.
        IReadOnlyList<string> egress = await DriveAsync(settings, proxy, until: () => Chatted(proxy, Judge));

        await proxy.DrainAsync();

        Uri[] seen = [.. proxy.Destinations];
        string requests = string.Join(Environment.NewLine, proxy.Requests);
        Chatted(proxy, Llm).Should().BeTrue($"the chat call went through the proxy: {requests}");
        Chatted(proxy, Judge).Should().BeTrue($"the judge graded the answer through the proxy: {requests}");
        seen.Select(d => d.Host).Should().Contain(Backup, $"doctor's connector checks went through the proxy: {requests}");
        proxy.WithoutDestination.Should().BeEmpty("every connection to the proxy says where it is going");
        string lines = string.Join(Environment.NewLine, egress);
        foreach (Uri destination in seen.Where(d => !d.IsLoopback))
        {
            Names(egress, destination).Should().BeTrue($"{destination.Authority} was contacted, so doctor lists it:{Environment.NewLine}{lines}");
        }

        Names(egress, new Uri($"https://{Hooks}/")).Should().BeTrue(
            "the webhook's host is listed although the 5-minute notice check does not run in this test (WebhookTests cover its delivery)");
    }

    [Fact]
    public async Task Offline_the_router_contacts_nothing()
    {
        using var proxy = new RecordingProxy(Reply);
        HostSettings settings = Configure("--SemanticRouter:Network:Offline=true");

        // Nothing to wait for, so watch for 30 seconds of what the router does in the background (the judge among it).
        IReadOnlyList<string> egress = await DriveAsync(settings, proxy, until: () => true, settle: TimeSpan.FromSeconds(30));

        await proxy.DrainAsync();

        // The dashboard's setup looks for LM Studio and Ollama on this machine, which offline mode allows.
        proxy.Destinations.Where(d => !d.IsLoopback).Should().BeEmpty(
            $"offline, the connectors, the judge and the webhook are on hosts that are not allowed: {string.Join(Environment.NewLine, proxy.Requests)}");
        proxy.WithoutDestination.Should().BeEmpty("a connection that says nothing about where it is going may be going anywhere");
        egress.Should().Contain(m => m.StartsWith($"connector llm: {Llm} (never contacted:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes the configuration: a cheap model on <see cref="Llm"/> that answers, a judge model on <see cref="Judge"/>
    /// grading every answer, an expensive model on <see cref="Backup"/> over HTTPS that only <c>doctor</c> checks, and
    /// a webhook on <see cref="Hooks"/>.
    /// </summary>
    private HostSettings Configure(params string[] arguments)
    {
        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Connectors["llm"] = new ConnectorOptions { BaseAddress = new Uri($"http://{Llm}/v1") };
        document.Connectors["grader"] = new ConnectorOptions { BaseAddress = new Uri($"http://{Judge}/v1") };
        document.Connectors["backup"] = new ConnectorOptions { BaseAddress = new Uri($"https://{Backup}/v1") };
        document.Models.Add(new ModelDefinition { Name = "worker", Connector = "llm", Model = "worker-model", Tier = 1, InputCostPerMillionTokens = 0.1m, OutputCostPerMillionTokens = 0.1m });
        document.Models.Add(new ModelDefinition { Name = "grader", Connector = "grader", Model = "grader-model", Tier = 4, InputCostPerMillionTokens = 10m, OutputCostPerMillionTokens = 10m });
        document.Models.Add(new ModelDefinition { Name = "backup", Connector = "backup", Model = "backup-model", Tier = 5, InputCostPerMillionTokens = 20m, OutputCostPerMillionTokens = 20m });
        document.Middleware.Judge.Enabled = true;
        document.Middleware.Judge.Model = "grader";
        document.Middleware.Judge.SampleRate = 1;
        document.Notifications.Webhooks["ops"] = new WebhookOptions { Url = $"https://{Hooks}/hook?token=egress-test" };
        string configuration = Directory.CreateDirectory(Path.Combine(_directory, "config")).FullName;
        // Port 0: the tool binds a free port and its banner says which, so no other test can take the port in between.
        HostSettings settings = HostSettings.Parse(["--config-dir", configuration, "--urls", "http://127.0.0.1:0", .. arguments], _ => null);
        new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(document);
        return settings;
    }

    /// <summary>
    /// Starts the tool, runs <c>doctor</c>, a chat and a classify call and loads the dashboard, then waits for
    /// <paramref name="until"/>. Returns the <c>egress</c> lines <c>doctor</c> printed: the child's own reading of the
    /// configuration, which this machine's <c>SemanticRouter__…</c> variables do not reach.
    /// </summary>
    private async Task<IReadOnlyList<string>> DriveAsync(HostSettings settings, RecordingProxy proxy, Func<bool> until, TimeSpan? settle = null)
    {
        using var limit = new CancellationTokenSource(Limit);
        CancellationToken cancellationToken = limit.Token;
        using RouterProcess router = RouterProcess.Start(Arguments(settings), proxy, Path.Combine(_directory, "keys"));
        try
        {
            string? address;
            while ((address = router.ListeningAddress) is null && !router.HasExited)
            {
                await Task.Delay(250, cancellationToken);
            }

            address.Should().NotBeNull($"the tool says where it listens: {router.Output}");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address!), Timeout = Limit };
            JsonNode? status = null;
            while (!router.HasExited)
            {
                status = JsonNode.Parse(await http.GetStringAsync(new Uri("/router/api/status", UriKind.Relative), cancellationToken));
                if (status?["warmedUp"]?.GetValue<bool>() == true || status?["warmUpError"] is not null)
                {
                    break;
                }

                await Task.Delay(250, cancellationToken);
            }

            (status?["warmedUp"]?.GetValue<bool>() ?? false).Should().BeTrue($"the router starts: {status?["warmUpError"]} {router.Output}");

            string checks;
            using (RouterProcess doctor = RouterProcess.Start(["doctor", .. Arguments(settings)], proxy, Path.Combine(_directory, "keys")))
            {
                await doctor.WaitAsync(cancellationToken);
                checks = doctor.Output;
            }

            using HttpResponseMessage chat = await http.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), new StringContent(
                """{"model":"auto","messages":[{"role":"user","content":"hi there"}]}""", Encoding.UTF8, "application/json"), cancellationToken);
            using HttpResponseMessage classify = await http.PostAsync(new Uri("/router/api/classify", UriKind.Relative), new StringContent(
                """{"prompt":"write a regex that matches email addresses"}""", Encoding.UTF8, "application/json"), cancellationToken);
            classify.StatusCode.Should().Be(HttpStatusCode.OK, await classify.Content.ReadAsStringAsync(cancellationToken));

            string page = await http.GetStringAsync(new Uri("/router/", UriKind.Relative), cancellationToken);
            foreach (Match asset in Regex.Matches(page, @"(?:src|href)=""(?![a-z][a-z0-9+.-]*:|//|#)([^""]+)"""))
            {
                using HttpResponseMessage _ = await http.GetAsync(new Uri(new Uri(address + "/router/"), asset.Groups[1].Value), cancellationToken);
            }

            // What the dashboard's pages ask the router for when they open.
            foreach (string api in new[]
                     {
                         "session", "status", "config", "policy", "setup", "digest", "activity", "activity/series", "usage", "reliability",
                         "routes/health", "memory", "tuning", "feedback/summary", "evaluation/set", "service", "themes", "judge/agreement",
                     })
            {
                using HttpResponseMessage response = await http.GetAsync(new Uri($"/router/api/{api}", UriKind.Relative), cancellationToken);
                response.IsSuccessStatusCode.Should().BeTrue($"dashboard API {api} returned {response.StatusCode}");
            }

            while (!until())
            {
                // The judge grades in the background.
                await Task.Delay(250, cancellationToken);
            }

            if (settle is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            router.HasExited.Should().BeFalse(router.Output);
            chat.IsSuccessStatusCode.Should().Be(!settings.HostArguments.Any(a => a.Contains("Offline=true", StringComparison.Ordinal)),
                $"{chat.StatusCode} {await chat.Content.ReadAsStringAsync(cancellationToken)} {router.Output}");

            string[] egress = [.. Regex.Matches(checks, $@"^\[(?: OK |WARN|FAIL)\] {EgressList.Subject}: (.+?)\r?$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
            egress.Should().NotBeEmpty($"doctor prints the egress list: {checks}");
            return egress;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The tool was not through within {Limit.TotalMinutes} minutes. The proxy saw:{Environment.NewLine}{string.Join(Environment.NewLine, proxy.Requests)}{Environment.NewLine}The tool wrote:{Environment.NewLine}{router.Output}");
        }
    }

    private static string[] Arguments(HostSettings settings) =>
        ["--config-dir", settings.ConfigurationDirectory, .. settings.HostArguments];

    /// <summary>What the made-up hosts answer: their models, and a short reply to every chat request.</summary>
    private static (HttpStatusCode, string) Reply(string method, Uri url) => url.AbsolutePath switch
    {
        _ when url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) =>
            (HttpStatusCode.OK, """{"object":"list","data":[{"id":"worker-model"},{"id":"grader-model"},{"id":"backup-model"}]}"""),
        _ when url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) && method == "POST" =>
            (HttpStatusCode.OK, """{"id":"chatcmpl-egress","object":"chat.completion","created":0,"model":"worker-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello there."},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""),
        _ => (HttpStatusCode.NotFound, """{"error":{"message":"not found"}}"""),
    };

    /// <summary>Whether the proxy carried a chat request to <paramref name="host"/>, not just a look at its models.</summary>
    private static bool Chatted(RecordingProxy proxy, string host) =>
        proxy.Destinations.Any(d => d.Host == host && d.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal));

    /// <summary>
    /// Whether an egress line names the destination's host (and its port, when it is not the scheme's default) as one the
    /// router contacts. A line saying the host is never contacted does not count: a contacted host must not have one.
    /// </summary>
    private static bool Names(IEnumerable<string> egress, Uri destination)
    {
        string authority = destination.IsDefaultPort ? destination.Host : destination.Authority;
        var named = new Regex($@"(?<![\w.-]){Regex.Escape(authority)}(?![\w-]|\.\w|:\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return egress.Any(line => named.IsMatch(line) && !line.Contains("never contacted", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tool (<c>semantic-router.dll</c>) in a child process whose only proxy is <see cref="RecordingProxy"/>.</summary>
    private sealed class RouterProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private RouterProcess(Process process) => _process = process;

        public bool HasExited => _process.HasExited;

        /// <summary>The address the tool's banner says it listens on, once it has printed it.</summary>
        public string? ListeningAddress =>
            Regex.Match(Output, @"is listening on (?<url>http://127\.0\.0\.1:\d+)") is { Success: true } match ? match.Groups["url"].Value : null;

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public static RouterProcess Start(IEnumerable<string> arguments, RecordingProxy proxy, string keys)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "semantic-router.dll"));
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // None of this machine's proxies or router settings: only the recording proxy, and the configuration above.
            foreach (string name in start.Environment.Keys.Where(k => k.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("SEMANTIC_ROUTER_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SemanticRouter__", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                start.Environment.Remove(name);
            }

            start.Environment[HostSettings.KeysVariable] = keys;
            foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
            {
                start.Environment[name] = proxy.Url;
            }

            var router = new RouterProcess(Process.Start(start)!);
            router._process.OutputDataReceived += (_, e) => router.Append(e.Data);
            router._process.ErrorDataReceived += (_, e) => router.Append(e.Data);
            router._process.BeginOutputReadLine();
            router._process.BeginErrorReadLine();
            return router;
        }

        public Task WaitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _process.Dispose();
        }

        private void Append(string? line)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}
