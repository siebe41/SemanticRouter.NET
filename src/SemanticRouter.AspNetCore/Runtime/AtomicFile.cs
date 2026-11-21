using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Replaces a file in one step, so a crash, a full disk or a second writer never leaves it truncated or half written:
/// the content goes to a temporary file with a name of its own in the same folder (so two processes never share it),
/// created readable by its owner only (<see cref="OwnerOnly"/>), flushed to disk, then renamed over the target. A reader
/// sees the old file or the new one, never a mix. On failure the temporary file is removed and the target is untouched.
/// The replaced file is owner-only too, as every file the router writes is.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="contents"/> as UTF-8 (no byte order mark, as <see cref="File.WriteAllText(string, string)"/>).</summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        WriteAllBytes(path, Utf8.GetBytes(contents));
    }

    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>, creating its folder when needed.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string target = Path.GetFullPath(path);
        var existing = new FileInfo(target);
        if (existing.LinkTarget is not null && existing.ResolveLinkTarget(returnFinalTarget: true) is { } linked)
        {
            // A symbolic link (a configuration kept with someone's dotfiles) stays one: the file it points to is replaced.
            target = linked.FullName;
        }

        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temp = TempNameFor(target);
        try
        {
            using (var stream = new FileStream(temp, CreateOptions()))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, target);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Reads a file that <see cref="WriteAllText"/> may be replacing at the same moment, in another thread or process.
    /// It is opened with <see cref="FileShare.Delete"/>, so the read does not stop a rename over it, and on Windows, where
    /// a rename holds the file for a moment, the read is tried again briefly instead of failing.
    /// </summary>
    public static string ReadAllText(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 10
                                       && ex is (IOException and not FileNotFoundException and not DirectoryNotFoundException) or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> the same way (owner-only, replaced in one step),
    /// byte for byte. Returns <see langword="false"/> when there is no <paramref name="source"/>.
    /// </summary>
    public static bool Copy(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        WriteAllBytes(destination, File.ReadAllBytes(source));
        return true;
    }

    /// <summary>A temporary name next to <paramref name="target"/> no other writer uses: <c>.name.&lt;random&gt;.tmp</c>.</summary>
    internal static string TempNameFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

    private static FileStreamOptions CreateOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            // Never readable by others, not even until the rename: 0600 from the start (the umask can only take bits away).
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void Replace(string temp, string target)
    {
        // On Windows a reader that has the target open without FILE_SHARE_DELETE (File.ReadAllText, for one) makes the
        // rename fail for a moment; on Unix the rename always replaces it at once.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; a dot file the next write does not collide with.
        }
    }
}
