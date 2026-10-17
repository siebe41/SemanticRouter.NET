using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SemanticRouter.Host;

/// <summary>
/// Linux: a systemd user unit, <c>~/.config/systemd/user/semantic-router.service</c>. The user's service manager starts
/// it at sign-in, restarts it if it fails and sends its output to the journal.
/// </summary>
internal sealed class SystemdUserService(string unitDirectory, ProcessRunner run) : ServicePlatform
{
    public const string UnitName = "semantic-router.service";

    /// <summary>Exit codes a restart cannot fix: a command-line error (2) and an address already in use (3).</summary>
    private const string PermanentExitCodes = "2 3";

    public override string Kind => "systemd user service";

    public override string Location => Path.Combine(unitDirectory, UnitName);

    public override string LogHint => "journalctl --user -u semantic-router -f";

    public override string EnvironmentAdvice =>
        $"A service does not see variables exported in your shell: add `Environment=NAME=value` lines with `systemctl --user edit {UnitName}`.";

    public override IEnumerable<string> InstallNotes =>
    [
        "It stops when you sign out of every session. To keep it running on a machine nobody signs in to, run `loginctl enable-linger`.",
        "It uses the PATH of this shell to find claude, codex and copilot. Run `semantic-router service install` again after installing one elsewhere.",
    ];

    public static string DefaultUnitDirectory(Func<string, string?> environment, string home) =>
        Path.Combine(environment("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"), "systemd", "user");

    public override bool IsInstalled() => File.Exists(Location);

    public override string? InstalledProgram() => ReadInstalled(Location, ProgramOfUnit);

    /// <summary>The program of a unit's <c>ExecStart</c>, as <see cref="Render"/> quotes it (or unquoted).</summary>
    internal static string? ProgramOfUnit(string unit)
    {
        string? line = unit.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
        if (line is null || line["ExecStart=".Length..].TrimStart() is not { Length: > 0 } command)
        {
            return null;
        }

        if (command[0] != '"')
        {
            return command.Split(' ', 2)[0];
        }

        var program = new StringBuilder();
        for (int i = 1; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '"')
            {
                return program.Replace("%%", "%").Replace("$$", "$").ToString();
            }

            program.Append(c == '\\' && i + 1 < command.Length ? command[++i] : c);
        }

