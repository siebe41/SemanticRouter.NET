using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>How the data-protection key ring is encrypted at rest.</summary>
internal enum KeyRingProtection
{
    /// <summary>Windows DPAPI, for the Windows account that runs the router.</summary>
    Dpapi,

    /// <summary>Encrypted to the certificate in <c>SemanticRouter:DataProtection:Certificate</c>.</summary>
    Certificate,

    /// <summary>Not encrypted: only the folder's and files' owner-only permissions protect it (macOS, Linux).</summary>
    FilePermissions,

    /// <summary>Windows without a user profile: ASP.NET Core's own fallback (the registry with machine DPAPI, or memory).</summary>
    Fallback,
}

/// <summary>
/// The ASP.NET Core data-protection key ring that encrypts the secrets file, and how it is protected at rest. ASP.NET
/// Core encrypts its default key ring (<c>%LOCALAPPDATA%\ASP.NET\DataProtection-Keys</c>) with DPAPI on Windows, but a
/// folder set with <c>PersistKeysToFileSystem</c> (<c>SEMANTIC_ROUTER_KEYS_DIR</c>) is not encrypted anywhere unless
/// an encryptor is configured, and on macOS and Linux the default key ring (<c>~/.aspnet/DataProtection-Keys</c>)
/// never is. So: <c>SemanticRouter:DataProtection:Certificate</c> encrypts new keys to a certificate on any platform;
/// otherwise a custom folder on Windows gets DPAPI too; on macOS and Linux the folder is kept owner-only (0700, files
/// 0600) and <c>doctor</c> says that file permissions are all that protects it. Keys written before a protection was
/// set stay readable: ASP.NET Core reads each key with the decryptor its file names, or as plain XML.
/// </summary>
internal sealed partial class KeyRing
{
    public const string Section = "SemanticRouter:DataProtection";
    public const string CertificateSetting = Section + ":Certificate";
    public const string CertificateKeySetting = Section + ":CertificateKey";
    public const string CertificatePasswordSetting = Section + ":CertificatePassword";

    /// <summary>The file the key ring decrypts: <c>semantic-router.secrets.json</c> in the configuration directory.</summary>
    private readonly string _secretsFile;

    private KeyRing(string? directory, bool customDirectory, KeyRingProtection protection, X509Certificate2? certificate, string secretsFile)
    {
        Directory = directory;
        CustomDirectory = customDirectory;
        Protection = protection;
        Certificate = certificate;
        _secretsFile = secretsFile;
    }

    /// <summary>The folder that holds the keys; <see langword="null"/> for <see cref="KeyRingProtection.Fallback"/>.</summary>
    public string? Directory { get; }

    /// <summary>Whether <see cref="Directory"/> was set (<c>SEMANTIC_ROUTER_KEYS_DIR</c>) rather than ASP.NET Core's default.</summary>
    public bool CustomDirectory { get; }

    public KeyRingProtection Protection { get; }

    /// <summary>The certificate new keys are encrypted to, with <see cref="KeyRingProtection.Certificate"/>.</summary>
    public X509Certificate2? Certificate { get; }

    /// <summary>Whether new keys are encrypted before they are written.</summary>
    public bool Encrypted => Protection is KeyRingProtection.Dpapi or KeyRingProtection.Certificate;

