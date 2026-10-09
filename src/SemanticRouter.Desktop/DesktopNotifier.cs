using System.Diagnostics;

namespace SemanticRouter.Desktop;

/// <summary>
/// Shows a system notification with the platform's own tool: <c>osascript</c> on macOS, <c>notify-send</c> on Linux, a
/// PowerShell toast on Windows. Best effort: where the tool is missing nothing is shown, and the tray menu still lists
/// the notice. The text is passed as arguments or environment variables, never put into a script.
/// </summary>
internal static class DesktopNotifier
{
    // Windows shows toasts only for a registered app id; PowerShell's is always there.
    private const string WindowsAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    private const string WindowsScript =
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null;" +
        "$xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
        "$text = $xml.GetElementsByTagName('text');" +
        "$null = $text.Item(0).AppendChild($xml.CreateTextNode($env:SR_TITLE));" +
        "$null = $text.Item(1).AppendChild($xml.CreateTextNode($env:SR_BODY));" +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:SR_APP).Show([Windows.UI.Notifications.ToastNotification]::new($xml))";

    public static void Show(string title, string body)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo("osascript");
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
            start = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true };
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
            start = new ProcessStartInfo("notify-send");
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