        return null;
    }

    public override string Render(ServiceDefinition definition)
    {
        var unit = new StringBuilder()
            .AppendLine("# Written by `semantic-router service install`; running it again replaces this file.")
            .AppendLine("[Unit]")
            .AppendLine("Description=SemanticRouter: routes each prompt to local models, subscription CLIs or paid APIs")
            .AppendLine("Documentation=https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/host.md#run-it-in-the-background")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=simple")
            .Append("ExecStart=").AppendLine(string.Join(' ', new[] { definition.Program }.Concat(definition.Arguments).Select(QuoteArgument)))
            .Append("WorkingDirectory=").AppendLine(EscapeSpecifiers(definition.ConfigurationDirectory));
        foreach ((string name, string value) in definition.Environment)
        {
            unit.Append("Environment=").AppendLine(QuoteValue($"{name}={value}"));
        }

        return unit
            .AppendLine("Restart=on-failure")
            .AppendLine("RestartSec=5")
            .AppendLine("# A command-line error (2) or an address already in use (3) is not fixed by restarting.")
            .Append("RestartPreventExitStatus=").AppendLine(PermanentExitCodes)
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=default.target")
            .ToString();
    }

    public override async Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
    {
        ProcessResult manager = await Systemctl("show-environment").ConfigureAwait(false);
        if (!manager.Succeeded)
        {
            return ServiceOutcome.Failed(
                $"there is no systemd user session to install into ({FirstLine(manager.Output)}). Without systemd (some containers, WSL without systemd), start `semantic-router` with your own supervisor.");
        }

        Directory.CreateDirectory(unitDirectory);
        await File.WriteAllTextAsync(Location, Render(definition)).ConfigureAwait(false);
        return await Sequence(["daemon-reload"], ["enable", UnitName], ["restart", UnitName]).ConfigureAwait(false);
    }

    public override async Task<ServiceOutcome> UninstallAsync()
    {
        // Disabling a unit that is not loaded fails harmlessly; the file is removed either way.
        await Systemctl("disable", "--now", UnitName).ConfigureAwait(false);
        File.Delete(Location);
        return await Sequence(["daemon-reload"]).ConfigureAwait(false);
    }

    public override async Task<ServiceOutcome> UnregisterAsync()
    {
        await Systemctl("disable", UnitName).ConfigureAwait(false);
        File.Delete(Location);
        return await Sequence(["daemon-reload"]).ConfigureAwait(false);
    }

    public override bool RunsThisProcess(Func<string, string?> environment)
    {
        try
        {
            return InUnit(File.ReadAllText("/proc/self/cgroup"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether a <c>/proc/self/cgroup</c> puts the process in this unit (<c>0::/user.slice/…/semantic-router.service</c>).</summary>
    internal static bool InUnit(string cgroup) =>
        cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(line => line.EndsWith("/" + UnitName, StringComparison.Ordinal));

    public override Task<ServiceOutcome> StartAsync() => Sequence(["start", UnitName]);

    public override Task<ServiceOutcome> StopAsync() => Sequence(["stop", UnitName]);

    public override Task<ServiceOutcome> RestartAsync() => Sequence(["restart", UnitName]);

    public override async Task<string> StatusAsync()
    {
        ProcessResult enabled = await Systemctl("is-enabled", UnitName).ConfigureAwait(false);
        ProcessResult active = await Systemctl("is-active", UnitName).ConfigureAwait(false);
        return $"{FirstLine(enabled.Output)}, {FirstLine(active.Output)}";
    }

    private Task<ProcessResult> Systemctl(params string[] arguments) => run("systemctl", ["--user", .. arguments]);

    private async Task<ServiceOutcome> Sequence(params string[][] commands)
    {
        foreach (string[] command in commands)
        {
            ProcessResult result = await Systemctl(command).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ServiceOutcome.Failed($"`systemctl --user {string.Join(' ', command)}` failed: {result.Output} (see {LogHint})");
            }
        }

        return ServiceOutcome.Ok();
    }

    /// <summary>A quoted <c>ExecStart</c> word: C-style escapes, <c>%</c> specifiers and <c>$</c> expansion disarmed.</summary>
    internal static string QuoteArgument(string argument) => "\"" + Escape(argument).Replace("$", "$$", StringComparison.Ordinal) + "\"";

    /// <summary>A quoted <c>Environment</c> assignment (no <c>$</c> expansion happens there).</summary>
    internal static string QuoteValue(string assignment) => "\"" + Escape(assignment) + "\"";

    private static string Escape(string value) => EscapeSpecifiers(value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal));

    private static string EscapeSpecifiers(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "no output";
}

/// <summary>
/// macOS: a launchd agent, <c>~/Library/LaunchAgents/io.github.siebe41.semantic-router.plist</c>. launchd starts it at
/// sign-in, restarts it if it exits with an error and writes its output to <c>~/Library/Logs/semantic-router</c>.
/// </summary>
internal sealed class LaunchAgentService(string agentsDirectory, string logDirectory, ProcessRunner run) : ServicePlatform
{
    public const string Label = "io.github.siebe41.semantic-router";

    public override string Kind => "launchd agent";

    public override string Location => Path.Combine(agentsDirectory, Label + ".plist");

    public string LogFile => Path.Combine(logDirectory, "semantic-router.log");

    public override string LogHint => $"tail -f \"{LogFile}\"";

    public override string StartsAgain =>
        "It starts again when you next sign in, or with `semantic-router service start`.";

    public override string EnvironmentAdvice =>
        "A launchd agent does not see variables exported in your shell. Run `semantic-router service install` from a shell where they are set to carry PATH; other variables are not copied, so keys are not written to disk in plain text.";

    public override IEnumerable<string> InstallNotes =>
    [
        "It uses the PATH of this shell to find claude, codex and copilot. Run `semantic-router service install` again after installing one elsewhere.",
    ];

    public override bool IsInstalled() => File.Exists(Location);

    public override string? InstalledProgram() => ReadInstalled(Location, ProgramOfPlist);

    /// <summary>The first of a property list's <c>ProgramArguments</c>.</summary>
    internal static string? ProgramOfPlist(string plist)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(plist), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
            System.Xml.Linq.XElement? dict = System.Xml.Linq.XDocument.Load(reader).Root?.Element("dict");
            System.Xml.Linq.XElement? key = dict?.Elements("key").FirstOrDefault(k => k.Value == "ProgramArguments");
            return key?.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "array" } array && array.Elements("string").FirstOrDefault()?.Value is { Length: > 0 } program
                ? program
                : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public override string Render(ServiceDefinition definition)
    {
        var plist = new StringBuilder()
            .AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">")
            .AppendLine("<!-- Written by `semantic-router service install`; running it again replaces this file. -->")
            .AppendLine("<plist version=\"1.0\">")
            .AppendLine("<dict>")
            .AppendLine("  <key>Label</key>")
            .Append("  <string>").Append(Label).AppendLine("</string>")
            .AppendLine("  <key>ProgramArguments</key>")
            .AppendLine("  <array>");
        foreach (string argument in new[] { definition.Program }.Concat(definition.Arguments))
        {
            plist.Append("    <string>").Append(SecurityElement.Escape(argument)).AppendLine("</string>");
        }

        plist
            .AppendLine("  </array>")
            .AppendLine("  <key>WorkingDirectory</key>")
            .Append("  <string>").Append(SecurityElement.Escape(definition.ConfigurationDirectory)).AppendLine("</string>")
            .AppendLine("  <key>EnvironmentVariables</key>")
            .AppendLine("  <dict>");
        foreach ((string name, string value) in LogEnvironment.Concat(definition.Environment))
        {
            plist
                .Append("    <key>").Append(SecurityElement.Escape(name)).AppendLine("</key>")
                .Append("    <string>").Append(SecurityElement.Escape(value)).AppendLine("</string>");
        }

        return plist
            .AppendLine("  </dict>")
            .AppendLine("  <key>RunAtLoad</key>")
            .AppendLine("  <true/>")
            .AppendLine("  <!-- Restart after a failure, not after a clean stop. -->")
            .AppendLine("  <key>KeepAlive</key>")
            .AppendLine("  <dict>")
            .AppendLine("    <key>SuccessfulExit</key>")
            .AppendLine("    <false/>")
            .AppendLine("  </dict>")
            .AppendLine("  <key>ThrottleInterval</key>")
            .AppendLine("  <integer>30</integer>")
            .AppendLine("  <key>StandardOutPath</key>")
            .Append("  <string>").Append(SecurityElement.Escape(LogFile)).AppendLine("</string>")
            .AppendLine("  <key>StandardErrorPath</key>")
            .Append("  <string>").Append(SecurityElement.Escape(LogFile)).AppendLine("</string>")
            .AppendLine("</dict>")
            .AppendLine("</plist>")
            .ToString();
    }

    /// <summary>A log file has no clock of its own, unlike the journal: the console logger adds the time.</summary>
    internal static readonly IReadOnlyDictionary<string, string> LogEnvironment = new Dictionary<string, string>
    {
        ["Logging__Console__FormatterOptions__TimestampFormat"] = "yyyy-MM-dd HH:mm:ss ",
        ["Logging__Console__FormatterOptions__ColorBehavior"] = "Disabled",
    };

    public override async Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
    {
        string domain = await DomainAsync().ConfigureAwait(false);
        if (await IsLoadedAsync(domain).ConfigureAwait(false))
        {
            await Launchctl("bootout", $"{domain}/{Label}").ConfigureAwait(false);
        }

        Directory.CreateDirectory(agentsDirectory);
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Location, Render(definition)).ConfigureAwait(false);
        return await BootstrapAsync(domain).ConfigureAwait(false);
    }

    public override async Task<ServiceOutcome> UninstallAsync()
    {
        string domain = await DomainAsync().ConfigureAwait(false);
        if (await IsLoadedAsync(domain).ConfigureAwait(false))
        {
            await Launchctl("bootout", $"{domain}/{Label}").ConfigureAwait(false);
        }

        File.Delete(Location);
        return ServiceOutcome.Ok();
    }

    public override Task<ServiceOutcome> UnregisterAsync()
    {
        // Left loaded until this sign-in ends; with the property list gone it does not start at the next one.
        File.Delete(Location);
        return Task.FromResult(ServiceOutcome.Ok());
    }

    /// <summary>launchd names the job it started in <c>XPC_SERVICE_NAME</c>.</summary>
    public override bool RunsThisProcess(Func<string, string?> environment) => environment("XPC_SERVICE_NAME") == Label;

    public override async Task<ServiceOutcome> StartAsync()
    {
        string domain = await DomainAsync().ConfigureAwait(false);
        return await IsLoadedAsync(domain).ConfigureAwait(false)
            ? Outcome(await Launchctl("kickstart", $"{domain}/{Label}").ConfigureAwait(false), "kickstart")
            : await BootstrapAsync(domain).ConfigureAwait(false);
    }

    public override async Task<ServiceOutcome> StopAsync()
    {
        // Unloading is the only stop launchd keeps: KeepAlive would restart a killed process.
        string domain = await DomainAsync().ConfigureAwait(false);
        return await IsLoadedAsync(domain).ConfigureAwait(false)
            ? Outcome(await Launchctl("bootout", $"{domain}/{Label}").ConfigureAwait(false), "bootout")
            : ServiceOutcome.Ok();
    }

    public override async Task<ServiceOutcome> RestartAsync()
    {
        string domain = await DomainAsync().ConfigureAwait(false);
        return await IsLoadedAsync(domain).ConfigureAwait(false)
            ? Outcome(await Launchctl("kickstart", "-k", $"{domain}/{Label}").ConfigureAwait(false), "kickstart -k")
            : await BootstrapAsync(domain).ConfigureAwait(false);
    }

    public override async Task<string> StatusAsync()
    {
        string domain = await DomainAsync().ConfigureAwait(false);
        ProcessResult print = await Launchctl("print", $"{domain}/{Label}").ConfigureAwait(false);
        if (!print.Succeeded)
        {
            return "installed, not loaded (it loads at your next sign-in, or with `semantic-router service start`)";
        }

        string? state = print.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("state = ", StringComparison.Ordinal));
        return "loaded, " + (state?["state = ".Length..] ?? "state unknown");
    }

    private async Task<ServiceOutcome> BootstrapAsync(string domain)
    {
        // Right after a bootout launchd can still be tearing the old job down and refuses with an I/O error.
        ProcessResult result = new(-1, string.Empty);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            result = await Launchctl("bootstrap", domain, Location).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return ServiceOutcome.Ok();
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        return Outcome(result, "bootstrap");
    }

    private async Task<bool> IsLoadedAsync(string domain) => (await Launchctl("print", $"{domain}/{Label}").ConfigureAwait(false)).Succeeded;

    private async Task<string> DomainAsync()
    {
        ProcessResult id = await run("id", ["-u"]).ConfigureAwait(false);
        return "gui/" + (id.Succeeded ? id.Output.Trim() : "501");
    }

    private Task<ProcessResult> Launchctl(params string[] arguments) => run("launchctl", arguments);

    private ServiceOutcome Outcome(ProcessResult result, string verb) => result.Succeeded
        ? ServiceOutcome.Ok()
        : ServiceOutcome.Failed($"`launchctl {verb}` failed: {result.Output} (see {LogHint})");
}

