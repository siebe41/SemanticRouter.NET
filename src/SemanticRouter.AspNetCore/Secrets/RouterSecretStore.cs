using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Keeps secret values (API keys, header values, gateway keys) outside the configuration document. The document then
/// holds a reference such as <c>${secret:connectors/openai/ApiKey}</c> (see <see cref="SecretReference"/>). When a
/// store is registered, keys typed into the dashboard are moved into it on save. Implement this to use a vault.
/// </summary>
public interface IRouterSecretStore
{
    /// <summary>A human-readable location, e.g. a file path or a vault name. Never contains a secret.</summary>
    string Location { get; }

    /// <summary>The value stored under <paramref name="name"/>, or <see langword="null"/>.</summary>
    string? GetSecret(string name);

    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>, replacing any previous value.</summary>
    void SetSecret(string name, string value);

    /// <summary>Removes the value stored under <paramref name="name"/>, if any.</summary>
    void RemoveSecret(string name);
}

/// <summary>
/// An <see cref="IRouterSecretStore"/> backed by a JSON file whose values are encrypted with ASP.NET Core Data
/// Protection. The data-protection keys live in the user profile unless the application configures another key ring;
/// ASP.NET Core encrypts its default key ring with DPAPI on Windows, and stores it unencrypted in
/// <c>~/.aspnet/DataProtection-Keys</c> on macOS and Linux unless the application calls a <c>ProtectKeysWith…</c>
/// method. Either way the file next to your configuration holds only ciphertext, so the configuration directory alone
/// can be copied or backed up without carrying usable keys. A value encrypted with a key other than the current one
/// is re-encrypted with the current key when it is read.
/// </summary>
internal sealed class ProtectedFileSecretStore : IRouterSecretStore
{
    private const string Purpose = "SemanticRouter.Secrets.v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NewLine = "\n" };

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Lock _gate = new();

    /// <summary>Creates a store at <paramref name="path"/> (relative paths resolve against the current directory).</summary>
    public ProtectedFileSecretStore(string path, IDataProtectionProvider dataProtection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _path = Path.GetFullPath(path);
        _protector = dataProtection.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string Location => _path;

    /// <inheritdoc />
    public string? GetSecret(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            Dictionary<string, string> all = ReadAll();
            if (!all.TryGetValue(name, out string? cipher))
            {
                return null;
            }

            try
            {
                if (_protector is not IPersistedDataProtector persisted)
                {
                    return _protector.Unprotect(cipher);
                }

                byte[] plain = persisted.DangerousUnprotect(
                    WebEncoders.Base64UrlDecode(cipher), ignoreRevocationErrors: false, out bool requiresMigration, out _);
                string value = Encoding.UTF8.GetString(plain);
                if (requiresMigration)
                {
                    Migrate(all, name, value);
                }

                return value;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Encrypted with a key this machine no longer has (a copied file, a lost key ring): treat as missing.
                return null;
            }
        }
    }

    /// <summary>
    /// Re-encrypts a value that was encrypted with a key other than the one in use: an older key, or one written before
    /// the key ring was encrypted at rest. The older key then protects nothing the store still needs.
    /// </summary>
    private void Migrate(Dictionary<string, string> all, string name, string value)
    {
        try
        {
            all[name] = _protector.Protect(value);
            WriteAll(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // A read-only file, or no key can be written: the value still decrypts as it is.
        }
    }

    /// <inheritdoc />
    public void SetSecret(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            Dictionary<string, string> all = ReadAll();
            all[name] = _protector.Protect(value);
            WriteAll(all);
        }
    }

    /// <inheritdoc />
    public void RemoveSecret(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            Dictionary<string, string> all = ReadAll();
            if (all.Remove(name))
            {
                WriteAll(all);
            }
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string json = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [], StringComparer.Ordinal);
    }

    private void WriteAll(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
