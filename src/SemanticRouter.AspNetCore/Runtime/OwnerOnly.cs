namespace SemanticRouter.AspNetCore;

/// <summary>
/// File modes for what the router keeps on disk: the configuration, prompt previews, the evaluation set and logs are
/// for the user who runs it, not for every account on the machine (the umask usually leaves files world-readable).
/// Nothing to do on Windows, where a profile's folders are private already.
/// </summary>
internal static class OwnerOnly
{
    private const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> if needed and leaves it readable by its owner only (0700).</summary>
    public static void Directory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
            return;
        }

        System.IO.Directory.CreateDirectory(path, Folder);
        Restrict(path, Folder);
    }

    /// <summary>Makes an existing file readable and writable by its owner only (0600); a missing file is left alone.</summary>
    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.File.Exists(path))
        {
            Restrict(path, File);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Restrict(string path, UnixFileMode mode)
    {
        try
        {
            if ((System.IO.File.GetUnixFileMode(path) & ~mode) != 0)
            {
                System.IO.File.SetUnixFileMode(path, mode);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Another user's file or folder (a shared SEMANTIC_ROUTER_HOME): theirs to change.
        }
    }
}
