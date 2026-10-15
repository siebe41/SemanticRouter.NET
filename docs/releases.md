# Verifying a release

Every release on [GitHub Releases](https://github.com/siebe41/SemanticRouter.NET/releases) can be checked, before you install it, to confirm two things: that it was built by this repository's release workflow ([`release.yml`](../.github/workflows/release.yml)) from a tagged commit on `main`, and that the files are the ones the workflow produced. The container image is also signed. None of this needs a key from us: signatures and attestations use short-lived [Sigstore](https://www.sigstore.dev/) certificates issued to the workflow run, and are recorded in Sigstore's public transparency log.

## What a release contains

| Asset | What it is |
|---|---|
| `SHA256SUMS` | The SHA-256 checksum of every other asset. |
| `SemanticRouter-<version>-<platform>.zip` / `.tar.gz`, the installers, the update feed (`releases.<channel>.json`, `*-full.nupkg`, `*-delta.nupkg`) | The desktop app ([desktop.md](desktop.md)). |
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

## Not covered yet

- **Operating system code signing.** Windows SmartScreen and macOS Gatekeeper check a publisher's code signature, not these attestations. The installers are code-signed only when the release has signing certificates ([desktop.md](desktop.md#signing)), and the archives' contents are not. Provenance shows where a file came from; it does not stop the warnings on first start.
- **NuGet author signatures.** Only nuget.org's repository signature, as above.
