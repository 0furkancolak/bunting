# ADR 0010: OFREP implementation, ETags and client-key isolation

- Status: Accepted

## Context
The server implements OFREP v0.4.0 (`docs/ofrep-openapi.yaml`, copied unchanged from open-feature/protocol). Client keys are
embedded in browsers and apps and must reveal nothing beyond the evaluated values of client-side flags.

## Decision
- Endpoints: `POST /ofrep/v1/evaluate/flags/{key}` and `POST /ofrep/v1/evaluate/flags` (bulk), authenticated with an API key.
- Status mapping: success 200; invalid context 400 `INVALID_CONTEXT`; unknown flag 404 `FLAG_NOT_FOUND`;
  evaluation failures (`TARGETING_KEY_MISSING`, `PARSE_ERROR`) 400 on the single endpoint and per-item failures in bulk.
  Context values must be strings, numbers or booleans; nested values are rejected.
- Bulk responses carry `metadata.version` and an `ETag` derived from ruleset version, key type and a hash of the context
  (the result depends on all three). `If-None-Match` is answered with 304 before any flag is evaluated.
  `flagConfigEtag` / `flagConfigLastModified` query parameters are accepted and ignored; `eventStreams` is added with the
  real-time phase.
- Client keys: bulk contains only `ClientSideAvailable` flags; a hidden flag is answered exactly like a missing one
  (404 `FLAG_NOT_FOUND`); rule ids (`metadata.ruleId`) are omitted; rules and segments are never serialised. A dedicated
  test class (`ClientKeyLeakTests`) asserts this on every response shape.
- Archived flags are left out of bulk responses; single evaluation reports them as `DISABLED`.
- `CF-IPCountry` fills `country` only when `Bunting:Ofrep:UseCloudflareCountry` is enabled and the context has none.
- Context data is never logged or stored; only transient evaluation uses it.
- Compiled rulesets are cached per environment (`RulesetCache`), invalidated right after local commits and re-checked every
  30 s; cross-instance push comes with LISTEN/NOTIFY in the real-time phase.

## Known deviation to confirm
The OFREP schema lists the success reasons `STATIC, TARGETING_MATCH, SPLIT, DISABLED, UNKNOWN`, but OpenFeature (and our
engine, as required) also defines `DEFAULT` for the fallthrough. We emit `DEFAULT` unchanged; the official JS providers accept it
(see conformance tests). If strict schema validation matters more than information, the mapping is a single line in
`OfrepEndpoints.Map`.

## Consequences
Official OFREP providers work unmodified. Conformance is verified by `Bunting.Server.OfrepConformance`, which drives a real
server through `@openfeature/server-sdk`, `@openfeature/ofrep-provider`, `@openfeature/web-sdk` and `@openfeature/ofrep-web-provider`.
