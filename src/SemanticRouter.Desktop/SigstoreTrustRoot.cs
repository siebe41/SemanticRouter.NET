using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>
/// What a Sigstore signature is checked against: the certificate authorities (Fulcio) that issue the short-lived
/// signing certificates, and the transparency logs (Rekor) that record each signature, each with the time it was in
/// use. The app carries Sigstore's public-good trust root (<c>Assets/sigstore-trusted-root.json</c>, from
/// <c>targets/trusted_root.json</c> in <c>sigstore/root-signing</c>), the one GitHub's attestations for a public
/// repository are made with.
/// </summary>
internal sealed class SigstoreTrustRoot
{
    /// <summary>A certificate authority: its chain, intermediate first and root last, and when it issued certificates.</summary>
    internal sealed record Authority(X509Certificate2[] Chain, DateTimeOffset Start, DateTimeOffset? End)
    {
        public bool ValidAt(DateTimeOffset time) => time >= Start && (End is null || time <= End);
    }

    /// <summary>A transparency log's ECDSA key, its id (the SHA-256 of the key) and when it signed.</summary>
    internal sealed record Log(byte[] KeyId, ECDsa Key, DateTimeOffset Start, DateTimeOffset? End)
    {
        public bool ValidAt(DateTimeOffset time) => time >= Start && (End is null || time <= End);
    }

    private SigstoreTrustRoot(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs) => (Authorities, Logs) = (authorities, logs);

    public IReadOnlyList<Authority> Authorities { get; }

    /// <summary>The logs this app can check: those with an ECDSA P-256 key (Rekor v1). Logs with other keys are left out.</summary>
    public IReadOnlyList<Log> Logs { get; }

    private static readonly Lazy<SigstoreTrustRoot> s_publicGood = new(() =>
    {
        using Stream stream = typeof(SigstoreTrustRoot).Assembly.GetManifestResourceStream("sigstore-trusted-root.json")
            ?? throw new InvalidOperationException("The app carries no Sigstore trust root.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>Sigstore's public-good trust root, as the app carries it.</summary>
    public static SigstoreTrustRoot PublicGood => s_publicGood.Value;

    /// <summary>Reads a trust root in Sigstore's <c>trusted_root.json</c> format.</summary>
    public static SigstoreTrustRoot Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        List<Authority> authorities = [];
        foreach (JsonElement authority in root.GetProperty("certificateAuthorities").EnumerateArray())
        {
            X509Certificate2[] chain = [.. authority.GetProperty("certChain").GetProperty("certificates").EnumerateArray()
                .Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c.GetProperty("rawBytes").GetString()!)))];
            (DateTimeOffset start, DateTimeOffset? end) = ValidFor(authority);
            authorities.Add(new Authority(chain, start, end));
        }

        List<Log> logs = [];
        foreach (JsonElement log in root.GetProperty("tlogs").EnumerateArray())
        {
            JsonElement key = log.GetProperty("publicKey");
            if (key.GetProperty("keyDetails").GetString() is not "PKIX_ECDSA_P256_SHA_256")
            {
                continue;
            }

            byte[] spki = Convert.FromBase64String(key.GetProperty("rawBytes").GetString()!);
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            (DateTimeOffset start, DateTimeOffset? end) = ValidFor(key);
            logs.Add(new Log(SHA256.HashData(spki), ecdsa, start, end));
        }

        return new SigstoreTrustRoot(authorities, logs);

        static (DateTimeOffset Start, DateTimeOffset? End) ValidFor(JsonElement element)
        {
            JsonElement validFor = element.GetProperty("validFor");
            return (Time(validFor.GetProperty("start")), validFor.TryGetProperty("end", out JsonElement end) ? Time(end) : null);
        }

        static DateTimeOffset Time(JsonElement value) =>
            DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>A trust root of the given authorities and logs, for tests.</summary>
    internal static SigstoreTrustRoot Of(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs) => new(authorities, logs);
}