/// <summary>
/// Windows: a value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (listed in Task Manager's Startup
/// apps) that runs <c>semantic-router service start</c> at sign-in. That starts the router without a console window and
/// returns; the router logs to <c>logs\semantic-router.log</c> in the configuration directory. What to run is kept in
/// <c>semantic-router.service.json</c>, so the registry value stays short.
/// </summary>
internal sealed class WindowsSignInService : ServicePlatform
{
    /// <summary>Set on the router started in the background; it then logs to a file instead of the console it lacks.</summary>
    public const string DetachedVariable = "SEMANTIC_ROUTER_DETACHED";

    public const string DefinitionFileName = "semantic-router.service.json";

    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string DefaultValueName = "SemanticRouter";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _configurationDirectory;
    private readonly string _logDirectory;
    private readonly string _runKey;
    private readonly string _valueName;

    /// <param name="configurationDirectory">Where <see cref="DefinitionFileName"/> is kept.</param>
    /// <param name="runKey">The registry key of the sign-in entry.</param>
    /// <param name="valueName">The sign-in entry's value name.</param>
    /// <param name="localDataDirectory">Where the router logs (<see cref="HostSettings.LocalDataDirectory"/>); the configuration directory by default.</param>
    public WindowsSignInService(string configurationDirectory, string runKey = DefaultRunKey, string valueName = DefaultValueName, string? localDataDirectory = null)
    {
        _configurationDirectory = configurationDirectory;
        _logDirectory = localDataDirectory ?? configurationDirectory;
        _runKey = runKey;
        _valueName = valueName;
    }

