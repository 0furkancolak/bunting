# ADR 0003: Build and test tooling

- Status: Accepted

## Context
We want reproducible builds, strict code quality and a modern test stack on .NET 10.

## Decision
- `Directory.Build.props` enables nullable, latest C#, `TreatWarningsAsErrors`, latest-recommended analyzers and deterministic builds.
- NuGet versions are managed centrally in `Directory.Packages.props` (central package management with transitive pinning).
- Tests use xunit.v3, which runs on Microsoft.Testing.Platform. `global.json` opts `dotnet test` into the MTP runner (required on .NET 10); use `dotnet test --solution Bunting.sln`.
- Formatting is enforced in CI with `dotnet format --verify-no-changes` against `.editorconfig`.
- Aspire 13 project templates are not installed in CI; AppHost and ServiceDefaults are hand-written and use the `Aspire.AppHost.Sdk`.

## Consequences
Contributors need only the .NET 10 SDK (and Docker for integration tests).
