namespace SemanticRouter.AspNetCore;

/// <summary>The result of <see cref="CliExecutableLocator.Find(string)"/>.</summary>
/// <param name="Requested">The executable as configured.</param>
/// <param name="Path">The full path of a directly startable executable, or <see langword="null"/>.</param>
/// <param name="ScriptPath">
/// A script wrapper that was found instead (an npm <c>.cmd</c> shim, a <c>.bat</c> or <c>.ps1</c> file). The router
/// never starts these: <c>cmd.exe</c> would re-parse the prompt, which may contain shell syntax.
/// </param>
public sealed record CliExecutableLookup(string Requested, string? Path, string? ScriptPath)
{
    /// <summary>Whether a startable executable was found.</summary>
    public bool Found => Path is not null;

    /// <summary>What is wrong and how to fix it, naming the setting; <see langword="null"/> when found.</summary>
    public string? Problem(string connector)
    {
        if (Found)
        {
            return null;
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(Requested);
        return ScriptPath is not null
            ? $"only a script wrapper was found ({ScriptPath}). The router starts executables directly, never through cmd.exe or " +
              $"PowerShell, because those would re-parse the prompt. Install the native binary ({name}.exe) or set " +
              $"SemanticRouter:Connectors:{connector}:Executable to the .exe."
            : $"'{Requested}' was not found on PATH. Install it, or set SemanticRouter:Connectors:{connector}:Executable to its full path.";
    }
}

/// <summary>
/// Finds CLI executables: a path as given, or a search of the absolute PATH entries that adds <c>.exe</c> on Windows. The
/// process runner starts the full path this returns, never a bare name, because the operating system would also look
/// in the current directory (and .NET on Unix in the application's directory) before PATH, and the router may be
/// started from a cloned repository that contains a file named <c>claude</c>. Relative and empty PATH entries are
/// skipped for the same reason. A relative path with a directory (<c>tools/llm</c>) is taken as configured, relative to
/// the router's current directory.
/// </summary>
public static class CliExecutableLocator
{
    private static readonly string[] ScriptExtensions = [".cmd", ".bat", ".ps1"];

    /// <summary>Whether <paramref name="executable"/> names a script the router refuses to start (<c>.cmd</c>, <c>.bat</c>, <c>.ps1</c>).</summary>
    public static bool IsScript(string executable) =>
        ScriptExtensions.Contains(Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks up <paramref name="executable"/> on this machine.</summary>
    public static CliExecutableLookup Find(string executable) =>
        Find(executable, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>Looks up <paramref name="executable"/> on <paramref name="searchPath"/> as Windows or POSIX would.</summary>
    public static CliExecutableLookup Find(string executable, string? searchPath, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (IsScript(executable))
        {
            string? script = Path.IsPathRooted(executable) || HasDirectory(executable)
                ? (File.Exists(executable) ? Path.GetFullPath(executable) : null)
                : Search(executable, [string.Empty], searchPath);
            return new CliExecutableLookup(executable, null, script ?? executable);
        }

        if (Path.IsPathRooted(executable) || HasDirectory(executable))
        {
            string full = Path.GetFullPath(executable);
            if (File.Exists(full))
            {
                return new CliExecutableLookup(executable, full, null);
            }

            if (windows && !Path.HasExtension(full) && File.Exists(full + ".exe"))
            {
                return new CliExecutableLookup(executable, full + ".exe", null);
            }

            return new CliExecutableLookup(executable, null, windows ? FindScriptBeside(full) : null);
        }

        string[] extensions = windows && !Path.HasExtension(executable) ? [".exe", ".com"] : [string.Empty];
        if (Search(executable, extensions, searchPath) is { } found)
        {
            return new CliExecutableLookup(executable, found, null);
        }

        return new CliExecutableLookup(executable, null, windows ? Search(executable, ScriptExtensions, searchPath) : null);
    }

    private static bool HasDirectory(string executable) =>
        executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar);

    private static string? FindScriptBeside(string full) =>
        ScriptExtensions.Select(extension => full + extension).FirstOrDefault(File.Exists);

    private static string? Search(string executable, string[] extensions, string? searchPath)
    {
        foreach (string directory in (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(entry))
            {
                continue; // "." or "bin" would mean the current directory, whatever that is
            }

            foreach (string extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(entry, executable + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
