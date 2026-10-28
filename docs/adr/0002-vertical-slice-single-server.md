# ADR 0002: Vertical slices and a single Server process

- Status: Accepted

## Context
The product's positioning is minimal infrastructure: one container plus PostgreSQL.

## Decision
- Features are organised as vertical slices inside `Bunting.Server`; no MediatR, AutoMapper, generic repositories or layered "Clean Architecture" templates.
- One `Bunting.Server` process (horizontally scalable) plus `Bunting.Admin`. No microservices.
- No Redis or message broker. Cross-instance change propagation uses PostgreSQL `LISTEN/NOTIFY` (Phase 2/4).
- `Bunting.Domain` has no dependencies; `Bunting.Evaluation` has no infrastructure dependencies and is shared by server and SDK as the single source of truth.

## Consequences
Fewer moving parts to operate and fewer abstractions to learn. Scaling beyond what Postgres notifications handle would require revisiting this.
