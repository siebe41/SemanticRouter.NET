using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Checks every few minutes what needs a decision (<see cref="RouterNotices"/>) and writes a log line once when a notice
/// appears and once when it clears, unless <see cref="NotificationOptions.Enabled"/> is off. With webhooks configured it
/// also sends them each notice that appears or clears, and the digest once a week. The desktop app shows its own
/// notifications from the digest.
/// </summary>
internal sealed partial class NoticeService : BackgroundService
{
    internal const string DigestSentKey = "webhook_digest_sent";

    private static readonly TimeSpan DigestEvery = TimeSpan.FromDays(7);

    private readonly RouterNotices _notices;
    private readonly RouterRuntimeProvider _provider;
    private readonly ILogger<NoticeService> _logger;
    private readonly WebhookNotifier? _webhooks;
    private readonly IServiceProvider? _services;
    private readonly IStateValueStore? _store;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, RouterNotice> _announced = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset? _digestSent;

    public NoticeService(
        RouterNotices notices, RouterRuntimeProvider provider, ILogger<NoticeService> logger, WebhookNotifier? webhooks = null,
        IServiceProvider? services = null, IStateValueStore? store = null, TimeProvider? time = null)
    {
        _notices = notices;
        _provider = provider;
        _logger = logger;
        _webhooks = webhooks;
        _services = services;
        _store = store;
        _time = time ?? TimeProvider.System;
        if (store?.Load(DigestSentKey) is { } saved
            && DateTimeOffset.TryParse(saved, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset sent))
        {
            _digestSent = sent;
        }
    }

    /// <summary>How often it checks.</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The webhook deliveries the last <see cref="Check"/> started; tests wait on it.</summary>
    internal Task LastDelivery { get; private set; } = Task.CompletedTask;

    /// <summary>Looks once: the notices that appeared and the ones that cleared since the last look, both logged.</summary>
    internal (IReadOnlyList<RouterNotice> Appeared, IReadOnlyList<RouterNotice> Cleared) Check()
    {
        IReadOnlyList<RouterNotice> current = _notices.Current();
        bool enabled = _provider.Current.Configuration.Notifications.Enabled;
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
                LogAppeared(_logger, notice.Title, notice.Detail);
            }

            foreach (RouterNotice notice in cleared)
            {
                LogCleared(_logger, notice.Title);
            }

            LastDelivery = Deliver(appeared, cleared);
        }

        return (appeared, cleared);
    }

    /// <summary>Starts sending the notices, and the digest when a week has passed, to the webhooks (in the background).</summary>
    private Task Deliver(RouterNotice[] appeared, RouterNotice[] cleared)
    {
        if (_webhooks is null)
        {
            return Task.CompletedTask;
        }

        var deliveries = new List<Task>();
        if ((appeared.Length > 0 || cleared.Length > 0) && _webhooks.Wants(digest: false))
        {
            deliveries.AddRange(appeared.Select(n => _webhooks.SendNoticeAsync(n, cleared: false)));
            deliveries.AddRange(cleared.Select(n => _webhooks.SendNoticeAsync(n, cleared: true)));
        }

        if (_webhooks.Wants(digest: true) && DigestDue() is { } digest)
        {
            deliveries.Add(_webhooks.SendDigestAsync(digest));
        }

        return Task.WhenAll(deliveries);
    }

    /// <summary>The digest of the past week once a week has passed since the last one (the first look only starts the clock).</summary>
    private DigestResponse? DigestDue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_digestSent is { } sent && now - sent < DigestEvery)
            {
                return null;
            }

            bool first = _digestSent is null;
            _digestSent = now;
            _store?.Save(DigestSentKey, now.ToString("O", CultureInfo.InvariantCulture));
            if (first || _services is null)
            {
                return null;
            }
        }

        IServiceProvider sp = _services;
        return DigestApi.Build(
            _provider.Current.Configuration, sp.GetRequiredService<IUsageLedger>(), sp.GetRequiredService<RoutingActivityLog>(),
            sp.GetRequiredService<RoutingFeedbackLog>(), sp.GetRequiredService<RouterTuner>(), _notices, null, now);
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
                    LogFailed(_logger, ex);
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