    /// <summary>
    /// Sets where <paramref name="dataProtection"/> keeps its keys and how it encrypts them, from
    /// <paramref name="keysDirectory"/> (<c>SEMANTIC_ROUTER_KEYS_DIR</c>) and the <see cref="Section"/> settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">The certificate is set but cannot be used; the message names the setting.</exception>
    public static KeyRing Configure(IDataProtectionBuilder dataProtection, IConfiguration configuration, string configurationDirectory, string? keysDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(configuration);
        bool custom = !string.IsNullOrWhiteSpace(keysDirectory);
        X509Certificate2? certificate = LoadCertificate(configuration, configurationDirectory);

        // Without a folder of its own, ASP.NET Core's default applies; it is resolved here only to report and tighten.
        // Leaving the repository unset keeps its fallbacks (and, on Windows, its DPAPI) when nothing else is set.
        // A relative SEMANTIC_ROUTER_KEYS_DIR resolves against the current directory, as it always has.
        string? directory = custom ? Path.GetFullPath(keysDirectory!) : FileSystemXmlRepository.DefaultKeyStorageDirectory?.FullName;
        if (custom)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(directory!));
        }

        KeyRingProtection protection;
        if (certificate is not null)
        {
            dataProtection.ProtectKeysWithCertificate(certificate);
            protection = KeyRingProtection.Certificate;
        }
        else if (OperatingSystem.IsWindows())
        {
            if (custom)
            {
                // Per user, like the default key ring: the router runs as the signed-in user (sign-in entry or app).
                dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: false);
            }

            protection = directory is null ? KeyRingProtection.Fallback : KeyRingProtection.Dpapi;
        }
        else
        {
            protection = KeyRingProtection.FilePermissions;
        }

        if (directory is not null && !OperatingSystem.IsWindows())
        {
            // ASP.NET Core writes key files 0600 but leaves the folder at the umask's mode, and older keys may be 0644.
            OwnerOnly.Directory(directory);
            foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.xml"))
            {
                OwnerOnly.Restrict(file);
            }
        }

        var keyRing = new KeyRing(directory, custom, protection, certificate, Path.Combine(configurationDirectory, "semantic-router.secrets.json"));
        dataProtection.Services.AddSingleton(keyRing);
        return keyRing;
    }

    /// <summary>The ids of the keys in <see cref="Directory"/> whose secret is written unencrypted.</summary>
    public IReadOnlyList<(Guid Id, string File)> UnencryptedKeys()
    {
        if (Directory is null || !System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var keys = new List<(Guid, string)>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "key-*.xml"))
        {
            try
            {
                XElement key = XElement.Load(file);
                if (key.Name.LocalName == "key" && Guid.TryParse((string?)key.Attribute("id"), out Guid id)
                    && !key.Descendants().Any(e => e.Name.LocalName == "encryptedSecret"))
                {
                    keys.Add((id, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // Not ours to judge: ASP.NET Core reports a key file it cannot read.
            }
        }

        return keys;
    }

    /// <summary>
    /// When new keys are encrypted but the key in use was written before that (a key ring from before the certificate
    /// or <c>SEMANTIC_ROUTER_KEYS_DIR</c> on Windows), makes an encrypted key the one in use now, so what is saved from
    /// now on is not protected by an unencrypted key for up to 90 days. The older keys stay and keep decrypting what
    /// was saved with them. Returns whether a key was created.
    /// </summary>
    public bool ReplaceUnencryptedDefaultKey(IKeyManager keys, TimeSpan lifetime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!Encrypted)
        {
            return false;
        }

        HashSet<Guid> unencrypted = [.. UnencryptedKeys().Select(k => k.Id)];
        if (unencrypted.Count == 0)
        {
            return false;
        }

        IKey? current = keys.GetAllKeys()
            .Where(k => !k.IsRevoked && k.ActivationDate <= now && k.ExpirationDate > now)
            .MaxBy(k => k.ActivationDate);
        if (current is null || !unencrypted.Contains(current.KeyId))
        {
            return false;
        }

        keys.CreateNewKey(now, now + lifetime);
        return true;
    }

    /// <summary>
    /// Whether the secrets file holds any secret, i.e. whether an unencrypted key ring has anything to give away. A file
    /// that cannot be read counts as holding some.
    /// </summary>
    public bool ProtectsSecrets()
    {
        try
        {
            string text = File.Exists(_secretsFile) ? File.ReadAllText(_secretsFile) : string.Empty;
            return !string.IsNullOrWhiteSpace(text) && System.Text.Json.Nodes.JsonNode.Parse(text) is not System.Text.Json.Nodes.JsonObject { Count: 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>What <c>doctor</c> reports: where the keys are, how they are protected, and older unencrypted keys.</summary>
    public IEnumerable<RouterCheck> Checks()
    {
        string where = Directory is null ? string.Empty : $"{Directory}{(CustomDirectory ? " (SEMANTIC_ROUTER_KEYS_DIR)" : string.Empty)}, ";
        yield return Protection switch
        {
            KeyRingProtection.Dpapi => new RouterCheck("key ring", RouterCheckStatus.Ok, $"{where}encrypted with DPAPI for this Windows account."),
            KeyRingProtection.Certificate => new RouterCheck("key ring", RouterCheckStatus.Ok,
                $"{where}encrypted to certificate {Certificate!.Subject} (thumbprint {Certificate.Thumbprint}, valid until {Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd})."),
            KeyRingProtection.FilePermissions when ProtectsSecrets() => new RouterCheck("key ring", RouterCheckStatus.Warning,
                $"{where}not encrypted: only its owner-only file permissions protect it, so a backup holding it and the secrets file can decrypt the secrets. " +
                $"Set {CertificateSetting} to encrypt it (see {SiteLinks.KeyRing})."),
            KeyRingProtection.FilePermissions => new RouterCheck("key ring", RouterCheckStatus.Ok,
                $"{where}not encrypted, which is fine while the secrets file holds no key. Before you type an API key into the dashboard, " +
                $"set {CertificateSetting} to encrypt it, or keep the key in the environment instead (see {SiteLinks.KeyRing})."),
            _ => new RouterCheck("key ring", RouterCheckStatus.Warning,
                "no user profile is loaded, so ASP.NET Core keeps the keys in the registry (HKLM, DPAPI for the machine) or only in memory. " +
                $"Set {HostSettings.KeysVariable} to a folder, or run the router as a signed-in user."),
        };

        IReadOnlyList<(Guid Id, string File)> unencrypted = Encrypted ? UnencryptedKeys() : [];
        if (unencrypted.Count > 0)
        {
            yield return new RouterCheck("key ring", RouterCheckStatus.Warning,
                $"{unencrypted.Count} older key(s) were written before the key ring was encrypted and are not: " +
                $"{string.Join(", ", unencrypted.Select(k => Path.GetFileName(k.File)))}. They still decrypt what was saved with them; " +
                "the router re-encrypts its secrets with the current key as it reads them, after which they can be deleted " +
                "(unless another ASP.NET Core app on this account uses this folder).");
        }
    }

    private static X509Certificate2? LoadCertificate(IConfiguration configuration, string baseDirectory)
    {
        string? setting = configuration[CertificateSetting];
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        var problems = new List<string>();
        string? password = SecretReference.Resolve(configuration[CertificatePasswordSetting], null, CertificatePasswordSetting, problems);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        setting = setting.Trim();
        string path = Path.GetFullPath(setting, baseDirectory);
        bool thumbprint = !File.Exists(path) && IsThumbprint(setting);
        X509Certificate2 certificate;
        try
        {
            certificate = thumbprint
                ? FromStore(setting)
                : FromFile(path, configuration[CertificateKeySetting] is { Length: > 0 } key ? Path.GetFullPath(key.Trim(), baseDirectory) : null, password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"{CertificateSetting}: {(thumbprint ? setting : path)} could not be loaded: {ex.Message}", ex);
        }

        using RSA? rsa = certificate.GetRSAPublicKey();
        string? problem = !certificate.HasPrivateKey
            ? "has no private key; the router needs it to read the keys back. Use a PFX, or a PEM file with the key (or CertificateKey)."
            : rsa is null ? "is not an RSA certificate; data protection encrypts keys to RSA certificates only." : null;
        if (problem is not null)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{CertificateSetting}: {(thumbprint ? setting : path)} {problem}");
        }

        return certificate;
    }

    private static X509Certificate2 FromFile(string path, string? keyPath, string? password)
    {
        byte[] content = File.ReadAllBytes(path);
        string text = System.Text.Encoding.ASCII.GetString(content);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            // A certificate alone loads, so the caller can say that its key is missing.
            return keyPath is null && !text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)
                ? X509CertificateLoader.LoadCertificate(content)
                : string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);
        }

        // Ephemeral keys leave nothing in the Windows key store; macOS does not support them.
        X509KeyStorageFlags flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12(content, password, flags);
    }

    private static X509Certificate2 FromStore(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return found.Count > 0
            ? found[0]
            : throw new CryptographicException("there is no such file, and no certificate with this thumbprint in the CurrentUser\\My store.");
    }

    private static bool IsThumbprint(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    /// <summary>Logs, once per start, what <c>doctor</c> would warn about: an unencrypted key ring only once it protects a secret.</summary>
    public void LogAtStart(ILogger logger, bool replacedKey)
    {
        if (Protection == KeyRingProtection.FilePermissions && ProtectsSecrets())
        {
            LogNotEncrypted(logger, Directory!, CertificateSetting);
        }

        if (replacedKey)
        {
            LogReplacedKey(logger, Directory!);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The data-protection keys that decrypt the secrets file ({Directory}) are not encrypted; only file permissions protect them. Set {Setting} to encrypt them.")]
    private static partial void LogNotEncrypted(ILogger logger, string directory, string setting);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Created an encrypted data-protection key in {Directory}: the key in use was written unencrypted. Older keys still decrypt what was saved with them.")]
    private static partial void LogReplacedKey(ILogger logger, string directory);
}
