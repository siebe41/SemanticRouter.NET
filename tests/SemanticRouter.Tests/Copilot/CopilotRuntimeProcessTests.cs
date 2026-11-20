using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;

namespace SemanticRouter.Tests.Copilot;

/// <summary>
/// The real Copilot SDK starting a runtime process: the stand-in CLI (<c>tests/SemanticRouter.FakeCli</c>) records the
/// environment it was given and exits, so the SDK's start fails after the process ran.
/// </summary>
public sealed class CopilotRuntimeProcessTests : IDisposable
{
    private static readonly string FakeCli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-cli.exe" : "fake-cli");

    private readonly string _scratch = Directory.CreateTempSubdirectory("fake-copilot").FullName;

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public async Task The_runtime_starts_without_the_routers_settings_and_secrets_but_with_the_connectors_variables()
    {
        const string Secret = "sk-copilot-test-secret-0123456789";
        string[] names = ["SR_TEST_COPILOT_HOLDS_KEY", "ROUTER_SR_TEST_COPILOT", "SemanticRouter__SrTestCopilot__ApiKey", "SR_TEST_COPILOT_PLAIN", "SR_TEST_COPILOT_REMOVED"];
        Environment.SetEnvironmentVariable(names[0], Secret);
        Environment.SetEnvironmentVariable(names[1], "admin-token-0123456789");
        Environment.SetEnvironmentVariable(names[2], "sk-config-0123456789");
        Environment.SetEnvironmentVariable(names[3], "not a secret");
        Environment.SetEnvironmentVariable(names[4], "removed by the connector");
        string marker = Path.Combine(_scratch, "environment.json");
        string home = Path.Combine(_scratch, "copilot-home");
        var options = new ConnectorOptions
        {
            Type = CopilotConnector.Type,
            Executable = FakeCli,
            HomeDirectory = home,
            RemoveEnvironmentVariables = [names[4]],
        };
        options.EnvironmentVariables["FAKE_CLI_MODE"] = "env-file";
        options.EnvironmentVariables["FAKE_CLI_MARKER"] = marker;
        options.EnvironmentVariables["SR_TEST_COPILOT_SET"] = "yes";
        var factory = new CopilotConnectorFactory(NullLoggerFactory.Instance);
        try
        {
            var connector = (IModelDiscovery)factory.Create("copilot", options, new SecretRedactor([Secret]));

            Func<Task> discover = () => connector.ListModelsAsync(CancellationToken.None);

            await discover.Should().ThrowAsync<InvalidOperationException>("the stand-in does not speak the SDK's protocol");
        }
        finally
        {
            await factory.DisposeAsync();
            foreach (string name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        string[] ours = [.. names, "SR_TEST_COPILOT_SET"];
        report.RootElement.GetProperty("inherited").EnumerateArray().Select(n => n.GetString()!).Where(n => ours.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(
                ["SR_TEST_COPILOT_PLAIN", "SR_TEST_COPILOT_SET"],
                "what configures the router, what holds a secret it resolved and what the connector removes stay out; what the connector sets is passed");
        report.RootElement.GetProperty("copilotHome").GetString().Should().Be(home, "the SDK adds COPILOT_HOME to the environment it is given");
        report.RootElement.GetProperty("hasPath").GetBoolean().Should().BeTrue();
    }
}
