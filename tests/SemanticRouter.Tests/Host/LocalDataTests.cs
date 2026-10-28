using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// What belongs to this machine (the state database, models, logs, the WebView2 profile) stays out of Windows' roaming
/// profile: where it goes, and how an install from before is moved.
/// </summary>
public sealed class LocalDataTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-local").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private HostSettings Separate() => new()
    {
        ConfigurationDirectory = Directory.CreateDirectory(Path.Combine(_directory, "Roaming", "semantic-router")).FullName,
        LocalDataDirectory = Path.Combine(_directory, "Local", "semantic-router"),
    };

    [Fact]
    public void On_windows_the_default_configuration_directory_keeps_its_local_data_in_local_app_data()
    {
        Func<string, string?> windows = name => name switch
        {
            "APPDATA" => "/users/ada/AppData/Roaming",
            "LOCALAPPDATA" => "/users/ada/AppData/Local",
            "USERPROFILE" => "/users/ada",
            _ => null,
        };
        string roaming = Path.GetFullPath("/users/ada/AppData/Roaming/semantic-router");
        string local = Path.GetFullPath("/users/ada/AppData/Local/semantic-router");

        HostSettings.LocalDataDirectoryFor(roaming, windows, windows: true).Should().Be(local, "also when the service names it with --config-dir");
        HostSettings.LocalDataDirectoryFor("/srv/router", windows, windows: true).Should().Be("/srv/router", "a directory someone chose keeps everything");
        HostSettings.LocalDataDirectoryFor(roaming, n => n == HostSettings.HomeVariable ? roaming : windows(n), windows: true).Should().Be(roaming);
        HostSettings.LocalDataDirectoryFor("/home/ada/.config/semantic-router", _ => null, windows: false).Should().Be("/home/ada/.config/semantic-router", "nothing roams on Linux and macOS");
        HostSettings.Parse(["--config-dir", _directory], _ => null).LocalDataDirectory.Should().Be(_directory);
    }

    [Fact]
    public void An_install_from_before_is_moved_out_of_the_roaming_profile()
    {
        HostSettings settings = Separate();
        string from = settings.ConfigurationDirectory;
        File.WriteAllText(Path.Combine(from, "semantic-router.json"), "{}");
        File.WriteAllText(Path.Combine(from, "semantic-router.db"), "db");
        File.WriteAllText(Path.Combine(from, "semantic-router.db-wal"), "wal");
        File.WriteAllText(Path.Combine(from, "semantic-router.db.v2.bak"), "backup");
        Directory.CreateDirectory(Path.Combine(from, "models", "all-MiniLM-L6-v2"));
        File.WriteAllText(Path.Combine(from, "models", "all-MiniLM-L6-v2", "model.onnx"), "model");
        Directory.CreateDirectory(Path.Combine(from, "logs"));
        File.WriteAllText(Path.Combine(from, "logs", "semantic-router.log"), "old log");
        Directory.CreateDirectory(Path.Combine(settings.LocalDataDirectory, "logs"));
        File.WriteAllText(Path.Combine(settings.LocalDataDirectory, "logs", "semantic-router.log"), "new log");

        IReadOnlyList<string> moved = LocalData.MoveFromConfigurationDirectory(settings);

        moved.Should().Equal("semantic-router.db", "semantic-router.db.v2.bak", "models");
        File.ReadAllText(Path.Combine(settings.LocalDataDirectory, "semantic-router.db-wal")).Should().Be("wal", "the write-ahead log goes with its database");
        File.ReadAllText(Path.Combine(settings.LocalDataDirectory, "models", "all-MiniLM-L6-v2", "model.onnx")).Should().Be("model");
        File.ReadAllText(Path.Combine(settings.LocalDataDirectory, "logs", "semantic-router.log")).Should().Be("new log", "a file already there is never overwritten");
        File.Exists(Path.Combine(from, "logs", "semantic-router.log")).Should().BeTrue("left where it was, rather than lost");
        Directory.GetFileSystemEntries(from).Select(Path.GetFileName).Should().BeEquivalentTo(["semantic-router.json", "logs"], "the configuration stays");
        LocalData.StateFile(settings).Should().Be(Path.Combine(settings.LocalDataDirectory, "semantic-router.db"));
        LocalData.MoveFromConfigurationDirectory(settings).Should().BeEmpty();
    }

    [Fact]
    public void A_backup_or_log_already_in_local_data_is_kept_when_the_move_cannot_take_its_place()
    {
        // #272: a failed move used to delete the file already at the target.
        HostSettings settings = Separate();
        string from = settings.ConfigurationDirectory;
        File.WriteAllText(Path.Combine(from, "semantic-router.db"), "db");
        File.WriteAllText(Path.Combine(from, "semantic-router.db.v2.bak"), "old backup");
        Directory.CreateDirectory(settings.LocalDataDirectory);
        File.WriteAllText(Path.Combine(settings.LocalDataDirectory, "semantic-router.db.v2.bak"), "kept backup");

        LocalData.MoveFromConfigurationDirectory(settings).Should().Equal("semantic-router.db");

        File.ReadAllText(Path.Combine(settings.LocalDataDirectory, "semantic-router.db.v2.bak")).Should().Be("kept backup");
        File.ReadAllText(Path.Combine(from, "semantic-router.db.v2.bak")).Should().Be("old backup", "left where it was, rather than lost");

        HostSettings other = new()
        {
            ConfigurationDirectory = Directory.CreateDirectory(Path.Combine(_directory, "Roaming2")).FullName,
            LocalDataDirectory = Directory.CreateDirectory(Path.Combine(_directory, "Local2")).FullName,
        };
        File.WriteAllText(Path.Combine(other.ConfigurationDirectory, "semantic-router.db"), "db");
        File.WriteAllText(Path.Combine(other.ConfigurationDirectory, "semantic-router.db-wal"), "wal");
        File.WriteAllText(Path.Combine(other.LocalDataDirectory, "semantic-router.db-wal"), "stale wal");

        LocalData.MoveFromConfigurationDirectory(other).Should().BeEmpty("the log cannot go with its database");
        File.ReadAllText(Path.Combine(other.LocalDataDirectory, "semantic-router.db-wal")).Should().Be("stale wal");
        File.ReadAllText(Path.Combine(other.ConfigurationDirectory, "semantic-router.db-wal")).Should().Be("wal");
    }

    [Fact]
    public void A_database_already_in_local_data_wins_and_an_unmoved_one_is_still_used()
    {
        HostSettings settings = Separate();
        File.WriteAllText(Path.Combine(settings.ConfigurationDirectory, "semantic-router.db"), "old");
        LocalData.StateFile(settings).Should().Be(Path.Combine(settings.ConfigurationDirectory, "semantic-router.db"), "until it is moved, the earlier database is the one with the data");

        Directory.CreateDirectory(settings.LocalDataDirectory);
        File.WriteAllText(Path.Combine(settings.LocalDataDirectory, "semantic-router.db"), "new");
        LocalData.MoveFromConfigurationDirectory(settings).Should().BeEmpty();
        File.ReadAllText(Path.Combine(settings.LocalDataDirectory, "semantic-router.db")).Should().Be("new");
        LocalData.StateFile(settings).Should().Be(Path.Combine(settings.LocalDataDirectory, "semantic-router.db"));
        LocalData.OnNetworkPath(_directory).Should().BeFalse();
    }

    [Fact]
    public async Task The_router_keeps_its_state_in_local_data()
    {
        HostSettings settings = Separate() with { HostArguments = ["--urls", "http://127.0.0.1:0"] };
        var output = new StringWriter();

        await using (RunningRouter? router = await RouterHost.StartAsync(settings, output, b => b.Logging.ClearProviders()))
        {
            router.Should().NotBeNull();
        }

        File.Exists(Path.Combine(settings.LocalDataDirectory, "semantic-router.db")).Should().BeTrue();
        File.Exists(Path.Combine(settings.ConfigurationDirectory, "semantic-router.db")).Should().BeFalse();
        File.Exists(settings.ConfigurationFile).Should().BeTrue("the configuration stays where it roams");
    }
}
