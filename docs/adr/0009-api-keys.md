# ADR 0009: API keys are stored as SHA-256 hashes and validated in constant time

- Status: Accepted

## Context
API keys grant read access to an environment's flags (server keys: full ruleset semantics, client keys: only
client-side flags). A leaked database must not leak usable keys.

## Decision
- Keys are `bnt_srv_` / `bnt_cli_` plus 32 random bytes (base64url, 256 bits of entropy). Because the secret is
  high-entropy, a fast unsalted SHA-256 is sufficient (no password-style KDF needed) and allows lookup by hash.
- Only the hash and a 4-character hint are persisted. The plaintext is returned once, in the creation response, and never
  appears in the audit log, listings or logs.
- Validation hashes the presented key, finds the row through the unique index on the hash, then compares with
  `CryptographicOperations.FixedTimeEquals`. Revoked keys are rejected.
- Validation results (including negative ones) are cached for 10 s per instance; revocation drops the local entry
  immediately, other instances converge within the TTL.
- Keys are accepted as `X-API-Key: <key>` or `Authorization: Bearer <key>` (the two OFREP mechanisms). Management tokens
  and API keys use separate authentication schemes and are never interchangeable.
- Rate limiting is per API key (hashed partition key, fixed window, `Bunting:RateLimit:PermitLimit` / `WindowSeconds`,
  default 6000 requests per second); anonymous traffic is limited per IP. Rejections return 429 with `Retry-After`.

## Consequences
- Lost keys cannot be recovered, only rotated.
- Up to 10 s revocation latency on other instances until the real-time phase pushes revocations.
