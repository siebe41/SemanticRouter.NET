using System.Diagnostics;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// Shows a system notification with the platform's own tool: <c>osascript</c> on macOS, <c>notify-send</c> on Linux, a
/// PowerShell toast on Windows. Best effort: where the tool is missing nothing is shown, and the tray menu still lists
/// the notice. The text is passed as arguments or environment variables, never put into a script. The tool is started
/// by its full path: by bare name, a file of that name in the current directory could be started instead.
/// </summary>
internal static class DesktopNotifier
{
    // Windows shows toasts only for a registered app id: the installer's Start menu entry carries the app's own; for the
    // zip download, PowerShell's, which is always there.
    private const string InstalledAppId = "velopack.SemanticRouter";
    private const string PowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    private static string WindowsAppId => File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe")) ? InstalledAppId : PowerShellAppId;

    private const string WindowsScript =
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null;" +
        "$xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
        "$text = $xml.GetElementsByTagName('text');" +
        "$null = $text.Item(0).AppendChild($xml.CreateTextNode($env:SR_TITLE));" +
        "$null = $text.Item(1).AppendChild($xml.CreateTextNode($env:SR_BODY));" +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:SR_APP).Show([Windows.UI.Notifications.ToastNotification]::new($xml))";

    /// <summary>The full path of the platform's notification tool, or <see langword="null"/> when it is not installed.</summary>
    internal static string? Tool() =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
        : ServicePlatform.SystemTool(OperatingSystem.IsMacOS() ? "osascript" : "notify-send", Environment.GetEnvironmentVariable("PATH"));

    public static void Show(string title, string body)
    {
        if (Tool() is not { } tool)
        {
            Console.Error.WriteLine("Could not show a notification: no notification tool (notify-send) was found.");
            return;
        }

        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo(tool);
            foreach (string line in new[] { "on run argv", "display notification (item 2 of argv) with title (item 1 of argv)", "end run" })
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(line);
            }

            start.ArgumentList.Add(title);
            start.ArgumentList.Add(body);
        }
        else if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo(tool) { CreateNoWindow = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", WindowsScript })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["SR_TITLE"] = title;
            start.Environment["SR_BODY"] = body;
            start.Environment["SR_APP"] = WindowsAppId;
        }
        else
        {
            start = new ProcessStartInfo(tool);
            start.ArgumentList.Add("--app-name=SemanticRouter");
            start.ArgumentList.Add(title);
            start.ArgumentList.Add(body);
        }

        start.UseShellExecute = false;
        try
        {
            using Process? _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not show a notification: {ex.Message}");
        }
    }
}
