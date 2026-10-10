# Contributing

Thanks for helping. Bug reports, fixes and documentation changes are welcome as issues and pull requests. For a larger change, open an issue first so the approach can be agreed before you write it; planned work is in [docs/roadmap.md](docs/roadmap.md).

## Reporting routing quality

You do not need to write code to help. If a prompt went to a model that was stronger than it needed (wasted quota or cost) or weaker than it needed (a poor answer), open a **Routed to the wrong tier** issue. The form asks for the router's decision (from the `X-Prompt-Route-*` headers, the dashboard's Activity page or the classify endpoint), the model that answered, what the prompt really needed, and your embedding settings. Describe or paraphrase the prompt instead of pasting anything private.

## Building and testing

You need the .NET SDK named in [`global.json`](global.json).

```bash
dotnet build      # warnings are errors; .NET analyzers at latest-recommended
dotnet test       # no network, model or vendor CLI needed
dotnet format     # fixes formatting in place; CI runs it with --verify-no-changes
```

Every pull request runs CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)): build, test and pack on Linux, Windows and macOS, the dashboard end-to-end tests, the installers, the container image, a secret scan of the git history, a check for NuGet packages with known vulnerabilities and the formatting check. The README's [Building and testing](README.md#building-and-testing) section says what each check does and how to run it locally.

## Guidelines

- **Keep `SemanticRouter.Core` dependency-free.** It references no NuGet packages and is AOT-compatible. Code that needs a package belongs in another project (`SemanticRouter.AspNetCore`, `SemanticRouter.Embeddings.Onnx`, …).
- **Package versions** are set centrally in `Directory.Packages.props`.
- **Add tests** for behaviour you change. Tests must not need the network, a model or a vendor CLI; the existing tests show how to use `TestServer` and the stand-in CLI in `tests/SemanticRouter.FakeCli`.
- **Update the documentation** (README or `docs/`) when you change configuration, endpoints or behaviour.

## Never commit keys

Do not commit API keys, tokens or a saved configuration file that contains them, not even in a test or a sample. Keep keys in user-secrets (`dotnet user-secrets set "SemanticRouter:Connectors:openai:ApiKey" "<key>"`), environment variables (`SemanticRouter__Connectors__openai__ApiKey`), or write a reference in the configuration (`"${env:OPENAI_API_KEY}"`). In tests, use obviously fake values.

The secret scan checks the whole history, so a key that is committed and then deleted still fails CI and is still leaked. If that happens, revoke the key at the provider; removing it from the code is not enough.

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
