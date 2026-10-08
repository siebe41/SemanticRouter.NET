using System.Security;
using System.Text;
using Microsoft.Win32;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// "Start at sign-in" for the desktop app: an XDG autostart entry on Linux, a launchd agent on macOS (loaded by launchd
/// at the next sign-in) and a value under <c>HKCU\…\Run</c> on Windows. The app starts with <c>--background</c>, in
/// the tray. It is separate from <c>semantic-router service</c>, which runs the router without the app.
/// </summary>
internal abstract class SignInLaunch
{
    /// <summary>Where the entry is written.</summary>
    public abstract string Location { get; }

    public abstract bool IsEnabled();

    public abstract void Enable(string program, IReadOnlyList<string> arguments);

    public abstract void Disable();

    /// <summary>The arguments the app starts with at sign-in.</summary>
    public static IReadOnlyList<string> Arguments(string configurationDirectory) => ["--background", "--config-dir", configurationDirectory];

    public static SignInLaunch? ForThisMachine()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRunValue();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgentFile(Path.Combine(home, "Library", "LaunchAgents"));
        }

        return OperatingSystem.IsLinux() ? new XdgAutostartFile(XdgAutostartFile.DefaultDirectory(Environment.GetEnvironmentVariable, home)) : null;
    }
}

/// <summary>Linux: <c>~/.config/autostart/semantic-router.desktop</c>, started by the desktop session at sign-in.</summary>
internal sealed class XdgAutostartFile(string directory) : SignInLaunch
{
    public override string Location => Path.Combine(directory, "semantic-router.desktop");

    public static string DefaultDirectory(Func<string, string?> environment, string home) =>
        Path.Combine(environment("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"), "autostart");

    public override bool IsEnabled() => File.Exists(Location);

    public override void Enable(string program, IReadOnlyList<string> arguments)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Location, Render(program, arguments));
    }

    public override void Disable() => File.Delete(Location);

    internal static string Render(string program, IReadOnlyList<string> arguments)
    {
        string icon = Path.Combine(AppContext.BaseDirectory, "semantic-router.png");
        return new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine("Name=SemanticRouter")
            .AppendLine("Comment=Routes each prompt to local models, subscription CLIs or paid APIs")
            .Append("Exec=").AppendLine(string.Join(' ', new[] { program }.Concat(arguments).Select(Quote)))
            .Append("Icon=").AppendLine(Escape(icon))
            .AppendLine("Terminal=false")
            .AppendLine("X-GNOME-Autostart-enabled=true")
            .ToString();
    }

    /// <summary>
    /// An <c>Exec</c> argument: double-quoted with <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> escaped (the Exec rules),
    /// <c>%</c> doubled (field codes), then the backslashes escaped again (the key file's string rules).
    /// </summary>
    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c == '%' ? "%%" : c.ToString());
        }

        return Escape(quoted.Append('"').ToString());
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
}

/// <summary>macOS: <c>~/Library/LaunchAgents/io.github.siebe41.semantic-router.desktop.plist</c>, run at sign-in.</summary>
internal sealed class LaunchAgentFile(string directory) : SignInLaunch
{
    public const string Label = LaunchAgentService.Label + ".desktop";

    public override string Location => Path.Combine(directory, Label + ".plist");

    public override bool IsEnabled() => File.Exists(Location);

    public override void Enable(string program, IReadOnlyList<string> arguments)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Location, Render(program, arguments));
    }

    public override void Disable() => File.Delete(Location);

    /// <summary>Started once at sign-in; quitting the app is respected (no KeepAlive).</summary>
    internal static string Render(string program, IReadOnlyList<string> arguments)
    {
        var plist = new StringBuilder()
            .AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">")
            .AppendLine("<plist version=\"1.0\">")
            .AppendLine("<dict>")
            .AppendLine("  <key>Label</key>")
            .Append("  <string>").Append(Label).AppendLine("</string>")
            .AppendLine("  <key>ProgramArguments</key>")
            .AppendLine("  <array>");
        foreach (string argument in new[] { program }.Concat(arguments))
        {
            plist.Append("    <string>").Append(SecurityElement.Escape(argument)).AppendLine("</string>");
        }

        return plist
            .AppendLine("  </array>")
            .AppendLine("  <key>RunAtLoad</key>")
            .AppendLine("  <true/>")
            .AppendLine("  <key>ProcessType</key>")
            .AppendLine("  <string>Interactive</string>")
            .AppendLine("</dict>")
            .AppendLine("</plist>")
            .ToString();
    }
}

/// <summary>
/// Windows: a <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value (Task Manager → Startup apps). The app is a
/// GUI program, so unlike the console tool it starts without any window flashing.
/// </summary>
internal sealed class WindowsRunValue(string runKey = WindowsSignInService.DefaultRunKey, string valueName = "SemanticRouter Desktop") : SignInLaunch
{
    public override string Location => $@"HKCU\{runKey}\{valueName}";

    public override bool IsEnabled() => Read() is not null;

    public string? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey);
        return key?.GetValue(valueName) as string;
    }

    public override void Enable(string program, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The sign-in entry is a Windows registry value.");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKey);
        key.SetValue(valueName, WindowsCommandLine.Join([program, .. arguments]), RegistryValueKind.String);
    }

    public override void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