    public override string Kind => "Windows sign-in entry";

    public override string Location => $@"HKCU\{_runKey}\{_valueName}";

    public string DefinitionFile => Path.Combine(_configurationDirectory, DefinitionFileName);

    public override string LogHint => $"Get-Content -Wait \"{RouterLog.FileIn(_logDirectory)}\"";

    public override string EnvironmentAdvice =>
        "The router started at sign-in sees your user environment variables (set with `setx` or in System Properties), not ones set only in a PowerShell session or profile.";

    public override IEnumerable<string> InstallNotes =>
    [
        "It appears in Task Manager under Startup apps as SemanticRouter. A console window may flash briefly at sign-in while it starts.",
        "Stop it with `semantic-router service stop` before `dotnet tool update`: Windows cannot replace a running program.",
    ];

    public override bool IsInstalled() => OperatingSystem.IsWindows() && ReadValue() is not null;

    public override string? InstalledProgram() => ReadValue() is { } command ? WindowsCommandLine.First(command) : null;

    public override string Render(ServiceDefinition definition) =>
        $"""
        {Location} = {SignInCommand(definition)}

        {DefinitionFile}:
        {JsonSerializer.Serialize(Stored.From(definition), Json)}
        """;

    /// <summary>
    /// The registry value. A program without a console window (the desktop app) runs the router itself; the console
    /// tool goes through <c>service start</c>, which starts the router without a console and returns.
    /// </summary>
    internal static string SignInCommand(ServiceDefinition definition) => definition.Windowless
        ? WindowsCommandLine.Join([definition.Program, .. definition.Arguments])
        : WindowsCommandLine.Join([definition.Program, .. definition.Command("service", "start", "--no-wait")]);

