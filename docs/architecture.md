# Architecture

> Work in progress — filled in as phases land. See [ADRs](adr/).

```mermaid
flowchart LR
  Admin[Bunting.Admin<br/>Blazor Server] -->|Management API| Server
  Apps[Apps using OpenFeature + OFREP] -->|OFREP| Server
  Sdk[Bunting.Sdk<br/>local evaluation] -->|ruleset + SSE| Server
  Server[Bunting.Server] --> PG[(PostgreSQL)]
  Server -. LISTEN/NOTIFY .- PG
```

Projects: `Domain` (entities) ← `Evaluation` (rule engine, shared by server and SDK) ← `Server`/`Sdk`; `ServiceDefaults` (OpenTelemetry, health), `AppHost` (Aspire).

## Change propagation

```mermaid
sequenceDiagram
  participant A as Server A
  participant PG as PostgreSQL
  participant B as Server B
  participant C as SSE client on B
  A->>PG: tx: change + audit + version bump + pg_notify
  PG-->>A: NOTIFY envId:version
  PG-->>B: NOTIFY envId:version
  B->>B: invalidate RulesetCache, ChangeHub.Publish
  B-->>C: event ruleset-updated {version}
  C->>B: GET /api/v1/sdk/ruleset (If-None-Match)
```

After a reconnect each instance re-reads versions from the database, so missed notifications are recovered. See [ADR 0011](adr/0011-realtime-propagation.md) and [sdk-api.md](sdk-api.md).

The .NET SDK evaluates locally against the downloaded ruleset and degrades gracefully; see [ADR 0012](adr/0012-sdk-local-evaluation.md) and [sdk.md](sdk.md).
