using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Storage.Sqlite;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>What the router keeps on disk is for the user who runs it (Unix file modes; on Windows, an ACL for a folder outside the profile).</summary>
public sealed class FileModeTests : IDisposable
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerFolder = OwnerFile | UnixFileMode.UserExecute;

    private readonly string _directory = Directory.CreateTempSubdirectory("router-modes").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_configuration_evaluation_set_and_state_are_readable_by_their_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string config = Path.Combine(_directory, "semantic-router.json");
        new JsonFileRouterConfigurationStore(config).Save(new RouterConfiguration());
        string evaluation = Path.Combine(_directory, "semantic-router.evaluation.jsonl");
        new JsonLinesEvaluationSetStore(evaluation).Save([new EvaluationCase { Prompt = "look up the customer record", ExpectedTier = 1 }]);
        string state = Path.Combine(_directory, "semantic-router.db");
        _ = new SqliteRouterDatabase(
            Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = state }), NullLogger<SqliteRouterDatabase>.Instance);

        File.GetUnixFileMode(config).Should().Be(OwnerFile);
        File.GetUnixFileMode(evaluation).Should().Be(OwnerFile);
        File.GetUnixFileMode(state).Should().Be(OwnerFile);
    }

    [Fact]
    public void Owner_only_files_are_created_with_their_mode_rather_than_restricted_afterwards()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The usual umask leaves a new file readable by everyone; these are 0600 from the moment they exist.
        string empty = Path.Combine(_directory, "state.db");
        OwnerOnly.CreateFile(empty);
        File.GetUnixFileMode(empty).Should().Be(OwnerFile);
        new FileInfo(empty).Length.Should().Be(0);

        // A leftover temporary file from an older release, readable by everyone, is not written into.
        string secrets = Path.Combine(_directory, "router.secrets.json");
        File.WriteAllText(secrets + ".tmp", "stale");
        File.SetUnixFileMode(secrets + ".tmp", OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        OwnerOnly.WriteAllText(secrets, "{\"k\":\"v\"}");
        File.GetUnixFileMode(secrets).Should().Be(OwnerFile);
        File.ReadAllText(secrets).Should().Be("{\"k\":\"v\"}");
        File.Exists(secrets + ".tmp").Should().BeFalse();

        // An existing file is left as it is: CreateFile does not truncate a database.
        File.WriteAllText(empty, "data");
        OwnerOnly.CreateFile(empty);
        File.ReadAllText(empty).Should().Be("data");
    }

    [Fact]
    public void Sqlite_copies_a_database_into_an_empty_owner_only_file()
    {
        string source = Path.Combine(_directory, "source.db");
        string copy = Path.Combine(_directory, "copy.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={source};Pooling=False"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (42);";
            create.ExecuteNonQuery();

            OwnerOnly.CreateFile(copy);
            using Microsoft.Data.Sqlite.SqliteCommand vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $file;";
            vacuum.Parameters.AddWithValue("$file", copy);
            vacuum.ExecuteNonQuery();
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT x FROM t;";
            Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(42);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(copy).Should().Be(OwnerFile, "VACUUM INTO keeps the mode of the file it writes into");
        }
    }

    [Fact]
    public void The_configuration_directory_is_tightened_to_its_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string home = Path.Combine(_directory, "semantic-router");
        Directory.CreateDirectory(home);
        File.SetUnixFileMode(home, OwnerFolder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        OwnerOnly.Directory(home);
        OwnerOnly.Directory(Path.Combine(_directory, "new"));

        File.GetUnixFileMode(home).Should().Be(OwnerFolder);
        File.GetUnixFileMode(Path.Combine(_directory, "new")).Should().Be(OwnerFolder);
    }

    [Fact]
    public void A_folder_is_in_the_profile_only_when_it_is_the_profile_or_under_it()
    {
        string profile = Path.Combine(_directory, "Users", "me");
        OwnerOnly.IsInside(profile, profile).Should().BeTrue();
        OwnerOnly.IsInside(Path.Combine(profile, "AppData", "Roaming", "semantic-router"), profile).Should().BeTrue();
        OwnerOnly.IsInside(Path.Combine(profile, "..", "me", "router"), profile).Should().BeTrue("the path is resolved first");
        OwnerOnly.IsInside(Path.Combine(_directory, "Users", "me-too", "router"), profile).Should().BeFalse("a sibling whose name starts the same is not inside");
        OwnerOnly.IsInside(Path.Combine(_directory, "ProgramData", "semantic-router"), profile).Should().BeFalse();
        OwnerOnly.IsInside(Path.Combine(profile, "router"), null).Should().BeFalse("without a profile every folder is outside it");

        // Only Windows has anything to say about a folder's ACL; on Unix the router keeps it at 0700 itself.
        if (!OperatingSystem.IsWindows())
        {
            SemanticRouter.Host.DoctorCommand.FolderAccessCheck("configuration folder", _directory, "the configuration").Should().BeNull();
        }
    }

    [Fact]
    public void On_Windows_a_new_folder_outside_the_profile_is_for_this_account_SYSTEM_and_Administrators_only()
    {
        if (OperatingSystem.IsWindows())
        {
            NewFolderOutsideTheProfile();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void NewFolderOutsideTheProfile()
    {
        // The temporary folder is in the profile on Windows (and on CI), so the profile is given as somewhere else.
        string home = Path.Combine(_directory, "ProgramData", "semantic-router");
        OwnerOnly.Directory(home, profile: Path.Combine(_directory, "Users", "me"));

        DirectorySecurity security = new DirectoryInfo(home).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue("nothing is inherited from the folder it is in");
        var sids = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (Sid: (SecurityIdentifier)r.IdentityReference, r.FileSystemRights, r.InheritanceFlags))
            .ToList();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        sids.Select(r => r.Sid).Should().BeEquivalentTo(
            new[] { user.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct());
        sids.Should().OnlyContain(r => r.FileSystemRights.HasFlag(FileSystemRights.FullControl) && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        OwnerOnly.BroadReaders(home).Should().BeEmpty();

        // What the router writes in it inherits that: the configuration, the state database, the logs.
        string config = Path.Combine(home, "semantic-router.json");
        File.WriteAllText(config, "{}");
        new FileInfo(config).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).Should().NotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        SemanticRouter.Host.DoctorCommand.FolderAccessCheck("configuration folder", home, "the configuration").Should().BeNull();

        // A folder in the profile keeps what it inherits, and one that exists already keeps its ACL.
        string inside = Path.Combine(_directory, "Users", "me", "semantic-router");
        OwnerOnly.Directory(inside, profile: Path.Combine(_directory, "Users", "me"));
        new DirectoryInfo(inside).GetAccessControl().AreAccessRulesProtected.Should().BeFalse();
        string existing = Directory.CreateDirectory(Path.Combine(_directory, "D", "router")).FullName;
        OwnerOnly.Directory(existing, profile: Path.Combine(_directory, "Users", "me"));
        new DirectoryInfo(existing).GetAccessControl().AreAccessRulesProtected.Should().BeFalse();

        // The ACL helper on its own, as on a folder outside the profile.
        string direct = Path.Combine(_directory, "direct");
        OwnerOnly.CreateProtected(direct).Should().BeTrue();
        new DirectoryInfo(direct).GetAccessControl().AreAccessRulesProtected.Should().BeTrue();
    }

    [Fact]
    public void On_Windows_doctor_warns_about_a_folder_that_Users_can_read()
    {
        if (OperatingSystem.IsWindows())
        {
            FolderThatUsersCanRead();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void FolderThatUsersCanRead()
    {
        string shared = Path.Combine(_directory, "shared");
        OwnerOnly.CreateProtected(shared).Should().BeTrue();
        SemanticRouter.Host.DoctorCommand.FolderAccessCheck("configuration folder", shared, "the configuration").Should().BeNull();

        // As C:\ProgramData or another drive's root hands it down: Users may read and list what is in it.
        var info = new DirectoryInfo(shared);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadData,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        info.SetAccessControl(security);

        OwnerOnly.BroadReaders(shared).Should().BeEquivalentTo("Users", "Authenticated Users");
        RouterCheck? check = SemanticRouter.Host.DoctorCommand.FolderAccessCheck("configuration folder", shared, "the configuration");
        check.Should().NotBeNull();
        check!.Status.Should().Be(RouterCheckStatus.Warning);
        check.Message.Should().StartWith($"{shared} can be read by ").And.Contain("Authenticated Users")
            .And.Contain("so other accounts on this machine can read the configuration.").And.Contain("icacls");
    }
}
