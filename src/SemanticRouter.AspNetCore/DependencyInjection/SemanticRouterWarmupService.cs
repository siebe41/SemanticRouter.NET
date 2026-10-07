using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Embeds route utterances in the background after start-up so the first routed request does not pay for it.
/// Failures are logged, not fatal: the classifier retries lazily on the next request.
/// </summary>
internal sealed partial class SemanticRouterWarmupService(
    IPromptClassifier classifier,
    IOptions<EmbeddingProviderOptions> options,
    ILogger<SemanticRouterWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WarmUpOnStartup)
        {
            return;
        }

        // Do not hold up host start-up on a (possibly slow) embeddings endpoint.
        await Task.Yield();

        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            await classifier.WarmUpAsync(stoppingToken).ConfigureAwait(false);
            LogWarmedUp(logger, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogWarmUpFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Semantic route index ready in {ElapsedMs:0} ms.")]
    private static partial void LogWarmedUp(ILogger logger, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic route index warm-up failed; it will be retried on the first request.")]
    private static partial void LogWarmUpFailed(ILogger logger, Exception exception);
}
