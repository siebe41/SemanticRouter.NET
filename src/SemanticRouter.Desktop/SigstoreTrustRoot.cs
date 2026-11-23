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
/// repository are made with.
/// </summary>
/// <remarks>
/// A release carries the trust root that was Sigstore's current one when it was built: the release workflow fails when
/// it is not (<c>deploy/update-attestation-check.sh --online</c>). <c>deploy/publish-desktop.sh</c> records when that
/// was, the date of the commit the app is built from, in the assembly's <see cref="CurrentAtMetadata"/> metadata
/// (<see cref="CurrentAt"/>). Whoever can change a release's files can change neither, so together with the time a
/// transparency log recorded a signature they tell an authority Sigstore may have added since (<see cref="StaleAfter"/>)
/// from a certificate someone made themselves.
/// </remarks>
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

    /// <summary>The assembly metadata that records <see cref="CurrentAt"/>, which <c>deploy/publish-desktop.sh</c> sets.</summary>
    public const string CurrentAtMetadata = "SigstoreTrustRootCurrentAt";

    /// <summary>
    /// How long after <see cref="CurrentAt"/> a certificate from an authority the trust root does not have may be from
    /// one Sigstore added since, rather than one someone made themselves: six months. Sigstore's public-good Fulcio has
    /// changed its authority once since it started (in 2022), and it publishes a new authority in its trust root before
    /// it issues certificates from it; the weekly CI run and every release fail until the app carries the new trust
    /// root. An app that updates itself is rarely more than a few weeks older than the latest release, so only an app
    /// that has not been updated for half a year can be told its trust root is outdated: whoever can change a release's
    /// files cannot make a current app say so. Until then such a certificate is refused like any other that does not
    /// verify.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(183);

    private SigstoreTrustRoot(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs, IReadOnlyList<byte[]> unreadable, DateTimeOffset? currentAt) =>
        (Authorities, Logs, Unreadable, CurrentAt) = (authorities, logs, unreadable, currentAt);

    public IReadOnlyList<Authority> Authorities { get; }

    /// <summary>The logs this app can check: those with an ECDSA P-256 key (Rekor v1).</summary>
    public IReadOnlyList<Log> Logs { get; }

    /// <summary>
    /// The ids of the trust root's other logs, whose keys this app cannot check (Rekor v2's Ed25519 key). An entry in one
    /// of them is a log entry this app cannot read; an entry in a log that is in neither list is not in a Sigstore log.
    /// </summary>
    public IReadOnlyList<byte[]> Unreadable { get; }

    /// <summary>
    /// When this trust root was last known to be Sigstore's current one (the app's build), or <see langword="null"/>
    /// when the build did not record it: then a certificate from an authority it does not have is always refused.
    /// </summary>
    public DateTimeOffset? CurrentAt { get; }

    private static readonly Lazy<SigstoreTrustRoot> s_publicGood = new(() =>
    {
        using Stream stream = typeof(SigstoreTrustRoot).Assembly.GetManifestResourceStream("sigstore-trusted-root.json")
            ?? throw new InvalidOperationException("The app carries no Sigstore trust root.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), CurrentAtOf(typeof(SigstoreTrustRoot).Assembly));
    });

    /// <summary>Sigstore's public-good trust root, as the app carries it.</summary>
    public static SigstoreTrustRoot PublicGood => s_publicGood.Value;

    /// <summary>The time <paramref name="assembly"/>'s build recorded as <see cref="CurrentAtMetadata"/>, if it recorded one.</summary>
    internal static DateTimeOffset? CurrentAtOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == CurrentAtMetadata)?.Value is { Length: > 0 } value
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time)
            ? time
            : null;

    /// <summary>Reads a trust root in Sigstore's <c>trusted_root.json</c> format, known to be current at <paramref name="currentAt"/>.</summary>
    public static SigstoreTrustRoot Parse(string json, DateTimeOffset? currentAt = null)
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

        return new SigstoreTrustRoot(authorities, logs, unreadable, currentAt);

        static (DateTimeOffset Start, DateTimeOffset? End) ValidFor(JsonElement element)
        {
            JsonElement validFor = element.GetProperty("validFor");
            return (Time(validFor.GetProperty("start")), validFor.TryGetProperty("end", out JsonElement end) ? Time(end) : null);
        }

        static DateTimeOffset Time(JsonElement value) =>
            DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>A trust root of the given authorities and logs, known to be current at <paramref name="currentAt"/>, for tests.</summary>
    internal static SigstoreTrustRoot Of(IReadOnlyList<Authority> authorities, IReadOnlyList<Log> logs, IReadOnlyList<byte[]>? unreadable = null, DateTimeOffset? currentAt = null) =>
        new(authorities, logs, unreadable ?? [], currentAt);
}
