using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Host;

namespace SemanticRouter.Tests.Host;

/// <summary>The data-protection key ring that decrypts the secrets file, and how it is protected at rest.</summary>
public sealed class KeyRingTests : IDisposable
{
    private const string Secret = "sk-key-ring-test-value";
    private const string Password = "test-only-password";

    private readonly string _directory = Directory.CreateTempSubdirectory("router-keys").FullName;

    private string Keys => Path.Combine(_directory, "keys");

    private string SecretsFile => Path.Combine(_directory, "semantic-router.secrets.json");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void An_unencrypted_key_ring_from_before_the_upgrade_still_decrypts_once_a_certificate_is_set()
    {
        // As every earlier version left SEMANTIC_ROUTER_KEYS_DIR (and macOS and Linux their default key ring): plain XML.
        using (ServiceProvider before = Before())
        {
            new ProtectedFileSecretStore(SecretsFile, before.GetRequiredService<IDataProtectionProvider>()).SetSecret("connectors/openai/ApiKey", Secret);
        }

        string oldKey = Directory.GetFiles(Keys, "key-*.xml").Should().ContainSingle().Subject;
        File.ReadAllText(oldKey).Should().NotContain("encryptedSecret");
        string oldCipher = File.ReadAllText(SecretsFile);
        string pfx = WritePfx("dp.pfx");

        (ServiceProvider after, KeyRing ring) = Configure(new() { [KeyRing.CertificateSetting] = pfx, [KeyRing.CertificatePasswordSetting] = Password });
        using (after)
        {
            ring.Protection.Should().Be(KeyRingProtection.Certificate);
            ring.UnencryptedKeys().Should().ContainSingle().Which.File.Should().Be(oldKey);
            var keys = after.GetRequiredService<IKeyManager>();
            ring.ReplaceUnencryptedDefaultKey(keys, TimeSpan.FromDays(90), DateTimeOffset.UtcNow).Should().BeTrue("the key in use was not encrypted");
            ring.ReplaceUnencryptedDefaultKey(keys, TimeSpan.FromDays(90), DateTimeOffset.UtcNow).Should().BeFalse("an encrypted key is in use now");

            new ProtectedFileSecretStore(SecretsFile, after.GetRequiredService<IDataProtectionProvider>()).GetSecret("connectors/openai/ApiKey").Should().Be(Secret);
        }

        string newKey = Directory.GetFiles(Keys, "key-*.xml").Should().HaveCount(2).And.ContainSingle(f => f != oldKey).Subject;
        string newXml = File.ReadAllText(newKey);
        newXml.Should().Contain("encryptedSecret").And.Contain("EncryptedXmlDecryptor").And.NotContain("<value>");
        File.ReadAllText(SecretsFile).Should().NotBe(oldCipher, "reading it re-encrypted the secret with the encrypted key");

        // The old, unencrypted key no longer protects anything the store needs; the certificate opens the rest.
        File.Delete(oldKey);
        (ServiceProvider withCertificate, _) = Configure(new() { [KeyRing.CertificateSetting] = pfx, [KeyRing.CertificatePasswordSetting] = Password });
        using (withCertificate)
        {
            new ProtectedFileSecretStore(SecretsFile, withCertificate.GetRequiredService<IDataProtectionProvider>()).GetSecret("connectors/openai/ApiKey").Should().Be(Secret);
        }

        using ServiceProvider withoutCertificate = Before();
        new ProtectedFileSecretStore(SecretsFile, withoutCertificate.GetRequiredService<IDataProtectionProvider>()).GetSecret("connectors/openai/ApiKey")
            .Should().BeNull("a copy of the key ring without the certificate's private key is useless");
    }

