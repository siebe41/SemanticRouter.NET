using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Checks every few minutes what needs a decision (<see cref="RouterNotices"/>) and writes a log line once when a notice
/// appears and once when it clears, unless <see cref="NotificationOptions.Enabled"/> is off. The desktop app shows its
/// own notifications from the digest.
/// </summary>
internal sealed partial class NoticeService(RouterNotices notices, RouterRuntimeProvider provider, ILogger<NoticeService> logger) : BackgroundService
{
    private readonly Dictionary<string, RouterNotice> _announced = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>How often it checks.</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Looks once: the notices that appeared and the ones that cleared since the last look, both logged.</summary>
    internal (IReadOnlyList<RouterNotice> Appeared, IReadOnlyList<RouterNotice> Cleared) Check()
    {
        IReadOnlyList<RouterNotice> current = notices.Current();
        bool enabled = provider.Current.Configuration.Notifications.Enabled;
        RouterNotice[] appeared, cleared;
        lock (_gate)
        {
            appeared = [.. current.Where(n => !_announced.ContainsKey(n.Key))];
            cleared = [.. _announced.Values.Where(a => !current.Any(n => n.Key == a.Key))];
            foreach (RouterNotice notice in appeared)
            {
                _announced[notice.Key] = notice;
            }

            foreach (RouterNotice notice in cleared)
            {
                _announced.Remove(notice.Key);
            }
        }

        if (enabled)
        {
            foreach (RouterNotice notice in appeared)
            {
                LogAppeared(logger, notice.Title, notice.Detail);
            }

            foreach (RouterNotice notice in cleared)
            {
                LogCleared(logger, notice.Title);
            }
        }

        return (appeared, cleared);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Check();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Needs a decision: {Title}. {Detail}")]
    private static partial void LogAppeared(ILogger logger, string title, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "No longer needs a decision: {Title}.")]
    private static partial void LogCleared(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking what needs a decision failed; it is tried again later.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
