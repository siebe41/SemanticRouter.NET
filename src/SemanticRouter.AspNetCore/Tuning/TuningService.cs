using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>Runs automatic tuning when it is due (<see cref="TuningOptions.Interval"/>, or after new labels).</summary>
internal sealed partial class TuningService(RouterTuner tuner, ILogger<TuningService> logger, TimeProvider? time = null) : BackgroundService
{
    /// <summary>How often the service checks whether a run is due.</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery, time ?? TimeProvider.System);
        try
        {
            do
            {
                try
                {
                    if (tuner.Due())
                    {
                        await tuner.RunAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An embedding provider that is down, or a configuration that cannot be applied: try again later.
                    LogFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic tuning failed; it is tried again later.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
