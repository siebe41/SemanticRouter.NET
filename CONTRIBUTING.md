# Contributing

Thanks for helping. Bug reports, fixes and documentation changes are welcome as issues and pull requests. For a larger change, open an issue first so the approach can be agreed before you write it; planned work is in [docs/roadmap.md](docs/roadmap.md).

Who maintains the project, how decisions are made and who can release is in [GOVERNANCE.md](GOVERNANCE.md).

## Reporting routing quality

You do not need to write code to help. If a prompt went to a model that was stronger than it needed (wasted quota or cost) or weaker than it needed (a poor answer), open a **Routed to the wrong tier** issue. The form asks for the router's decision (from the `X-Prompt-Route-*` headers, the dashboard's Activity page or the classify endpoint), the model that answered, what the prompt really needed, and your embedding settings. Describe or paraphrase the prompt instead of pasting anything private.

## Building and testing

You need a .NET 10 SDK from the 10.0.4xx feature band, any patch. [`global.json`](global.json) pins the band so that every machine and CI build with the same compiler and MSBuild. If `dotnet` reports "A compatible .NET SDK was not found", `dotnet --list-sdks` shows what you have: install the latest 10.0.4xx SDK beside it.

```bash
dotnet build      # warnings are errors; .NET analyzers at latest-recommended
dotnet test       # no network, model or vendor CLI needed
dotnet format     # fixes formatting in place; CI runs it with --verify-no-changes
```

`dotnet test` includes fuzz tests for protocol translation (`ProtocolFuzzTests`): mutated request and reply bodies for every protocol must parse, translate and be written without an unhandled exception. CI runs them with a fixed seed. When you change a parser, translator or response writer, run them longer, with other seeds:

```bash
SEMANTIC_ROUTER_FUZZ_ITERATIONS=5000 SEMANTIC_ROUTER_FUZZ_SEED=42 dotnet test --filter "FullyQualifiedName~Fuzz"
```

A failure lists each distinct crash site with the seed, the iteration and the body that caused it.

Builds send no telemetry from Avalonia: [`Directory.Build.targets`](Directory.Build.targets) replaces its build-telemetry target, so you don't need `AVALONIA_TELEMETRY_OPTOUT`. The .NET SDK has its own; set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to turn it off. [docs/network.md](docs/network.md) lists what the router itself contacts.

Every pull request runs CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)): build, test and pack on Linux, Windows and macOS, the dashboard end-to-end tests, the installers, the container image, a secret scan of the git history, a check for NuGet packages with known vulnerabilities and the formatting check. The README's [Building and testing](README.md#building-and-testing) section says what each check does and how to run it locally.

## Guidelines

- **Keep `SemanticRouter.Core` dependency-free.** It references no NuGet packages and is AOT-compatible. Code that needs a package belongs in another project (`SemanticRouter.AspNetCore`, `SemanticRouter.Embeddings.Onnx`, …).
- **Package versions** are set centrally in `Directory.Packages.props`. Each project's `packages.lock.json` records every package it restores, transitive ones included, with a content hash. After changing a version or a package reference, run `dotnet restore` and commit the changed lock files: CI, the release and the container build restore with `--locked-mode` and fail when a lock file is out of date. The desktop app and the libraries it references also record the runtimes it is published for (`DesktopRuntimeIdentifiers` in `Directory.Build.props`); add a runtime there before publishing for it.
- **Add tests** for behaviour you change. Tests must not need the network, a model or a vendor CLI; the existing tests show how to use `TestServer` and the stand-in CLI in `tests/SemanticRouter.FakeCli`.
- **Update the documentation** (README or `docs/`) when you change configuration, endpoints or behaviour.

## Changelog

[CHANGELOG.md](CHANGELOG.md) lists every user-visible change. If your pull request changes what someone using the packages, the tool, the container image or the desktop app sees (a setting, an endpoint, a default, a fix to behaviour they could notice, a security fix), add one line under **Unreleased**, in the right group:

- **Security** for anything that changes what can be read, reached or run, or by whom;
- **Added**, **Changed** or **Fixed** for the rest.

Write what changed for the user, not how, and end the line with the pull request number, e.g. `(#123)`. Internal refactoring, tests, CI and documentation-only changes need no line. When a release is cut, the maintainer renames **Unreleased** to the version and its date.

## Never commit keys

Do not commit API keys, tokens or a saved configuration file that contains them, not even in a test or a sample. Keep keys in user-secrets (`dotnet user-secrets set "SemanticRouter:Connectors:openai:ApiKey" "<key>"`), environment variables (`SemanticRouter__Connectors__openai__ApiKey`), or write a reference in the configuration (`"${env:OPENAI_API_KEY}"`). In tests, use obviously fake values.

The secret scan checks the whole history, so a key that is committed and then deleted still fails CI and is still leaked. If that happens, revoke the key at the provider; removing it from the code is not enough.

## Branch and tag rulesets

[`.github/rulesets/`](.github/rulesets) records the intended repository rulesets in GitHub's import format:

- `protect-main.json`: changes reach `main` only through a pull request with an approving review from a code owner, and only when every CI job (all three `build-and-test` runs, the secret scan, the dashboard end-to-end tests, the three installers and the container image) and both CodeQL analyses pass. Nobody can bypass it, administrators included, so the owner's own pull requests also need another code owner's approval.
- `protect-release-tags.json`: a `v*` tag, which starts a release, cannot be moved or deleted by anyone.
- `create-release-tags.json`: only administrators can create `v*` tags.

The files do not apply themselves. A repository administrator applies each one under Settings > Rules > Rulesets (Import a ruleset, or edit the existing ruleset to match). When a CI job is added or renamed, update the required checks in `protect-main.json` and in the settings.

## Roles and how to become a collaborator

Everyone starts at the first step. Moving up is by invitation from the owner, based on merged work and helpful reports over time; it is not something to request. Each step adds permissions; none of them removes the owner's review of changes to `main`.

| Role | What you can do | How you get it |
|---|---|---|
| **Tester** | Use the router and report bugs, wrong routing decisions and ideas through the issue forms. | Nothing needed: no access or invitation. |
| **Contributor** | Fork the repository and open pull requests. CI on a pull request from a fork runs after the owner approves it. A pull request merges after a review from a code owner (see `.github/CODEOWNERS`) and passing checks. | Nothing needed beyond a GitHub account. |
| **Triage** | Label, close, reopen and assign issues and pull requests, and mark duplicates. No push access to the code. | Invitation, usually after several good reports or merged pull requests. |
| **Write** | Push branches to this repository and review pull requests. You still cannot merge to `main` on your own: the branch ruleset and CODEOWNERS require the owner's review. Someone with sustained work in one area may be added to CODEOWNERS for that area, so their review is requested there too. | Rarely, by invitation, after sustained merged work. |
| **Maintain / Admin** | Repository settings, rulesets, releases and secrets. | Stays with the owner. |

There is no contributor licence agreement and no DCO sign-off (`Signed-off-by`) is required. The project is under the [MIT licence](LICENSE), and under GitHub's terms of service a contribution is made under the same licence as the project (inbound = outbound).

## Security issues

Do not report vulnerabilities in public issues or pull requests. Follow [SECURITY.md](SECURITY.md) and use GitHub's private vulnerability reporting.
