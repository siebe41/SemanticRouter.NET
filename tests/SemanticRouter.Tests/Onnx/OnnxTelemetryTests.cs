using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Onnx;

/// <summary>
/// ONNX Runtime's Linux and macOS builds send usage data to Microsoft unless <c>ORT_DISABLE_TELEMETRY</c> is in the native
/// environment before they load (#186).
/// </summary>
public sealed class OnnxTelemetryTests : IDisposable
{
    private static readonly string ModelCache = Environment.GetEnvironmentVariable("SEMANTIC_ROUTER_TEST_MODELS") is { Length: > 0 } cache
        ? cache
        : Path.Combine(Path.GetTempPath(), "semantic-router-test-models");

    private readonly string _directory = Directory.CreateTempSubdirectory("router-onnx-telemetry").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_variable_is_in_the_native_environment_before_a_session_is_created()
    {
        if (OperatingSystem.IsWindows())
        {
            // .NET sets Windows' process environment itself; there ONNX Runtime's events go to ETW, turned off per session.
            return;
        }

        using var factory = new OnnxEmbeddingProviderFactory(new ServiceCollection().BuildServiceProvider(), Options.Create(new OnnxEmbeddingOptions { ModelsDirectory = _directory }));

        string? native = Marshal.PtrToStringUTF8(GetEnv(Encoding.UTF8.GetBytes(OnnxRuntimeEnvironment.TelemetryVariable + '\0')));
        native.Should().NotBeNull("the factory sets it with setenv, which ONNX Runtime reads, not only in .NET's copy");
        Environment.GetEnvironmentVariable(OnnxRuntimeEnvironment.TelemetryVariable).Should().Be(native, "processes started from here get it too");
        OnnxRuntimeEnvironment.TelemetrySetting.Should().Be(native);
    }

    [Fact]
    public void A_value_someone_set_is_never_replaced()
    {
        OnnxRuntimeEnvironment.ValueToSet(native: null, managed: null).Should().Be("1");
        OnnxRuntimeEnvironment.ValueToSet(native: "0", managed: "0").Should().BeNull("the process was started with it");
        OnnxRuntimeEnvironment.ValueToSet(native: string.Empty, managed: null).Should().BeNull();
        OnnxRuntimeEnvironment.ValueToSet(native: null, managed: "0").Should().Be("0", "set in this process through .NET: passed on to ONNX Runtime");
        OnnxRuntimeEnvironment.TurnsTelemetryOff("1").Should().BeTrue();
        OnnxRuntimeEnvironment.TurnsTelemetryOff("0").Should().BeFalse();
    }

    /// <summary>
    /// The router as the tool runs it, with the in-process model and a recording proxy in its environment: after the model
    /// is loaded nothing connects anywhere for 30 seconds (ONNX Runtime 1.31 called home after about 10), and no device
    /// id is written to the home directory.
    /// </summary>
    [Fact]
    public async Task The_router_with_onnx_embeddings_connects_nowhere_and_writes_no_device_id()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (string modelFile, _) = await OnnxModelDownloader.EnsureAsync(OnnxEmbeddingModels.Find(null)!, ModelCache, new HttpClient());
        using var proxy = new RecordingProxy();
        string home = Directory.CreateDirectory(Path.Combine(_directory, "home")).FullName;
        // The configuration init writes when it finds no embedding server, with the model already on disk.
        HostSettings settings = HostSettings.Parse(["--config-dir", Path.Combine(_directory, "config")], _ => null);
        RouterConfiguration document = RouterConfiguration.FromJson(RouterHost.Template());
        InitCommand.UseInProcessEmbeddings(document);
        document.Embeddings.ModelPath = Path.GetDirectoryName(modelFile);
        Directory.CreateDirectory(settings.ConfigurationDirectory);
        new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(document);
        int port = FreePort();

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "semantic-router.dll"), "--config-dir", settings.ConfigurationDirectory,
                     "--urls", $"http://127.0.0.1:{port}", "--SemanticRouter:Network:Offline=true",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // A clean start: none of this process's proxies or ONNX Runtime settings (this test process has set the variable).
        foreach (string name in start.Environment.Keys.Where(k => k.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("ORT_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("XDG_", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(name);
        }

        start.Environment["HOME"] = home;
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            start.Environment[name] = $"http://127.0.0.1:{proxy.Port}";
        }

        using Process process = Process.Start(start)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            JsonNode? status = null;
            for (DateTime until = DateTime.UtcNow.AddSeconds(90); DateTime.UtcNow < until && !process.HasExited; await Task.Delay(500))
            {
                try
                {
                    status = JsonNode.Parse(await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/router/api/status")));
                    if (status?["warmedUp"]?.GetValue<bool>() == true || status?["warmUpError"] is not null)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
            }

            string Log() { lock (output) { return output.ToString(); } }
            (status?["warmedUp"]?.GetValue<bool>() ?? false).Should().BeTrue($"the router loads the model and embeds its routes: {status?["warmUpError"]} {Log()}");

            await Task.Delay(TimeSpan.FromSeconds(30));

            process.HasExited.Should().BeFalse(Log());
            proxy.Requests.Should().BeEmpty("offline, with the in-process model, nothing is contacted");
            Directory.EnumerateDirectories(home, ".onnxruntime", SearchOption.AllDirectories).Should().BeEmpty("ONNX Runtime keeps no device id");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [DllImport("libc", EntryPoint = "getenv")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetEnv(byte[] name);
}
