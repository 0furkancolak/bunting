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