    [Fact]
    public void A_certificate_loads_from_a_pfx_a_pem_with_its_key_or_a_pem_and_a_key_file()
    {
        using RSA rsa = RSA.Create(2048);
        using X509Certificate2 certificate = SelfSigned(rsa);
        File.WriteAllText(Path.Combine(_directory, "both.pem"), certificate.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_directory, "tls.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "tls.key"), rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_directory, "encrypted.pem"), certificate.ExportCertificatePem() + "\n"
            + rsa.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        string pfx = WritePfx("relative.pfx");

        foreach (Dictionary<string, string?> settings in new Dictionary<string, string?>[]
        {
            new() { [KeyRing.CertificateSetting] = Path.GetFileName(pfx), [KeyRing.CertificatePasswordSetting] = Password },
            new() { [KeyRing.CertificateSetting] = "both.pem" },
            new() { [KeyRing.CertificateSetting] = "tls.crt", [KeyRing.CertificateKeySetting] = "tls.key" },
            new() { [KeyRing.CertificateSetting] = "encrypted.pem", [KeyRing.CertificatePasswordSetting] = Password },
        })
        {
            (ServiceProvider services, KeyRing ring) = Configure(settings);
            using (services)
            {
                ring.Protection.Should().Be(KeyRingProtection.Certificate, settings[KeyRing.CertificateSetting]);
                ring.Certificate!.HasPrivateKey.Should().BeTrue();
                IDataProtector protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
                protector.Unprotect(protector.Protect(Secret)).Should().Be(Secret);
            }
        }
    }

    [Fact]
    public void A_certificate_that_cannot_be_used_stops_start_up_with_the_setting_named()
    {
        using RSA rsa = RSA.Create(2048);
        using X509Certificate2 certificate = SelfSigned(rsa);
        File.WriteAllText(Path.Combine(_directory, "public.pem"), certificate.ExportCertificatePem());
        using ECDsa ecdsa = ECDsa.Create();
        using X509Certificate2 elliptic = new CertificateRequest("CN=Elliptic", ecdsa, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllText(Path.Combine(_directory, "elliptic.pem"), elliptic.ExportCertificatePem() + "\n" + ecdsa.ExportPkcs8PrivateKeyPem());
        string pfx = WritePfx("dp.pfx");

        Fail(new() { [KeyRing.CertificateSetting] = "missing.pfx" }).Should().Contain(Path.Combine(_directory, "missing.pfx")).And.Contain("could not be loaded");
        Fail(new() { [KeyRing.CertificateSetting] = "public.pem" }).Should().Contain("has no private key");
        Fail(new() { [KeyRing.CertificateSetting] = "elliptic.pem" }).Should().Contain("is not an RSA certificate");
        Fail(new() { [KeyRing.CertificateSetting] = pfx, [KeyRing.CertificatePasswordSetting] = "wrong" }).Should().Contain("could not be loaded");
        Fail(new() { [KeyRing.CertificateSetting] = pfx, [KeyRing.CertificatePasswordSetting] = "${env:SEMANTIC_ROUTER_TEST_UNSET_PASSWORD}" })
            .Should().Contain(KeyRing.CertificatePasswordSetting).And.Contain("SEMANTIC_ROUTER_TEST_UNSET_PASSWORD");
        Fail(new() { [KeyRing.CertificateSetting] = "00112233445566778899AABBCCDDEEFF00112233" })
            .Should().Contain("00112233445566778899AABBCCDDEEFF00112233").And.Contain("could not be loaded");

        string Fail(Dictionary<string, string?> settings)
        {
            Action configure = () => Configure(settings).Services.Dispose();
            return configure.Should().Throw<InvalidOperationException>().Which.Message.Should().StartWith(KeyRing.Section + ":").And.Subject;
        }
    }

    [Fact]
    public void Without_a_certificate_the_key_ring_is_owner_only_on_unix_and_dpapi_protected_on_windows()
    {
        Directory.CreateDirectory(Keys);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Keys, (UnixFileMode)0b111_101_101);
        }

        (ServiceProvider services, KeyRing ring) = Configure([]);
        using (services)
        {
            IDataProtector protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            protector.Unprotect(protector.Protect(Secret)).Should().Be(Secret);
        }

        string key = Directory.GetFiles(Keys, "key-*.xml").Should().ContainSingle().Subject;
        RouterCheck[] checks = [.. ring.Checks()];
        checks.Should().ContainSingle();
        if (OperatingSystem.IsWindows())
        {
            // ASP.NET Core leaves a PersistKeysToFileSystem folder unencrypted unless asked; the router asks for DPAPI.
            ring.Protection.Should().Be(KeyRingProtection.Dpapi);
            File.ReadAllText(key).Should().Contain("encryptedSecret").And.Contain("DpapiXmlDecryptor");
            checks[0].Should().Be(new RouterCheck("key ring", RouterCheckStatus.Ok, $"{Keys} (SEMANTIC_ROUTER_KEYS_DIR), encrypted with DPAPI for this Windows account."));
            return;
        }

        ring.Protection.Should().Be(KeyRingProtection.FilePermissions);
        File.ReadAllText(key).Should().NotContain("encryptedSecret");
        File.GetUnixFileMode(Keys).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(key).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "ASP.NET Core writes new keys 0600");
        checks[0].Status.Should().Be(RouterCheckStatus.Ok, "the secrets file holds nothing the keys could give away yet");
        checks[0].Message.Should().StartWith($"{Keys} (SEMANTIC_ROUTER_KEYS_DIR), not encrypted, which is fine while the secrets file holds no key")
            .And.Contain(KeyRing.CertificateSetting).And.Contain(SiteLinks.KeyRing);

