using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>
/// Checks a Sigstore bundle (<c>application/vnd.dev.sigstore.bundle.v0.3+json</c>), as GitHub's attestations API
/// returns it, the way <c>gh attestation verify</c> does for a public repository:
/// <list type="number">
/// <item>The DSSE envelope holds an in-toto statement that names the file with its SHA-256.</item>
/// <item>The signing certificate was issued to the expected workflow (its URI subject alternative name, e.g.
/// <c>https://github.com/owner/repo/.github/workflows/release.yml@refs/tags/v1.0.0</c>) for a token from the expected
/// OIDC issuer (Fulcio's extension 1.3.6.1.4.1.57264.1.8), for code signing.</item>
/// <item>The envelope's signature verifies with that certificate's key.</item>
/// <item>A Rekor transparency log entry records that signature and certificate: its signed entry timestamp verifies
/// with the log's key, its inclusion proof leads to the root hash of a checkpoint the log signed, and the time it was
/// logged is within the certificate's validity (about ten minutes).</item>
/// <item>The certificate chains to a Fulcio authority of the trust root that issued certificates at that time.</item>
/// </list>
/// A bundle whose certificate, signature and statement check out, but whose log entry is in a form this app cannot read
/// (a log it does not know, such as Rekor v2 with its Ed25519 key), is reported as such
/// (<see cref="Outcome.VerifiedWithoutLog"/>); its certificate is then checked at the time it was issued.
/// </summary>
internal static class SigstoreBundle
{
    internal enum Outcome
    {
        /// <summary>Every check passed.</summary>
        Verified,

        /// <summary>The certificate, signature and statement passed; the transparency log entry could not be checked.</summary>
        VerifiedWithoutLog,

        /// <summary>A check failed.</summary>
        Failed,
    }

    internal readonly record struct Result(Outcome Outcome, string Detail);

    /// <summary>The OIDC issuer of GitHub Actions' tokens, which Fulcio records in the certificates it issues to a workflow.</summary>
    public const string GitHubActions = "https://token.actions.githubusercontent.com";

    private const string InToto = "application/vnd.in-toto+json";
    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string IssuerV2 = "1.3.6.1.4.1.57264.1.8";
    private const string IssuerV1 = "1.3.6.1.4.1.57264.1.1";

    /// <summary>
    /// Whether <paramref name="bundle"/> shows that <paramref name="identity"/>, with a token from
    /// <paramref name="issuer"/>, attested the file <paramref name="name"/> with SHA-256 <paramref name="sha256"/> (hex).
    /// </summary>
    public static Result Verify(JsonElement bundle, SigstoreTrustRoot trust, string identity, string issuer, string name, string sha256)
    {
        try
        {
            return Check(bundle, trust, identity, issuer, name, sha256);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException
            or IndexOutOfRangeException or CryptographicException or AsnContentException or JsonException or ArgumentException)
        {
            return Failed($"its bundle cannot be read ({ex.Message})");
        }
    }

