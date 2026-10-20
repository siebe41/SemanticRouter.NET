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
    public void Gateway_keys_from_the_environment_apply_over_a_saved_file()
    {
        var saved = new RouterConfiguration();
        saved.Middleware.GatewayKeys["laptop"] = "gw-laptop-key";
        var baseline = new RouterConfiguration();
        baseline.Middleware.GatewayKeys["default"] = "${env:ROUTER_GATEWAY_KEY}";
        baseline.Middleware.GatewayKeys["laptop"] = "gw-from-env";

        RouterConfiguration.InheritSecrets(saved, baseline);

        saved.Middleware.GatewayKeys.Should().Contain("default", "${env:ROUTER_GATEWAY_KEY}")
            .And.Contain("laptop", "gw-laptop-key", "the file's own key wins");
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
        var claude = new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode };
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
        var cli = new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode };
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
    public void A_stored_secret_reference_is_not_carried_to_a_new_address_over_the_admin_api()
    {
        // As the host keeps it: the typed key moved to the secret file, the document holding a reference to it.
        RouterConfiguration current = Document();
        current.Connectors["azure"].ApiKey = "${secret:connectors/azure/ApiKey}";
        current.Embeddings.ApiKey = "${secret:embeddings/ApiKey}";

        RouterConfiguration unchanged = current.Masked();
        unchanged.ReferenceErrors(current).Should().BeEmpty("references stay where they were configured");

        RouterConfiguration moved = current.Masked();
        moved.Connectors["azure"].BaseAddress = new Uri("https://collector.example");
        moved.Connectors["openai"].BaseAddress = new Uri("https://collector.example");
        moved.Embeddings.Endpoint = new Uri("https://collector.example");
        moved.ReferenceErrors(current).Should().Contain(e => e.StartsWith("Connectors:azure:ApiKey: enter the secret itself", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("Connectors:openai:ApiKey: enter the secret itself", StringComparison.Ordinal), "an environment reference moves the same way")
            .And.Contain(e => e.StartsWith("Embeddings:ApiKey: enter the secret itself", StringComparison.Ordinal));

        RouterConfiguration borrowed = current.Masked();
        borrowed.Connectors["collector"] = new ConnectorOptions { BaseAddress = new Uri("https://collector.example"), ApiKey = "${secret:connectors/azure/ApiKey}" };
        borrowed.ReferenceErrors(current).Should().ContainSingle()
            .Which.Should().StartWith("Connectors:collector:ApiKey: a stored-secret reference may only name this setting's own secret");

        RouterConfiguration added = current.Masked();
        added.Connectors["new"] = new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com"), ApiKey = "${env:OPENAI_API_KEY}" };
        added.ReferenceErrors(current).Should().BeEmpty("a new connector may still name an environment variable, as the docs show");
        added.Connectors["new"].ApiKey = "${secret:connectors/new/ApiKey}";
        added.ReferenceErrors(current).Should().ContainSingle("a new connector has no stored secret yet");
    }

    [Fact]
    public void Every_resolved_value_is_checked_not_only_the_ones_that_look_secret()
    {
        RouterConfiguration current = Document();
        current.Connectors["azure"].ApiKey = "${secret:connectors/azure/ApiKey}";

        // "X-Org" does not look like a key, but it is resolved and sent like one.
        RouterConfiguration borrowed = current.Masked();
        borrowed.Connectors["collector"] = new ConnectorOptions { BaseAddress = new Uri("https://collector.example") };
        borrowed.Connectors["collector"].Headers["X-Org"] = "${secret:connectors/azure/ApiKey}";
        borrowed.ReferenceErrors(current).Should().ContainSingle()
            .Which.Should().StartWith("Connectors:collector:Headers:X-Org: a stored-secret reference may only name this setting's own secret");
        borrowed.ReferenceErrors(current, fromThisMachine: true).Should().ContainSingle("a stored secret stays where it was configured, whoever asks");
    }

    [Fact]
    public void Only_this_machine_can_name_an_environment_variable_a_setting_did_not_have()
    {
        RouterConfiguration current = Document();
        current.Connectors["openai"].Headers["X-Project"] = "${env:OPENAI_PROJECT}";
        current.Notifications.Webhooks["team"] = new WebhookOptions { Url = "https://hooks.example/team" };

        RouterConfiguration unchanged = current.Masked();
        unchanged.ReferenceErrors(current).Should().BeEmpty("a reference the setting already had stays");

        RouterConfiguration collector = current.Masked();
        collector.Connectors["collector"] = new ConnectorOptions { BaseAddress = new Uri("https://collector.example"), ApiKey = "${env:ROUTER_ADMIN_TOKEN}" };
        collector.ReferenceErrors(current).Should().ContainSingle()
            .Which.Should().StartWith("Connectors:collector:ApiKey: only this machine can name an environment variable (${env:ROUTER_ADMIN_TOKEN})");
        collector.ReferenceErrors(current, fromThisMachine: true).Should().BeEmpty("the router's own account can read its environment anyway");

        RouterConfiguration header = current.Masked();
        header.Connectors["openai"].Headers["X-Org"] = "${env:ANTHROPIC_API_KEY}";
        header.ReferenceErrors(current).Should().ContainSingle("a variable a connector did not have, even at the same address")
            .Which.Should().StartWith("Connectors:openai:Headers:X-Org:");

        RouterConfiguration webhook = current.Masked();
        webhook.Notifications.Webhooks["team"].Url = "https://hooks.example/team?k=${env:OPENAI_API_KEY}";
        webhook.ReferenceErrors(current).Should().ContainSingle().Which.Should().StartWith("Notifications:Webhooks:team:Url:");

        RouterConfiguration embeddings = current.Masked();
        embeddings.Embeddings.ApiKey = "${env:OPENROUTER_API_KEY}";
        embeddings.ReferenceErrors(current).Should().ContainSingle().Which.Should().StartWith("Embeddings:ApiKey:");

        RouterConfiguration preset = current.Masked();
        preset.Connectors["anthropic"] = new ConnectorOptions
        {
            Type = ConnectorTypes.Anthropic,
            BaseAddress = new Uri("https://api.anthropic.com"),
            ApiKey = "${env:ANTHROPIC_API_KEY}",
        };
        preset.ReferenceErrors(current).Should().BeEmpty("a preset's own key for its own provider, as the dashboard's setup adds it");
        preset.Connectors["anthropic"].BaseAddress = new Uri("https://api.anthropic.com.collector.example");
        preset.ReferenceErrors(current).Should().ContainSingle("only for the preset's address");

        RouterConfiguration gateway = current.Masked();
        gateway.Middleware.GatewayKeys["laptop"] = "${env:ROUTER_KEY_LAPTOP}";
        gateway.ReferenceErrors(current).Should().BeEmpty("a gateway key goes nowhere");
    }

    [Theory]
    [InlineData("https://user:sk-secret@api.example.com/v1?api-key=sk-query#sk-fragment", "https://api.example.com/v1")]
    [InlineData("http://localhost:1234", "http://localhost:1234/")]
    [InlineData("https://x.openai.azure.com:8443/openai/deployments/gpt?api-version=2024", "https://x.openai.azure.com:8443/openai/deployments/gpt")]
    public void Addresses_are_shown_without_credentials_query_or_fragment(string address, string shown) =>
        AddressDisplay.Show(new Uri(address)).Should().Be(shown);

    [Fact]
    public void Credentials_in_addresses_are_redacted_wherever_they_appear()
    {
        var configuration = new RouterConfiguration();
        configuration.Connectors["x"] = new ConnectorOptions { BaseAddress = new Uri("https://user:sk-in%2Fuserinfo@api.example.com/?api-version=2024&api-key=sk-in-query") };
        configuration.Embeddings.Endpoint = new Uri("https://embed.example.com/?token=tok-in-embeddings");
        var redactor = new SecretRedactor(configuration.SecretValues());

        redactor.Redact("failed: user:sk-in%2Fuserinfo, sk-in/userinfo, sk-in-query, tok-in-embeddings, 2024")
            .Should().Be("failed: ***, ***, ***, ***, 2024");
    }

    [Fact]
    public async Task A_probe_names_the_address_without_its_credentials()
    {
        var connector = new ConnectorOptions { BaseAddress = new Uri("https://user:sk-in-userinfo@api.example.com/?key=sk-in-query") };
        using var answers = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"m"}]}""") }));
        using var refuses = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection refused (api.example.com:443)")));

        ConnectorProbeResult ok = await ConnectorProbe.ListModelsAsync("x", connector, answers, TimeSpan.FromSeconds(5));
        ConnectorProbeResult down = await ConnectorProbe.ListModelsAsync("x", connector, refuses, TimeSpan.FromSeconds(5));

        ok.Message.Should().Be("https://api.example.com/v1/models: 1 model(s).");
        down.Message.Should().StartWith("https://api.example.com/v1/models is not reachable").And.NotContain("sk-in-userinfo");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
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
