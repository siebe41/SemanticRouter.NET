using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Onnx;

/// <summary>
/// ONNX Runtime's own telemetry, in a router process of its own with a fresh home directory and a recording proxy in
/// <c>HTTPS_PROXY</c>: with the in-process embeddings, nothing leaves and no device id is written.
/// </summary>
public sealed class OnnxTelemetryTests : IDisposable
{
    private static readonly string ModelCache = Environment.GetEnvironmentVariable("SEMANTIC_ROUTER_TEST_MODELS") is { Length: > 0 } cache
        ? cache
        : Path.Combine(Path.GetTempPath(), "semantic-router-test-models");

    private readonly string _directory = Directory.CreateTempSubdirectory("router-onnx-telemetry").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Telemetry_is_off_in_this_process_once_the_provider_is_loaded()
    {
        OnnxRuntimeTelemetry.TurnOff();

        OnnxRuntimeTelemetry.IsOff.Should().BeTrue(OnnxRuntimeTelemetry.Setting ?? "not set");
    }

    [Fact]
    public async Task A_router_with_onnx_embeddings_contacts_no_telemetry_host_and_writes_no_device_id()
    {
        // ONNX Runtime uploads telemetry from Linux and macOS (on Windows it writes ETW events instead). Linux only: on
        // macOS a home directory of its own would also move the key ring away from the keychain it expects.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        OnnxModelInfo model = OnnxEmbeddingModels.Find(OnnxEmbeddingModels.DefaultModel)!;
        using (var download = new HttpClient())
        {
            await OnnxModelDownloader.EnsureAsync(model, ModelCache, download);
        }

        string home = Directory.CreateDirectory(Path.Combine(_directory, "home")).FullName;
        string configuration = Path.Combine(_directory, "config");
        string models = Directory.CreateDirectory(Path.Combine(configuration, "models", model.Name)).FullName;
        foreach (string file in new[] { "model.onnx", "vocab.txt" })
        {
            File.Copy(Path.Combine(ModelCache, model.Name, file), Path.Combine(models, file));
        }

        // The configuration file decides the embeddings (a command-line setting would not override it): the template's
        // routes, with the default of `init` on a machine with no embedding server.
        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        document.Embeddings = new EmbeddingProviderOptions { Provider = EmbeddingProviderKind.Onnx, Model = model.Name };
        await File.WriteAllTextAsync(Path.Combine(configuration, "semantic-router.json"), document.ToJson());

        using var proxy = new RecordingProxy();
        int port = FreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "semantic-router"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     "--config-dir", configuration, "--urls", $"http://127.0.0.1:{port}", "--SemanticRouter:Network:Offline=true",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // The process starts without the variable, as an installed tool would: the router must set it itself.
        foreach (string name in new[] { OnnxRuntimeTelemetry.Variable, "XDG_CACHE_HOME", "SEMANTIC_ROUTER_HOME", "NO_PROXY", "no_proxy" })
        {
            start.Environment.Remove(name);
        }

        start.Environment["HOME"] = home;
        start.Environment["HTTPS_PROXY"] = start.Environment["https_proxy"] = proxy.Address;

        var output = new StringBuilder();
        using Process process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            RouterInstance? instance = await RouterInstance.WaitForStartAsync(configuration, DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromSeconds(60));
            instance.Should().NotBeNull(Output());
            using (var http = new HttpClient())
            {
                HttpResponseMessage classified = await http.PostAsync(
                    new Uri($"http://127.0.0.1:{port}/router/api/classify"),
                    new StringContent("""{"prompt":"why does this async code deadlock?"}""", Encoding.UTF8, "application/json"));
                classified.StatusCode.Should().Be(HttpStatusCode.OK, await classified.Content.ReadAsStringAsync());
            }

            // ONNX Runtime reports about 10 seconds after its first session.
            await Task.Delay(TimeSpan.FromSeconds(30));

            proxy.Requests.Should().BeEmpty("nothing may leave the router in offline mode");
            Directory.EnumerateDirectories(home, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Where(d => d.Contains(".onnxruntime", StringComparison.Ordinal) || d.Contains("DeveloperTools", StringComparison.Ordinal))
                .Should().BeEmpty("ONNX Runtime keeps no device id or event queue");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        string Output()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An HTTPS proxy that records the first line of each request (<c>CONNECT host:443 …</c>) and refuses it.</summary>
    private sealed class RecordingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<string> _requests = [];

        public RecordingProxy()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
                    string? line = await reader.ReadLineAsync(_stopping.Token);
                    lock (_requests)
                    {
                        _requests.Add(line ?? "(nothing)");
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }
    }
}
