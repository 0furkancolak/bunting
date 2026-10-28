# Bunting

Open source, self-hosted feature flag platform for .NET. OpenFeature/OFREP first, minimal infrastructure (one container + PostgreSQL), modern .NET.

> **Status:** early development (Phase 0 — skeleton). See [docs/roadmap.md](docs/roadmap.md) and [docs/adr](docs/adr).

*Why "bunting"?* Bunting is a string of flags strung together — this project manages a whole collection of flags, not a single one.

## Evaluation

The rule engine ([`Bunting.Evaluation`](src/Bunting.Evaluation)) is shared by the server and the SDK. Its exact behaviour
is specified in [docs/evaluation-semantics.md](docs/evaluation-semantics.md). Highlights: first matching rule wins,
OpenFeature-compatible reasons and error codes, deterministic sticky rollouts (MurmurHash3, 0.01 % precision),
conditions on missing attributes never match.

Benchmarks (Apple M4, .NET 10): a 20-rule flag evaluates in **< 1 µs with zero allocations**
([details](docs/benchmarks.md)).

## OFREP

Bunting speaks [OFREP](https://openfeature.dev/docs/reference/other-technologies/ofrep/) (`docs/ofrep-openapi.yaml`), so the
standard OpenFeature providers work in any language without a Bunting-specific client:

```js
OpenFeature.setProvider(new OFREPProvider({ baseUrl: 'https://flags.example.com', headers: [['X-API-Key', 'bnt_srv_…']] }));
```

Server keys evaluate everything; client keys (`bnt_cli_…`) only see flags marked client-side and never receive rules.
Conformance is tested against the official JS providers in `tests/Bunting.Server.OfrepConformance`.

## Run locally

```bash
dotnet run --project src/Bunting.AppHost
```

Requires the .NET 10 SDK and Docker. Aspire starts PostgreSQL, the Server and the Admin app.

## License

Apache-2.0
