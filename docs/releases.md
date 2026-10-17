# Verifying a release

Every release on [GitHub Releases](https://github.com/siebe41/SemanticRouter.NET/releases) can be checked, before you install it, to confirm two things: that it was built by this repository's release workflow ([`release.yml`](../.github/workflows/release.yml)) from a tagged commit on `main`, and that the files are the ones the workflow produced. The container image is also signed. None of this needs a key from us: signatures and attestations use short-lived [Sigstore](https://www.sigstore.dev/) certificates issued to the workflow run, and are recorded in Sigstore's public transparency log.

## What a release contains

| Asset | What it is |
|---|---|
| `SHA256SUMS` | The SHA-256 checksum of every other asset. |
| `SemanticRouter-<version>-<platform>.zip` / `.tar.gz`, the installers, the update feed (`releases.<channel>.json`, `*-full.nupkg`, `*-delta.nupkg`) | The desktop app ([Desktop app](desktop.md)). |
| `SemanticRouter.*.<version>.nupkg` | The NuGet packages and the `semantic-router` tool, exactly as pushed to nuget.org. |
| `SemanticRouter-<version>-packages.cdx.json` | CycloneDX SBOM of the NuGet packages and the tool. |
| `SemanticRouter-<version>-desktop.cdx.json` | CycloneDX SBOM of the desktop app, for every platform (the archives and installers ship the same app). |
| `SemanticRouter-<version>-image.cdx.json` | CycloneDX SBOM of the container image. It is also attached to the image as a signed attestation. |
| `THIRD-PARTY-NOTICES.txt` | The licence, copyright and notices of every third-party package the release ships, the .NET runtime included. |

Every asset, `SHA256SUMS` included, has a build provenance attestation: a signed record of the repository, commit, tag and workflow run that produced it ([SLSA provenance](https://slsa.dev/spec/v1.0/provenance)). The repository's **Attestations** page lists them.

## Check a download

You need the [GitHub CLI](https://cli.github.com/) 2.49 or later, signed in (`gh auth login`); verification reads the attestations through the GitHub API.

```bash
gh attestation verify SemanticRouter-0.3.0-linux-x64.tar.gz --repo siebe41/SemanticRouter.NET
```

It prints `Verification succeeded!` with the workflow and commit that built the file, or fails if the file was changed or did not come from this repository. Add `--signer-workflow siebe41/SemanticRouter.NET/.github/workflows/release.yml` to also require that the release workflow built it.

To check several files at once, verify `SHA256SUMS` the same way and then the files against it:

```bash
gh release download v0.3.0 --repo siebe41/SemanticRouter.NET --pattern SHA256SUMS
gh attestation verify SHA256SUMS --repo siebe41/SemanticRouter.NET
sha256sum -c SHA256SUMS --ignore-missing                    # Linux
shasum -a 256 -c SHA256SUMS --ignore-missing                # macOS
```

On Windows, compare `(Get-FileHash .\SemanticRouter-win-x64-Setup.exe -Algorithm SHA256).Hash` with the line for that file in `SHA256SUMS` (the case of the letters differs).

A checksum alone only shows the download is intact; it proves where the file came from once `SHA256SUMS` itself has been verified with `gh attestation verify`.

### NuGet packages

The `.nupkg` files on the release are the packages as the workflow built them, and verify as above. nuget.org adds its own repository signature to every package it serves, which changes the file, so a package downloaded from nuget.org (or your NuGet cache) does not match the release's attestation or checksum. Check that copy's nuget.org signature instead:

```bash
dotnet nuget verify --all SemanticRouter.Host.0.3.0.nupkg
```

The packages are not author-signed.

## Check the container image

The image (`ghcr.io/siebe41/semantic-router`) is signed with keyless [cosign](https://docs.sigstore.dev/cosign/) and carries provenance and SBOM attestations in the registry. With cosign 3 or later:

```bash
cosign verify ghcr.io/siebe41/semantic-router:0.3.0 \
  --certificate-identity-regexp '^https://github\.com/siebe41/SemanticRouter\.NET/\.github/workflows/release\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The output names the image digest that was verified; `docker pull ghcr.io/siebe41/semantic-router@sha256:<digest>` then runs exactly that image, whatever the tag points to later. With the GitHub CLI:

```bash
gh attestation verify oci://ghcr.io/siebe41/semantic-router:0.3.0 --repo siebe41/SemanticRouter.NET
gh attestation verify oci://ghcr.io/siebe41/semantic-router:0.3.0 --repo siebe41/SemanticRouter.NET \
  --predicate-type https://cyclonedx.org/bom    # the SBOM attestation; add --format json to print it
```

## SBOMs and third-party notices

The SBOMs are [CycloneDX](https://cyclonedx.org/) JSON, generated with [Syft](https://github.com/anchore/syft) from what was built: the desktop archives' contents, the NuGet packages' contents and the container image. Feed them to a vulnerability scanner (`grype sbom:SemanticRouter-0.3.0-desktop.cdx.json`) or your inventory.

`THIRD-PARTY-NOTICES.txt` also ships inside everything that redistributes those packages:

| | Where |
|---|---|
| Desktop app, archives and installers | Next to the executable (on macOS, `SemanticRouter.app/Contents/MacOS/`) |
| The `semantic-router` tool | At the package root; once installed, under `~/.dotnet/tools/.store/semanticrouter.host/<version>/semanticrouter.host/<version>/` |
| Container image | `/app/THIRD-PARTY-NOTICES.txt`. The image has no shell: `docker create --name n ghcr.io/siebe41/semantic-router:0.3.0 && docker cp n:/app/THIRD-PARTY-NOTICES.txt . && docker rm n` |

[`deploy/third-party-notices.cs`](../deploy/third-party-notices.cs) writes it at publish time from the build's `*.deps.json` and each package's `.nuspec` and licence files in the NuGet cache, without network access. The same packages always give the same file. The container's Ubuntu and ASP.NET Core base layers come from Microsoft's image, with their own licence files.

## Cutting a release

For the maintainer. A release is published by one workflow run, which only the repository owner can start:

1. **Optional:** rename **Unreleased** in the [changelog](../CHANGELOG.md) to `[<version>] - <date>` in a pull request, and merge it. The release notes are that section; without it, they are the Unreleased section.
2. Wait for CI to pass on `main`'s latest commit.
3. **Actions > Cut a release > Run workflow**, on `main`, with the version (`0.3.0`: three numbers, no `v`). Normally that is `VersionPrefix` in [`Directory.Build.props`](../Directory.Build.props), the version CI has been packing.
4. Approve the `release` environment when asked (if it requires a reviewer), once for this run and once for the release run it starts.
5. Once nuget.org lists the new packages, move the package validation baseline and the next version on ([After a release](#after-a-release)).

[`cut-release.yml`](../.github/workflows/cut-release.yml) refuses to tag when:
- it wasn't started by the owner, or not from `main`;
- the version isn't a new `X.Y.Z` above the latest release;
- CI hasn't passed on that commit;
- code signing or NuGet publishing isn't set up (the same checks the release makes, so a release that would fail never gets a tag).

Otherwise it creates the tag `v<version>` on `main`'s latest commit. That tag starts [`release.yml`](../.github/workflows/release.yml), which builds, tests, signs and publishes everything, creates the GitHub release with its notes, and attaches the assets and `SHA256SUMS`. Follow it under **Actions > Release**. It also refuses a tag that someone other than the owner pushed.

**After each release,** add its upgrade fixture in a pull request: `tools/upgrade-fixtures/make-fixture.sh <version>` installs the release from nuget.org into a throwaway home, sets it up and uses it through its own API against a stub server, and writes `tests/fixtures/upgrade/<version>.zip`. `UpgradeFromReleasedVersionsTests` then starts every build on every released version's home, on every pull request: it fails when the build cannot start on one, loses a row, cannot decrypt the release's API key, refuses its gateway key, cannot roll back to its first configuration, or leaves its template behind.

**A release candidate** (`v1.0.0-rc.1`) is a tag you push yourself on a commit on `main`: "Cut a release" makes stable versions only. `release.yml` publishes a version with a suffix as a prerelease everywhere:
- the GitHub release and the desktop update feeds in it are marked as a prerelease, so installed desktop apps, which only take stable releases, never update to it;
- the image is pushed as `:1.0.0-rc.1` only, never as `:latest`;
- NuGet lists the packages as prereleases, so `dotnet tool update` skips them without `--prerelease`.

**One-time setup:**
- **A token to create the tag.** A tag created with a workflow's own token does not start another workflow, and wouldn't be the owner's, so the tag is created with a token of yours.
  - Create a [fine-grained personal access token](https://github.com/settings/personal-access-tokens/new) for this repository only, with **Contents: Read and write** and nothing else.
  - Store it as the secret `RELEASE_TAG_TOKEN` in the `release` environment (**Settings > Environments > release > Environment secrets**).
  - When it expires, "Cut a release" fails at its last step, before anything is tagged; replace the secret and run it again.
- **The `release` environment:**
  - If you limit its deployment branches and tags, allow both `main` (where "Cut a release" runs) and `v*` tags (where the release runs).
  - Add yourself as a required reviewer to approve each release before anything is published.
- **`NUGET_USER`** and the signing settings, or `ALLOW_UNSIGNED_RELEASE`, as described in [Code signing](#code-signing) and [Signing the desktop app](desktop.md#signing).

### After a release

`dotnet pack` validates each library package against the last release on nuget.org, the baseline, and fails when a public API was removed or changed ([CONTRIBUTING.md](../CONTRIBUTING.md#public-api-and-package-validation)). After each release, in one pull request:

1. Set `PackageValidationBaselineVersion` in [`Directory.Build.targets`](../Directory.Build.targets) to the version just released. Restore downloads it from nuget.org, so wait until nuget.org lists the packages.
2. Set `VersionPrefix` in [`Directory.Build.props`](../Directory.Build.props) to the next version: `0.7.0` after `0.6.0`, or `1.0.0` when that is next. CI packs without a version, so its packages carry `VersionPrefix`, which must be above the baseline: below it, every pack fails with CP0003.
3. Delete every `src/*/CompatibilitySuppressions.xml`. The breaks they approved are in the new baseline, and a suppression that no longer matches anything fails the pack ("Unnecessary suppressions found").
4. Run `dotnet pack -c Release`; it must pass with no CP diagnostic. CI runs the same pack on the pull request.

## Code signing

Windows SmartScreen and macOS Gatekeeper check a publisher's code signature, not these attestations. Provenance shows where a file came from; it does not stop the warnings on first start.

The release workflow signs the Windows installer with Azure Trusted Signing and signs and notarizes the macOS installers ([Signing the desktop app](desktop.md#signing)). A release fails, before anything is published, when a platform's signing is not set up, unless the maintainer has set the repository variable `ALLOW_UNSIGNED_RELEASE` to `true`; until the signing certificates exist, that variable is what lets a release through, and those installers are unsigned. The workflow run's summary says, per installer, whether it was signed, and a platform that is meant to be signed has each signature checked before anything is uploaded.

To check an installer you downloaded:

```powershell
Get-AuthenticodeSignature .\SemanticRouter-win-x64-Setup.exe     # Status: Valid when signed
```

```bash
pkgutil --check-signature SemanticRouter-osx-arm64-Setup.pkg     # "Status: signed by a developer certificate ..."
```

The Linux AppImage and the `.zip` and `.tar.gz` archives are not code-signed; check them with `gh attestation verify` as above.

## Not covered yet

- **Code signing until the certificates exist.** While `ALLOW_UNSIGNED_RELEASE` is set, the installers are unsigned and SmartScreen and Gatekeeper warn on first start ([Code signing](#code-signing)). The archives' contents are not signed in any case.
- **NuGet author signatures.** Only nuget.org's repository signature, as above.
