using System.Diagnostics;

namespace SemanticRouter.Desktop;

/// <summary>
/// A program started from the Dock, Finder or a desktop launcher does not get the PATH your shell sets up, so
/// <c>claude</c>, <c>codex</c> and <c>copilot</c> in <c>~/.local/bin</c>, Homebrew or an npm prefix would not be found.
/// Like editors do, the app asks your login shell for its PATH once at start and adds what is missing.
/// </summary>
internal static class ShellEnvironment
{
    private const string Marker = "__SEMANTIC_ROUTER_PATH__";

    /// <summary>Merges the login shell's PATH into this process's; does nothing on Windows or when the shell fails.</summary>
    public static void AdoptLoginShellPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string? shellPath = ReadLoginShellPath(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh");
        string merged = Merge(Environment.GetEnvironmentVariable("PATH"), shellPath, CommonDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Environment.SetEnvironmentVariable("PATH", merged);
    }

    /// <summary>The current entries first, then the shell's and the usual install locations that exist, without repeats.</summary>
    internal static string Merge(string? current, string? shell, IEnumerable<string> common)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (string entry in (current ?? string.Empty).Split(':')
            .Concat((shell ?? string.Empty).Split(':'))
            .Concat(common.Where(Directory.Exists)))
        {
            if (entry.Length > 0 && seen.Add(entry))
            {
                entries.Add(entry);
            }
        }

        return string.Join(':', entries);
    }

    internal static IEnumerable<string> CommonDirectories(string home) =>
    [
        Path.Combine(home, ".local", "bin"),
        Path.Combine(home, ".npm-global", "bin"),
        Path.Combine(home, ".dotnet", "tools"),
        "/opt/homebrew/bin",
        "/usr/local/bin",
    ];

    /// <summary>The PATH an interactive login shell ends up with, or <see langword="null"/> after five seconds.</summary>
    internal static string? ReadLoginShellPath(string shell)
    {
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-l", "-i", "-c", $"printf '{Marker}%s{Marker}' \"$PATH\"" })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start)!;
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return Extract(output.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The PATH between the markers; shell start-up files may print anything around it.</summary>
    internal static string? Extract(string output)
    {
        int start = output.IndexOf(Marker, StringComparison.Ordinal);
        int end = start < 0 ? -1 : output.IndexOf(Marker, start + Marker.Length, StringComparison.Ordinal);
        return end < 0 ? null : output[(start + Marker.Length)..end];
    }
}
