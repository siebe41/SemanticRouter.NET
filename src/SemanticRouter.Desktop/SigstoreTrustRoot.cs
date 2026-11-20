using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>
/// What a Sigstore signature is checked against: the certificate authorities (Fulcio) that issue the short-lived
/// signing certificates, and the transparency logs (Rekor) that record each signature, each with the time it was in
/// use. The app carries Sigstore's public-good trust root (<c>Assets/sigstore-trusted-root.json</c>, from
/// <c>targets/trusted_root.json</c> in <c>sigstore/root-signing</c>), the one GitHub's attestations for a public
/// repository are made with. <see cref="AsOf"/> says when that copy was last known to be Sigstore's current one.
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

    /// <summary>
    /// The key of the assembly metadata (<see cref="AssemblyMetadataAttribute"/>) in which the desktop app's build records
    /// the time of the commit it was built from (<c>SemanticRouter.Desktop.csproj</c>, from <c>git log</c>).
    /// </summary>
    internal const string SourceCommitTime = "SourceCommitTime";

    private SigstoreTrustRoot(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs, IReadOnlyList<byte[]> unreadable, DateTimeOffset? asOf) =>
        (Authorities, Logs, Unreadable, AsOf) = (authorities, logs, unreadable, asOf);

    public IReadOnlyList<Authority> Authorities { get; }

    /// <summary>The logs this app can check: those with an ECDSA P-256 key (Rekor v1).</summary>
    public IReadOnlyList<Log> Logs { get; }

    /// <summary>
    /// The ids of the trust root's other logs, whose keys this app cannot check (Rekor v2's Ed25519 key). An entry in one
    /// of them is a log entry this app cannot read; an entry in a log that is in neither list is not in a Sigstore log.
    /// </summary>
    public IReadOnlyList<byte[]> Unreadable { get; }

    /// <summary>
    /// When this trust root was last known to be Sigstore's current one, or <see langword="null"/> when that is not known.
    /// For the one the app carries: the time of the commit the app was built from. A release fails unless the trust root
    /// it carries is Sigstore's current one (<c>deploy/update-attestation-check.sh --online</c>), so it cannot lack a
    /// certificate authority Sigstore had added by then. A build made outside a git checkout does not know it.
    /// </summary>
    public DateTimeOffset? AsOf { get; }

    private static readonly Lazy<SigstoreTrustRoot> s_publicGood = new(() =>
    {
        using Stream stream = typeof(SigstoreTrustRoot).Assembly.GetManifestResourceStream("sigstore-trusted-root.json")
            ?? throw new InvalidOperationException("The app carries no Sigstore trust root.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), BuiltFrom(typeof(SigstoreTrustRoot).Assembly));
    });

    /// <summary>Sigstore's public-good trust root, as the app carries it.</summary>
    public static SigstoreTrustRoot PublicGood => s_publicGood.Value;

    /// <summary>
    /// The time of the commit <paramref name="assembly"/> was built from (its <see cref="SourceCommitTime"/> metadata, an
    /// ISO 8601 time), or <see langword="null"/> when its build recorded none.
    /// </summary>
    internal static DateTimeOffset? BuiltFrom(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == SourceCommitTime)?.Value is { } value
        && DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time)
            ? time
            : null;

    /// <summary>Reads a trust root in Sigstore's <c>trusted_root.json</c> format, known to be current at <paramref name="asOf"/>.</summary>
    public static SigstoreTrustRoot Parse(string json, DateTimeOffset? asOf = null)
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
        List<byte[]> unreadable = [];
        foreach (JsonElement log in root.GetProperty("tlogs").EnumerateArray())
        {
            JsonElement key = log.GetProperty("publicKey");
            if (key.GetProperty("keyDetails").GetString() is not "PKIX_ECDSA_P256_SHA_256")
            {
                unreadable.Add(Convert.FromBase64String(log.GetProperty("logId").GetProperty("keyId").GetString()!));
                continue;
            }

            byte[] spki = Convert.FromBase64String(key.GetProperty("rawBytes").GetString()!);
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            (DateTimeOffset start, DateTimeOffset? end) = ValidFor(key);
            logs.Add(new Log(SHA256.HashData(spki), ecdsa, start, end));
        }

        return new SigstoreTrustRoot(authorities, logs, unreadable, asOf);

        static (DateTimeOffset Start, DateTimeOffset? End) ValidFor(JsonElement element)
        {
            JsonElement validFor = element.GetProperty("validFor");
            return (Time(validFor.GetProperty("start")), validFor.TryGetProperty("end", out JsonElement end) ? Time(end) : null);
        }

        static DateTimeOffset Time(JsonElement value) =>
            DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>A trust root of the given authorities and logs, known to be current at <paramref name="asOf"/>, for tests.</summary>
    internal static SigstoreTrustRoot Of(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs, IReadOnlyList<byte[]>? unreadable = null, DateTimeOffset? asOf = null) =>
        new(authorities, logs, unreadable ?? [], asOf);
}
