# Contributing to Bunting

Thanks for your interest! Bunting is an Apache-2.0 licensed, self-hosted feature flag platform built on .NET 10.

## Ground rules

- **Vertical slices**, not layers. No MediatR, AutoMapper, generic repositories or Clean Architecture scaffolding.
- `Bunting.Evaluation` has **no infrastructure dependencies** (no EF, no HTTP, no mandatory DI). Keep it that way.
- Evaluation is a pure function: same ruleset + same context → same result.
- Significant design decisions are recorded as ADRs in [`docs/adr`](docs/adr). Add one in your PR when you make or change a decision.
- Context data (user attributes) must never be logged or stored.

## Development setup

Requirements: .NET 10 SDK, Docker (for Postgres via Aspire and for Testcontainers).

```bash
dotnet build Bunting.sln
dotnet test --solution Bunting.sln
dotnet format Bunting.sln --verify-no-changes
dotnet run --project src/Bunting.AppHost   # starts Postgres, Server and Admin via Aspire
```

Warnings are errors. CI runs the format check, build and tests.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:` …).
- Keep PRs small and focused; include tests for behaviour changes.
- Code, comments and docs are written in English.

## Adding a NuGet dependency

Versions are managed centrally in `Directory.Packages.props`. Verify the package is actively maintained and pick a current stable version.