        File.WriteAllText(SecretsFile, "{}");
        ring.Checks().First().Status.Should().Be(RouterCheckStatus.Ok, "an empty secrets file holds nothing either");
        File.WriteAllText(SecretsFile, "{\"connectors/openai/ApiKey\":\"CfDJ8…\"}");
        RouterCheck warning = ring.Checks().First();
        warning.Status.Should().Be(RouterCheckStatus.Warning);
        warning.Message.Should().StartWith($"{Keys} (SEMANTIC_ROUTER_KEYS_DIR), not encrypted: only its owner-only file permissions protect it")
            .And.Contain(KeyRing.CertificateSetting).And.Contain(SiteLinks.KeyRing).And.NotContain("docs/");
    }

    [Fact]
    public void An_older_key_file_is_tightened_to_its_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Keys);
        string older = Path.Combine(Keys, "key-00000000-0000-0000-0000-000000000001.xml");
        File.WriteAllText(older, "<key />");
        File.SetUnixFileMode(older, (UnixFileMode)0b110_100_100);

        Configure([]).Services.Dispose();

        File.GetUnixFileMode(older).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task Doctor_says_how_the_key_ring_is_protected_and_lists_keys_written_before()
    {
        using (ServiceProvider before = Before())
        {
            new ProtectedFileSecretStore(SecretsFile, before.GetRequiredService<IDataProtectionProvider>()).SetSecret("connectors/openai/ApiKey", Secret);
        }

        string oldKey = Path.GetFileName(Directory.GetFiles(Keys, "key-*.xml").Single());
        string thumbprint = WritePfx("dp.pfx", out _);
        new JsonFileRouterConfigurationStore(Path.Combine(_directory, "semantic-router.json")).Save(RouterConfiguration.FromJson(RouterHost.Template()));
        HostSettings settings = HostSettings.Parse(["doctor", "--config-dir", _directory], _ => null) with { KeysDirectory = Keys };

        var encrypted = new StringWriter();
        await DoctorCommand.RunAsync(settings with
        {
            HostArguments = [$"--{KeyRing.CertificateSetting}=dp.pfx", $"--{KeyRing.CertificatePasswordSetting}={Password}"],
        }, encrypted);
        var plain = new StringWriter();
        await DoctorCommand.RunAsync(settings, plain);
        var broken = new StringWriter();
        int exit = await DoctorCommand.RunAsync(settings with { HostArguments = [$"--{KeyRing.CertificateSetting}=missing.pfx"] }, broken);

        encrypted.ToString().Should().Contain($"[ OK ] key ring: {Keys} (SEMANTIC_ROUTER_KEYS_DIR), encrypted to certificate CN=Semantic Router key ring test (thumbprint {thumbprint}")
            .And.Contain($"[WARN] key ring: 1 older key(s) were written before the key ring was encrypted and are not: {oldKey}.");
        Directory.GetFiles(Keys, "key-*.xml").Should().HaveCount(2, "the router made an encrypted key the one in use");
        plain.ToString().Should().Contain(OperatingSystem.IsWindows()
            ? $"[ OK ] key ring: {Keys} (SEMANTIC_ROUTER_KEYS_DIR), encrypted with DPAPI for this Windows account."
            : $"[WARN] key ring: {Keys} (SEMANTIC_ROUTER_KEYS_DIR), not encrypted: only its owner-only file permissions protect it");
        exit.Should().Be(1);
        broken.ToString().Should().Contain($"[FAIL] configuration: {KeyRing.CertificateSetting}: {Path.Combine(_directory, "missing.pfx")} could not be loaded");
    }

    [Fact]
    public void The_keys_folder_comes_from_the_environment()
    {
        HostSettings.Parse([], n => n == HostSettings.KeysVariable ? "/data/keys" : null).KeysDirectory.Should().Be("/data/keys");
        HostSettings.Parse([], n => n == HostSettings.KeysVariable ? string.Empty : null).KeysDirectory.Should().BeNull();
    }

    /// <summary>A key ring as earlier versions configured <c>SEMANTIC_ROUTER_KEYS_DIR</c>: no encryptor at all.</summary>
    private ServiceProvider Before()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("semantic-router").PersistKeysToFileSystem(new DirectoryInfo(Keys));
        return services.BuildServiceProvider();
    }

    private (ServiceProvider Services, KeyRing Ring) Configure(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName("semantic-router");
        KeyRing ring = KeyRing.Configure(dataProtection, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _directory, Keys);
        return (services.BuildServiceProvider(), ring);
    }

    private string WritePfx(string name)
    {
        WritePfx(name, out string path);
        return path;
    }

    /// <summary>Writes a password-protected PFX with an RSA key; returns its thumbprint.</summary>
    private string WritePfx(string name, out string path)
    {
        using RSA rsa = RSA.Create(2048);
        using X509Certificate2 certificate = SelfSigned(rsa);
        path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return certificate.Thumbprint;
    }

    private static X509Certificate2 SelfSigned(RSA rsa) =>
        new CertificateRequest("CN=Semantic Router key ring test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
}
