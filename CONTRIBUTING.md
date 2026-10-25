# Contributing

Thanks for helping. Bug reports, fixes and documentation changes are welcome as issues and pull requests. For a larger change, open an issue first so the approach can be agreed before you write it; planned work is in the [roadmap](docs/roadmap.md).

Who maintains the project, how decisions are made and who can release is in [Governance](GOVERNANCE.md).

## Reporting routing quality

You do not need to write code to help. If a prompt went to a model that was stronger than it needed (wasted quota or cost) or weaker than it needed (a poor answer), open a **Routed to the wrong tier** issue. The quickest way is **Report** next to the request on the dashboard's Activity page: it opens the form with the router's decision line, the model that answered, your embedding settings and the router version filled in, and leaves the prompt out. Otherwise the form asks for the decision (from the `X-Prompt-Route-*` headers or the classify endpoint), the model that answered, what the prompt really needed, your embedding settings and the version. Describe or paraphrase the prompt instead of pasting anything private.

## Building and testing

You need .NET SDK 10.0.402 or a later patch in the 10.0.4xx feature band. [`global.json`](global.json) pins 10.0.402 for CI and permits a later patch when that version is not installed, so every machine builds with the same compiler and MSBuild feature band. If `dotnet` reports "A compatible .NET SDK was not found", `dotnet --list-sdks` shows what you have: install the latest 10.0.4xx SDK beside it.

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

Builds send no telemetry from Avalonia: [`Directory.Build.targets`](Directory.Build.targets) replaces its build-telemetry target, so you don't need `AVALONIA_TELEMETRY_OPTOUT`. The .NET SDK has its own; set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to turn it off. [Network traffic](docs/network.md) lists what the router itself contacts.

Every pull request runs CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)): build, test and pack on Linux, Windows and macOS, the dashboard end-to-end tests, the installers, the container image, a secret scan of the git history, a check for NuGet packages with known vulnerabilities and the formatting check. The README's [Building and testing](README.md#building-and-testing) section says what each check does and how to run it locally.

## Guidelines

- **Keep `SemanticRouter.Core` dependency-free.** It references no NuGet packages and is AOT-compatible. Code that needs a package belongs in another project (`SemanticRouter.AspNetCore`, `SemanticRouter.Embeddings.Onnx`, …).
- **Package versions** are set centrally in `Directory.Packages.props`. Each project's `packages.lock.json` records every package it restores, transitive ones included, with a content hash. After changing a version or a package reference, run `dotnet restore` and commit the changed lock files: CI, the release and the container build restore with `--locked-mode` and fail when a lock file is out of date. The desktop app and the libraries it references also record the runtimes it is published for (`DesktopRuntimeIdentifiers` in `Directory.Build.props`); add a runtime there before publishing for it.
- **Namespaces.** A new type goes in its package's own namespace. Only static classes of setup extension methods go in `Microsoft.Extensions.DependencyInjection` (on `IServiceCollection` and `SemanticRouterBuilder`) or `Microsoft.AspNetCore.Builder` (`Use*` and `Map*`), and request and response bodies of HTTP endpoints are `internal` ([Namespaces](docs/architecture.md#namespaces)).
- **Add tests** for behaviour you change. Tests must not need the network, a model or a vendor CLI; the existing tests show how to use `TestServer` and the stand-in CLI in `tests/SemanticRouter.FakeCli`.
- **Update the documentation** (README or `docs/`) when you change configuration, endpoints or behaviour.
- **Changing the built-in template** (`src/SemanticRouter.Hosting/Templates/semantic-router.template.json`) reaches existing installs only through a template upgrade. The first change after a release raises its `TemplateVersion` (and `TemplateMigration.Current`) and adds the released files to `Templates/Previous` as `semantic-router.template.<old version>.json` and `semantic-router.evaluation.<old version>.jsonl`. `TemplateMigrationTests` fails until the fingerprint it keeps for the current template is updated, as a reminder.

## Package versions and dependencies

The five library packages (`SemanticRouter.Core`, `SemanticRouter.AspNetCore`, `SemanticRouter.Storage.Sqlite`, `SemanticRouter.Embeddings.Onnx` and `SemanticRouter.Connectors.Copilot`) and the `semantic-router` tool are versioned together: every release publishes all of them with the same version, built from one commit.

They also depend on each other exactly. `SemanticRouter.AspNetCore` 0.6.0 depends on `SemanticRouter.Core` `[0.6.0]`, and Storage.Sqlite, Embeddings.Onnx and Connectors.Copilot 0.6.0 depend on `SemanticRouter.AspNetCore` `[0.6.0]`, not on 0.6.0 or later. Each is compiled against one build of the package below it, and Storage.Sqlite and Connectors.Copilot use its internals through `InternalsVisibleTo`, which carry no compatibility promise. With an open lower bound NuGet would pair an old package with a newer sibling without a word, and the app would fail at run time with a `MissingMethodException`. With an exact version, two packages that want different versions of a sibling fail the restore (NU1107), and an app that references a newer sibling directly gets warning NU1608. An app updates all the SemanticRouter packages it uses together.

