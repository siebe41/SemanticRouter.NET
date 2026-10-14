using Microsoft.AspNetCore.DataProtection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Secret references, the secret store and redaction, without a host.</summary>
public sealed class SecretsTests
{
    private static readonly string Variable = "SR_TEST_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Environment_references_resolve_alone_and_inside_a_value()
    {
        Environment.SetEnvironmentVariable(Variable, "sk-from-env");
        try
        {
            SecretReference.Resolve("${env:" + Variable + "}", null, "X").Should().Be("sk-from-env");
            SecretReference.Resolve("Bearer ${env:" + Variable + "}", null, "X").Should().Be("Bearer sk-from-env");
            SecretReference.Resolve("plain", null, "X").Should().Be("plain");
            SecretReference.Resolve(null, null, "X").Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public void Unresolvable_references_become_empty_and_name_the_setting()
    {
        var problems = new List<string>();

        string? env = SecretReference.Resolve("${env:SR_TEST_SURELY_UNSET}", null, "Connectors:openai:ApiKey", problems);
        string? store = SecretReference.Resolve("${secret:connectors/openai/ApiKey}", null, "Connectors:openai:ApiKey", problems);

        env.Should().BeEmpty();
        store.Should().BeEmpty();
        problems.Should().HaveCount(2);
        problems[0].Should().Contain("Connectors:openai:ApiKey").And.Contain("SR_TEST_SURELY_UNSET").And.Contain("not set");
        problems[1].Should().Contain("no secret store is registered");
    }

    [Fact]
    public void Masking_keeps_references_visible_and_hides_literal_secrets()
    {
        RouterConfiguration document = Document();

        RouterConfiguration masked = document.Masked();

        masked.Connectors["openai"].ApiKey.Should().Be("${env:OPENAI_API_KEY}", "a reference is not a secret");
        masked.Connectors["azure"].ApiKey.Should().Be("***");
        masked.Connectors["azure"].Headers["api-key"].Should().Be("***");
        masked.Connectors["azure"].Headers["x-tenant"].Should().Be("acme");
        masked.Middleware.GatewayKeys["laptop"].Should().Be("***");
        masked.Embeddings.ApiKey.Should().Be("***");
        document.LiteralSecretSettings().Should().BeEquivalentTo(
            "Connectors:azure:ApiKey", "Connectors:azure:Headers:api-key", "Embeddings:ApiKey", "Middleware:GatewayKeys:laptop");
    }

    [Fact]
    public void Literal_secrets_move_to_the_store_and_resolve_back()
    {
        RouterConfiguration document = Document();
        var store = new MemorySecretStore();

        IReadOnlyList<string> moved = document.MoveSecretsToStore(store);
        var problems = new List<string>();
        RouterConfiguration resolved = document.ResolveSecrets(store, problems);

        moved.Should().HaveCount(4);
        document.ToJson().Should().NotContain("sk-azure").And.NotContain("hdr-secret").And.NotContain("gw-laptop-key").And.NotContain("emb-key");
        document.Connectors["azure"].ApiKey.Should().Be("${secret:connectors/azure/ApiKey}");
        store.Values["connectors/azure/ApiKey"].Should().Be("sk-azure");
        resolved.Connectors["azure"].ApiKey.Should().Be("sk-azure");
        resolved.Connectors["azure"].Headers["api-key"].Should().Be("hdr-secret-123");
        resolved.Middleware.GatewayKeys["laptop"].Should().Be("gw-laptop-key");
        problems.Should().ContainSingle(p => p.Contains("OPENAI_API_KEY"), "only the unset environment variable is a problem");
    }

    [Fact]
    public void Cli_environment_variables_and_cookie_headers_are_secrets()
    {
        RouterConfiguration document = Document();
        var claude = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };
        claude.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-secret-123";
        document.Connectors["claude"] = claude;
        document.Connectors["azure"].Headers["Cookie"] = "session=abc123";

        RouterConfiguration masked = document.Masked();
        masked.Connectors["claude"].EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("***");
        masked.Connectors["azure"].Headers["Cookie"].Should().Be("***");

        masked.UnmaskFrom(document);
        masked.Connectors["claude"].EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("oauth-secret-123");

        var store = new MemorySecretStore();
        document.MoveSecretsToStore(store);
        document.ToJson().Should().NotContain("oauth-secret-123").And.NotContain("abc123");
        document.ResolveSecrets(store).Connectors["claude"].EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("oauth-secret-123");
    }

    [Fact]
    public void A_stored_secret_is_not_sent_where_its_connector_now_points()
    {
        RouterConfiguration current = Document();
        var cli = new ConnectorOptions { Type = ConnectorType.Cli, Preset = CliPreset.ClaudeCode };
        cli.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-secret-123";
        current.Connectors["claude"] = cli;

        RouterConfiguration moved = current.Masked();
        moved.Connectors["azure"].BaseAddress = new Uri("https://collector.example");
        moved.Connectors["claude"].Preset = CliPreset.Custom;
        moved.Connectors["claude"].Executable = "/bin/sh";
        moved.Embeddings.Endpoint = new Uri("https://collector.example");
        moved.UnmaskFrom(current);

        moved.ToJson().Should().NotContain("sk-azure").And.NotContain("hdr-secret-123").And.NotContain("oauth-secret-123").And.NotContain("emb-key");
        RouterRuntimeProvider.Validate(moved).Should().Contain(e => e.StartsWith("Connectors:azure:ApiKey: enter the secret again", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("Connectors:claude:EnvironmentVariables:CLAUDE_CODE_OAUTH_TOKEN", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("Embeddings:ApiKey", StringComparison.Ordinal));

        RouterConfiguration unchanged = current.Masked();
        unchanged.UnmaskFrom(current);
        unchanged.Connectors["azure"].ApiKey.Should().Be("sk-azure");
        RouterRuntimeProvider.Validate(unchanged).Should().NotContain(e => e.Contains("enter the secret again", StringComparison.Ordinal));
    }

    [Fact]
    public void Protected_file_store_round_trips_without_plain_text_on_disk()
    {
        string directory = Directory.CreateTempSubdirectory("router-secrets").FullName;
        try
        {
            string path = Path.Combine(directory, "semantic-router.secrets.json");
            var provider = new EphemeralDataProtectionProvider();
            var store = new ProtectedFileSecretStore(path, provider);

            store.SetSecret("connectors/openai/ApiKey", "sk-very-secret-value");
            store.SetSecret("gateway/laptop", "gw-key");
            store.RemoveSecret("gateway/laptop");

            new ProtectedFileSecretStore(path, provider).GetSecret("connectors/openai/ApiKey").Should().Be("sk-very-secret-value");
            store.GetSecret("gateway/laptop").Should().BeNull();
            File.ReadAllText(path).Should().Contain("connectors/openai/ApiKey").And.NotContain("sk-very-secret-value");
            new ProtectedFileSecretStore(path, new EphemeralDataProtectionProvider()).GetSecret("connectors/openai/ApiKey")
                .Should().BeNull("a file copied to a machine without the key ring cannot be read");
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Redactor_replaces_known_secrets_longest_first_and_ignores_short_values()
    {
        var redactor = new SecretRedactor(["sk-abcdef", "sk-abcdef-longer", "abc"]);

        redactor.Redact("key sk-abcdef-longer and sk-abcdef, abc").Should().Be("key *** and ***, abc");
        SecretRedactor.None.Redact("sk-abcdef").Should().Be("sk-abcdef");
    }

    private static RouterConfiguration Document()
    {
        var document = new RouterConfiguration();
        document.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com"), ApiKey = "${env:OPENAI_API_KEY}" };
        document.Connectors["azure"] = new ConnectorOptions { BaseAddress = new Uri("https://x.openai.azure.com"), ApiKey = "sk-azure" };
        document.Connectors["azure"].Headers["api-key"] = "hdr-secret-123";
        document.Connectors["azure"].Headers["x-tenant"] = "acme";
        document.Embeddings.ApiKey = "emb-key";
        document.Middleware.GatewayKeys["laptop"] = "gw-laptop-key";
        return document;
    }

    internal sealed class MemorySecretStore : IRouterSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string Location => "memory";

        public string? GetSecret(string name) => Values.TryGetValue(name, out string? value) ? value : null;

        public void SetSecret(string name, string value) => Values[name] = value;

        public void RemoveSecret(string name) => Values.Remove(name);
    }
}