    private static Result Check(JsonElement bundle, SigstoreTrustRoot trust, string identity, string issuer, string name, string sha256)
    {
        JsonElement material = bundle.GetProperty("verificationMaterial");
        byte[] certificateBytes = material.TryGetProperty("certificate", out JsonElement single)
            ? Base64(single.GetProperty("rawBytes"))
            : Base64(material.GetProperty("x509CertificateChain").GetProperty("certificates")[0].GetProperty("rawBytes"));
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(certificateBytes);

        JsonElement envelope = bundle.GetProperty("dsseEnvelope");
        string payloadType = envelope.GetProperty("payloadType").GetString() ?? string.Empty;
        byte[] payload = Base64(envelope.GetProperty("payload"));
        JsonElement[] signatures = [.. envelope.GetProperty("signatures").EnumerateArray()];
        if (signatures.Length != 1)
        {
            return Failed($"its envelope has {signatures.Length} signatures, not one");
        }

        byte[] signature = Base64(signatures[0].GetProperty("sig"));

        // What was attested: the file, by name and checksum.
        if (payloadType != InToto)
        {
            return Failed($"it attests a {payloadType}, not an in-toto statement");
        }

        if (StatementRefuses(payload, name, sha256) is { } statement)
        {
            return Failed(statement);
        }

        // Who attested it: the workflow the certificate was issued to, and the issuer of the token it showed.
        string[] uris = SubjectUris(certificate);
        if (!uris.Contains(identity, StringComparer.OrdinalIgnoreCase))
        {
            return Failed($"it was signed by {(uris.Length > 0 ? string.Join(", ", uris) : "a certificate that names no workflow")}, not by {identity}");
        }

        string? tokenIssuer = Issuer(certificate);
        if (!string.Equals(tokenIssuer, issuer, StringComparison.Ordinal))
        {
            return Failed($"its certificate was issued for a token from {tokenIssuer ?? "an unknown issuer"}, not from {issuer}");
        }

        if (!certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning)))
        {
            return Failed("its certificate is not for code signing");
        }

        // The signature, over DSSE's pre-authentication encoding of the statement.
        using (ECDsa? key = certificate.GetECDsaPublicKey())
        {
            HashAlgorithmName? hash = key?.KeySize switch
            {
                256 => HashAlgorithmName.SHA256,
                384 => HashAlgorithmName.SHA384,
                _ => null,
            };
            if (key is null || hash is not { } algorithm)
            {
                return Failed("its certificate's key is not an ECDSA P-256 or P-384 key");
            }

            if (!key.VerifyData(Pae(payloadType, payload), signature, algorithm, DSASignatureFormat.Rfc3279DerSequence))
            {
                return Failed("its signature does not verify");
            }
        }

        // When it was signed, as the transparency log recorded it.
        (DateTimeOffset? logged, string? unlogged, string? refused) = Logged(material, trust, certificate, payload, signature);
        if (refused is not null)
        {
            return Failed(refused);
        }

        // The certificate, from a Fulcio authority that issued certificates at that time. Without a log entry only the
        // chain itself can be checked, at the time the certificate was issued.
        DateTimeOffset at = logged ?? new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        if (ChainRefuses(certificate, trust, at) is { } chain)
        {
            return Failed(chain);
        }

        return logged is { } time
            ? new Result(Outcome.Verified, $"signed by {identity}, logged in Rekor at {time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC")
            : new Result(Outcome.VerifiedWithoutLog, $"signed by {identity}, but {unlogged}");
    }

    private static string? StatementRefuses(byte[] payload, string name, string sha256)
    {
        using JsonDocument statement = JsonDocument.Parse(payload);
        string? type = statement.RootElement.GetProperty("_type").GetString();
        if (type is not ("https://in-toto.io/Statement/v1" or "https://in-toto.io/Statement/v0.1"))
        {
            return $"it attests a {type}, not an in-toto statement";
        }

        string? named = null;
        foreach (JsonElement subject in statement.RootElement.GetProperty("subject").EnumerateArray())
        {
            if (subject.GetProperty("digest").TryGetProperty("sha256", out JsonElement digest)
                && string.Equals(digest.GetString(), sha256, StringComparison.OrdinalIgnoreCase))
            {
                string? subjectName = subject.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (subjectName == name)
                {
                    return null;
                }

                named ??= subjectName;
            }
        }

        return named is not null
            ? $"it attests that checksum as {named}, not {name}"
            : $"it does not attest {name} with SHA-256 {sha256}";
    }

    /// <summary>
    /// When the bundle's Rekor entry says the signature was logged, once the entry checks out; or why no entry could be
    /// checked (<c>Unlogged</c>: none is in a form this app reads), or why the entry is refused (<c>Refused</c>: it is
    /// in a log this app knows, and does not check out).
    /// </summary>
    private static (DateTimeOffset? Logged, string? Unlogged, string? Refused) Logged(
        JsonElement material, SigstoreTrustRoot trust, X509Certificate2 certificate, byte[] payload, byte[] signature)
    {
        string? unlogged = null;
        JsonElement[] entries = material.TryGetProperty("tlogEntries", out JsonElement list) ? [.. list.EnumerateArray()] : [];
        foreach (JsonElement entry in entries)
        {
            byte[] keyId = Base64(entry.GetProperty("logId").GetProperty("keyId"));
            SigstoreTrustRoot.Log? log = trust.Logs.FirstOrDefault(l => l.KeyId.AsSpan().SequenceEqual(keyId));
            if (log is null)
            {
                unlogged ??= "its transparency log entry is in a log this app does not know";
                continue;
            }

            JsonElement kind = entry.GetProperty("kindVersion");
            (string? kindName, string? kindVersion) = (kind.GetProperty("kind").GetString(), kind.GetProperty("version").GetString());
            if (kindName != "dsse" || kindVersion != "0.0.1")
            {
                unlogged ??= $"its transparency log entry is a {kindName} {kindVersion} entry, which this app does not read";
                continue;
            }

            if (!entry.TryGetProperty("inclusionPromise", out JsonElement promise) || !entry.TryGetProperty("inclusionProof", out JsonElement proof))
            {
                return (null, null, "its transparency log entry has no signed entry timestamp or no inclusion proof");
            }

            byte[] body = Base64(entry.GetProperty("canonicalizedBody"));
            if (BodyRefuses(body, certificate, payload, signature) is { } bound)
            {
                return (null, null, bound);
            }

            // The signed entry timestamp: the log's signature over the entry, its index and the time it was logged,
            // as canonical JSON (keys sorted, no spaces). Base64 and hex need no escaping; a JSON writer would escape '+'.
            long integrated = Integer(entry.GetProperty("integratedTime"));
            string promised = string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"body":"{{Convert.ToBase64String(body)}}","integratedTime":{{integrated}},"logID":"{{Convert.ToHexStringLower(keyId)}}","logIndex":{{Integer(entry.GetProperty("logIndex"))}}}""");
            byte[] timestamp = Base64(promise.GetProperty("signedEntryTimestamp"));
            if (!log.Key.VerifyData(Encoding.UTF8.GetBytes(promised), timestamp, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return (null, null, "its transparency log entry's signed timestamp does not verify");
            }

            if (InclusionRefuses(proof, body, log) is { } included)
            {
                return (null, null, included);
            }

            DateTimeOffset time = DateTimeOffset.FromUnixTimeSeconds(integrated);
            if (time.UtcDateTime < certificate.NotBefore.ToUniversalTime() || time.UtcDateTime > certificate.NotAfter.ToUniversalTime() || !log.ValidAt(time))
            {
                return (null, null, $"it was logged at {time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC, when its certificate or the log's key was not valid");
            }

            return (time, null, null);
        }

        // GitHub logs every attestation of a public repository: a bundle without an entry is refused, not let through.
        return unlogged is not null ? (null, unlogged, null) : (null, null, "it has no transparency log entry");
    }

    /// <summary>Why the log entry's body (a Rekor <c>dsse</c> 0.0.1 entry) is not about this signature, statement and certificate.</summary>
    private static string? BodyRefuses(byte[] body, X509Certificate2 certificate, byte[] payload, byte[] signature)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement spec = document.RootElement.GetProperty("spec");
        JsonElement payloadHash = spec.GetProperty("payloadHash");
        JsonElement[] signatures = [.. spec.GetProperty("signatures").EnumerateArray()];
        bool same = document.RootElement.GetProperty("kind").GetString() == "dsse"
            && payloadHash.GetProperty("algorithm").GetString() == "sha256"
            && string.Equals(payloadHash.GetProperty("value").GetString(), Convert.ToHexStringLower(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase)
            && signatures.Length == 1
            && Base64(signatures[0].GetProperty("signature")).AsSpan().SequenceEqual(signature)
            && Verifier(signatures[0].GetProperty("verifier")).AsSpan().SequenceEqual(certificate.RawData);
        return same ? null : "its transparency log entry is for another signature, statement or certificate";

        // The certificate as PEM, base64-encoded once more.
        static byte[] Verifier(JsonElement value)
        {
            using X509Certificate2 logged = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(Base64(value)));
            return logged.RawData;
        }
    }

    /// <summary>
    /// Why the inclusion proof does not show the entry in the log: the proof (RFC 9162, section 2.1.3.2) must lead from
    /// the entry to the root hash, and a checkpoint the log signed must name that root hash and tree size.
    /// </summary>
    private static string? InclusionRefuses(JsonElement proof, byte[] body, SigstoreTrustRoot.Log log)
    {
        long index = Integer(proof.GetProperty("logIndex"));
        long size = Integer(proof.GetProperty("treeSize"));
        byte[] root = Base64(proof.GetProperty("rootHash"));
        byte[][] hashes = [.. proof.GetProperty("hashes").EnumerateArray().Select(Base64)];
        if (!Includes(Leaf(body), index, size, hashes, root))
        {
            return "its transparency log entry's inclusion proof does not lead to the log's root hash";
        }

        string checkpoint = proof.GetProperty("checkpoint").GetProperty("envelope").GetString() ?? string.Empty;
        return CheckpointSigned(checkpoint, log, size, root) ? null : "its transparency log checkpoint is not signed by the log, or is for another tree";
    }

    /// <summary>The hash of a log entry in the Merkle tree: SHA-256 of 0x00 and the entry.</summary>
    internal static byte[] Leaf(byte[] entry) => SHA256.HashData([0, .. entry]);

    /// <summary>The hash of an inner node of the Merkle tree: SHA-256 of 0x01 and its two children.</summary>
    internal static byte[] Node(byte[] left, byte[] right) => SHA256.HashData([1, .. left, .. right]);

    /// <summary>Whether <paramref name="proof"/> shows <paramref name="leaf"/> at <paramref name="index"/> in a tree of <paramref name="size"/> entries with <paramref name="root"/>.</summary>
    internal static bool Includes(byte[] leaf, long index, long size, IReadOnlyList<byte[]> proof, byte[] root)
    {
        if (index < 0 || index >= size)
        {
            return false;
        }

        (long fn, long sn, byte[] hash) = (index, size - 1, leaf);
        foreach (byte[] sibling in proof)
        {
            if (sn == 0)
            {
                return false;
            }

            if ((fn & 1) == 1 || fn == sn)
            {
                hash = Node(sibling, hash);
                if ((fn & 1) == 0)
                {
                    do
                    {
                        (fn, sn) = (fn >> 1, sn >> 1);
                    }
                    while ((fn & 1) == 0 && fn != 0);
                }
            }
            else
            {
                hash = Node(hash, sibling);
            }

            (fn, sn) = (fn >> 1, sn >> 1);
        }

        return sn == 0 && hash.AsSpan().SequenceEqual(root);
    }

    /// <summary>
    /// Whether <paramref name="checkpoint"/>, a signed note (<c>origin\nsize\nroot\n\n— name base64(keyhint + signature)\n</c>),
    /// names <paramref name="size"/> and <paramref name="root"/> and carries a signature by <paramref name="log"/>'s key.
    /// </summary>
    internal static bool CheckpointSigned(string checkpoint, SigstoreTrustRoot.Log log, long size, byte[] root)
    {
        int split = checkpoint.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
        {
            return false;
        }

        string text = checkpoint[..(split + 1)];
        string[] lines = text.Split('\n');
        if (lines.Length < 4
            || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out long noted) || noted != size
            || !Convert.TryFromBase64String(lines[2], new byte[32], out int length) || length != 32
            || !Convert.FromBase64String(lines[2]).AsSpan().SequenceEqual(root))
        {
            return false;
        }

        byte[] signed = Encoding.UTF8.GetBytes(text);
        foreach (string line in checkpoint[(split + 2)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = line.LastIndexOf(' ');
            if (!line.StartsWith("— ", StringComparison.Ordinal) || space < 2)
            {
                continue;
            }

            byte[] value = Convert.FromBase64String(line[(space + 1)..]);
            if (value.Length > 4 && value.AsSpan(0, 4).SequenceEqual(log.KeyId.AsSpan(0, 4))
                && log.Key.VerifyData(signed, value.AsSpan(4), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why <paramref name="certificate"/> does not chain, at <paramref name="at"/>, to an authority that issued certificates then.</summary>
    private static string? ChainRefuses(X509Certificate2 certificate, SigstoreTrustRoot trust, DateTimeOffset at)
    {
        SigstoreTrustRoot.Authority[] authorities = [.. trust.Authorities.Where(a => a.ValidAt(at) && a.Chain.Length > 0)];
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = at.UtcDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        foreach (SigstoreTrustRoot.Authority authority in authorities)
        {
            chain.ChainPolicy.CustomTrustStore.Add(authority.Chain[^1]);
            chain.ChainPolicy.ExtraStore.AddRange(authority.Chain[..^1]);
        }

        if (authorities.Length == 0 || !chain.Build(certificate))
        {
            string status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
            return $"its certificate was not issued by Sigstore's certificate authority{(status.Length > 0 ? $" ({status})" : string.Empty)}";
        }

        return null;
    }

    /// <summary>The URIs in the certificate's subject alternative name, where Fulcio puts the workflow's identity.</summary>
    internal static string[] SubjectUris(X509Certificate2 certificate)
    {
        if (certificate.Extensions["2.5.29.17"] is not { } extension)
        {
            return [];
        }

        List<string> uris = [];
        AsnReader names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
        while (names.HasData)
        {
            if (names.PeekTag() == uri)
            {
                uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, uri));
            }
            else
            {
                names.ReadEncodedValue();
            }
        }

        return [.. uris];
    }

    /// <summary>The OIDC issuer Fulcio recorded in the certificate: its DER extension, else the older raw one.</summary>
    internal static string? Issuer(X509Certificate2 certificate) =>
        certificate.Extensions[IssuerV2] is { } v2 ? new AsnReader(v2.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.UTF8String)
        : certificate.Extensions[IssuerV1] is { } v1 ? Encoding.UTF8.GetString(v1.RawData)
        : null;

    /// <summary>DSSE's pre-authentication encoding, what the envelope's signature is over.</summary>
    internal static byte[] Pae(string payloadType, byte[] payload)
    {
        byte[] type = Encoding.UTF8.GetBytes(payloadType);
        return [.. Encoding.ASCII.GetBytes($"DSSEv1 {type.Length} "), .. type, .. Encoding.ASCII.GetBytes($" {payload.Length} "), .. payload];
    }

    private static byte[] Base64(JsonElement value) => Convert.FromBase64String(value.GetString() ?? throw new FormatException("a value is missing"));

    /// <summary>A 64-bit integer, which protobuf's JSON mapping writes as a string.</summary>
    private static long Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture) : value.GetInt64();

    private static Result Failed(string detail) => new(Outcome.Failed, detail);
}
