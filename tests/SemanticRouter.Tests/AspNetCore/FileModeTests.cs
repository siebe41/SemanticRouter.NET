using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>What the router keeps on disk is for the user who runs it (Unix file modes; Windows profiles are private).</summary>
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
        new JsonLinesEvaluationSetStore(evaluation).Save([new EvaluationCase("look up the customer record", 1)]);
        string state = Path.Combine(_directory, "semantic-router.db");
        _ = new SqliteRouterDatabase(
            Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = state }), NullLogger<SqliteRouterDatabase>.Instance);

        File.GetUnixFileMode(config).Should().Be(OwnerFile);
        File.GetUnixFileMode(evaluation).Should().Be(OwnerFile);
        File.GetUnixFileMode(state).Should().Be(OwnerFile);
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
}
