namespace SemanticRouter.AspNetCore;

/// <summary>
/// The working directories CLI runs get. A vendor CLI reads instructions, settings and hooks from its working directory,
/// and Claude Code reads <c>CLAUDE.md</c> from every directory above it too, so nobody else may be able to write to any of
/// them: a shared, world-writable parent such as <c>/tmp</c> would let another local user steer the CLI.
/// </summary>
/// <remarks>
/// On Linux the parent is <c>$XDG_RUNTIME_DIR/semantic-router</c> (per user, mode 0700 by specification), or else
/// <c>$XDG_CACHE_HOME/semantic-router</c> (<c>~/.cache</c>). On macOS it is under the per-user <c>$TMPDIR</c>, or else
/// <c>~/Library/Caches</c>. On Unix it is created with mode 0700 and checked before every use. On Windows the temporary
/// directory is already private to the user.
/// </remarks>
internal static class CliScratch
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="name"/> (if needed) in the private parent and returns its full path.</summary>
    /// <exception cref="IOException">The parent is not private to this user, or cannot be created.</exception>
    public static string Create(string name)
    {
        string directory = Path.Combine(Root(), name);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, Private);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Could not create the CLI working directory {directory}: {ex.Message}", ex);
        }

        return directory;
    }

    /// <summary>The private parent of every working directory, created and checked.</summary>
    /// <exception cref="IOException">It is not private to this user, or cannot be created.</exception>
    public static string Root()
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "semantic-router")).FullName;
        }

        return EnsurePrivate(DefaultRoot(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsMacOS()));
    }

    /// <summary>Where the parent goes on a Unix system with this environment and home directory.</summary>
    internal static string DefaultRoot(Func<string, string?> environment, string home, bool macOS)
    {
        // The runtime directory is set up by the login session; a stale variable pointing nowhere is skipped.
        if (Absolute(environment("XDG_RUNTIME_DIR")) is { } runtime && Directory.Exists(runtime))
        {
            return Path.Combine(runtime, "semantic-router");
        }

        // macOS gives every user a private TMPDIR under /var/folders; /tmp, its fallback, is shared.
        if (macOS && Absolute(environment("TMPDIR")) is { } temp && Directory.Exists(temp))
        {
            return Path.Combine(temp, "semantic-router");
        }

        string cache = Absolute(environment("XDG_CACHE_HOME")) ?? Path.Combine(home, macOS ? Path.Combine("Library", "Caches") : ".cache");
        return Path.Combine(cache, "semantic-router");
    }

    /// <summary>
    /// Creates <paramref name="path"/> with mode 0700 and checks that only this user can write to it or to any directory
    /// above it. Proves ownership by setting the mode: only the owner may.
    /// </summary>
    /// <exception cref="IOException">It is not private to this user.</exception>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string EnsurePrivate(string path)
    {
        const string Fix = "Set XDG_RUNTIME_DIR (or XDG_CACHE_HOME) to a directory only you can write to.";
        if (!Path.IsPathFullyQualified(path))
        {
            throw new IOException($"The CLI working directory parent '{path}' is not an absolute path (is HOME set?). {Fix}");
        }

        try
        {
            Directory.CreateDirectory(path, Private);
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                throw new IOException($"{path} is a symbolic link; CLI working directories must be in a real directory. {Fix}");
            }

            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new IOException($"{path} can be written to by other users, who could have left instructions for the CLI in it. Remove it, or: {Fix}");
            }

            // chmod succeeds only for the owner, so this also refuses a directory another user created first.
            File.SetUnixFileMode(path, Private);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"{path} belongs to another user or cannot be used ({ex.Message}). {Fix}", ex);
        }

        for (DirectoryInfo? above = Directory.GetParent(path); above?.Parent is not null; above = above.Parent)
        {
            if ((File.GetUnixFileMode(above.FullName) & UnixFileMode.OtherWrite) != 0)
            {
                throw new IOException(
                    $"{path} is inside {above.FullName}, which every user can write to, and CLIs read instruction files from the " +
                    $"directories above their working directory. {Fix}");
            }
        }

        return path;
    }

    private static string? Absolute(string? path) => path is { Length: > 0 } && Path.IsPathFullyQualified(path) ? path : null;
}
