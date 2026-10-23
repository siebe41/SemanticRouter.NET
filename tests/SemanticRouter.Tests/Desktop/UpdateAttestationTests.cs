using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.Desktop;
using static SemanticRouter.Tests.Desktop.UpdateControlTests;

namespace SemanticRouter.Tests.Desktop;

/// <summary>
/// The default check of an update from GitHub (#181): without a publisher in the policy, the downloaded package must be
/// attested by the release workflow at its version's tag. Checked against the real attestation of the 0.5.0 release
/// and against bundles made here with a certificate authority and a transparency log of the test's own.
/// </summary>
public sealed class UpdateAttestationTests : IDisposable
{
    private const string Repository = "siebe41/SemanticRouter.NET";
    private const string RepositoryId = ProjectLinks.RepositoryId;
    private const string Package = "SemanticRouter-0.5.0-win-x64-full.nupkg";

    private readonly string _directory = Directory.CreateTempSubdirectory("attestation").FullName;
    private readonly TestSigstore _sigstore = new();

    public void Dispose()
    {
        _sigstore.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static string Release050 => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "attestation", "v0.5.0.json"));

    /// <summary>The 0.5.0 release's assets, by name, with their SHA-256 as its attestation names them.</summary>
    private static Dictionary<string, string> Subjects050()
    {
        using JsonDocument response = JsonDocument.Parse(Release050);
        string payload = response.RootElement.GetProperty("attestations")[0].GetProperty("bundle").GetProperty("dsseEnvelope").GetProperty("payload").GetString()!;
        using JsonDocument statement = JsonDocument.Parse(Convert.FromBase64String(payload));
        return statement.RootElement.GetProperty("subject").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("digest").GetProperty("sha256").GetString()!);
    }

    private static UpdateAttestation GitHub(string? response, SigstoreTrustRoot? trust = null) =>
        new(Repository, RepositoryId, (_, _) => Task.FromResult(response), trust ?? SigstoreTrustRoot.PublicGood);

    [Fact]
    public async Task The_050_release_attestation_verifies_against_sigstores_trust_root()
    {
        Dictionary<string, string> subjects = Subjects050();
        subjects.Should().HaveCount(33).And.ContainKey(Package);

        UpdateAttestation.Verdict verdict = await GitHub(Release050).CheckDigestAsync(subjects[Package], Package, "0.5.0");

        verdict.Should().Be(new UpdateAttestation.Verdict(
            UpdateAttestation.Outcome.Verified,
            "its attestation is signed by https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v0.5.0, logged in Rekor at 2026-10-16 18:11:38 UTC"));
        foreach ((string name, string sha256) in subjects)
        {
            (await GitHub(Release050).CheckDigestAsync(sha256, name, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Verified, name);
        }
    }

    [Fact]
    public async Task The_050_attestation_does_not_vouch_for_another_version_file_or_repository()
    {
        Dictionary<string, string> subjects = Subjects050();
        string sha256 = subjects[Package];

        UpdateAttestation.Verdict newer = await GitHub(Release050).CheckDigestAsync(sha256, Package, "0.5.1");
        newer.Outcome.Should().Be(UpdateAttestation.Outcome.Refused, "an old package offered as a new version");
        newer.Detail.Should().Be("its attestation does not hold: it was signed by https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v0.5.0, "
            + "not by https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v0.5.1");

        UpdateAttestation.Verdict renamed = await GitHub(Release050).CheckDigestAsync(subjects["SemanticRouter-0.5.0-osx-arm64-full.nupkg"], Package, "0.5.0");
        renamed.Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Refused,
            $"its attestation does not hold: it attests that checksum as SemanticRouter-0.5.0-osx-arm64-full.nupkg, not {Package}"));

        string other = new('0', 64);
        UpdateAttestation.Verdict changed = await GitHub(Release050).CheckDigestAsync(other, Package, "0.5.0");
        changed.Detail.Should().Be($"its attestation does not hold: it does not attest {Package} with SHA-256 {other}");

        var fork = new UpdateAttestation("mallory/SemanticRouter.NET", RepositoryId, (_, _) => Task.FromResult<string?>(Release050), SigstoreTrustRoot.PublicGood);
        (await fork.CheckDigestAsync(sha256, Package, "0.5.0")).Detail.Should().Contain("not by https://github.com/mallory/SemanticRouter.NET/");

        // The identity is compared exactly, as gh does: another spelling of the owner is another identity.
        var spelled = new UpdateAttestation("Siebe41/SemanticRouter.NET", RepositoryId, (_, _) => Task.FromResult<string?>(Release050), SigstoreTrustRoot.PublicGood);
        (await spelled.CheckDigestAsync(sha256, Package, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Refused);

        // A repository registered again under the same name has another id.
        var again = new UpdateAttestation(Repository, "42", (_, _) => Task.FromResult<string?>(Release050), SigstoreTrustRoot.PublicGood);
        (await again.CheckDigestAsync(sha256, Package, "0.5.0")).Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Refused,
            $"its attestation does not hold: its certificate was issued to the repository with id {RepositoryId}, not 42"));
    }

    [Fact]
    public async Task Without_sigstores_certificate_authority_or_log_the_050_attestation_does_not_verify()
    {
        string sha256 = Subjects050()[Package];
        SigstoreTrustRoot publicGood = SigstoreTrustRoot.PublicGood;
        (publicGood.Authorities.Count, publicGood.Logs.Count, publicGood.Unreadable.Count).Should().Be((2, 1, 1), "two Fulcio authorities; Rekor v1 has an ECDSA key, Rekor v2 an Ed25519 one");

        // An authority the trust root does not have, as after Sigstore adds one: this version cannot check it, which is
        // not the same as a certificate that does not verify.
        UpdateAttestation.Verdict noAuthority = await GitHub(Release050, SigstoreTrustRoot.Of([], publicGood.Logs, publicGood.Unreadable)).CheckDigestAsync(sha256, Package, "0.5.0");
        noAuthority.Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Outdated,
            "its attestation is signed by https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v0.5.0, but its certificate is from a certificate authority this version of the app does not know"));

        // A log of the trust root whose key the app cannot check (as Rekor v2's): the check could not run.
        byte[] rekor = publicGood.Logs[0].KeyId;
        UpdateAttestation.Verdict unreadable = await GitHub(Release050, SigstoreTrustRoot.Of(publicGood.Authorities, [], [rekor])).CheckDigestAsync(sha256, Package, "0.5.0");
        unreadable.Outcome.Should().Be(UpdateAttestation.Outcome.Unchecked);
        unreadable.Detail.Should().EndWith("but its transparency log entry is in a log whose key this version of the app cannot check");

        // A log that is not in the trust root at all: the log id is not signed, so that is refused.
        UpdateAttestation.Verdict noLog = await GitHub(Release050, SigstoreTrustRoot.Of(publicGood.Authorities, [], publicGood.Unreadable)).CheckDigestAsync(sha256, Package, "0.5.0");
        noLog.Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Refused,
            "its attestation does not hold: its transparency log entry is in a log that is not in Sigstore's trust root"));
    }

    [Fact]
    public async Task What_github_answers_decides_between_refused_and_unchecked()
    {
        string sha256 = new('a', 64);

        (await GitHub(null).CheckDigestAsync(sha256, Package, "0.5.0")).Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Refused,
            $"GitHub has no attestation for it (SHA-256 {sha256}), so it is not a package {Repository}'s release workflow built"));
        (await GitHub("""{"attestations":[]}""").CheckDigestAsync(sha256, Package, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Refused);

        (await GitHub("<html>").CheckDigestAsync(sha256, Package, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Unchecked);
        (await GitHub("""{"message":"x"}""").CheckDigestAsync(sha256, Package, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Unchecked);
        (await GitHub("""{"attestations":[{"bundle_url":"https://example.com/b"}]}""").CheckDigestAsync(sha256, Package, "0.5.0")).Should()
            .Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Unchecked, "GitHub's attestations API gave no attestation the app can read"));
        (await GitHub("""{"attestations":[{"bundle":{"dsseEnvelope":{}}}]}""").CheckDigestAsync(sha256, Package, "0.5.0")).Detail.Should()
            .StartWith("its attestation does not hold: its bundle cannot be read");

        var offline = new UpdateAttestation(Repository, RepositoryId, (_, _) => throw new HttpRequestException("No such host is known."), SigstoreTrustRoot.PublicGood);
        (await offline.CheckDigestAsync(sha256, Package, "0.5.0")).Should().Be(new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Unchecked,
            "GitHub's attestations API could not be asked (No such host is known.)"));
        var slow = new UpdateAttestation(Repository, RepositoryId, (_, _) => throw new TaskCanceledException(), SigstoreTrustRoot.PublicGood);
        (await slow.CheckDigestAsync(sha256, Package, "0.5.0")).Detail.Should().Be("GitHub's attestations API did not answer in time");
    }

    [Fact]
    public async Task A_bundle_from_the_release_workflow_verifies_and_every_tampering_is_refused()
    {
        const string sha256 = "84c8087cb6e605ca52ba615249f8a16a33b631b39283ef08176660cab71346c3";
        string identity = GitHub(null).Identity("0.5.0");
        JsonObject Good() => _sigstore.Bundle(identity, [(Package, sha256)]);
        Task<UpdateAttestation.Verdict> Check(JsonNode bundle, SigstoreTrustRoot? trust = null) =>
            GitHub(TestSigstore.Response(bundle), trust ?? _sigstore.Trust).CheckDigestAsync(sha256, Package, "0.5.0");
        async Task<string> Refused(JsonNode bundle, SigstoreTrustRoot? trust = null)
        {
            UpdateAttestation.Verdict verdict = await Check(bundle, trust);
            verdict.Outcome.Should().Be(UpdateAttestation.Outcome.Refused, verdict.Detail);
            return verdict.Detail;
        }

        (await Check(Good())).Outcome.Should().Be(UpdateAttestation.Outcome.Verified);

        (await Refused(_sigstore.Bundle(identity.Replace("release.yml", "ci.yml", StringComparison.Ordinal), [(Package, sha256)]))).Should().Contain("ci.yml@refs/tags/v0.5.0, not by");
        (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], issuer: "https://token.example.com"))).Should().Contain("issued for a token from https://token.example.com");
        (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], codeSigning: false))).Should().Contain("not for code signing");
        (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], repositoryId: null))).Should().Contain("issued to the repository with id (none)");
        (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], predicateType: "https://example.com/anything"))).Should()
            .Contain("it is a https://example.com/anything statement, not build provenance (https://slsa.dev/provenance/v1)");
        using (var mallory = new TestSigstore())
        {
            // Naming the trust root's authority as its issuer, the certificate must chain to it.
            (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], authority: mallory, named: _sigstore))).Should().Contain("not issued by Sigstore's certificate authority");

            // Naming an authority the trust root does not have: as after Sigstore adds one, not applied but not refused either.
            UpdateAttestation.Verdict unknown = await Check(_sigstore.Bundle(identity, [(Package, sha256)], authority: mallory));
            unknown.Outcome.Should().Be(UpdateAttestation.Outcome.Outdated);
            unknown.Detail.Should().EndWith("but its certificate is from a certificate authority this version of the app does not know");
        }

        (await Refused(_sigstore.Bundle(identity, [(Package, sha256)], logged: TestSigstore.Issued.AddMinutes(20)))).Should().Contain("when its certificate or the log's key was not valid");

        JsonObject signature = Good();
        Flip(signature["dsseEnvelope"]!["signatures"]![0]!, "sig");
        (await Refused(signature)).Should().Contain("its signature does not verify");

        JsonObject statement = Good();
        statement["dsseEnvelope"]!["payload"] = _sigstore.Bundle(identity, [(Package, sha256), ("extra.txt", sha256)])["dsseEnvelope"]!["payload"]!.DeepClone();
        (await Refused(statement)).Should().Contain("its signature does not verify");

        JsonObject timestamp = Good();
        Flip(Entry(timestamp)["inclusionPromise"]!, "signedEntryTimestamp");
        (await Refused(timestamp)).Should().Contain("signed timestamp does not verify");

        JsonObject proof = Good();
        JsonNode hashes = Entry(proof)["inclusionProof"]!["hashes"]!;
        hashes[0] = Flipped(hashes[0]!);
        (await Refused(proof)).Should().Contain("inclusion proof does not lead to the log's root hash");

        JsonObject checkpoint = Good();
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Entry(checkpoint)["inclusionProof"]!["checkpoint"] = Entry(_sigstore.Bundle(identity, [(Package, sha256)], checkpointKey: other))["inclusionProof"]!["checkpoint"]!.DeepClone();
        }

        (await Refused(checkpoint)).Should().Contain("checkpoint is not signed by the log");

        // A log entry that is genuine, but for another signature.
        JsonObject swapped = Good();
        swapped["verificationMaterial"]!["tlogEntries"] = Good()["verificationMaterial"]!["tlogEntries"]!.DeepClone();
        (await Refused(swapped)).Should().Contain("is for another signature, statement or certificate");

        JsonObject unlogged = Good();
        unlogged["verificationMaterial"]!["tlogEntries"] = new JsonArray();
        (await Refused(unlogged)).Should().Contain("it has no transparency log entry");

        JsonObject twice = Good();
        twice["dsseEnvelope"]!["signatures"]!.AsArray().Add(twice["dsseEnvelope"]!["signatures"]![0]!.DeepClone());
        (await Refused(twice)).Should().Contain("2 signatures, not one");

        // Neither the entry's kind nor its log id is signed, so neither can turn the log check off.
        JsonObject kind = Good();
        Entry(kind)["kindVersion"]!["version"] = "0.0.2";
        (await Refused(kind)).Should().Contain("is a dsse 0.0.2 entry, not the dsse 0.0.1 entry an attestation is logged as");
        JsonObject log = Good();
        Entry(log)["logId"]!["keyId"] = Convert.ToBase64String(new byte[32]);
        (await Refused(log)).Should().Contain("in a log that is not in Sigstore's trust root");
        (await Refused(Good(), SigstoreTrustRoot.Of(_sigstore.Trust.Authorities, []))).Should().Contain("in a log that is not in Sigstore's trust root");

        // An entry in a log of the trust root whose key this app cannot check: neither refused nor verified.
        UpdateAttestation.Verdict unreadable = await Check(Good(), SigstoreTrustRoot.Of(_sigstore.Trust.Authorities, [], [_sigstore.Trust.Logs[0].KeyId]));
        unreadable.Outcome.Should().Be(UpdateAttestation.Outcome.Unchecked);

        // One good attestation among others is enough.
        (await GitHub(TestSigstore.Response(signature, Good()), _sigstore.Trust).CheckDigestAsync(sha256, Package, "0.5.0")).Outcome.Should().Be(UpdateAttestation.Outcome.Verified);

        static JsonNode Entry(JsonObject bundle) => bundle["verificationMaterial"]!["tlogEntries"]![0]!;

        static void Flip(JsonNode parent, string name) => parent[name] = Flipped(parent[name]!);

        static string Flipped(JsonNode base64)
        {
            byte[] bytes = Convert.FromBase64String(base64.GetValue<string>());
            bytes[^1] ^= 1;
            return Convert.ToBase64String(bytes);
        }
    }

    [Fact]
    public void The_inclusion_proof_is_checked_as_rfc_9162_says_for_every_leaf_of_every_tree_size()
    {
        for (int size = 1; size <= 17; size++)
        {
            byte[][] entries = [.. Enumerable.Range(0, size).Select(i => Encoding.UTF8.GetBytes($"entry {i}"))];
            byte[] root = TestSigstore.TreeHash(entries);
            for (int index = 0; index < size; index++)
            {
                byte[][] path = [.. TestSigstore.AuditPath(index, entries)];
                byte[] leaf = SigstoreBundle.Leaf(entries[index]);
                SigstoreBundle.Includes(leaf, index, size, path, root).Should().BeTrue($"leaf {index} of {size}");
                SigstoreBundle.Includes(leaf, index, size + 2 * size, path, root).Should().BeFalse("the proof is too short for that tree");
                SigstoreBundle.Includes(SigstoreBundle.Leaf([.. entries[index], 0]), index, size, path, root).Should().BeFalse();
                if (size > 1)
                {
                    SigstoreBundle.Includes(leaf, (index + 1) % size, size, path, root).Should().BeFalse();
                    SigstoreBundle.Includes(leaf, index, size, path[..^1], root).Should().BeFalse();
                }
            }
        }
    }

    [Fact]
    public async Task An_attested_update_is_applied()
    {
        var updates = new FakeUpdates { Package = PackageFile() };
        var log = new StringWriter();
        var updater = new DesktopUpdater(updates, null, log, attestation: Attesting(updates.Package));

        (await updater.CheckAsync()).Should().Be("1.0.1");

        (await updater.ApplyOnQuitAsync()).Should().BeTrue();
        updates.Calls.Should().Equal("check", "download", "package", "apply 1.0.1 restart=False");
        updater.Problem.Should().BeNull();
        log.ToString().Should().Contain("Update 1.0.1 was built by the project's release workflow: its attestation is signed by "
            + "https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v1.0.1");
    }

    [Fact]
    public async Task An_update_checked_against_its_attestation_is_downloaded_whole_not_rebuilt_from_deltas()
    {
        // Velopack zips a package rebuilt from deltas itself, so its checksum is not the attested package's.
        var attested = new FakeUpdates { Package = PackageFile(), Deltas = true };
        await new DesktopUpdater(attested, null, TextWriter.Null, attestation: Attesting(attested.Package)).CheckAsync();
        attested.Requested!.Should().Match<Velopack.UpdateInfo>(u => u.DeltasToTarget.Length == 0 && u.BaseRelease == null && u.TargetFullRelease.Version.ToString() == "1.0.1");

        var mirrored = new FakeUpdates { Package = PackageFile(), Deltas = true };
        await new DesktopUpdater(mirrored, null, TextWriter.Null).CheckAsync();
        mirrored.Requested!.DeltasToTarget.Should().ContainSingle("without the check, deltas save the download");
    }

    [Fact]
    public async Task An_update_without_an_attestation_is_refused_deleted_and_not_offered_again()
    {
        var updates = new FakeUpdates { Package = PackageFile() };
        var log = new StringWriter();
        var updater = new DesktopUpdater(updates, null, log, attestation: GitHub(null, _sigstore.Trust));

        (await updater.CheckAsync()).Should().BeNull();

        updater.Problem.Should().StartWith("Update 1.0.1 was refused: GitHub has no attestation for it");
        log.ToString().Should().Contain("only updates the project's release workflow attested are applied");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        await updater.CheckAsync();
        updates.Calls.Should().Equal("check", "download", "package", "discard", "check");
    }

    [Fact]
    public async Task An_update_that_cannot_be_checked_is_kept_not_applied_and_checked_again_at_the_next_check()
    {
        // Whoever can reset the app's connection to GitHub's API can make the check fail, so that must not let the update in.
        var updates = new FakeUpdates { Package = PackageFile() };
        var log = new StringWriter();
        (string sha256, string response) = Attestation(updates.Package);
        bool offline = true;
        var flaky = new UpdateAttestation(Repository, RepositoryId, (asked, _) =>
            offline ? throw new HttpRequestException("rate limited") : Task.FromResult(asked == sha256 ? response : null), _sigstore.Trust);
        var updater = new DesktopUpdater(updates, null, log, attestation: flaky);

        await updater.CheckAsync();

        updater.Problem.Should().Be("Update 1.0.1 is waiting to be checked against its attestation: GitHub's attestations API could not be asked (rate limited)");
        updater.Ready.Should().BeNull("the package is kept, but not ready");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        log.ToString().Should().Contain("It is not applied until the check passes; the app tries again at the next check.");
        updates.Calls.Should().NotContain("discard").And.NotContain(c => c.StartsWith("apply", StringComparison.Ordinal));

        offline = false;
        (await updater.CheckAsync()).Should().Be("1.0.1");
        updater.Ready.Should().Be("1.0.1");
        updater.Problem.Should().BeNull();
        (await updater.ApplyOnQuitAsync()).Should().BeTrue();
        updates.Calls.Should().EndWith("apply 1.0.1 restart=False");
    }

    [Fact]
    public async Task An_update_whose_package_is_missing_is_not_applied_without_the_check()
    {
        var updates = new FakeUpdates { Package = Path.Combine(_directory, "gone.nupkg") };
        var updater = new DesktopUpdater(updates, null, TextWriter.Null, attestation: GitHub(null, _sigstore.Trust));

        await updater.CheckAsync();

        updater.Problem.Should().Be("Update 1.0.1 is waiting to be checked against its attestation: its package is not where the updater keeps it");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        updates.Calls.Should().NotContain(c => c.StartsWith("apply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_this_version_is_too_old_to_check_is_kept_not_applied_and_not_offered_again()
    {
        // Sigstore added a certificate authority after this version was released: say so, and do not delete the package.
        var updates = new FakeUpdates { Package = PackageFile() };
        var log = new StringWriter();
        using var newer = new TestSigstore();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(updates.Package)));
        string response = TestSigstore.Response(newer.Bundle(GitHub(null).Identity("1.0.1"), [(Path.GetFileName(updates.Package), sha256)]));
        var updater = new DesktopUpdater(updates, null, log, attestation: new UpdateAttestation(Repository, RepositoryId, (_, _) => Task.FromResult<string?>(response), SigstoreTrustRoot.Of(_sigstore.Trust.Authorities, newer.Trust.Logs)));

        (await updater.CheckAsync()).Should().BeNull();

        updater.Problem.Should().StartWith("Update 1.0.1 cannot be checked by this version of the app: its attestation is signed by")
            .And.EndWith($"Install the new version yourself from {ProjectLinks.Releases}");
        log.ToString().Should().Contain("Sigstore changed its certificate authorities after this version was released");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        await updater.CheckAsync();
        updates.Calls.Should().Equal("check", "download", "package", "package", "check");
        updates.Downloaded.Should().BeTrue("the package is kept, so a later start does not download it again");
    }

    [Fact]
    public async Task With_a_publisher_in_the_policy_the_attestation_is_not_asked_for()
    {
        var updates = new FakeUpdates { Package = PackageFile() };
        int asked = 0;
        var attestation = new UpdateAttestation(Repository, RepositoryId, (_, _) =>
        {
            asked++;
            return Task.FromResult<string?>(null);
        }, _sigstore.Trust);
        var updater = new DesktopUpdater(updates, null, TextWriter.Null, publisher: new UpdatePublisher("Contoso Ltd", UpdatePublisher.Platform.Linux, null), attestation: attestation);

        await updater.CheckAsync();

        updater.Problem.Should().Contain("cannot be checked on Linux", "the publisher decides");
        asked.Should().Be(0);
    }

    /// <summary>A downloaded package: any bytes do, the check is by checksum.</summary>
    private string PackageFile()
    {
        string path = Path.Combine(_directory, "SemanticRouter-1.0.1-win-x64-full.nupkg");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a package"));
        return path;
    }

    /// <summary>A check that finds <paramref name="package"/> attested by the release workflow at v1.0.1.</summary>
    private UpdateAttestation Attesting(string package)
    {
        (string sha256, string response) = Attestation(package);
        return new UpdateAttestation(Repository, RepositoryId, (asked, _) => Task.FromResult(asked == sha256 ? response : null), _sigstore.Trust);
    }

    /// <summary><paramref name="package"/>'s SHA-256, and the API's answer for it with an attestation by the release workflow at v1.0.1.</summary>
    private (string Sha256, string Response) Attestation(string package)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package)));
        return (sha256, TestSigstore.Response(_sigstore.Bundle($"https://github.com/{Repository}/{UpdateAttestation.Workflow}@refs/tags/v1.0.1", [(Path.GetFileName(package), sha256)])));
    }

    /// <summary>
    /// A certificate authority and a transparency log of the test's own, standing in for Fulcio and Rekor: bundles in the
    /// form GitHub's attestations API returns, signed and logged the way Sigstore does it.
    /// </summary>
    internal sealed class TestSigstore : IDisposable
    {
        /// <summary>When the bundles' certificates are issued; they are valid for ten minutes, as Fulcio's are.</summary>
        public static readonly DateTimeOffset Issued = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        private readonly ECDsa _authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa _logKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 _authority;
        private readonly byte[] _keyId;

        public TestSigstore()
        {
            var request = new CertificateRequest("O=sigstore.test, CN=sigstore", _authorityKey, HashAlgorithmName.SHA384);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            _authority = request.CreateSelfSigned(Issued.AddYears(-1), Issued.AddYears(10));
            _keyId = SHA256.HashData(_logKey.ExportSubjectPublicKeyInfo());
            Trust = SigstoreTrustRoot.Of(
                [new SigstoreTrustRoot.Authority([_authority], Issued.AddYears(-1), null)],
                [new SigstoreTrustRoot.Log(_keyId, _logKey, Issued.AddYears(-1), null)]);
        }

        public SigstoreTrustRoot Trust { get; }

        public void Dispose()
        {
            _authority.Dispose();
            _authorityKey.Dispose();
            _logKey.Dispose();
        }

        /// <summary>The API's answer with these bundles.</summary>
        public static string Response(params JsonNode[] bundles) =>
            new JsonObject { ["attestations"] = new JsonArray([.. bundles.Select(b => (JsonNode)new JsonObject { ["bundle"] = b.DeepClone() })]) }.ToJsonString();

        /// <summary>A bundle in which <paramref name="identity"/> attests <paramref name="subjects"/>, logged in this log at <paramref name="logged"/>.</summary>
        public JsonObject Bundle(
            string identity,
            IEnumerable<(string Name, string Sha256)> subjects,
            string issuer = SigstoreBundle.GitHubActions,
            bool codeSigning = true,
            TestSigstore? authority = null,
            DateTimeOffset? logged = null,
            ECDsa? checkpointKey = null,
            string? repositoryId = RepositoryId,
            string predicateType = "https://slsa.dev/provenance/v1",
            TestSigstore? named = null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            TestSigstore issuing = authority ?? this;
            using X509Certificate2 certificate = issuing.Issue(key, identity, issuer, codeSigning, repositoryId, named ?? issuing);

            var statement = new JsonObject
            {
                ["_type"] = "https://in-toto.io/Statement/v1",
                ["subject"] = new JsonArray([.. subjects.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["digest"] = new JsonObject { ["sha256"] = s.Sha256 } })]),
                ["predicateType"] = predicateType,
                ["predicate"] = new JsonObject(),
            };
            byte[] payload = Encoding.UTF8.GetBytes(statement.ToJsonString());
            const string payloadType = "application/vnd.in-toto+json";
            byte[] signature = key.SignData(SigstoreBundle.Pae(payloadType, payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            // The Rekor dsse 0.0.1 entry, in a tree of three with it in the middle.
            var body = new JsonObject
            {
                ["apiVersion"] = "0.0.1",
                ["kind"] = "dsse",
                ["spec"] = new JsonObject
                {
                    ["payloadHash"] = new JsonObject { ["algorithm"] = "sha256", ["value"] = Convert.ToHexStringLower(SHA256.HashData(payload)) },
                    ["signatures"] = new JsonArray(new JsonObject
                    {
                        ["signature"] = Convert.ToBase64String(signature),
                        ["verifier"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem())),
                    }),
                },
            };
            byte[] entry = Encoding.UTF8.GetBytes(body.ToJsonString());
            byte[][] entries = [Encoding.UTF8.GetBytes("before"), entry, Encoding.UTF8.GetBytes("after")];
            byte[] root = TreeHash(entries);
            long time = (logged ?? Issued.AddMinutes(1)).ToUnixTimeSeconds();
            const long logIndex = 4242;

            string promised = string.Create(CultureInfo.InvariantCulture,
                $$"""{"body":"{{Convert.ToBase64String(entry)}}","integratedTime":{{time}},"logID":"{{Convert.ToHexStringLower(_keyId)}}","logIndex":{{logIndex}}}""");
            byte[] timestamp = _logKey.SignData(Encoding.UTF8.GetBytes(promised), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            string note = $"rekor.sigstore.test - 1\n3\n{Convert.ToBase64String(root)}\n";
            byte[] noteSignature = (checkpointKey ?? _logKey).SignData(Encoding.UTF8.GetBytes(note), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            return new JsonObject
            {
                ["mediaType"] = "application/vnd.dev.sigstore.bundle.v0.3+json",
                ["verificationMaterial"] = new JsonObject
                {
                    ["tlogEntries"] = new JsonArray(new JsonObject
                    {
                        ["logIndex"] = logIndex.ToString(CultureInfo.InvariantCulture),
                        ["logId"] = new JsonObject { ["keyId"] = Convert.ToBase64String(_keyId) },
                        ["kindVersion"] = new JsonObject { ["kind"] = "dsse", ["version"] = "0.0.1" },
                        ["integratedTime"] = time.ToString(CultureInfo.InvariantCulture),
                        ["inclusionPromise"] = new JsonObject { ["signedEntryTimestamp"] = Convert.ToBase64String(timestamp) },
                        ["inclusionProof"] = new JsonObject
                        {
                            ["logIndex"] = "1",
                            ["rootHash"] = Convert.ToBase64String(root),
                            ["treeSize"] = "3",
                            ["hashes"] = new JsonArray([.. AuditPath(1, entries).Select(h => (JsonNode)Convert.ToBase64String(h))]),
                            ["checkpoint"] = new JsonObject { ["envelope"] = $"{note}\n— rekor.sigstore.test {Convert.ToBase64String([.. _keyId[..4], .. noteSignature])}\n" },
                        },
                        ["canonicalizedBody"] = Convert.ToBase64String(entry),
                    }),
                    ["certificate"] = new JsonObject { ["rawBytes"] = Convert.ToBase64String(certificate.RawData) },
                },
                ["dsseEnvelope"] = new JsonObject
                {
                    ["payload"] = Convert.ToBase64String(payload),
                    ["payloadType"] = payloadType,
                    ["signatures"] = new JsonArray(new JsonObject { ["sig"] = Convert.ToBase64String(signature) }),
                },
            };
        }

        /// <summary>The Merkle tree hash of <paramref name="entries"/> (RFC 9162, section 2.1.1).</summary>
        public static byte[] TreeHash(ReadOnlySpan<byte[]> entries)
        {
            if (entries.Length == 1)
            {
                return SigstoreBundle.Leaf(entries[0]);
            }

            int split = Split(entries.Length);
            return SigstoreBundle.Node(TreeHash(entries[..split]), TreeHash(entries[split..]));
        }

        /// <summary>The inclusion proof of entry <paramref name="index"/> (RFC 9162, section 2.1.3.1).</summary>
        public static List<byte[]> AuditPath(int index, ReadOnlySpan<byte[]> entries)
        {
            if (entries.Length == 1)
            {
                return [];
            }

            int split = Split(entries.Length);
            List<byte[]> path = index < split ? AuditPath(index, entries[..split]) : AuditPath(index - split, entries[split..]);
            path.Add(index < split ? TreeHash(entries[split..]) : TreeHash(entries[..split]));
            return path;
        }

        /// <summary>The largest power of two smaller than <paramref name="n"/>.</summary>
        private static int Split(int n) => 1 << (int)Math.Floor(Math.Log2(n - 1));

        /// <summary>
        /// A certificate as Fulcio issues one to a workflow: no subject, the workflow as a URI, the token's issuer, the
        /// repository's id, and <paramref name="named"/>'s key as its issuer's (the authority key identifier).
        /// </summary>
        private X509Certificate2 Issue(ECDsa key, string identity, string issuer, bool codeSigning, string? repositoryId, TestSigstore named)
        {
            var request = new CertificateRequest(new X500DistinguishedName(string.Empty), key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddUri(new Uri(identity));
            request.CertificateExtensions.Add(names.Build(critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(codeSigning ? "1.3.6.1.5.5.7.3.3" : "1.3.6.1.5.5.7.3.1")], false));
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.8", Utf8(issuer), false));
            if (repositoryId is not null)
            {
                request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.15", Utf8(repositoryId), false));
            }

            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(named._authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            return request.Create(_authority, Issued, Issued.AddMinutes(10), RandomNumberGenerator.GetBytes(16));

            static byte[] Utf8(string value)
            {
                var writer = new AsnWriter(AsnEncodingRules.DER);
                writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                return writer.Encode();
            }
        }
    }
}