    public override async Task<ServiceOutcome> InstallAsync(ServiceDefinition definition)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceOutcome.Failed("the sign-in entry is a Windows registry value.");
        }

        Directory.CreateDirectory(_configurationDirectory);
        await File.WriteAllTextAsync(DefinitionFile, JsonSerializer.Serialize(Stored.From(definition), Json)).ConfigureAwait(false);
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_runKey))
        {
            key.SetValue(_valueName, SignInCommand(definition), RegistryValueKind.String);
        }

        return await RestartAsync().ConfigureAwait(false);
    }

    public override async Task<ServiceOutcome> UninstallAsync()
    {
        ServiceOutcome stopped = await StopAsync().ConfigureAwait(false);
        RemoveEntry();
        return stopped;
    }

    private void RemoveEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }

        File.Delete(DefinitionFile);
    }

    public override Task<ServiceOutcome> UnregisterAsync()
    {
        RemoveEntry();
        return Task.FromResult(ServiceOutcome.Ok());
    }

    /// <summary>The router <c>service start</c> starts in the background carries <see cref="DetachedVariable"/>.</summary>
    public override bool RunsThisProcess(Func<string, string?> environment) => IsInstalled() && environment(DetachedVariable) == "1";

    public override Task<ServiceOutcome> StartAsync()
    {
        if (RouterInstance.ReadRunning(_configurationDirectory) is { } running)
        {
            return Task.FromResult(ServiceOutcome.Ok($"Already running (pid {running.ProcessId})."));
        }

        Stored? stored = ReadDefinition();
        if (stored is null)
        {
            return Task.FromResult(ServiceOutcome.Failed($"{DefinitionFile} is missing or unreadable. Run `semantic-router service install` again."));
        }

        var start = new ProcessStartInfo(stored.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _configurationDirectory,
        };
        foreach (string argument in stored.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[DetachedVariable] = "1";

        try
        {
            using Process process = Process.Start(start)!;
            return Task.FromResult(ServiceOutcome.Ok($"Started the router in the background (pid {process.Id})."));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Task.FromResult(ServiceOutcome.Failed($"could not start {stored.Program}: {ex.Message}"));
        }
    }

    public override async Task<ServiceOutcome> StopAsync()
    {
        if (RouterInstance.ReadRunning(_configurationDirectory) is not { } instance)
        {
            return ServiceOutcome.Ok("The router was not running.");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(instance.ProcessId);
        }
        catch (ArgumentException)
        {
            return ServiceOutcome.Ok("The router was not running.");
        }

        using (process)
        {
            return await StopAsync(instance, process).ConfigureAwait(false);
        }
    }

    private static async Task<ServiceOutcome> StopAsync(RouterInstance instance, Process process)
    {
        if (OperatingSystem.IsWindows() && instance.RequestStop())
        {
            using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
                return ServiceOutcome.Ok();
            }
            catch (OperationCanceledException)
            {
                // It did not finish shutting down in time; end it below.
            }
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return ServiceOutcome.Ok();
    }

    public override async Task<ServiceOutcome> RestartAsync()
    {
        ServiceOutcome stopped = await StopAsync().ConfigureAwait(false);
        return stopped.Succeeded ? await StartAsync().ConfigureAwait(false) : stopped;
    }

    public override Task<string> StatusAsync() => Task.FromResult(
        ReadValue() is { } command ? $"starts at sign-in: {command}" : "not set to start at sign-in");

    /// <summary>The background router: no console, so the banner and the logs go to <see cref="RouterLog"/>.</summary>
    public static Task<int> RunDetachedAsync(HostSettings settings) => RouterLog.RunAsync(settings, RouterLifecycle.ForThisTool(settings));

    private string? ReadValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(_valueName) as string;
    }

    private Stored? ReadDefinition()
    {
        try
        {
            return JsonSerializer.Deserialize<Stored>(File.ReadAllText(DefinitionFile), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>What <c>semantic-router.service.json</c> holds: the command that runs the router.</summary>
    internal sealed record Stored(string Program, IReadOnlyList<string> Arguments)
    {
        public static Stored From(ServiceDefinition definition) => new(definition.Program, definition.Arguments);
    }
}

/// <summary>Windows command lines, quoted the way <c>CommandLineToArgvW</c> and the C runtime read them back.</summary>
internal static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    /// <summary>
    /// The program of a command line, read as Windows reads it: up to the closing quote when it starts with one (a
    /// program path holds no quotes), otherwise up to the first space or tab.
    /// </summary>
    public static string? First(string commandLine)
    {
        string line = commandLine.TrimStart();
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            return end > 1 ? line[1..end] : null;
        }

        return line.Split([' ', '\t'], 2)[0] is { Length: > 0 } program ? program : null;
    }

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote; then each one and the quote need escaping.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
