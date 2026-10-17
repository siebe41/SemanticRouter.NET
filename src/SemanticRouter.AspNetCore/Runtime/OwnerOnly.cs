using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// File modes for what the router keeps on disk: the configuration, prompt previews, the evaluation set and logs are
/// for the user who runs it, not for every account on the machine (the umask usually leaves files world-readable).
/// On Windows a profile's folders are private already; a folder the router creates outside the profile (a custom
/// <c>SEMANTIC_ROUTER_HOME</c> or <c>SEMANTIC_ROUTER_KEYS_DIR</c>, which would inherit an ACL that usually lets every
/// user read it) gets an ACL of its own instead (<see cref="CreateProtected"/>).
/// </summary>
internal static class OwnerOnly
{
    private const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates <paramref name="path"/> if needed and leaves it readable by its owner only: 0700 on Unix; on Windows, a
    /// folder it creates outside the user's profile is for this account, SYSTEM and Administrators only.
    /// </summary>
    public static void Directory(string path) => Directory(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary><see cref="Directory(string)"/>, with the user's profile given (for tests).</summary>
    internal static void Directory(string path, string? profile)
    {
        if (OperatingSystem.IsWindows())
        {
            // An existing folder keeps its ACL (it may be shared on purpose); doctor says when others can read it.
            if (!System.IO.Directory.Exists(path) && !IsInside(path, profile))
            {
                CreateProtected(path);
            }

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

    /// <summary>Whether <paramref name="path"/> is <paramref name="profile"/> or a folder in it (case ignored, as on Windows).</summary>
    internal static bool IsInside(string path, string? profile)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return false;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates <paramref name="path"/> (its parents as usual) with a protected ACL: full control for this account,
    /// SYSTEM and Administrators, inherited by everything in it, and nothing inherited from its parent. Returns
    /// whether it did; on failure the folder is left to <see cref="System.IO.Directory.CreateDirectory(string)"/> and
    /// the reason written to standard error, since the router starts without it (doctor warns about the folder).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool CreateProtected(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } parent)
            {
                System.IO.Directory.CreateDirectory(parent);
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            foreach (SecurityIdentifier sid in new[] { user.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(full).Create(security);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Could not limit {path} to this account ({ex.Message}); it keeps the permissions of the folder it is in. `semantic-router doctor` says who can read it.");
            return false;
        }
    }

    /// <summary>
    /// The broad groups that may read what is in <paramref name="directory"/>: <c>Users</c>, <c>Everyone</c> and
    /// <c>Authenticated Users</c>, by an allowing ACE on the folder itself or one its files inherit. Empty when none
    /// may, or when the ACL cannot be read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<string> BroadReaders(string directory)
    {
        (SecurityIdentifier Sid, string Name)[] broad =
        [
            (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), "Users"),
            (new SecurityIdentifier(WellKnownSidType.WorldSid, null), "Everyone"),
            (new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), "Authenticated Users"),
        ];

        // Read access as a file right, or as GENERIC_READ / GENERIC_ALL, which an inheritable ACE can hold unmapped.
        const int Read = (int)FileSystemRights.ReadData | unchecked((int)0x80000000) | 0x10000000;
        try
        {
            DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            var names = new List<string>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && ((int)rule.FileSystemRights & Read) != 0
                    && rule.IdentityReference is SecurityIdentifier sid
                    && broad.FirstOrDefault(b => b.Sid == sid).Name is { } name && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not this account's to read: nothing to say about it.
            return [];
        }
    }

    [UnsupportedOSPlatform("windows")]
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
