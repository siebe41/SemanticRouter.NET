# CLAUDE.md

SemanticRouter.NET routes each prompt to a local model, a subscription CLI (Claude Code, Copilot, Codex) or a paid API, by meaning and difficulty. The contributor rules below are imported in full; read them before changing code.

@CONTRIBUTING.md

## Before you finish a change

- `dotnet build` and `dotnet test` pass. Warnings are errors, so a new analyzer warning fails the build.
- `dotnet format --verify-no-changes` passes (CI runs it).
- **Changed a package version or reference?** Run `dotnet restore --force-evaluate` and commit every changed `packages.lock.json`. CI, the release and the container build restore in locked mode and fail with `NU1004` otherwise (issue #369).
- **Changed a library's public API?** The `PublicAPI.Unshipped.txt` lines are in the diff (RS0016/RS0017). Never edit `PublicAPI.Shipped.txt`.

## Where things live

| Path | What |
| --- | --- |
| `src/SemanticRouter.Core` | Zero-dependency routing primitives: similarity, complexity heuristics, route selection |
| `src/SemanticRouter.AspNetCore` | The router itself: DI, routing middleware, connectors, dispatch, the embedded dashboard (`Dashboard/`, plain JS in `wwwroot/`), runtime and tuning |
| `src/SemanticRouter.Embeddings.Onnx` | In-process sentence embeddings (ONNX Runtime) |
| `src/SemanticRouter.Storage.Sqlite` | Durable state in one SQLite file |
| `src/SemanticRouter.Connectors.Copilot` | GitHub Copilot connector (its SDK's permission API is experimental; see the version bound in CONTRIBUTING) |
| `src/SemanticRouter.Hosting` | Shared hosting glue for the host and the desktop app |
| `src/SemanticRouter.Host` | The ready-to-run router app and container image |
| `src/SemanticRouter.Desktop` | The tray/menu-bar desktop app (Avalonia), its updater and service install |
| `tests/SemanticRouter.Tests` | Unit and integration tests, including the protocol fuzz tests |
| `tests/dashboard-e2e`, `tests/installer`, `tests/fixtures`, `tests/SemanticRouter.FakeCli` | Dashboard browser tests, installer checks, fixture homes for upgrade tests, a fake vendor CLI |

## Docs: read only the one you need

- `docs/architecture.md`: how a request moves, hot reload, extension points.
- `docs/api-surface.md`: what each public type is for (pairs with the PublicAPI files).
- `docs/providers.md`, `docs/clients.md`: connecting models and connecting tools.
- `docs/host.md`, `docs/desktop.md`, `docs/enterprise.md`: the three ways it runs, and machine policy.
- `docs/dashboard.md`, `docs/observability.md`, `docs/memory.md`: dashboard, spend and tracing, notes.
- `docs/routing-profiles.md`, `docs/routing-quality.md`: profiles and measuring routing.
- `docs/network.md`: everything the router contacts. A new outbound call needs a line here.
- `docs/releases.md`: verifying and cutting releases. `docs/roadmap.md`: what's planned.
- `docs/TERMS.md`: the subscription-CLI terms.

The README is the user guide (~21,000 tokens); open it only for setup and options.
