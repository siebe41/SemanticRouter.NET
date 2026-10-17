# Governance

SemanticRouter.NET is a small project with one maintainer. This page says who decides what, who can release, and what happens if the maintainer is not available. It changes through a pull request like any other file.

## Maintainers

| Who | Role |
|---|---|
| [@siebe41](https://github.com/siebe41) | Owner and maintainer: reviews and merges changes, cuts releases, handles security reports, holds the repository settings, the signing credentials and the nuget.org packages. |

A second maintainer is wanted, so that a change can be reviewed by someone other than its author, and so that the project does not stop when one person is away. Maintainers are invited by the owner after sustained merged work; the steps up to that are in [Roles and how to become a collaborator](CONTRIBUTING.md#roles-and-how-to-become-a-collaborator). A second maintainer gets the **Write** role, a line in `CODEOWNERS`, and is added as a reviewer of the `release` environment. Merge rights (code owner) and release rights (environment reviewer, tag creation) are separate and are granted separately.

## How decisions are made

- **Day to day:** through pull requests. A change merges when a code owner approves it and CI passes.
- **Larger changes** (a new connector type, a change to defaults, anything that breaks configuration or an API): an issue first, so the approach is agreed before the code is written. Planned work and its reasons are in the [roadmap](docs/roadmap.md).
- **Disagreements** are discussed in the issue or pull request. With one maintainer, the maintainer decides and writes down why. With more than one, the maintainers aim for agreement; if they cannot agree, the owner decides.
- **Security reports** are handled privately, as in the [security policy](SECURITY.md), and are not discussed in public until the fix is released.

## Code owners

[`.github/CODEOWNERS`](.github/CODEOWNERS) names who must approve a change to `main`. Today that is the owner for every path. The `protect-main` ruleset ([Branch and tag rulesets](CONTRIBUTING.md#branch-and-tag-rulesets)) requires a code owner's approval with no bypass, so once it is applied as recorded, the owner's own pull requests need a second code owner. Until there is one, the owner merges their own changes after CI passes. <!-- owner: confirm how your own pull requests merge until there is a second code owner -->

## Releases

- **Who can release:** under the recorded rulesets, only repository administrators can create a `v*` tag, and nobody can move or delete one (`create-release-tags`, `protect-release-tags`). Every publishing job of the [release workflow](.github/workflows/release.yml) runs in the `release` environment, whose required reviewer approves it: today the owner. <!-- owner: confirm the release environment's reviewers -->
- **What a release is built from:** a tag on a commit that is on `main`; the workflow refuses any other tag, and publishes nothing before the tests pass.
- **How a release is cut:** rename **Unreleased** in the [changelog](CHANGELOG.md) to the version and date in a pull request and merge it. Then run **Actions > Cut a release** on `main` with the version, and approve the `release` environment ([Cutting a release](docs/releases.md#cutting-a-release)). Only the repository owner can run it, and the release workflow refuses a `v*` tag pushed by anyone else. The Windows and macOS installers must be code-signed, or the release fails unless `ALLOW_UNSIGNED_RELEASE` is set ([Code signing](docs/releases.md#code-signing)).
- **Versions:** [Semantic Versioning](https://semver.org/). Which versions get security fixes is in [Supported versions](SECURITY.md#supported-versions).

## Security response

The owner receives and handles vulnerability reports, with the targets in [Response targets](SECURITY.md#response-targets). A second maintainer, once there is one, is added to the repository's security advisories and covers reports when the owner is away.

## If the maintainer is not available

The project depends on accounts that only the owner holds: GitHub administration of the repository, the nuget.org account that owns the packages (Trusted Publishing), the `ghcr.io` image, and the code-signing accounts. Succession covers all of them.

- **Backup contact:** `<backup name>` <!-- owner: name a person, or delete this line until there is one -->. They have Write access to the repository, and are the owner's designated successor in GitHub's account settings, which lets them archive or transfer the owner's repositories if the owner dies.
- **Planned step-down:** the owner either hands the project over (repository admin, nuget.org package ownership, the `ghcr.io` package and the `release` environment) to a maintainer who agrees to take it, or archives the repository.
- **Unplanned absence:** if issues, pull requests and security reports have had no maintainer response for 90 days <!-- owner: confirm the period -->, the backup contact adds a note at the top of the README and a pinned issue saying the project is unmaintained and gets no security fixes. Without admin rights they cannot release, so nothing new is published.
- **After archiving:** published packages, images and releases stay available and verifiable, but get no more fixes, security fixes included. The code stays under the [MIT licence](LICENSE), so anyone may fork it and continue under another name.
