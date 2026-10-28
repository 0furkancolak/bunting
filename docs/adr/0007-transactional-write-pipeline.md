# ADR 0007: One transaction per Management API write

- Status: Accepted

## Context
A change must be visible to evaluators exactly when it is durable, it must be attributable, and instances must learn about
it without polling or a message broker (see [ADR 0002](0002-vertical-slice-single-server.md)).

## Decision
Every Management API write runs through `ChangeLog.WriteAsync`, a single database transaction containing:
1. the change itself;
2. an `AuditEntry` with actor, entity, action and before/after JSON;
3. `UPDATE environments SET RulesetVersion = RulesetVersion + 1 ... RETURNING` for each affected environment;
4. `SELECT pg_notify('bunting_ruleset_changed', '<environmentId>:<version>')`.

PostgreSQL delivers `NOTIFY` only if the transaction commits, so listeners never observe a rolled-back change, and the
row lock taken by the version bump serialises writers of the same environment. Optimistic concurrency uses a `Version`
column per flag, flag-environment config and segment: `If-Match` is mandatory (428 when absent), a stale value returns
409 with the current version, and EF's concurrency token catches races that slip between check and write
(only one of N parallel writers succeeds). Failures inside a write scope are thrown as `ApiException`, which rolls
back, so a rejected request leaves no audit entry, no version bump and no notification.

Flag-level changes that affect evaluation (variants, archive, client-side availability) bump every environment of the
project; environment-scoped changes (config, segments) bump only that environment. Environments are always locked in id order to
avoid deadlocks.

## Consequences
- Audit, versioning and notification can never drift apart.
- Writers of one environment are serialised for the duration of the transaction; acceptable for configuration traffic.
- `NOTIFY` payloads are limited to 8000 bytes; we only send `<id>:<version>` and listeners re-read state.
