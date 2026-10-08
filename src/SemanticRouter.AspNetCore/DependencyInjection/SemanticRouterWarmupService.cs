using Microsoft.Extensions.Hosting;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Builds the initial runtime when the host starts (so configuration problems fail start-up) and lets the runtime
/// provider warm up the route index in the background. Failures there are logged, not fatal: the classifier retries
/// lazily on the next request.
/// </summary>
internal sealed class SemanticRouterWarmupService(RouterRuntimeProvider runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = runtime.Current;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