[`Directory.Build.targets`](Directory.Build.targets) makes every project reference an exact dependency in the package (`UseExactProjectReferenceVersions`), and `dotnet pack` fails when a generated nuspec gives a `SemanticRouter.*` dependency anything else (`CheckExactSemanticRouterDependencies`). So a new library package depends on its siblings through a `ProjectReference`, never a `PackageReference`.

The internals the sibling packages use are a contract between the packages of one version, not a public API. Change them freely, but build and test the siblings with the change in the same pull request (the solution build does). Today they are:

- Connectors.Copilot: `ChatResponseWriterFactory`, `IChatResponseWriter`, `ChatTranscript`, `CliScratch`, `DroppedFeatures`, `PromptedToolFormat` and `ConnectorOptions.EffectiveTimeout`.
- Storage.Sqlite: `IStateRetention` and `OwnerOnly`.

Do not add `InternalsVisibleTo` for a package outside this repository.

**Third-party dependencies** have an open lower bound (the version in `Directory.Packages.props`, or later), except when a package uses an API its author marks as experimental or otherwise not covered by their compatibility promise. Then the dependency gets an upper bound up to the next version that may break it, and the bound moves only after the package was tested against that version. Today that is `GitHub.Copilot.SDK` `[1.0.22, 1.1)`: Connectors.Copilot uses its permission API, marked experimental (GHCP001). Raising the bound changes the version range in `Directory.Packages.props`; run `dotnet restore` and commit the lock files as for any version change.

## Public API and package validation

The public API is every public type and member of the five library packages, and every protected member of a public type that can be inherited, as compiled: names, parameter and return types, and members the compiler generates, such as a record's constructor and `Deconstruct`. Internals are not, nor is the code of the `semantic-router` tool and the desktop app. The HTTP endpoints, the configuration and the behaviour are part of what users depend on too, but no tool checks them: the changelog and the review do.

Adding to the public API is fine. Removing or changing a member breaks every application compiled against the old one, even when its source would still compile: adding an optional parameter, for example, removes the method that had one parameter less. Prefer a new overload, and mark what it replaces `[Obsolete]` rather than removing it.

**Adding a field or a dependency without a break.** Write the public API so that it grows by addition:

- A data type is a record with `required` and `init` properties (`public required int Tier { get; init; }`), not a positional record (`record X(int Tier)`): a positional record's constructor and `Deconstruct` change with every member added. A new field is a new `init` property that is not `required`, whose default keeps today's behaviour. A positional record that is complete by definition, such as `RouteScore` (a route and a similarity) or `TimeWindow` (a start and an end), stays positional, and its documentation says it is frozen.
- A service class takes its required collaborators as constructor parameters, and the ones that have a default in a dependencies object: `new PromptRouter(classifier, options, new PromptRouterDependencies { Health = health })`. A new collaborator is a new property of that object. The constructor takes the object as nullable (`PromptRouterDependencies? dependencies`) and treats `null` as all defaults (`dependencies ??= new();`, not `ThrowIfNull`). `PromptRouterDependencies`, `SemanticPromptClassifierDependencies`, `DefaultModelSelectorDependencies` and `ModelHealthTrackerDependencies` are the ones today.
- Anywhere else, a new parameter is a new overload, never an optional parameter appended to a shipped signature. A trailing `CancellationToken cancellationToken = default` is the one exception, and only on a new method. The public API analyzer (below) reports RS0026 when two overloads that are not yet in `PublicAPI.Shipped.txt` both have an optional parameter, such as two new overloads that both end in a `CancellationToken`: disable it around them with a comment.
- An enum has a member with the value 0, because `default` of an enum (an unset property, a missing JSON field) is 0. An enum that has none says why in its documentation, as `ResponseFormatKind` does.

Package validation (below) enforces this: appending a parameter or a positional member removes a member the baseline has, and the pack fails. `PublicApiShapeTests` checks every library package for the shape: no positional record except the frozen ones, no public constructor with an optional parameter, and no enum without a zero member except the documented ones.

**The public API files.** Each library lists its public API, one line per type and member, in `src/<Project>/PublicAPI.Shipped.txt` (as of the last release) and `PublicAPI.Unshipped.txt` (what has changed since: new lines, and `*REMOVED*` lines for what is gone). [Microsoft.CodeAnalysis.PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md) fails the build on a public type or member that is not listed (RS0016) or a listed one that no longer exists (RS0017), and on new overloads that break the rules above (RS0026, RS0027; members already in `PublicAPI.Shipped.txt` are not reported). So every change to the public API, an addition included, is a line in the pull request's diff, where package validation (below) only sees removals and changes, and only at pack time. When you change the public API:

