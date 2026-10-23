// Classification latency and accuracy per embedding provider, on the starting routes and the sample evaluation set.
//
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks -- --remote http://localhost:11434 nomic-embed-text
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks -- latency http://localhost:5000 <model> …   (see LatencyBenchmark.cs)
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks -- tool-calls http://localhost:5000 <model> …   (see ToolCallBenchmark.cs)
//   dotnet run -c Release --project benchmarks/SemanticRouter.Benchmarks -- gateway [--quick] [--soak 300] [--faults] [--embeddings onnx]   (see Gateway/GatewayBenchmark.cs)
//
// Each provider classifies every prompt of the set (the classification cache is off, so every prompt is embedded)
// for several rounds after a warm-up, and is calibrated on the set to find the range of thresholds that route it best.
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

if (args is ["latency", ..])
{
    return await SemanticRouter.Benchmarks.LatencyBenchmark.RunAsync(args);
}

if (args is ["tool-calls", ..])
{
    return await SemanticRouter.Benchmarks.ToolCallBenchmark.RunAsync(args);
}

if (args is ["gateway", ..])
{
    return await SemanticRouter.Benchmarks.Gateway.GatewayBenchmark.RunAsync(args);
}

string root = FindRepository();
string template = File.ReadAllText(Path.Combine(root, "src", "SemanticRouter.Hosting", "Templates", "semantic-router.template.json"));
IReadOnlyList<EvaluationCase> cases = EvaluationSet.Parse(File.ReadAllText(Path.Combine(root, "src", "SemanticRouter.Hosting", "Templates", "semantic-router.evaluation.jsonl")));
string models = Environment.GetEnvironmentVariable("SEMANTIC_ROUTER_TEST_MODELS") is { Length: > 0 } cache ? cache : Path.Combine(Path.GetTempPath(), "semantic-router-test-models");
const int Rounds = 5;

using ServiceProvider services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
var onnx = new List<(string Name, Func<IEmbeddingProvider> Create)>();
foreach (OnnxModelInfo model in OnnxEmbeddingModels.Known)
{
    (string modelFile, string vocabFile) = await OnnxModelDownloader.EnsureAsync(model, models, services.GetRequiredService<IHttpClientFactory>().CreateClient());
    onnx.Add(($"ONNX {model.Name} (in-process)", () => new OnnxEmbeddingProvider(modelFile, vocabFile, model.Pooling)));
}

var providers = new List<(string Name, Func<IEmbeddingProvider> Create)> { ("Hashing (in-process, lexical)", () => new HashingEmbeddingProvider()) };
providers.AddRange(onnx);
if (args.Length >= 3 && args[0] == "--remote")
{
    var options = new EmbeddingProviderOptions { Provider = EmbeddingProviderKinds.OpenAICompatible, Endpoint = new Uri(args[1]), Model = args[2] };
    providers.Add(($"{args[2]} at {args[1]}", () => new OpenAICompatibleEmbeddingProvider(new HttpClient(), options)));
}

Console.WriteLine($"{cases.Count} prompts × {Rounds} rounds on {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine();
Console.WriteLine("| Embeddings | Index warm-up | Classify p50 | Classify p95 | Accuracy at its suggested threshold | Best thresholds on the set |");
Console.WriteLine("|---|---|---|---|---|---|");
foreach ((string name, Func<IEmbeddingProvider> create) in providers)
{
    IEmbeddingProvider provider = create();
    RouterConfiguration options = RouterConfiguration.FromJson(template);
    options.ClassificationCache.Enabled = false;
    float suggested = OnnxEmbeddingModels.Known.FirstOrDefault(m => name.Contains(m.Name, StringComparison.Ordinal))?.SuggestedThreshold ?? options.DefaultSimilarityThreshold;
    options.DefaultSimilarityThreshold = suggested;

    var classifier = new SemanticPromptClassifier(provider, options);
    long started = Stopwatch.GetTimestamp();
    await classifier.WarmUpAsync();
    TimeSpan warmUp = Stopwatch.GetElapsedTime(started);

    var timings = new List<double>();
    for (int round = 0; round < Rounds + 1; round++)
    {
        foreach (EvaluationCase item in cases)
        {
            long t = Stopwatch.GetTimestamp();
            await classifier.ClassifyAsync(item.Prompt);
            if (round > 0)
            {
                timings.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds); // round 0 warms up the JIT and the model
            }
        }
    }

    var evaluator = new RoutingEvaluator(provider, options);
    PreparedEvaluation prepared = await evaluator.PrepareAsync(cases);
    EvaluationReport report = evaluator.Evaluate(prepared);
    CalibrationReport calibration = evaluator.Calibrate(prepared);
    float[] best = calibration.Sweep.Where(p => p.Correct == calibration.Recommended.Correct).Select(p => p.Threshold).ToArray();
    timings.Sort();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"| {name} | {warmUp.TotalMilliseconds:0} ms ({options.Routes.Sum(r => r.Utterances.Count)} utterances) | {Percentile(timings, 0.50):0.00} ms | {Percentile(timings, 0.95):0.00} ms | {report.Correct}/{report.Total} at {suggested:0.00} | {best.Min():0.00}–{best.Max():0.00} ({calibration.Recommended.Correct}/{report.Total}) |"));
    (provider as IDisposable)?.Dispose();
}

return 0;

static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

static string FindRepository()
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
