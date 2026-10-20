using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using SemanticRouter.Storage.Sqlite;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.Host;

/// <summary>
/// The current build started on a configuration directory that each released version set up and used through its own
/// API (<c>tests/fixtures/upgrade/&lt;version&gt;.zip</c>, made by <c>tools/upgrade-fixtures/make-fixture.sh</c>): it
/// starts without a warning, migrates the state database, keeps every row, decrypts the release's API key, accepts its
/// gateway key, rolls back to its first configuration and brings its template up to date.
/// </summary>
public sealed class UpgradeFromReleasedVersionsTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "upgrade");

    /// <summary>The tables that hold what the router learned and recorded, which an upgrade must keep.</summary>
    private static readonly string[] Tables = ["activity", "usage", "feedback", "config_history", "tuning_changes", "cooldowns"];

    private readonly string _directory = Directory.CreateTempSubdirectory("router-upgrade").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    public static TheoryData<string> Releases() => [.. ReleaseNames()];

    private static IEnumerable<string> ReleaseNames() =>
        Directory.GetFiles(Fixtures, "*.zip").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal);

    [Fact]
    public void There_is_a_fixture_for_every_release()
    {
        ReleaseNames().Should().Contain(["0.1.0", "0.2.0", "0.2.1", "0.3.0", "0.4.0", "0.5.0"], "docs/releases.md: add each release's fixture after it is published");
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task The_current_build_takes_over_a_home_made_by(string release)
    {
        string home = Path.Combine(_directory, release);
        ZipFile.ExtractToDirectory(Path.Combine(Fixtures, release + ".zip"), home);
        Fixture fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(home, "fixture.json")), JsonSerializerOptions.Web)!;
        string database = Path.Combine(home, HostSettings.StateFileName);
        int schema = SqliteRouterDatabase.StoredSchemaVersion(database)!.Value;
        MoveToNow(database);
        Dictionary<string, long> rows = Count(database);
        (long Requests, decimal Cost) usage = Usage(database);
        rows.Values.Should().OnlyContain(n => n > 0, "the fixture has something in every table");

        var logs = new CapturingLoggerProvider();
        var authorizations = new ConcurrentQueue<string?>();
        HostSettings settings = HostSettings.Parse(
            ["--config-dir", home, "--urls", "http://127.0.0.1:0"],
            name => name == HostSettings.KeysVariable ? Path.Combine(home, "keys") : null);
        var output = new StringWriter();
        RunningRouter? started = await RouterHost.StartAsync(settings, output, builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + fixture.Connector)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler((request, _) =>
                {
                    authorizations.Enqueue(request.Headers.Authorization?.ToString());
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"id":"c","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""",
                            Encoding.UTF8, "application/json"),
                    };
                }));
        });

        await using RunningRouter router = started!;
        RouterRuntimeProvider provider = router.App.Services.GetRequiredService<RouterRuntimeProvider>();
        RouterRuntime runtime = provider.Current;
        runtime.Warnings.Should().BeEmpty();
        logs.Messages.Where(m => m.StartsWith("Warning", StringComparison.Ordinal) || m.StartsWith("Error", StringComparison.Ordinal) || m.StartsWith("Critical", StringComparison.Ordinal))
            .Where(m => !KnownWarning(m))
            .Should().BeEmpty();

        // The state: migrated, a copy of the old file kept, every row and the usage totals still there.
        SqliteRouterDatabase.StoredSchemaVersion(database).Should().Be(SqliteRouterDatabase.SchemaVersion);
        File.Exists(SqliteRouterDatabase.BackupFile(database, schema)).Should().Be(schema < SqliteRouterDatabase.SchemaVersion);
        Dictionary<string, long> after = Count(database);
        foreach (string table in Tables)
        {
            after[table].Should().BeGreaterThanOrEqualTo(rows[table], $"{release}'s {table} rows are kept");
        }

        Usage(database).Should().Be(usage);

        // The configuration: the release's secrets decrypt, its gateway key works, its template is brought up to date.
        runtime.GatewayKeys.Should().ContainKey(fixture.GatewayKey).WhoseValue.Should().Be(fixture.GatewayClient);
        runtime.Configuration.TemplateVersion.Should().Be(TemplateMigration.Current);
        runtime.Configuration.Middleware.ModelAliases["*opus*"].Should().Be("auto");
        runtime.Configuration.Routes.Should().Contain(r => r.Name == "quick-answers");

        using var client = new HttpClient { BaseAddress = new Uri(router.LocalUrl) };
        using HttpResponseMessage chat = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "small", messages = new[] { new { role = "user", content = "hello" } } });
        chat.StatusCode.Should().Be(HttpStatusCode.OK, await chat.Content.ReadAsStringAsync());
        authorizations.Should().Contain($"Bearer {fixture.ApiKey}", "the API key the release encrypted still decrypts");

        using HttpResponseMessage config = await client.GetAsync("/router/api/config");
        config.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonArray versions = (await client.GetFromJsonAsync<JsonObject>("/router/api/config/history"))!["versions"]!.AsArray();
        long oldest = versions.Min(v => v!["id"]!.GetValue<long>());
        using HttpResponseMessage rollback = await client.PostAsync($"/router/api/config/history/{oldest}/rollback", null);
        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync());
    }

    /// <summary>Warnings the current build gives on any home, upgraded or not.</summary>
    private static bool KnownWarning(string message) =>
        message.Contains("SemanticRouter.Host.KeyRing", StringComparison.Ordinal); // keys not encrypted at rest (Linux and macOS without a certificate)

    /// <summary>
    /// Moves every timestamp in the fixture's database forward so its newest record is a minute old: retention (30 days
    /// of activity) must not age a fixture out between its making and the test.
    /// </summary>
    private static void MoveToNow(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        long newest = Scalar(connection, "SELECT MAX(timestamp_ticks) FROM (SELECT timestamp_ticks FROM activity UNION ALL SELECT timestamp_ticks FROM usage);");
        long shift = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks - newest;
        foreach ((string table, string column) in Columns(connection).Where(c => c.Column.EndsWith("_ticks", StringComparison.Ordinal)))
        {
            using SqliteCommand move = connection.CreateCommand();
            move.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = \"{column}\" + $shift;";
            move.Parameters.AddWithValue("$shift", shift);
            move.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, long> Count(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return Tables.ToDictionary(t => t, t => Scalar(connection, $"SELECT COUNT(*) FROM \"{t}\";"));
    }

    private static (long Requests, decimal Cost) Usage(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT cost FROM usage;";
        using SqliteDataReader reader = read.ExecuteReader();
        long requests = 0;
        decimal cost = 0;
        while (reader.Read())
        {
            requests++;
            cost += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        }

        return (requests, cost);
    }

    private static List<(string Table, string Column)> Columns(SqliteConnection connection)
    {
        var columns = new List<(string, string)>();
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type = 'table';";
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>fixture.json, written by <c>tools/upgrade-fixtures/drive.py</c>.</summary>
    private sealed record Fixture(string Version, string Connector, string ApiKey, string GatewayKey, string GatewayClient);
}
