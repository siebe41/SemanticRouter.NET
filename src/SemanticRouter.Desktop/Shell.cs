using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SemanticRouter.Desktop;

/// <summary>Hands URLs and folders to the operating system, and says whether the app window can show the dashboard.</summary>
internal static class Shell
{
    /// <summary>Opens <paramref name="target"/> (a URL, file or folder) with its default application.</summary>
    public static void Open(string target)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not open {target}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the platform web view is there: always on macOS, WebView2 on Windows (part of Windows 11, installed on
    /// most Windows 10 machines), and WPE WebKit or WebKitGTK on Linux, which some distributions do not install.
    /// </summary>
    public static bool WebViewAvailable { get; } = DetectWebView();

    /// <summary>What to install so the app window works, for the menu.</summary>
    public static string WebViewRequirement => OperatingSystem.IsWindows()
        ? "needs the WebView2 runtime"
        : "needs WebKitGTK 4.1 or WPE WebKit 2.0";

    private static bool DetectWebView()
    {
        if (OperatingSystem.IsMacOS())
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            // Microsoft's documented check: the runtime registers a version under EdgeUpdate's client key.
            const string Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            foreach ((RegistryKey root, string path) in new[]
            {
                (Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\{Client}"),
                (Registry.LocalMachine, $@"SOFTWARE\{Client}"),
                (Registry.CurrentUser, $@"Software\{Client}"),
            })
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0")
                {
                    return true;
                }
            }

            return false;
        }

        foreach (string library in new[] { "libWPEWebKit-2.0.so.1", "libwebkit2gtk-4.1.so.0" })
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }
}