- New or changed members: apply the code fix for RS0016 in the IDE, or run `dotnet format analyzers src/<Project>/<Project>.csproj --diagnostics RS0016 --severity info`. Either adds the lines to `PublicAPI.Unshipped.txt`. A plain `dotnet format` applies the same fix, and `dotnet format --verify-no-changes` (CI's Formatting step) reports a missing line as RS0016, so an unlisted member fails there as well as in the build.
- A removed or changed member that is in `PublicAPI.Shipped.txt`: add its line to `PublicAPI.Unshipped.txt` with `*REMOVED*` in front. One that is only in `PublicAPI.Unshipped.txt`: delete its line there.
- A new, removed or renamed public type: add, remove or rename its line in [docs/api-surface.md](docs/api-surface.md), which says in one line what each public type is for. `ApiSurfaceDocumentationTests` fails until it matches.
- Leave `PublicAPI.Shipped.txt` alone: it changes only after a release ([After a release](docs/releases.md#after-a-release)).

`dotnet pack -c Release` (run by CI on every pull request) validates each library package against the last release on nuget.org, the baseline set by `PackageValidationBaselineVersion` in [`Directory.Build.targets`](Directory.Build.targets). A type or member that the baseline has and the new package does not, or one changed in a way that breaks callers (made sealed, abstract or less visible, for example), fails the pack with a CP diagnostic, such as `CP0002: Member '...' exists on [Baseline] ... but not on ...`.

**A deliberate break** (before 1.0, or for a new major version) is suppressed in the pull request that makes it, so it is reviewed there:

```bash
dotnet pack -c Release -p:ApiCompatGenerateSuppressionFile=true
```

This writes `src/<Project>/CompatibilitySuppressions.xml` with one entry per break. Commit it, and say what broke in the changelog line. The suppression files are deleted after the release that ships the breaks ([After a release](docs/releases.md#after-a-release)).

Validation is incremental: a second local pack with no file changed skips it, also when it passes another `-p:PackageValidationBaselineVersion`. Delete `src/*/obj/*/*/Microsoft.NET.ApiCompat.ValidatePackage.semaphore` (or the `obj` folders) before packing again. CI starts from a clean checkout.

**The baseline.** Until 1.0.0 is released, it is the latest 0.x release. For 1.0 the API is cleaned up, the deliberate breaks against 0.x are suppressed, and 1.0.0 is released. From then on the baseline is the latest 1.x release, and the 1.x line allows no suppressions: a break waits for 2.0. A new library package has no baseline until its first release; its project file sets `<DisablePackageBaselineValidation>true</DisablePackageBaselineValidation>` until then.

## Changelog

The [changelog](CHANGELOG.md) lists every user-visible change. If your pull request changes what someone using the packages, the tool, the container image or the desktop app sees (a setting, an endpoint, a default, a fix to behaviour they could notice, a security fix), add one line under **Unreleased**, in the right group:

- **Security** for anything that changes what can be read, reached or run, or by whom;
- **Added**, **Changed** or **Fixed** for the rest.

Write what changed for the user, not how, and end the line with the pull request number, e.g. `(#123)`. Internal refactoring, tests, CI and documentation-only changes need no line. When a release is cut, the maintainer renames **Unreleased** to the version and its date.

## Never commit keys

Do not commit API keys, tokens or a saved configuration file that contains them, not even in a test or a sample. Keep keys in user-secrets (`dotnet user-secrets set "SemanticRouter:Connectors:openai:ApiKey" "<key>"`), environment variables (`SemanticRouter__Connectors__openai__ApiKey`), or write a reference in the configuration (`"${env:OPENAI_API_KEY}"`). In tests, use obviously fake values.

The secret scan checks the whole history, so a key that is committed and then deleted still fails CI and is still leaked. If that happens, revoke the key at the provider; removing it from the code is not enough.

## Branch and tag rulesets

[`.github/rulesets/`](.github/rulesets) records the intended repository rulesets in GitHub's import format:

- `protect-main.json`: changes reach `main` only through a pull request with an approving review from a code owner, and only when every CI job (all three `build-and-test` runs, the secret scan, the dashboard end-to-end tests, the three installers, the installer SBOMs and the container image) and both CodeQL analyses pass. Nobody can bypass it, administrators included, so the owner's own pull requests also need another code owner's approval.
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

Do not report vulnerabilities in public issues or pull requests. Follow the [security policy](SECURITY.md) and use GitHub's private vulnerability reporting.
