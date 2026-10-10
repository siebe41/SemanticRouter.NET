# Contributing

Thanks for helping. Bug reports, fixes and documentation changes are welcome as issues and pull requests. For a larger change, open an issue first so the approach can be agreed before you write it; planned work is in [docs/roadmap.md](docs/roadmap.md).

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

## Security issues

Do not report vulnerabilities in public issues or pull requests. Follow [SECURITY.md](SECURITY.md) and use GitHub's private vulnerability reporting.
