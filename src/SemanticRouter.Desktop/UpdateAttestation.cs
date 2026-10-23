using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>
/// The check of an update from GitHub Releases when the machine policy names no publisher: the downloaded package must
/// be attested by this project's release workflow, at the tag of the version it is offered as. The release's
/// <c>checksums</c> job attests every asset, the <c>*-full.nupkg</c> packages included, and GitHub keeps the attestations
/// apart from the release's assets. The app asks GitHub's attestations API for the package's SHA-256 (no token: the
/// repository is public) and checks the Sigstore bundle it gets (<see cref="SigstoreBundle"/>) against the trust root
/// it carries (<see cref="SigstoreTrustRoot.PublicGood"/>). So whoever can change a release's assets, its packages or
/// its <c>releases.&lt;channel&gt;.json</c>, cannot change the app unless they can also run the release workflow for a
/// new version tag.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Outcome.Verified"/>: an attestation checks out.</item>
/// <item><see cref="Outcome.Refused"/>: the check ran and failed. GitHub has no attestation for the package, or none
/// that checks out (another workflow or tag, a signature or log entry that does not verify).</item>
/// <item><see cref="Outcome.Unchecked"/>: the check could not run. GitHub's API could not be asked (offline,
/// rate-limited, an error, an answer without a bundle), or the only attestation that otherwise checks out has a
/// transparency log entry in a log of the trust root whose key this app cannot check. The update is not applied; it is
/// kept and checked again later.</item>
/// <item><see cref="Outcome.Outdated"/>: the only attestation that otherwise checks out has a certificate from an
/// authority the trust root this version carries does not have (Sigstore added one since). This version cannot check
/// that update, or later ones: it is not applied, and the person has to install the new version themselves.</item>
/// </list>
/// </remarks>
internal sealed class UpdateAttestation(string repository, string repositoryId, Func<string, CancellationToken, Task<string?>> lookup, SigstoreTrustRoot trust)
{
    internal enum Outcome
    {
        Verified,
        Unchecked,
        Outdated,
        Refused,
    }

    internal readonly record struct Verdict(Outcome Outcome, string Detail);

    /// <summary>The workflow that attests the release's assets.</summary>
    public const string Workflow = ".github/workflows/release.yml";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The check for updates from the releases of <paramref name="repositoryUrl"/> (GitHub id <paramref name="repositoryId"/>), through GitHub's API.</summary>
    public static UpdateAttestation ForGitHub(string repositoryUrl, string repositoryId)
    {
        string repository = new Uri(repositoryUrl).AbsolutePath.Trim('/');
        return new UpdateAttestation(repository, repositoryId, (sha256, cancellationToken) => AskGitHubAsync(s_http, repository, sha256, cancellationToken), SigstoreTrustRoot.PublicGood);
    }

    /// <summary>The identity a release's attestations are made with: the release workflow, run for the version's tag.</summary>
    public string Identity(string version) => $"https://github.com/{repository}/{Workflow}@refs/tags/v{version}";

    /// <summary>Checks the downloaded <paramref name="package"/>, offered as the release asset <paramref name="name"/> of <paramref name="version"/>.</summary>
    public async Task<Verdict> CheckAsync(string package, string name, string version, CancellationToken cancellationToken = default)
    {
        string sha256;
        try
        {
            using FileStream stream = File.OpenRead(package);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Verdict(Outcome.Unchecked, $"its package cannot be read ({ex.Message})");
        }

        return await CheckDigestAsync(sha256, name, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks the release asset <paramref name="name"/> of <paramref name="version"/>, whose SHA-256 is <paramref name="sha256"/>.</summary>
    internal async Task<Verdict> CheckDigestAsync(string sha256, string name, string version, CancellationToken cancellationToken = default)
    {
        string? response;
        try
        {
            response = await lookup(sha256, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new Verdict(Outcome.Unchecked, $"GitHub's attestations API could not be asked ({ex.Message})");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Verdict(Outcome.Unchecked, "GitHub's attestations API did not answer in time");
        }

        string missing = $"GitHub has no attestation for it (SHA-256 {sha256}), so it is not a package {repository}'s release workflow built";
        if (response is null)
        {
            return new Verdict(Outcome.Refused, missing);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response);
        }
        catch (JsonException)
        {
            return new Verdict(Outcome.Unchecked, "GitHub's attestations API answered with something other than JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("attestations", out JsonElement attestations)
                || attestations.ValueKind != JsonValueKind.Array)
            {
                return new Verdict(Outcome.Unchecked, "GitHub's attestations API answered without a list of attestations");
            }

            string identity = Identity(version);
            (string? failed, string? unlogged, string? outdated, bool unread) = (null, null, null, false);
            foreach (JsonElement attestation in attestations.EnumerateArray())
            {
                if (attestation.ValueKind != JsonValueKind.Object || !attestation.TryGetProperty("bundle", out JsonElement bundle) || bundle.ValueKind != JsonValueKind.Object)
                {
                    // GitHub can leave a bundle out and link to it instead (bundle_url); the app does not fetch it.
                    unread = true;
                    continue;
                }

                SigstoreBundle.Result result = SigstoreBundle.Verify(bundle, trust, identity, repositoryId, SigstoreBundle.GitHubActions, name, sha256);
                switch (result.Outcome)
                {
                    case SigstoreBundle.Outcome.Verified:
                        return new Verdict(Outcome.Verified, $"its attestation is {result.Detail}");
                    case SigstoreBundle.Outcome.VerifiedWithoutLog:
                        unlogged ??= result.Detail;
                        break;
                    case SigstoreBundle.Outcome.UnknownAuthority:
                        outdated ??= result.Detail;
                        break;
                    default:
                        failed ??= result.Detail;
                        break;
                }
            }

            return outdated is not null ? new Verdict(Outcome.Outdated, $"its attestation is {outdated}")
                : unlogged is not null ? new Verdict(Outcome.Unchecked, $"its attestation is {unlogged}")
                : failed is not null ? new Verdict(Outcome.Refused, $"its attestation does not hold: {failed}")
                : unread ? new Verdict(Outcome.Unchecked, "GitHub's attestations API gave no attestation the app can read")
                : new Verdict(Outcome.Refused, missing);
        }
    }

    /// <summary>
    /// GitHub's attestations for <paramref name="sha256"/> in <paramref name="repository"/> (the API's JSON), or
    /// <see langword="null"/> when there are none. Throws <see cref="HttpRequestException"/> when GitHub cannot be asked.
    /// </summary>
    internal static async Task<string?> AskGitHubAsync(HttpClient http, string repository, string sha256, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{repository}/attestations/sha256:{sha256}?per_page=100"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("SemanticRouter");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            bool limited = response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? left) && left.FirstOrDefault() == "0");
            throw new HttpRequestException(limited ? "GitHub's rate limit for this address is used up" : $"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
