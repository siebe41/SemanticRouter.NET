using Microsoft.AspNetCore.DataProtection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Configuration and state files are replaced in one step, through a temporary file of their own, so a crash or a second
/// writer never leaves one truncated (#400); the warm-cache store never loses a concurrent update.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory = Directory.CreateTempSubdirectory("router-atomic").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string[] Files() => [.. Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void A_file_is_replaced_whole_owner_only_and_without_a_temporary_file_left()
    {
        string file = Path.Combine(_directory, "nested", "state.json");

        AtomicFile.WriteAllText(file, "first");
        AtomicFile.WriteAllText(file, "second ✓");

        File.ReadAllText(file).Should().Be("second ✓");
        File.ReadAllBytes(file).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte order mark, as File.WriteAllText");
        Directory.GetFiles(Path.GetDirectoryName(file)!).Should().ContainSingle();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(file).Should().Be(OwnerFile);
        }
    }

    [Fact]
    public void A_file_others_could_read_is_replaced_by_one_only_its_owner_can()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string file = Path.Combine(_directory, "semantic-router.json");
        File.WriteAllText(file, "{}");
        File.SetUnixFileMode(file, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        AtomicFile.WriteAllText(file, "{ }");

        File.GetUnixFileMode(file).Should().Be(OwnerFile);
    }

    [Fact]
    public void A_write_that_fails_leaves_the_target_and_no_temporary_file()
    {
        string target = Directory.CreateDirectory(Path.Combine(_directory, "a-folder")).FullName;
        File.WriteAllText(Path.Combine(target, "inside.txt"), "kept");

        FluentActions.Invoking(() => AtomicFile.WriteAllText(target, "x")).Should().Throw<IOException>();

        File.ReadAllText(Path.Combine(target, "inside.txt")).Should().Be("kept");
        Files().Should().BeEmpty("the temporary file is removed");
    }

    [Fact]
    public void A_symbolic_link_stays_a_link_to_the_replaced_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string real = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "dotfiles")).FullName, "semantic-router.json");
        File.WriteAllText(real, "old");
        string link = Path.Combine(_directory, "semantic-router.json");
        File.CreateSymbolicLink(link, real);

        AtomicFile.WriteAllText(link, "new");

        new FileInfo(link).LinkTarget.Should().Be(real);
        File.ReadAllText(real).Should().Be("new");
    }

    [Fact]
    public async Task Writers_that_do_not_share_a_lock_never_collide_or_leave_half_a_file()
    {
        // As the desktop app, the background service and init would: each its own store, so its own lock.
        string file = Path.Combine(_directory, "semantic-router.json");
        RouterConfiguration Document(int i) => new() { DefaultSimilarityThreshold = 0.5f + (i / 1000f) };
        string[] valid = [.. Enumerable.Range(0, 8).Select(i => Document(i).ToJson())];

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var store = new JsonFileRouterConfigurationStore(file);
            for (int n = 0; n < 25; n++)
            {
                store.Save(Document(i));
            }
        })));

        valid.Should().Contain(File.ReadAllText(file), "the file is one writer's whole document");
        Files().Should().Equal("semantic-router.json");
    }

    [Fact]
    public void The_secret_store_and_the_evaluation_set_do_not_write_through_a_fixed_temporary_name()
    {
        // Something left at the old fixed name (here a folder) stopped every save; two writers shared it.
        string secrets = Path.Combine(_directory, "semantic-router.secrets.json");
        string evaluation = Path.Combine(_directory, "semantic-router.evaluation.jsonl");
        Directory.CreateDirectory(secrets + ".tmp");
        Directory.CreateDirectory(evaluation + ".tmp");
        var protection = new EphemeralDataProtectionProvider();

        new ProtectedFileSecretStore(secrets, protection).SetSecret("connectors/openai/ApiKey", "sk-fake-test-value");
        new JsonLinesEvaluationSetStore(evaluation).Save([new EvaluationCase { Prompt = "look up the customer record", ExpectedTier = 1 }]);

        new ProtectedFileSecretStore(secrets, protection).GetSecret("connectors/openai/ApiKey").Should().Be("sk-fake-test-value");
        new JsonLinesEvaluationSetStore(evaluation).Load().Single().Prompt.Should().Be("look up the customer record");
        Files().Should().Equal("semantic-router.evaluation.jsonl", "semantic-router.secrets.json");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(secrets).Should().Be(OwnerFile);
            File.GetUnixFileMode(evaluation).Should().Be(OwnerFile);
        }
    }

    [Fact]
    public async Task Secret_and_evaluation_stores_that_do_not_share_a_lock_never_collide_or_leave_half_a_file()
    {
        string secrets = Path.Combine(_directory, "semantic-router.secrets.json");
        string evaluation = Path.Combine(_directory, "semantic-router.evaluation.jsonl");
        var protection = new EphemeralDataProtectionProvider();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var store = new ProtectedFileSecretStore(secrets, protection);
            var cases = new JsonLinesEvaluationSetStore(evaluation);
            for (int n = 0; n < 25; n++)
            {
                store.SetSecret("gateway/" + i, "gw-fake-" + n);
                cases.Save([new EvaluationCase { Prompt = $"prompt {i}", ExpectedTier = 1 }]);
            }
        })));

        FluentActions.Invoking(() => new ProtectedFileSecretStore(secrets, protection).GetSecret("gateway/0"))
            .Should().NotThrow("the file is one writer's whole document");
        new JsonLinesEvaluationSetStore(evaluation).Load().Should().ContainSingle();
        Files().Should().Equal("semantic-router.evaluation.jsonl", "semantic-router.secrets.json");
    }

    [Fact]
    public void The_saved_addresses_and_the_instance_record_are_written_whole()
    {
        File.WriteAllText(HostListen.FileIn(_directory), """{ "Logging": { "LogLevel": { "Default": "Warning" } } }""");

        HostListen.Save(_directory, "http://localhost:5081");
        new RouterInstance(Environment.ProcessId, ["http://localhost:5081"], DateTimeOffset.UtcNow, "1.0.0").Write(_directory);

        HostListen.ReadSaved(_directory).Should().Be("http://localhost:5081");
        File.ReadAllText(HostListen.FileIn(_directory)).Should().Contain("\"Warning\"", "the rest of the file is kept");
        RouterInstance.Read(_directory)!.Urls.Should().Equal("http://localhost:5081");
        Files().Should().Equal([.. new[] { HostListen.FileName, RouterInstance.FileName }.Order(StringComparer.Ordinal)]);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(HostListen.FileIn(_directory)).Should().Be(OwnerFile);
            File.GetUnixFileMode(RouterInstance.PathIn(_directory)).Should().Be(OwnerFile);
        }
    }

    [Fact]
    public async Task Forgetting_one_models_cache_never_drops_another_remembered_at_the_same_time()
    {
        // #400: Remove read the caches and wrote the rest back, so a Remember in between was lost.
        const int Rounds = 2000;
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
        var store = new WarmCacheStore();
        using var start = new Barrier(2);

        await Task.WhenAll(
            Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < Rounds; i++)
                {
                    store.Remember("c", new WarmCache { Model = "a", CachedTokens = 1, ExpiresAt = expires });
                    store.Remove("c", "a").Should().BeTrue();
                }
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < Rounds; i++)
                {
                    store.Remember("c", new WarmCache { Model = "b" + i, CachedTokens = 1, ExpiresAt = expires });
                }
            }));

        IReadOnlyList<WarmCache> caches = store.Lookup("c");
        caches.Should().NotContain(c => c.Model == "a");
        caches.Should().HaveCount(Rounds, "every model remembered alongside is still there");
    }
}
