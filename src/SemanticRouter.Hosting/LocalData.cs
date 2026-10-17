namespace SemanticRouter.Host;

/// <summary>
/// What the router keeps that belongs to this machine rather than to its configuration: the state database, the
/// downloaded embedding models, the logs and the desktop window's WebView2 profile. They live in
/// <see cref="HostSettings.LocalDataDirectory"/>, which is the configuration directory except on Windows by default:
/// <c>%APPDATA%</c> is the roaming profile, which managed machines copy to a server at every sign-out or redirect to a
/// file share, where SQLite's write-ahead log does not work and caches do not belong. There they go to
/// <c>%LOCALAPPDATA%\semantic-router</c>, and an install from before is moved there on start.
/// </summary>
internal static class LocalData
{
    /// <summary>The folders that move with the state database.</summary>
    internal static readonly string[] Folders = ["models", "logs", "webview"];

    /// <summary>
    /// The state database: in the local data directory, unless an earlier install's database is still in the
    /// configuration directory because it could not be moved (another router had it open); then that one, so nothing
    /// is lost and the next start tries again.
    /// </summary>
    public static string StateFile(HostSettings settings)
    {
        string local = Path.Combine(settings.LocalDataDirectory, HostSettings.StateFileName);
        string earlier = Path.Combine(settings.ConfigurationDirectory, HostSettings.StateFileName);
        return Separate(settings) && !File.Exists(local) && File.Exists(earlier) ? earlier : local;
    }

    /// <summary>The folder for downloaded embedding models.</summary>
    public static string ModelsDirectory(HostSettings settings) => Path.Combine(settings.LocalDataDirectory, "models");

    /// <summary>
    /// Moves what an earlier install kept in the configuration directory to the local data directory: the state
    /// database with its write-ahead log and backups, then <see cref="Folders"/>, merged into what is there already. A
    /// database another process holds open stays where it is (<see cref="StateFile"/> keeps using it); a folder on
    /// another drive (a redirected <c>%APPDATA%</c>) is copied, then removed. Returns what was moved, for the log.
    /// </summary>
    public static IReadOnlyList<string> MoveFromConfigurationDirectory(HostSettings settings)
    {
        var moved = new List<string>();
        if (!Separate(settings) || !Directory.Exists(settings.ConfigurationDirectory))
        {
            return moved;
        }

        Directory.CreateDirectory(settings.LocalDataDirectory);
        string database = Path.Combine(settings.ConfigurationDirectory, HostSettings.StateFileName);
        string target = Path.Combine(settings.LocalDataDirectory, HostSettings.StateFileName);
        if (File.Exists(database) && !File.Exists(target) && !InUse(database))
        {
            // The write-ahead log and its index go with the database or not at all: one without the other loses data.
            string[] log = [.. new[] { database + "-wal", database + "-shm" }.Where(File.Exists)];
            string[] logMoved = [.. log.TakeWhile(f => TryMove(f, Path.Combine(settings.LocalDataDirectory, Path.GetFileName(f))))];
            if (logMoved.Length == log.Length && TryMove(database, target))
            {
                moved.Add(HostSettings.StateFileName);
                foreach (string backup in Directory.GetFiles(settings.ConfigurationDirectory, HostSettings.StateFileName + ".v*.bak"))
                {
                    if (TryMove(backup, Path.Combine(settings.LocalDataDirectory, Path.GetFileName(backup))))
                    {
                        moved.Add(Path.GetFileName(backup));
                    }
                }
            }
            else
            {
                foreach (string file in logMoved)
                {
                    TryMove(Path.Combine(settings.LocalDataDirectory, Path.GetFileName(file)), file);
                }
            }
        }

        foreach (string folder in Folders)
        {
            string from = Path.Combine(settings.ConfigurationDirectory, folder);
            if (Directory.Exists(from) && Merge(from, Path.Combine(settings.LocalDataDirectory, folder)))
            {
                moved.Add(folder);
            }
        }

        return moved;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is on a network file system (a UNC path, or a mapped network drive) on Windows,
    /// where SQLite's write-ahead log cannot be shared safely between the tray and the background service.
    /// </summary>
    public static bool OnNetworkPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return !full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Path.GetPathRoot(full) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Separate(HostSettings settings) =>
        !string.Equals(Path.GetFullPath(settings.LocalDataDirectory), Path.GetFullPath(settings.ConfigurationDirectory), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether another process has <paramref name="file"/> open (on Windows, where an open file cannot be moved).</summary>
    private static bool InUse(string file)
    {
        try
        {
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open in another process, or on another drive: copied only when nothing has it open, else left for next time.
            try
            {
                File.Copy(from, to);
                File.Delete(from);
                return true;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                File.Delete(to);
                return false;
            }
        }
    }

    /// <summary>Moves every file and folder in <paramref name="from"/> that <paramref name="to"/> lacks, then removes what is left empty.</summary>
    private static bool Merge(string from, string to)
    {
        bool any = false;
        try
        {
            Directory.CreateDirectory(to);
            foreach (string entry in Directory.EnumerateFileSystemEntries(from).ToArray())
            {
                string target = Path.Combine(to, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    any |= Merge(entry, target);
                }
                else if (!File.Exists(target))
                {
                    any |= TryMove(entry, target);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(from).Any())
            {
                Directory.Delete(from);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: a cache, recreated where it now belongs.
        }

        return any;
    }
}
