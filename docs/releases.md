# Verifying a release

Every release on [GitHub Releases](https://github.com/siebe41/SemanticRouter.NET/releases) can be checked, before you install it, to confirm two things: that it was built by this repository's release workflow ([`release.yml`](../.github/workflows/release.yml)) from a tagged commit on `main`, and that the files are the ones the workflow produced. The container image is also signed. None of this needs a key from us: signatures and attestations use short-lived [Sigstore](https://www.sigstore.dev/) certificates issued to the workflow run, and are recorded in Sigstore's public transparency log.

## What a release contains

| Asset | What it is |
|---|---|
| `SHA256SUMS` | The SHA-256 checksum of every other asset. |
| `SemanticRouter-<version>-<platform>.zip` / `.tar.gz`, the installers, the update feed (`releases.<channel>.json`, `*-full.nupkg`, `*-delta.nupkg`) | The desktop app ([Desktop app](desktop.md)). |
| `SemanticRouter.*.<version>.nupkg` | The NuGet packages and the `semantic-router` tool, exactly as pushed to nuget.org. |
| `SemanticRouter.*.<version>.snupkg` | The libraries' symbol packages, as pushed to nuget.org's symbol server: their PDBs, which map to the tagged commit's sources with SourceLink. The tool package carries its PDB inside. |
| `SemanticRouter-<version>-packages.cdx.json` | CycloneDX SBOM of the NuGet packages and the tool. |
| `SemanticRouter-<version>-<platform>.cdx.json` | CycloneDX SBOM of the desktop app's archive for that platform. |
| `SemanticRouter-<version>-<platform>-installer.cdx.json` | CycloneDX SBOM of that platform's installer and update package: the app, plus Velopack's updater, setup and stub and, on Linux, the AppImage runtime, which vpk adds. |
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

A debugger finds the libraries' symbols on nuget.org's symbol server (in Visual Studio, enable **NuGet.org Symbol Server** under Tools > Options > Debugging > Symbols, and turn off **Just My Code**) and steps into the source of the released commit, which SourceLink fetches from GitHub.

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

The image says which release and commit it was built from, in its [OCI labels](https://github.com/opencontainers/image-spec/blob/main/annotations.md) and in `semantic-router version`; the release checks that both, the dashboard and the image's SBOM name the tag's version before it pushes:

```bash
docker image inspect ghcr.io/siebe41/semantic-router:0.3.0 \
  --format '{{index .Config.Labels "org.opencontainers.image.version"}} {{index .Config.Labels "org.opencontainers.image.revision"}}'
docker run --rm ghcr.io/siebe41/semantic-router:0.3.0 version
```

## SBOMs and third-party notices

The SBOMs are [CycloneDX](https://cyclonedx.org/) JSON, generated with [Syft](https://github.com/anchore/syft) from what was built: the NuGet packages' contents, each desktop archive's contents, each platform's full update package as vpk built it (on Linux, the contents of the AppImage in it), and the container image. Feed them to a vulnerability scanner (`grype sbom:SemanticRouter-0.6.0-win-x64-installer.cdx.json`) or your inventory (Dependency-Track and the like).

[`deploy/sbom-complete.cs`](../deploy/sbom-complete.cs) then adds what Syft cannot read, from the NuGet cache that restore filled and without network access:

- **The subject.** `metadata.component` names what the SBOM describes, with the release version and a purl: `pkg:github/siebe41/semanticrouter.net@v<version>` for the packages, `pkg:docker/siebe41/semantic-router@<version>?repository_url=ghcr.io` for the image, and `pkg:generic/SemanticRouter-<platform>[-installer]@<version>?download_url=…` for the desktop app, naming the asset it was made from.
- **A licence for every NuGet component**: the SPDX expression in the package's `.nuspec`, or for a package that names a licence file instead, the one in [`deploy/third-party/extra.json`](../deploy/third-party/extra.json) (as for the notices); this repository's projects get their package licence. Packages carry their NuGet version, not their assembly's (`0.6.0`, not `0.6.0.0`), and the tool is `pkg:nuget/SemanticRouter.Host`. The desktop app's own assembly is not a NuGet package, so it has no purl.
- **The .NET runtime under its package id.** Syft names the runtime the desktop app bundles `runtimepack.Microsoft.NETCore.App.Runtime.<rid>` (and the same for ASP.NET Core), which no advisory matches; the SBOMs name `Microsoft.NETCore.App.Runtime.<rid>`.
- **What no scanner finds**, as `THIRD-PARTY-NOTICES.txt` lists it: the dashboard's fonts and, in the installer SBOMs, Velopack's native binaries (at the Velopack version), Rust's standard library, and the AppImage runtime with the libraries linked into it.

A NuGet component whose licence cannot be found fails the release. CI makes the packages', the image's and the Linux, Windows (win-x64) and macOS (osx-arm64) installers' SBOMs on every pull request, so a new package without one usually fails there first; it does not make the archives' SBOMs or the osx-x64 installer's, so a package that only win-arm64, osx-x64 or linux-arm64 restores can still first fail at a tag. [`deploy/sbom-check.sh`](../deploy/sbom-check.sh) checks each SBOM independently of the generator.

Each week, [Release SBOM scan](../.github/workflows/release-sbom-scan.yml) scans the latest release's packages SBOM and all its desktop SBOMs with [Trivy](https://trivy.dev), and opens an issue asking for a patch release when one names a high or critical vulnerability that has a fix (the container image has its own check, [Image freshness](../.github/workflows/image-freshness.yml)). For releases from before the SBOMs named the runtime's package, it removes the `runtimepack.` prefix first, as you should when you scan one of those.

`THIRD-PARTY-NOTICES.txt` also ships inside everything that redistributes those packages:

| | Where |
|---|---|
| Desktop app, archives and installers | Next to the executable (on macOS, `SemanticRouter.app/Contents/MacOS/`) |
| The `semantic-router` tool | At the package root; once installed, under `~/.dotnet/tools/.store/semanticrouter.host/<version>/semanticrouter.host/<version>/` |
| Container image | `/app/THIRD-PARTY-NOTICES.txt`. The image has no shell: `docker create --name n ghcr.io/siebe41/semantic-router:0.3.0 && docker cp n:/app/THIRD-PARTY-NOTICES.txt . && docker rm n` |

[`deploy/third-party-notices.cs`](../deploy/third-party-notices.cs) writes it at publish time from the build's `*.deps.json` and each package's `.nuspec` and licence files in the NuGet cache, without network access. It adds what no package names, from [`deploy/third-party/extra.json`](../deploy/third-party/extra.json): the Inter and Fira Sans fonts the dashboard embeds (SIL OFL 1.1), and, for the desktop app, Velopack's native updater, setup and stub, Rust's standard library they link, and the AppImage runtime with the libraries linked into it. Every entry has an SPDX licence, and every licence has its text in the file: the package's own licence file, or else the standard text from [`deploy/third-party/licences`](../deploy/third-party/licences). A package that declares a licence file instead of an SPDX expression needs its id in `extra.json`, and a licence without a text fails the generator, so the pull request that adds such a package fails CI. The same packages always give the same file. The container's Ubuntu and ASP.NET Core base layers come from Microsoft's image, with their own licence files.

Velopack's native binaries also statically link several hundred Rust crates, and Velopack publishes no list of them. [`deploy/third-party/velopack-crates.json`](../deploy/third-party/velopack-crates.json) is that list for one Velopack release, and the desktop notices include each crate on the platforms whose binary links it. [`deploy/third-party/velopack-crates.py`](../deploy/third-party/velopack-crates.py) writes it, through `gh api` and with no Rust toolchain. It walks the `velopack_bins` package's normal dependencies in Velopack's `Cargo.lock` at the release tag for each target Velopack builds, using the crates.io index for dependency kinds and targets, and takes each crate's licence and copyright from GitHub's dependency graph (ClearlyDefined's data). The list is a superset: a proc macro, or an optional dependency only another Velopack crate enables, is listed too.

MIT, BSD, ISC and Unicode-3.0 ask binary copies to carry the holder's copyright notice, and ClearlyDefined has no copyright line for many of the crates. A crate that offers another licence that doesn't ask for one (`MIT OR Apache-2.0`) is listed under that one. For the rest, the script reads the copyright lines from the licence files in the crate's repository, which its `COPYRIGHTS` table names, and records the files in `copyrightFrom` (a few name no holder, and the notices say so). The generator fails when a crate's licence needs a copyright line the list doesn't have: add the crate's repository to `COPYRIGHTS` and rerun the script.

When you bump Velopack, bump `vpk` in the workflows to the same version and rerun `python deploy/third-party/velopack-crates.py <version>`. [`deploy/velopack-version-check.sh`](../deploy/velopack-version-check.sh) fails CI and the release until all three match, and the generator refuses a list made for another Velopack version. Dependabot opens Velopack updates as a pull request of their own (the `velopack` group in [`.github/dependabot.yml`](../.github/dependabot.yml)), which stays red until someone pushes the `vpk` pins and the rerun list onto its branch.

The SBOMs don't read `velopack-crates.json`: they list what Syft finds in the built files, so a Rust crate is in an SBOM only if Syft recognises it in Velopack's binaries. The notices are the complete list.

The `SemanticRouter.AspNetCore` package is `MIT AND OFL-1.1`, because its DLL embeds the fonts; it carries their licences under `licenses/`.

### The embedding models

No release contains an ONNX embedding model: the router downloads the one it is configured with on its first start, from a pinned revision, checked by SHA-256 ([Network access](network.md)). Each model is under its authors' licence:

| Model | Downloaded from | Upstream | Licence |
|---|---|---|---|
| `all-MiniLM-L6-v2` (the default) | [`Xenova/all-MiniLM-L6-v2`](https://huggingface.co/Xenova/all-MiniLM-L6-v2), an ONNX export | [`sentence-transformers/all-MiniLM-L6-v2`](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) | Apache-2.0 |
| `bge-small-en-v1.5` | [`Xenova/bge-small-en-v1.5`](https://huggingface.co/Xenova/bge-small-en-v1.5), an ONNX export | [`BAAI/bge-small-en-v1.5`](https://huggingface.co/BAAI/bge-small-en-v1.5) | MIT, upstream's; the export declares no licence of its own |

## Update attestations

The installed desktop app checks every update it downloads from GitHub against the attestation of its `*-full.nupkg` ([What an update is checked against](desktop.md#updates)), much as `gh attestation verify` would, with code of its own and the Sigstore trust root it carries (`src/SemanticRouter.Desktop/Assets/sigstore-trusted-root.json`, a copy of `targets/trusted_root.json` from [sigstore/root-signing](https://github.com/sigstore/root-signing)). It accepts an attestation when:

- its statement is SLSA build provenance (`https://slsa.dev/provenance/v1`) and names the package's file name and SHA-256;
- its certificate was issued by Sigstore's certificate authority (Fulcio) to exactly `https://github.com/siebe41/SemanticRouter.NET/.github/workflows/release.yml@refs/tags/v<version>`, in the repository with GitHub's id 1409353960 (so a repository registered again under this name does not pass), for a GitHub Actions token, for code signing;
- its signature verifies with that certificate;
- its entry in Sigstore's transparency log (Rekor) is about that signature and certificate, carries the log's signed timestamp, and its inclusion proof leads to a checkpoint the log signed, at a time the certificate was valid. An entry in a log that is not in the trust root, or of another kind than `dsse` 0.0.1, is refused: neither the log id nor the kind is signed.

Unlike `gh`, it does not check the certificate's signed certificate timestamp (the certificate transparency log's promise).

So the release workflow's `checksums` job has to keep attesting every asset of the release, the update packages included, from a run for the version's tag; `deploy/update-attestation-check.sh`, in CI and in the release's first job, fails when it no longer does. The same script, with `--online`, fails when Sigstore has published a new trust root (a new certificate authority or log) that the app does not carry yet: copy it in as the script says and commit it, so that the next release carries it. An app checks the release after it with the trust root of its own version, so the trust root must be current at each release.

Releases from 0.5.0 on have these attestations. A missing or failing attestation refuses the update. When the check cannot run (GitHub's API does not answer, or the attestation's only log entry is in Sigstore's newer Rekor v2, whose Ed25519 key this check does not read yet), the app does not apply the update and tries again later; so if GitHub moves attestations to Rekor v2, installed apps stop updating until a release that reads it is installed by hand. An app whose trust root does not have the certificate authority that issued the attestation's certificate (one Sigstore added after that app was released) says it can no longer check updates.

## Cutting a release

For the maintainer. A release is published by one workflow run, which only the repository owner can start:

1. **Optional:** rename **Unreleased** in the [changelog](../CHANGELOG.md) to `[<version>] - <date>` in a pull request, and merge it. The release notes are that section; without it, they are the Unreleased section.
2. Wait for CI to pass on `main`'s latest commit.
3. **Actions > Cut a release > Run workflow**, on `main`, with the version (`0.3.0`: three numbers, no `v`). Normally that is `VersionPrefix` in [`Directory.Build.props`](../Directory.Build.props), the version CI has been packing. Tick **Allow unsigned installers** only while a platform has no signing certificates yet ([Code signing](#code-signing)).
4. Approve the `release` environment when asked (if it requires a reviewer). GitHub asks for each job that uses it, as it comes up: once in this run, then in the release run for `packages`, `desktop`, each installer job, `checksums`, `image`, `image-sign`, `nuget` and `publish`. Rejecting one after `image` has run leaves the image tags pushed ([When a release fails](#when-a-release-fails)).
5. Once the release run's last job (`publish`) has published the release and nuget.org lists the new packages, move the package validation baseline and the next version on ([After a release](#after-a-release)). If the run fails instead, see [When a release fails](#when-a-release-fails).

[`cut-release.yml`](../.github/workflows/cut-release.yml) refuses to tag when:
- it wasn't started by the owner, or not from `main`;
- the version isn't a new `X.Y.Z` above the latest release;
- CI hasn't passed on that commit;
- code signing or NuGet publishing isn't set up (the same checks the release makes, so a release that would fail never gets a tag);
- a platform has no signing secrets and **Allow unsigned installers** wasn't ticked, even with `ALLOW_UNSIGNED_RELEASE` set.

Otherwise it creates the tag `v<version>` on `main`'s latest commit. That tag starts [`release.yml`](../.github/workflows/release.yml), which builds, tests and signs everything into a draft GitHub release with its notes, the assets and `SHA256SUMS`, and only then pushes the image and the NuGet packages and publishes the release ([The order of a release](#the-order-of-a-release)). Follow it under **Actions > Release**. It also refuses a tag that someone other than the owner pushed.

**After each release,** add its upgrade fixture in a pull request: `tools/upgrade-fixtures/make-fixture.sh <version>` installs the release from nuget.org into a throwaway home, sets it up and uses it through its own API against a stub server, and writes `tests/fixtures/upgrade/<version>.zip`. `There_is_a_fixture_for_every_release` fails until it is there: it takes the released versions from the changelog's version headings and the package validation baseline. `UpgradeFromReleasedVersionsTests` then starts every build on every released version's home, on every pull request: it fails when the build cannot start on one, loses a row, cannot decrypt the release's API key, refuses its gateway key, cannot roll back to its first configuration, or leaves its template behind. The installer jobs install the desktop app on the newest release's home and check its usage is still there after the update. Every night the **Downgrade** workflow (`tests/installer/downgrade.sh`) migrates that home with the current build, then starts the newest published release on it, which must refuse or report the newer database, and on the copy the upgrade kept, where it must find the home's usage again.

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
  - Limit its deployment branches and tags to `main` (where "Cut a release" and the signing dry run run) and `v*` tags (where the release runs). This, not a check inside a workflow, is what keeps a branch's copy of a workflow away from the signing secrets.
  - Add yourself as a required reviewer to approve each release before anything is published.
- **The `windows-signing` environment**, with the same deployment rule, for the Windows signing login ([Signing in to Azure](desktop.md#signing-in-to-azure)).
- **`NUGET_USER`** and the signing settings, or `ALLOW_UNSIGNED_RELEASE`, as described in [Code signing](#code-signing) and [Signing the desktop app](desktop.md#signing).

### The order of a release

`release.yml` makes nothing public until everything is built, signed and checked. A job that fails before the last stage leaves a draft release (and, after `checksums`, its provenance attestation) and nothing else: no package on nuget.org, no new image tag, no public release, and no update for installed desktop apps.

1. **Build and check.** `packages` checks who released and that the tag is on `main`, checks code signing, runs the tests and packs the NuGet packages. The image is built, scanned and given its SBOM (`image-build`, `image-scan`, `image-sbom`), and the desktop app is built for every platform (`desktop-build`, `installer-build`). None of these jobs can write anywhere.
2. **The draft release.** `desktop` creates the GitHub release as a **draft**, with its notes, the archives, their SBOMs and `THIRD-PARTY-NOTICES.txt`. Only people with write access to the repository see a draft; the desktop app's updater does not.
3. **The installers.** `installers` (Linux), `installer-windows` and `installer-macos` pack the installers and update packages, sign them (on macOS also notarize), check each signature, and upload them with their update feeds to the draft, which vpk leaves a draft. `installer-sbom` makes their SBOMs.
4. **Checksums.** `checksums` checks that the draft has every platform's update feed and `THIRD-PARTY-NOTICES.txt`, attaches the NuGet packages and the remaining SBOMs, writes `SHA256SUMS` over every asset and makes the build provenance attestation. The attestation names only the files' digests, but it is public from here on: it is written to the public Sigstore log and the repository's attestations, and stays there if the version is given up.
5. **Publishing**, one job after another:
   1. `image` pushes `:<version>`, and for a stable version `:latest`, to GHCR;
   2. `image-sign` signs the image and attaches its provenance and SBOM attestations;
   3. `nuget` pushes the packages to nuget.org;
   4. `publish` checks that the draft has every platform's update feed, `THIRD-PARTY-NOTICES.txt` and `SHA256SUMS`, and publishes it, as the repository's latest release for a stable version.

The image goes before NuGet because an image tag can be deleted or moved back, but a version on nuget.org can never be pushed again. The release is published last because that is when installed desktop apps start to update. [`deploy/release-order-check.sh`](../deploy/release-order-check.sh), which CI runs on every pull request, fails when a job that publishes doesn't wait for every job that doesn't (each build, check, SBOM and upload to the draft), when the release is created already published, or when vpk is asked to publish it.

### When a release fails

Open the run under **Actions > Release** and read the failed job's log. The usual causes are a missing or wrong secret or variable, an expired certificate or password, or an outage at Apple's notary service or Azure Trusted Signing. Then do one of the following.

- **Nothing in the repository has to change** (an outage, or a secret or variable you have since corrected): use **Re-run failed jobs** on the same run. The jobs that succeeded are kept, and the rest continue from there into the same draft. Do it within a week: the image, its SBOM and the installers' update packages are kept as artifacts for 7 days.
  - An installer job that failed after it had started uploading cannot upload the same files again: GitHub refuses an asset name that is already there, and vpk refuses an update feed that is already there. Delete that platform's files from the draft first (`gh release delete-asset v0.6.0 <name>`, or edit the draft on the Releases page), then re-run.
- **The code has to change**: give up the version. Delete the draft (`gh release delete v0.6.0`). Leave the tag: once the `v*` tag ruleset is applied ([CONTRIBUTING.md](../CONTRIBUTING.md#branch-and-tag-rulesets)) it cannot be deleted, and a version that was tagged is not reused either way. Merge the fix and cut the next patch version. Nothing was published, so nobody has the version you gave up. Delete the draft rather than leaving it: the installer jobs look for the previous version's update package (for the delta update) in the latest releases, drafts included.
- **A publishing job failed** (`image`, `image-sign`, `nuget` or `publish`): what was already pushed stays public, and every installer is signed and attached. Re-run the failed jobs. Pushing and signing the image again is harmless, `nuget` skips packages that are already on nuget.org, and publishing a release that is already public changes nothing. If the version cannot be finished, unlist its packages on nuget.org, and move `:latest` back to the previous version with `docker buildx imagetools create -t ghcr.io/siebe41/semantic-router:latest ghcr.io/siebe41/semantic-router:<previous>`. Then delete the draft and release the next patch version.

### After a release

`dotnet pack` validates each library package against the last release on nuget.org, the baseline, and fails when a public API was removed or changed ([CONTRIBUTING.md](../CONTRIBUTING.md#public-api-and-package-validation)). After each release, in one pull request:

1. Set `PackageValidationBaselineVersion` in [`Directory.Build.targets`](../Directory.Build.targets) to the version just released. Restore downloads it from nuget.org, so wait until nuget.org lists the packages.
2. Set `VersionPrefix` in [`Directory.Build.props`](../Directory.Build.props) to the next version: `0.7.0` after `0.6.0`, or `1.0.0` when that is next. CI packs without a version, so its packages carry `VersionPrefix`, which must be above the baseline: below it, every pack fails with CP0003.
3. Delete every `src/*/CompatibilitySuppressions.xml`. The breaks they approved are in the new baseline, and a suppression that no longer matches anything fails the pack ("Unnecessary suppressions found").
4. In each `src/*/PublicAPI.Unshipped.txt`, move the lines to the project's `PublicAPI.Shipped.txt`: add each new line to it, and for each `*REMOVED*` line delete the line it names from it. Leave `PublicAPI.Unshipped.txt` with only its first line, `#nullable enable` ([CONTRIBUTING.md](../CONTRIBUTING.md#public-api-and-package-validation)).
5. Run `dotnet build -c Release` and `dotnet pack -c Release`; both must pass, with no RS or CP diagnostic. CI runs the same on the pull request.

## Code signing

Windows SmartScreen and macOS Gatekeeper check a publisher's code signature, not these attestations. Provenance shows where a file came from; it does not stop the warnings on first start.

The release workflow signs the Windows installer with Azure Trusted Signing and signs and notarizes the macOS installers ([Signing the desktop app](desktop.md#signing)). A release fails, before anything is published, when a platform's signing is not set up, unless the maintainer has set the repository variable `ALLOW_UNSIGNED_RELEASE` to `true` and, when cutting the release, ticked **Allow unsigned installers** ("Cut a release" asks for both; a tag pushed by hand needs only the variable). Until the signing certificates exist, that is what lets a release through, and those installers are unsigned. A signature, notarization or check that fails stops the release while it is still a draft, before anything is published ([When a release fails](#when-a-release-fails)). The workflow run's summary says, per installer, whether it was signed, and a platform that is meant to be signed has each signature checked before anything is uploaded.

What is checked covers the code that ends up running, not just the installer, and the update package the installed app updates to (the `-full.nupkg` on the release):

| Platform | Checked | How |
| --- | --- | --- |
| Windows | `SemanticRouter-win-x64-Setup.exe` | `Get-AuthenticodeSignature`: `Valid`. Its signer is taken as the publisher for the rows below: that certificate, or any with the same subject, since Azure Trusted Signing renews its short-lived certificates often |
| Windows | `SemanticRouter.exe`, `Squirrel.exe` (Velopack's updater), `SemanticRouter_ExecutionStub.exe` and `SemanticRouter.Files.dll` (the signed data-file list) in the update package's `lib/app/`, and the app's own `SemanticRouter.*` files there | `Get-AuthenticodeSignature`: `Valid`, signed by the publisher |
| Windows | every other executable file in `lib/app/` (a program or library, by its `MZ` header) | `Get-AuthenticodeSignature`: `Valid`, signed by the publisher or by Microsoft |
| macOS | `SemanticRouter-<rid>-Setup.pkg` | `pkgutil --check-signature`, signed by the team, and `xcrun stapler validate` for the notarization ticket |
| macOS | the `.app` inside the `.pkg` (`pkgutil --expand-full`) | `codesign --verify --deep --strict` with the team's Developer ID signature, and `spctl --assess` (Gatekeeper) reporting `source=Notarized Developer ID` |
| macOS | the `.app` in the update package | `codesign --verify --deep --strict` with the team's Developer ID signature |

These are the signature rules the desktop app applies to an update when `Updates:Publisher` is set ([UpdatePublisher](../src/SemanticRouter.Desktop/UpdatePublisher.cs)), with the publisher taken from the release itself: the installer's signer on Windows, the repository's Apple team ID (`APPLE_TEAM_ID`) on macOS. The app's check also refuses .NET host files (`*.runtimeconfig.json`, `*.deps.json`) that point outside the package; the release does not check those.

The checks are [`deploy/installer-signature-check.ps1`](../deploy/installer-signature-check.ps1) and [`deploy/installer-signature-check.sh`](../deploy/installer-signature-check.sh). On a platform that is meant to be signed, any check that does not pass fails the release; otherwise each line in the summary says **unsigned**. A file that is missing fails either way. CI runs both on its unsigned installer builds, so a change in what Velopack puts where is caught before a release.

### The signing dry run

[`signing-dry-run.yml`](../.github/workflows/signing-dry-run.yml) signs a throw-away build with the release's own secrets and checks the signatures, but uploads nothing except the macOS notarization submission to Apple: no release, no update feed, no artifact of what it signed, and its token can only read. It runs the release's own scripts, so it signs and checks exactly as a release would: [`deploy/signing-setup.sh`](../deploy/signing-setup.sh) writes the Trusted Signing metadata, or creates the keychain and the `notarytool` profile; `deploy/package-installer.sh` has vpk sign, notarize and staple; [`deploy/check-signature-windows.ps1`](../deploy/check-signature-windows.ps1) and [`deploy/check-signature-macos.sh`](../deploy/check-signature-macos.sh) check the signatures. It builds `win-x64` and `osx-arm64` as version `0.0.0-dryrun.<run number>`, in a job without secrets, and signs them in the environments the release signs in (`windows-signing` for Windows, `release` for macOS). Each signing job checks again that the owner started the run from `main`, so re-running that job alone is checked too; what keeps a branch's copy of the workflow away from the secrets is the environments' deployment rules (`main` and `v*` tags only, [Cutting a release](#cutting-a-release)). A platform whose secrets or variables are missing fails the run and names them: unlike a release, the dry run has no unsigned mode.

**Actions > Signing dry run > Run workflow**, on `main`, with **both**, **windows** or **macos**. Only the repository owner can run it. The run's summary names the certificate that signed each installer. Run it, and have it pass:
- on both platforms, before you delete `ALLOW_UNSIGNED_RELEASE`: the first signed release then is not the first run of the signing steps;
- for a platform, after you rotate or renew any of its secrets, certificates or the app-specific password, and before the next release.

A failure points to the same fixes as a failed release's signing job ([When a release fails](#when-a-release-fails)), with no tag, version or draft to clean up. [`deploy/signing-dry-run-check.sh`](../deploy/signing-dry-run-check.sh), which CI runs, fails when the dry run could upload or publish anything, asks for more than `contents: read` (only its `windows` job may add `id-token: write`, for the Azure login; `write-all` and `read-all` fail), checks a signature without requiring one, or runs other than by hand. It also fails when the release and the dry run stop calling the shared scripts, when a workflow signs or checks a signature inline instead, or when they install different vpk versions or log in to Azure with different `azure/login` versions.

To check an installer you downloaded:

```powershell
Get-AuthenticodeSignature .\SemanticRouter-win-x64-Setup.exe     # Status: Valid when signed
```

```bash
pkgutil --check-signature SemanticRouter-osx-arm64-Setup.pkg     # "Status: signed by a developer certificate ..."
```

The Linux AppImage and the `.zip` and `.tar.gz` archives are not code-signed; check them with `gh attestation verify` as above.

### Where the secrets are

Each secret goes to the steps that use it, never to a whole job or workflow, and the jobs that build hold none. Where a federated (OIDC) login exists, the release uses it instead of a stored key:

| What | Stored secret | Which steps get it |
|---|---|---|
| Windows signing (Azure Trusted Signing) | none: an OIDC login under a federated credential for the `windows-signing` environment, which only `installer-windows` and the dry run's `windows` job use ([Signing in to Azure](desktop.md#signing-in-to-azure)); the tenant and app ids are kept as secrets but grant nothing | the ids: the signing setup and the `azure/login` step of those two jobs. The OIDC token: any step of those jobs could ask for one (`id-token: write` is job-wide), so they run only vpk, pinned actions and this repository's scripts, and log out of Azure right after signing |
| macOS signing and notarization | the two `.p12` certificates, their password, the Apple ID and its app-specific password, the team id | the **Signing keychain** step of `installer-macos` and of the dry run's `macos` job; later steps use the temporary keychain |
| nuget.org | none: Trusted Publishing, an OIDC exchange for a key that lasts an hour | the `nuget` job's login step |
| Container image signature | none: keyless cosign (Sigstore, OIDC) | `image-sign` |
| The workflow's own token (`contents: write` where a job attaches to the release) | issued per run | the steps that call `gh` or `vpk upload` / `vpk download`, and the registry logins |
| `RELEASE_TAG_TOKEN` (the owner's token that creates the tag) | a fine-grained token | the tag step of "Cut a release" |

The release's first job, which runs the tests and packs, sees only whether each signing secret is set (`true` or `false`), never its value. The signing jobs build nothing: they install vpk first, before any step that holds a secret or logs in, because a login and a keychain outlast the step that made them.

[`deploy/workflow-secrets-check.sh`](../deploy/workflow-secrets-check.sh), which CI runs on every pull request, keeps it so in every workflow. It fails when a job's or a workflow's `env:` holds a secret or the workflow's token (a test such as `secrets.X != ''`, which passes only whether it is set, is allowed), when a job that builds (`dotnet restore`, `build`, `publish`, `test` or `pack`, `deploy/publish-desktop.sh`, or `deploy/package-installer.sh` without `PUBLISH_DIR`) holds a secret in any step or can ask for an OIDC token, and when a step runs `dotnet` while holding a secret or after a step that held one or logged in to Azure. A secret is any use of the `secrets` context (`secrets.X`, `secrets['X']`, `toJSON(secrets)`) or `github.token`. It also fails when a workflow, or a job not on its list, asks for an OIDC token (`id-token: write`, or `write-all`), and when a job other than the two that sign Windows binaries runs in `windows-signing`. A credential that an action makes at run time and passes on as a step output (NuGet/login's API key) is not one it can see. On GitHub Actions, `deploy/package-installer.sh` also refuses to build while a token or a signing setting is in its environment.

## Not covered yet

- **Code signing until the certificates exist.** While `ALLOW_UNSIGNED_RELEASE` is set, the installers are unsigned and SmartScreen and Gatekeeper warn on first start ([Code signing](#code-signing)). The archives' contents are not signed in any case.
- **NuGet author signatures.** Only nuget.org's repository signature, as above.
- **Federated credentials on macOS.** Apple has no OIDC login: the macOS signing certificates and the Apple ID's app-specific password are stored secrets, given only to the step that sets up signing ([Where the secrets are](#where-the-secrets-are)). An App Store Connect API key could replace the app-specific password, but it is a stored key too.
- **Signing with the production credentials.** The [signing dry run](#the-signing-dry-run) exists, but it can't pass until the certificates exist ([#102](https://github.com/siebe41/SemanticRouter.NET/issues/102)), so the signing steps have never run with real certificates. Until it passes on both platforms, keep `ALLOW_UNSIGNED_RELEASE`.
