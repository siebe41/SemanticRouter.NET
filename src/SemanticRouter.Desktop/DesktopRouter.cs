using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

internal enum RouterState
{
    Starting,

    /// <summary>The router runs inside the app.</summary>
    Hosting,

    /// <summary>Another router (the background service, a terminal) already serves this configuration directory.</summary>
    Attached,

    Stopped,
    Failed,
}

/// <summary>
/// The router the app shows: its own, started in-process, or the one already running for the configuration directory
/// (typically <c>semantic-router service</c>), so the app and the background service never fight over the port.
/// </summary>
internal sealed class DesktopRouter(HostSettings settings, TextWriter log, TimeSpan? attachWait = null) : IAsyncDisposable
{
    /// <summary>How long to wait for a router that holds the port but has not recorded itself yet.</summary>
    private readonly TimeSpan _attachWait = attachWait ?? TimeSpan.FromSeconds(15);

    private RunningRouter? _router;
    private bool _disposing;

    public RouterState State { get; private set; } = RouterState.Starting;

    /// <summary>The router's address on this machine while it runs.</summary>
    public string? Url { get; private set; }

    public int? AttachedProcessId { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event Action? Changed;

    public string Status => State switch
    {
        RouterState.Starting => "Starting the router…",
        RouterState.Hosting => $"Running on {Url}",
        RouterState.Attached => $"Using the router already running on {Url} (pid {AttachedProcessId})",
        RouterState.Stopped => "The router is not running",
        _ => $"Could not start: {Problem}",
    };

    public async Task StartAsync()
    {
        Set(RouterState.Starting);
        if (RouterInstance.ReadRunning(settings.ConfigurationDirectory) is { } running)
        {
            Attach(running);
            return;
        }

        RunningRouter? router;
        try
        {
            router = await RouterHost.StartAsync(settings, log, RouterLog.Configure);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await log.WriteLineAsync($"The router did not start: {ex}");
            Fail(ex.Message);
            return;
        }

        if (router is null)
        {
            // The port is taken. At sign-in that is usually the background service starting at the same moment.
            for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed < _attachWait; await Task.Delay(500))
            {
                if (RouterInstance.ReadRunning(settings.ConfigurationDirectory) is { } other)
                {
                    Attach(other);
                    return;
                }
            }

            Fail("its address is in use by another program. Change it with --urls, or stop that program.");
            return;
        }

        _router = router;
        Url = router.LocalUrl;
        AttachedProcessId = null;
        router.App.Lifetime.ApplicationStopping.Register(() =>
        {
            // Stopped from outside (on Windows, `semantic-router service stop` signals every router on the directory).
            if (!_disposing)
            {
                Set(RouterState.Stopped);
            }
        });
        await log.WriteLineAsync($"SemanticRouter {RouterHost.Version} is listening on {string.Join(", ", router.Instance.Urls)}");
        Set(RouterState.Hosting);
    }

    /// <summary>Notices a router it attached to going away, or coming back (the service restarted).</summary>
    public void Refresh()
    {
        if (State is not (RouterState.Attached or RouterState.Stopped) || _router is not null)
        {
            return;
        }

        RouterInstance? running = RouterInstance.ReadRunning(settings.ConfigurationDirectory);
        if (running is not null && (State != RouterState.Attached || running.ProcessId != AttachedProcessId))
        {
            Attach(running);
        }
        else if (running is null && State == RouterState.Attached)
        {
            Set(RouterState.Stopped);
        }
    }

    /// <summary>Stops the app's own router, if it runs one, so another (the background service) can take the port.</summary>
    public async Task ReleaseAsync()
    {
        await StopOwnRouterAsync();
        Set(RouterState.Starting);
    }

    /// <summary>Starts the in-app router again (after it stopped or failed); an attached router is not the app's to restart.</summary>
    public async Task RestartAsync()
    {
        await StopOwnRouterAsync();
        await StartAsync();
    }

    private void Attach(RouterInstance instance)
    {
        Url = RouterHost.LocalAddress(instance.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? instance.Urls[0]);
        AttachedProcessId = instance.ProcessId;
        Set(RouterState.Attached);
    }

    private void Fail(string problem)
    {
        Problem = problem;
        Url = null;
        Set(RouterState.Failed);
    }

    private void Set(RouterState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private async Task StopOwnRouterAsync()
    {
        if (_router is { } router)
        {
            _disposing = true;
            _router = null;
            await router.DisposeAsync();
            _disposing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        if (_router is { } router)
        {
            _router = null;
            await router.DisposeAsync();
        }
    }
}
