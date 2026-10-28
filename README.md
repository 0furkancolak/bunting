# Bunting

Open source, self-hosted feature flag platform for .NET. OpenFeature/OFREP first, minimal infrastructure (one container + PostgreSQL), modern .NET.

> **Status:** early development (Phase 0 — skeleton). See [docs/roadmap.md](docs/roadmap.md) and [docs/adr](docs/adr).

*Why "bunting"?* Bunting is a string of flags strung together — this project manages a whole collection of flags, not a single one.

## Run locally

```bash
dotnet run --project src/Bunting.AppHost
```

Requires the .NET 10 SDK and Docker. Aspire starts PostgreSQL, the Server and the Admin app.

## License

Apache-2.0
