# 12. SDK: local evaluation with graceful degradation

Status: accepted

## Context

Applications must keep working when the flag server is slow, restarting or gone, and flag evaluation sits on hot paths. The SDK is an OpenFeature provider, so it has to follow the OpenFeature rules (never throw, defaults plus reason, provider events).

## Decision

- **Local evaluation.** The provider downloads the environment's whole ruleset (`GET /api/v1/sdk/ruleset`), compiles it with the same `Bunting.Evaluation` engine the server uses, and evaluates in memory. Evaluation makes no network call and takes no lock: the compiled ruleset is replaced by a single reference swap (`Volatile`).
- **Synchronization.** Three background loops: an initial download, an SSE stream (`ruleset-updated` carries only a version; on a new version the ruleset is fetched with `If-None-Match`), and a poll loop that only fetches while the stream is down (30 s by default). A failed download drops the stream so the reconnect, with exponential backoff and jitter, retries it. A stream that is silent for 70 s (heartbeats are 25 s) is considered dead.
- **Never go back.** A downloaded ruleset with a lower version than the current one is ignored.
- **Optional snapshot file.** Every new ruleset is written atomically (temp file + rename, mode 0600 on Unix). On start-up it is loaded first, so an application boots with the last known flags when the server is unreachable. The snapshot is treated as stale until the server confirms it. A corrupt file is ignored.
- **Provider states.** With no ruleset at all, initialization completes after `InitializeTimeout` without throwing; evaluations return the default with `PROVIDER_NOT_READY`, and a `Ready` event follows when the server becomes reachable. If the ruleset cannot be confirmed for `StaleAfter` (2 min) the provider emits `Stale` and reports reason `STALE`; it emits `Ready` when confirmed again. A new version emits `ConfigurationChanged`. Without a ruleset, the first failure emits `Error`.
- **Privacy.** The evaluation context is converted for the engine and never logged or stored. Telemetry carries the flag key and reason only (`bunting.sdk.evaluations`), plus `bunting.sdk.ruleset.age`, `bunting.sdk.connection.state` and an `ActivitySource` around synchronizations.
- **DI.** `AddOpenFeature(o => o.AddBuntingProvider(...))` (OpenFeature.Hosting 2.14) with an overload that takes a domain.

## Consequences

- The ruleset (including segment member lists) lives in the application's memory and, optionally, on disk.
- Targeting changes reach applications in well under a second while the stream is up; during an outage staleness is bounded by the poll interval once the server is reachable again.
- Structure/list attributes in the OpenFeature context cannot be targeted and are skipped.
