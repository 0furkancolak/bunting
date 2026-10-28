# 11. Real-time propagation with LISTEN/NOTIFY and SSE

Status: accepted

## Context

A flag change made through any Server instance must reach SSE clients connected to any other instance in under a second, without Redis or a message queue.

## Decision

- Every write transaction ends with `pg_notify('bunting_ruleset_changed', '<envId>:<version>')`, so a notification is only sent for committed changes.
- Each instance runs `RulesetChangeListener`, which holds one dedicated `LISTEN` connection. On a notification it invalidates `RulesetCache` for that environment (only if the cache is older than the announced version) and publishes the version to `ChangeHub`.
- `ChangeHub` keeps per-environment subscriber sets. Each subscriber owns a bounded(1) `DropOldest` channel: a slow client coalesces to the latest version instead of building a backlog, and publishers never block. Versions at or below the last published one are ignored.
- NOTIFY is not durable. After every (re)connect, and every 15 s while connected, the listener reconciles by reading the current versions from the database for all subscribed or cached environments. This covers changes missed while the connection was down. Reconnects use exponential backoff (250 ms to 10 s) with jitter.
- Npgsql TCP keepalive is enabled so half-open connections are detected.
- SSE events carry only `{ "version": N }`. Clients re-fetch the ruleset (`/api/v1/sdk/ruleset`, ETag = version) or the OFREP bulk endpoint. No flag data or context travels over the stream.
- A `heartbeat` event is sent every 25 s so proxies keep the connection open; `X-Accel-Buffering: no` is set.
- `EventSource` cannot set headers, so `/api/v1/client/stream` additionally accepts `?apiKey=` for client keys only. Client keys are public by design; server keys are never accepted in a query string.
- The OFREP `eventStreams` discovery field is not advertised: OFREP expects `message` events carrying `refetchEvaluation`, while this API uses `flags-changed` as specified.

## Consequences

- No extra infrastructure, and propagation latency is a single NOTIFY round trip.
- The `LISTEN` connection is a long-lived connection outside the pool budget; one per instance.
- Each instance can serve a stale ruleset for at most the NOTIFY delay; the periodic reconcile bounds staleness after a lost notification to about 15 s.
